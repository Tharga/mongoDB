﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tharga.MongoDB.Atlas;
using Tharga.MongoDB.Configuration;
using Tharga.MongoDB.Internals;
using Tharga.Toolkit.TypeService;

namespace Tharga.MongoDB;

public static class AddMongoDbExtensions
{
    private static readonly ConcurrentDictionary<Type, Type> _registeredRepositories = new();
    private static readonly ConcurrentDictionary<Type, Type> _registeredCollections = new();
    private static Action<ActionEventArgs> _actionEvent;

    public static void UseMongoDB(this IApplicationBuilder applicationBuilder, Action<DatabaseUsage> options = null)
    {
        var repositoryConfiguration = applicationBuilder.ApplicationServices.GetService<IRepositoryConfiguration>();

        var databaseOptions = new DatabaseUsage
        {
            FirewallConfigurationNames = repositoryConfiguration.GetDatabaseConfigurationNames().ToArray()
        };

        options?.Invoke(databaseOptions);

        var mongoDbFirewallStateService = applicationBuilder.ApplicationServices.GetService<IMongoDbFirewallStateService>();
        var mongoDbServiceFactory = applicationBuilder.ApplicationServices.GetService<IMongoDbServiceFactory>();

        Task.Run(async () =>
        {
            foreach (var configurationName in databaseOptions.FirewallConfigurationNames)
            {
                var configuration = repositoryConfiguration.GetConfiguration(configurationName);
                if (configuration.AccessInfo.HasMongoDbApiAccess())
                {
                    var mongoDbService = mongoDbServiceFactory.GetMongoDbService(() => new DatabaseContext { ConfigurationName = configurationName });
                    if (!mongoDbService.GetDatabaseHostName().Contains("localhost", StringComparison.InvariantCultureIgnoreCase))
                    {
                        await mongoDbFirewallStateService.AssureFirewallAccessAsync(configuration.AccessInfo);
                    }
                }
            }
        });
    }

