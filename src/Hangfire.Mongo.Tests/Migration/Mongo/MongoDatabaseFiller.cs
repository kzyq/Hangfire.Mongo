﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Hangfire.Mongo.Migration;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Hangfire.Mongo.Tests.Utils;
using Hangfire.Server;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using Xunit;

namespace Hangfire.Mongo.Tests.Migration.Mongo
{
#pragma warning disable 1591
    [Collection("Database")]
    public class MongoDatabaseFiller
    {
        private readonly MongoIntegrationTestFixture _fixture;

        public MongoDatabaseFiller(MongoIntegrationTestFixture fixture)
        {
            _fixture = fixture;
        }

        //[Fact, Trait("Category", "DataGeneration")]
        public void Clean_Database_Filled()
        {
            var databaseName = "Mongo-Hangfire-Filled";
            var context = _fixture.CreateDbContext(databaseName);
            // Make sure we start from scratch
            context.Database.Client.DropDatabase(databaseName);

            var storageOptions = new MongoStorageOptions
            {
                MigrationOptions = new MongoMigrationOptions
                {
                    MigrationStrategy = new DropMongoMigrationStrategy(),
                    BackupStrategy = new NoneMongoBackupStrategy()
                },
                CheckQueuedJobsStrategy = CheckQueuedJobsStrategy.TailNotificationsCollection,
                QueuePollInterval = TimeSpan.FromMilliseconds(500)
            };
            var serverOptions = new BackgroundJobServerOptions
            {
                ShutdownTimeout = TimeSpan.FromSeconds(15)
            };
            var dbContext = _fixture.CreateDbContext();
            var mongoStorage = new MongoStorage(dbContext.Client, databaseName, storageOptions);
            JobStorage.Current = mongoStorage;

            using (new BackgroundJobServer(serverOptions))
            {
                // Recurring Job
                RecurringJob.AddOrUpdate(() => HangfireTestJobs.ExecuteRecurringJob("Recurring job"), Cron.Minutely);

                // Scheduled job
                BackgroundJob.Schedule(() => HangfireTestJobs.ExecuteScheduledJob("Scheduled job"), TimeSpan.FromSeconds(30));

                // Enqueued job
                BackgroundJob.Enqueue(() => HangfireTestJobs.ExecuteEnqueuedJob("Enqueued job"));

                // Continued job
                var parentId = BackgroundJob.Schedule(() => HangfireTestJobs.ExecuteContinueWithJob("ContinueWith job", false), TimeSpan.FromSeconds(15));
                BackgroundJob.ContinueWith(parentId, () => HangfireTestJobs.ExecuteContinueWithJob("ContinueWith job continued", true));

                // Now the waiting game starts
                HangfireTestJobs.ScheduleEvent.WaitOne();
                BackgroundJob.Schedule(() => HangfireTestJobs.ExecuteScheduledJob("Scheduled job (*)"), TimeSpan.FromMinutes(30));

                HangfireTestJobs.ContinueWithEvent.WaitOne();
                HangfireTestJobs.RecurringEvent.WaitOne();

                HangfireTestJobs.EnqueueEvent.WaitOne();
                BackgroundJob.Enqueue(() => HangfireTestJobs.ExecuteEnqueuedJob("Enqueued job (*)"));
            }


            // Some data are cleaned up when hangfire shuts down.
            // Grab a copy so we can write it back - needed for migration tests.
            var connection = JobStorage.Current.GetConnection();
            connection.AnnounceServer("test-server", new ServerContext
            {
                WorkerCount = serverOptions.WorkerCount,
                Queues = serverOptions.Queues
            });

            connection.AcquireDistributedLock("test-lock", TimeSpan.FromSeconds(30));
            var migrationManager = new MongoMigrationManager(storageOptions, dbContext.Database);
            // Create database snapshot in zip file
            var schemaVersion = (int)migrationManager.RequiredSchemaVersion;
            using (var stream = new FileStream($@"Hangfire-Mongo-Schema-{schemaVersion:000}.zip", FileMode.Create))
            {
                var allowedEmptyCollections = new List<string>
                {
                    "hangfire.migrationLock",
                    "hangfire.notifications"
                };

                if (migrationManager.RequiredSchemaVersion is >= MongoSchema.Version09 and <= MongoSchema.Version15)
                {
                    // Signal collection work was initiated in schema version 9,
                    // and still not put to use in schema version 15.
                    allowedEmptyCollections.Add($@"{storageOptions.Prefix}.signal");
                }
                BackupDatabaseToStream(databaseName, stream, allowedEmptyCollections.ToArray());
            }
        }


        private void BackupDatabaseToStream(string databaseName, Stream stream, params string[] allowedEmptyCollections)
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                var context = _fixture.CreateDbContext(databaseName);
                foreach (var collectionName in context.Database.ListCollections().ToList()
                    .Select(c => c["name"].AsString))
                {
                    var fileName = $@"{collectionName}.json";
                    var collectionFile = archive.CreateEntry(fileName);

                    var collection = context.Database.GetCollection<BsonDocument>(collectionName);
                    var jsonDocs = collection.Find(Builders<BsonDocument>.Filter.Empty)
                        .ToList()
                        .Select(d => d.ToJson(JsonWriterSettings.Defaults))
                        .ToList();

                    Assert.True(jsonDocs.Any() || allowedEmptyCollections.Contains(collectionName),
                        $@"Expected collection '{collectionName}' to contain documents");

                    using (var entryStream = collectionFile.Open())
                    {
                        using (var streamWriter = new StreamWriter(entryStream))
                        {
                            streamWriter.Write("[" + string.Join(",", jsonDocs) + "]");
                        }
                    }
                }
            }
        }
    }

}