﻿using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Tharga.MongoDB;
using Tharga.MongoDB.Disk;
using Tharga.MongoDB.SimpleConsoleDemo;

var services = new ServiceCollection();
services.AddMongoDB(o =>
{
    o.ConnectionStringLoader = _ => "mongodb://localhost:27017/SimpleDemo";
    o.ActionEvent = e => { Console.WriteLine((string?)e.Action.Message); };
});

var serviceProvider = services.BuildServiceProvider();

var simpleRepo = serviceProvider.GetService<MySimpleRepo>();
await simpleRepo!.AddAsync(new MyEntity());
var oneItem = await simpleRepo.GetFirstOrDefaultAsync();

Console.WriteLine($"Got item with id '{oneItem.Id}' from the database.");

namespace Tharga.MongoDB.SimpleConsoleDemo
{
    public class MySimpleRepo : DiskRepositoryCollectionBase<MyEntity, ObjectId>
    {
        public Task<MyEntity> GetFirstOrDefaultAsync()
        {
            return base.GetOneAsync(x => true);
        }

        public MySimpleRepo(IMongoDbServiceFactory mongoDbServiceFactory)
            : base(mongoDbServiceFactory)
        {
        }
    }

    public record MyEntity : EntityBase<ObjectId>
    {
    }
}