    public static IServiceCollection AddMongoDB(this IServiceCollection services, Action<DatabaseOptions> options = null)
    {
        var databaseOptions = new DatabaseOptions
        {
            ConfigurationName = "Default",
            AutoRegisterRepositories = Constants.AutoRegisterRepositoriesDefault,
            AutoRegisterCollections = Constants.AutoRegisterCollectionsDefault,
        };

        options?.Invoke(databaseOptions);

        _actionEvent = databaseOptions.ActionEvent;

        RepositoryCollectionBase.ActionEvent += (_, e) => { _actionEvent?.Invoke(e); };
        //TODO: Fix
        //AtlasAdministrationService.ActionEvent += (_, e) => { _actionEvent?.Invoke(e); };
        //MongoDbFirewallService.ActionEvent += (_, e) => { _actionEvent?.Invoke(e); };
        //ExternalIpAddressService.ActionEvent += (_, e) => { _actionEvent?.Invoke(e); };

        services.AddAssemblyService();

        services.AddHttpClient();

        services.AddTransient<IExternalIpAddressService, ExternalIpAddressService>();
        services.AddTransient<IMongoDbFirewallService, MongoDbFirewallService>();
        services.AddSingleton<IMongoDbFirewallStateService, MongoDbFirewallStateService>();
        services.AddTransient<IMongoDbServiceFactory>(serviceProvider =>
        {
            var repositoryConfigurationLoader = serviceProvider.GetService<IRepositoryConfigurationLoader>();
            var mongoDbFirewallStateService = serviceProvider.GetService<IMongoDbFirewallStateService> ();
            var logger = serviceProvider.GetService<ILogger<MongoDbServiceFactory>>();
            return new MongoDbServiceFactory(repositoryConfigurationLoader, mongoDbFirewallStateService, logger);
        });
        services.AddTransient<IRepositoryConfigurationLoader>(serviceProvider =>
        {
            var mongoUrlBuilderLoader = serviceProvider.GetService<IMongoUrlBuilderLoader>();
            var repositoryConfiguration = serviceProvider.GetService<IRepositoryConfiguration>();
            return new RepositoryConfigurationLoader(mongoUrlBuilderLoader, repositoryConfiguration, databaseOptions);
        });
        services.AddTransient<IMongoUrlBuilderLoader>(serviceProvider => new MongoUrlBuilderLoader(serviceProvider, databaseOptions));
        services.AddTransient<IRepositoryConfiguration>(serviceProvider => new RepositoryConfiguration(serviceProvider, databaseOptions));

        services.AddTransient<ICollectionProvider, CollectionProvider>(provider =>
        {
            var mongoDbServiceFactory = provider.GetService<IMongoDbServiceFactory>();
            return new CollectionProvider(mongoDbServiceFactory, type =>
            {
                var service = provider.GetService(type);
                return service;
            }, type =>
            {
                _registeredCollections.TryGetValue(type, out var implementationType);
                return implementationType;
            });
        });

        if (databaseOptions.AutoRegisterRepositories || databaseOptions.AutoRegisterCollections)
        {
            _actionEvent?.Invoke(new ActionEventArgs(new ActionEventArgs.ActionData
            {
                Message = $"Looking for assemblies in {string.Join(", ", (databaseOptions.AutoRegistrationAssemblies ?? AssemblyService.GetAssemblies()).Select(x => x.GetName().Name).ToArray())}.",
                Level = LogLevel.Debug
            }, new ActionEventArgs.ContextData()));
        }

        if (databaseOptions.AutoRegisterRepositories)
        {
            var currentDomainDefinedTypes = AssemblyService.GetTypes<IRepository>(x => !x.IsGenericType && !x.IsInterface, databaseOptions.AutoRegistrationAssemblies).ToArray();
            foreach (var repositoryType in currentDomainDefinedTypes)
            {
                var serviceTypes = repositoryType.ImplementedInterfaces.Where(x => x.IsInterface && !x.IsGenericType && x != typeof(IRepository)).ToArray();
                if (serviceTypes.Length > 1) throw new InvalidOperationException($"There are {serviceTypes.Length} interfaces for repository type '{repositoryType.Name}' ({string.Join(", ", serviceTypes.Select(x => x.Name))}).");
                var implementationType = repositoryType.AsType();
                var serviceType = serviceTypes.Length == 0 ? implementationType : serviceTypes.Single();

                if (!_registeredRepositories.TryAdd(serviceType, implementationType))
                {
                    _registeredRepositories.TryGetValue(serviceType, out var other);
                    throw new InvalidOperationException($"There are multiple implementations for interface '{serviceType.Name}' ({implementationType.Name} and {other?.Name}). {nameof(DatabaseOptions.AutoRegisterRepositories)} in {nameof(DatabaseOptions)} cannot be used.");
                }
                services.AddTransient(serviceType, implementationType);

                _actionEvent?.Invoke(new ActionEventArgs(new ActionEventArgs.ActionData
                {
                    Message = $"Auto registered repository {serviceType.Name} ({implementationType.Name}).",
                    Level = LogLevel.Debug
                }, new ActionEventArgs.ContextData()));
            }
        }

        if (databaseOptions.RegisterCollections?.Any() ?? false)
        {
            foreach (var registerCollection in databaseOptions.RegisterCollections)
            {
                RegisterCollection(services, registerCollection.Interface, registerCollection.Implementation, "Manual");
            }
        }

        if (databaseOptions.AutoRegisterCollections)
        {
            var currentDomainDefinedTypes = AssemblyService.GetTypes<IRepositoryCollection>(x => !x.IsGenericType && !x.IsInterface, databaseOptions.AutoRegistrationAssemblies).ToArray();
            foreach (var collectionType in currentDomainDefinedTypes)
            {
                var serviceTypes = collectionType.ImplementedInterfaces.Where(x => x.IsInterface && !x.IsGenericType && x != typeof(IRepositoryCollection)).ToArray();
                if (serviceTypes.Length > 1) throw new InvalidOperationException($"There are {serviceTypes.Length} interfaces for collection type '{collectionType.Name}' ({string.Join(", ", serviceTypes.Select(x => x.Name))}).");
                var implementationType = collectionType.AsType();
                var serviceType = serviceTypes.Length == 0 ? implementationType : serviceTypes.Single();

                RegisterCollection(services, serviceType, implementationType, "Auto");
            }
        }

        return services;
    }

    private static void RegisterCollection(IServiceCollection services, Type serviceType, Type implementationType, string regTypeName)
    {
        if (!_registeredCollections.TryAdd(serviceType, implementationType))
        {
            if (_registeredCollections.TryGetValue(serviceType, out var other))
            {
                if (other == implementationType)
                {
                    _actionEvent?.Invoke(new ActionEventArgs(new ActionEventArgs.ActionData
                    {
                        Message = $"Collection {serviceType.Name} has already been manually registered.",
                        Level = LogLevel.Trace
                    }, new ActionEventArgs.ContextData()));
                    return;
                }
            }

            throw new InvalidOperationException($"There are multiple implementations for interface '{serviceType.Name}' ({implementationType.Name} and {other?.Name}). {nameof(DatabaseOptions.AutoRegisterCollections)} in {nameof(DatabaseOptions)} cannot be used.");
        }

        string message = null;
        if (implementationType.GetConstructors().Any(x => x.GetParameters().All(y => y.ParameterType != typeof(DatabaseContext))))
        {
            services.AddTransient(serviceType, implementationType);
        }
        else
        {
            //TODO: Add note about what parameters (like DatabaseContext) makes it not beeing registered in IOC)
            message = " Not registered in IOC (IServiceCollection), can not be injected in constructor because it requires ICollectionProvider.";
        }

        _actionEvent?.Invoke(new ActionEventArgs(new ActionEventArgs.ActionData
        {
            Message = $"{regTypeName} registered collection {serviceType.Name} ({implementationType.Name}).{message}",
            Level = LogLevel.Debug
        }, new ActionEventArgs.ContextData()));
    }
}