﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.Tests.Utils;
using Hangfire.States;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using Xunit;

namespace Hangfire.Mongo.Tests
{
#pragma warning disable 1591
    [Collection("Database")]
    public class MongoWriteOnlyTransactionFacts
    {
        private readonly HangfireDbContext _database = ConnectionUtils.CreateDbContext();

        [Fact]
        public void Ctor_ThrowsAnException_IfConnectionIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => new MongoWriteOnlyTransaction(null, new MongoStorageOptions()));

            Assert.Equal("dbContext", exception.ParamName);
        }

        [Fact]
        [CleanDatabase]
        public void ExpireJob_SetsJobExpirationData()
        {
            var job = new JobDto
            {
                Id = ObjectId.GenerateNewId(1),
                InvocationData = "",
                Arguments = "",
                CreatedAt = DateTime.UtcNow
            };
            _database.JobGraph.InsertOne(job);

            var anotherJob = new JobDto
            {
                Id = ObjectId.GenerateNewId(2),
                InvocationData = "",
                Arguments = "",
                CreatedAt = DateTime.UtcNow
            };
            _database.JobGraph.InsertOne(anotherJob);

            var jobId = job.Id.ToString();
            var anotherJobId = anotherJob.Id.ToString();

            Commit(x => x.ExpireJob(jobId, TimeSpan.FromDays(1)));

            var testJob = GetTestJob(_database, jobId);
            Assert.True(DateTime.UtcNow.AddMinutes(-1) < testJob.ExpireAt &&
                        testJob.ExpireAt <= DateTime.UtcNow.AddDays(1));

            var anotherTestJob = GetTestJob(_database, anotherJobId);
            Assert.Null(anotherTestJob.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void PersistJob_ClearsTheJobExpirationData()
        {
            var job = new JobDto
            {
                Id = ObjectId.GenerateNewId(1),
                InvocationData = "",
                Arguments = "",
                CreatedAt = DateTime.UtcNow,
                ExpireAt = DateTime.UtcNow
            };
            _database.JobGraph.InsertOne(job);

            var anotherJob = new JobDto
            {
                Id = ObjectId.GenerateNewId(2),
                InvocationData = "",
                Arguments = "",
                CreatedAt = DateTime.UtcNow,
                ExpireAt = DateTime.UtcNow
            };
            _database.JobGraph.InsertOne(anotherJob);

            var jobId = job.Id.ToString();
            var anotherJobId = anotherJob.Id.ToString();

            Commit(x => x.PersistJob(jobId));

            var testjob = GetTestJob(_database, jobId);
            Assert.Null(testjob.ExpireAt);

            var anotherTestJob = GetTestJob(_database, anotherJobId);
            Assert.NotNull(anotherTestJob.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void SetJobState_AppendsAStateAndSetItToTheJob()
        {
            var job = new JobDto
            {
                Id = ObjectId.GenerateNewId(1),
                InvocationData = "",
                Arguments = "",
                CreatedAt = DateTime.UtcNow
            };
            _database.JobGraph.InsertOne(job);

            var anotherJob = new JobDto
            {
                Id = ObjectId.GenerateNewId(2),
                InvocationData = "",
                Arguments = "",
                CreatedAt = DateTime.UtcNow
            };
            _database.JobGraph.InsertOne(anotherJob);

            var jobId = job.Id.ToString();
            var anotherJobId = anotherJob.Id.ToString();
            var serializedData = new Dictionary<string, string> {{"Name", "Value"}};

            var state = new Mock<IState>();
            state.Setup(x => x.Name).Returns("State");
            state.Setup(x => x.Reason).Returns("Reason");
            state.Setup(x => x.SerializeData()).Returns(serializedData);

            Commit(x => x.SetJobState(jobId, state.Object));

            var testJob = GetTestJob(_database, jobId);
            Assert.Equal("State", testJob.StateName);
            Assert.Single(testJob.StateHistory);

            var anotherTestJob = GetTestJob(_database, anotherJobId);
            Assert.Null(anotherTestJob.StateName);
            Assert.Empty(anotherTestJob.StateHistory);

            var jobWithStates = _database.JobGraph.OfType<JobDto>().Find(new BsonDocument()).FirstOrDefault();

            var jobState = jobWithStates.StateHistory.Single();
            Assert.Equal("State", jobState.Name);
            Assert.Equal("Reason", jobState.Reason);
            Assert.Equal(serializedData, jobState.Data);
        }

        [Fact]
        [CleanDatabase]
        public void AddJobState_JustAddsANewRecordInATable()
        {
            var job = new JobDto
            {
                Id = ObjectId.GenerateNewId(1),
                InvocationData = "",
                Arguments = "",
                CreatedAt = DateTime.UtcNow
            };
            _database.JobGraph.InsertOne(job);

            var jobId = job.Id.ToString();
            var serializedData = new Dictionary<string, string> {{"Name", "Value"}};

            var state = new Mock<IState>();
            state.Setup(x => x.Name).Returns("State");
            state.Setup(x => x.Reason).Returns("Reason");
            state.Setup(x => x.SerializeData()).Returns(serializedData);

            Commit(x => x.AddJobState(jobId, state.Object));

            var testJob = GetTestJob(_database, jobId);
            Assert.Null(testJob.StateName);

            var jobWithStates = _database.JobGraph.OfType<JobDto>().Find(new BsonDocument()).ToList().Single();
            var jobState = jobWithStates.StateHistory.Last();
            Assert.Equal("State", jobState.Name);
            Assert.Equal("Reason", jobState.Reason);
            Assert.Equal(serializedData, jobState.Data);
        }

        [Fact]
        [CleanDatabase]
        public void AddToQueue_CallsEnqueue_OnTargetPersistentQueue()
        {
            var jobId = ObjectId.GenerateNewId().ToString();
            Commit(x => x.AddToQueue("default", jobId));

            var jobQueueDto = _database
                .JobGraph
                .OfType<JobQueueDto>()
                .Find(j => j.Queue == "default" && j.JobId == ObjectId.Parse(jobId))
                .FirstOrDefault();

            Assert.NotNull(jobQueueDto);
        }

        [Fact]
        [CleanDatabase]
        public void IncrementCounter_AddsRecordToCounterTable_WithPositiveValue()
        {
            Commit(x => x.IncrementCounter("my-key"));

            var record = _database.JobGraph.OfType<CounterDto>().Find(new BsonDocument()).ToList().Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal(1L, record.Value);
            Assert.Null(record.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void IncrementCounter_WithExpiry_AddsARecord_WithExpirationTimeSet()
        {
            Commit(x => x.IncrementCounter("my-key", TimeSpan.FromDays(1)));

            var counter = _database.JobGraph.OfType<CounterDto>().Find(new BsonDocument()).Single();

            Assert.Equal("my-key", counter.Key);
            Assert.Equal(1L, counter.Value);
            Assert.NotNull(counter.ExpireAt);

            var expireAt = (DateTime) counter.ExpireAt;

            Assert.True(DateTime.UtcNow.AddHours(23) < expireAt);
            Assert.True(expireAt < DateTime.UtcNow.AddHours(25));
        }

        [Fact]
        [CleanDatabase]
        public void IncrementCounter_WithExistingKey_AddsAnotherRecord()
        {
            Commit(x =>
            {
                x.IncrementCounter("my-key");
                x.IncrementCounter("my-key");
            });

            var counter = _database.JobGraph.OfType<CounterDto>()
                .Find(new BsonDocument(nameof(CounterDto.Key), "my-key")).FirstOrDefault();

            Assert.NotNull(counter);
            Assert.Equal(2, counter.Value);
        }

        [Fact]
        [CleanDatabase]
        public void DecrementCounter_AddsRecordToCounterTable_WithNegativeValue()
        {
            Commit(x => x.DecrementCounter("my-key"));

            var record = _database.JobGraph.OfType<CounterDto>().Find(new BsonDocument()).Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal(-1L, record.Value);
            Assert.Null(record.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void DecrementCounter_WithExpiry_AddsARecord_WithExpirationTimeSet()
        {
            Commit(x => x.DecrementCounter("my-key", TimeSpan.FromDays(1)));

            var counter = _database.JobGraph.OfType<CounterDto>().Find(new BsonDocument()).Single();

            Assert.Equal("my-key", counter.Key);
            Assert.Equal(-1L, counter.Value);
            Assert.NotNull(counter.ExpireAt);

            var expireAt = (DateTime) counter.ExpireAt;

            Assert.True(DateTime.UtcNow.AddHours(23) < expireAt);
            Assert.True(expireAt < DateTime.UtcNow.AddHours(25));
        }

        [Fact]
        [CleanDatabase]
        public void DecrementCounter_WithExistingKey_AddsAnotherRecord()
        {
            Commit(x =>
            {
                x.DecrementCounter("my-key");
                x.DecrementCounter("my-key");
            });

            var counter = _database.JobGraph.OfType<CounterDto>().Find(new BsonDocument()).Single();

            Assert.Equal(-2, counter.Value);
        }

        [Fact]
        [CleanDatabase]
        public void AddToSet_AddsARecord_IfThereIsNo_SuchKeyAndValue()
        {
            Commit(x => x.AddToSet("my-key", "my-value"));

            var record = _database.JobGraph.OfType<SetDto>().Find(new BsonDocument()).ToList().Single();

            Assert.Equal("my-key<my-value>", record.Key);
            Assert.Equal(0.0, record.Score, 2);
        }

        [Fact]
        [CleanDatabase]
        public void AddToSet_AddsARecord_WhenKeyIsExists_ButValuesAreDifferent()
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.AddToSet("my-key", "another-value");
            });

            var recordCount = _database.JobGraph.OfType<SetDto>().Count(new BsonDocument());

            Assert.Equal(2, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void AddToSet_DoesNotAddARecord_WhenBothKeyAndValueAreExist()
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.AddToSet("my-key", "my-value");
            });

            var recordCount = _database.JobGraph.OfType<SetDto>().Count(new BsonDocument());

            Assert.Equal(1, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void AddToSet_WithScore_AddsARecordWithScore_WhenBothKeyAndValueAreNotExist()
        {
            Commit(x => x.AddToSet("my-key", "my-value", 3.2));

            var record = _database.JobGraph.OfType<SetDto>().Find(new BsonDocument()).ToList().Single();

            Assert.Equal("my-key<my-value>", record.Key);
            Assert.Equal(3.2, record.Score, 3);
        }

        [Fact]
        [CleanDatabase]
        public void AddToSet_WithScore_UpdatesAScore_WhenBothKeyAndValueAreExist()
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.AddToSet("my-key", "my-value", 3.2);
            });

            var record = _database.JobGraph.OfType<SetDto>().Find(new BsonDocument()).ToList().Single();

            Assert.Equal(3.2, record.Score, 3);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveFromSet_RemovesARecord_WithGivenKeyAndValue()
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.RemoveFromSet("my-key", "my-value");
            });

            var recordCount = _database.JobGraph.OfType<SetDto>().Count(new BsonDocument());

            Assert.Equal(0, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveFromSet_DoesNotRemoveRecord_WithSameKey_AndDifferentValue()
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.RemoveFromSet("my-key", "different-value");
            });

            var recordCount = _database.JobGraph.OfType<SetDto>().Count(new BsonDocument());

            Assert.Equal(1, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveFromSet_DoesNotRemoveRecord_WithSameValue_AndDifferentKey()
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.RemoveFromSet("different-key", "my-value");
            });

            var recordCount = _database.JobGraph.OfType<SetDto>().Count(new BsonDocument());

            Assert.Equal(1, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void InsertToList_AddsARecord_WithGivenValues()
        {
            Commit(x => x.InsertToList("my-key", "my-value"));

            var record = _database.JobGraph.OfType<ListDto>().Find(new BsonDocument()).ToList().Single();
            Assert.Equal("my-key", record.Item);
            Assert.Equal("my-value", record.Value);
        }

        [Fact]
        [CleanDatabase]
        public void InsertToList_AddsAnotherRecord_WhenBothKeyAndValueAreExist()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "my-value");
                x.InsertToList("my-key", "my-value");
            });

            var recordCount = _database.JobGraph.OfType<ListDto>().Count(new BsonDocument());

            Assert.Equal(2, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveFromList_RemovesAllRecords_WithGivenKeyAndValue()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "my-value");
                x.InsertToList("my-key", "my-value");
                x.RemoveFromList("my-key", "my-value");
            });

            var recordCount = _database.JobGraph.OfType<ListDto>().Count(new BsonDocument());

            Assert.Equal(0, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveFromList_DoesNotRemoveRecords_WithSameKey_ButDifferentValue()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "my-value");
                x.RemoveFromList("my-key", "different-value");
            });

            var recordCount = _database.JobGraph.OfType<ListDto>().Count(new BsonDocument());

            Assert.Equal(1, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveFromList_DoesNotRemoveRecords_WithSameValue_ButDifferentKey()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "my-value");
                x.RemoveFromList("different-key", "my-value");
            });

            var recordCount = _database.JobGraph.OfType<ListDto>().Count(new BsonDocument());

            Assert.Equal(1, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void TrimList_TrimsAList_ToASpecifiedRange()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.InsertToList("my-key", "1");
                x.InsertToList("my-key", "2");
                x.InsertToList("my-key", "3");
                x.TrimList("my-key", 1, 2);
            });

            var records = _database.JobGraph.OfType<ListDto>().Find(new BsonDocument()).ToList().ToArray();

            Assert.Equal(2, records.Length);
            Assert.Equal("1", records[0].Value);
            Assert.Equal("2", records[1].Value);
        }

        [Fact]
        [CleanDatabase]
        public void TrimList_RemovesRecordsToEnd_IfKeepAndingAt_GreaterThanMaxElementIndex()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.InsertToList("my-key", "1");
                x.InsertToList("my-key", "2");
                x.TrimList("my-key", 1, 100);
            });

            var recordCount = _database.JobGraph.OfType<ListDto>().Count(new BsonDocument());

            Assert.Equal(2, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void TrimList_RemovesAllRecords_WhenStartingFromValue_GreaterThanMaxElementIndex()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.TrimList("my-key", 1, 100);
            });

            var recordCount = _database.JobGraph.OfType<ListDto>().Count(new BsonDocument());

            Assert.Equal(0, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void TrimList_RemovesAllRecords_IfStartFromGreaterThanEndingAt()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.TrimList("my-key", 1, 0);
            });

            var recordCount = _database.JobGraph.OfType<ListDto>().Count(new BsonDocument());

            Assert.Equal(0, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void TrimList_RemovesRecords_OnlyOfAGivenKey()
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.TrimList("another-key", 1, 0);
            });

            var recordCount = _database.JobGraph.OfType<ListDto>().Count(new BsonDocument());

            Assert.Equal(1, recordCount);
        }

        [Fact]
        [CleanDatabase]
        public void SetRangeInHash_ThrowsAnException_WhenKeyIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.SetRangeInHash(null, new Dictionary<string, string>())));

            Assert.Equal("key", exception.ParamName);
        }

        [Fact]
        [CleanDatabase]
        public void SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.SetRangeInHash("some-hash", null)));

            Assert.Equal("keyValuePairs", exception.ParamName);
        }

        [Fact]
        [CleanDatabase]
        public void SetRangeInHash_MergesAllRecords()
        {
            Commit(x => x.SetRangeInHash("some-hash", new Dictionary<string, string>
            {
                {"Key1", "Value1"},
                {"Key2", "Value2"}
            }));

            var result = _database.JobGraph.OfType<HashDto>()
                .Find(new BsonDocument(nameof(KeyJobDto.Key), "some-hash")).FirstOrDefault();

            Assert.Equal("Value1", result.Fields["Key1"]);
            Assert.Equal("Value2", result.Fields["Key2"]);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveHash_ThrowsAnException_WhenKeyIsNull()
        {
            Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.RemoveHash(null)));
        }

        [Fact]
        [CleanDatabase]
        public void RemoveHash_RemovesAllHashRecords()
        {
            // Arrange
            Commit(x => x.SetRangeInHash("some-hash", new Dictionary<string, string>
            {
                {"Key1", "Value1"},
                {"Key2", "Value2"}
            }));

            // Act
            Commit(x => x.RemoveHash("some-hash"));

            // Assert
            var count = _database.JobGraph.OfType<HashDto>().Count(new BsonDocument());
            Assert.Equal(0, count);
        }

        [Fact]
        [CleanDatabase]
        public void ExpireSet_SetsSetExpirationData()
        {
            var set1 = new SetDto {Key = "Set1<value1>", Value = "value1", SetType = "Set1"};
            _database.JobGraph.InsertOne(set1);

            var set2 = new SetDto {Key = "Set2<value2>", Value = "value2", SetType = "Set2"};
            _database.JobGraph.InsertOne(set2);

            Commit(x => x.ExpireSet("Set1", TimeSpan.FromDays(1)));

            var testSet1 = GetTestSet(_database, "Set1").FirstOrDefault();
            var testSet2 = GetTestSet(_database, "Set2").FirstOrDefault();

            Assert.NotNull(testSet1);
            Assert.True(DateTime.UtcNow.AddMinutes(-1) < testSet1.ExpireAt &&
                        testSet1.ExpireAt <= DateTime.UtcNow.AddDays(1));

            Assert.NotNull(testSet2);
            Assert.Null(testSet2.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void ExpireSet_SetsSetExpirationData_WhenKeyContainsRegexSpecialChars()
        {
            var key = "some+-[regex]?-#set";


            var set1 = new SetDto {Key = $"{key}<value1>", Value = "value1", SetType = key};
            _database.JobGraph.InsertOne(set1);

            Commit(x => x.ExpireSet(key, TimeSpan.FromDays(1)));

            var testSet1 = GetTestSet(_database, key).FirstOrDefault();

            Assert.NotNull(testSet1);
            Assert.True(DateTime.UtcNow.AddMinutes(-1) < testSet1.ExpireAt &&
                        testSet1.ExpireAt <= DateTime.UtcNow.AddDays(1));
        }

        [Fact]
        [CleanDatabase]
        public void ExpireList_SetsListExpirationData()
        {
            var list1 = new ListDto {Item = "List1", Value = "value1"};
            _database.JobGraph.InsertOne(list1);

            var list2 = new ListDto {Item = "List2", Value = "value2"};
            _database.JobGraph.InsertOne(list2);

            Commit(x => x.ExpireList(list1.Item, TimeSpan.FromDays(1)));

            var testList1 = GetTestList(_database, list1.Item);
            Assert.True(DateTime.UtcNow.AddMinutes(-1) < testList1.ExpireAt &&
                        testList1.ExpireAt <= DateTime.UtcNow.AddDays(1));

            var testList2 = GetTestList(_database, list2.Item);
            Assert.Null(testList2.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void ExpireHash_SetsHashExpirationData()
        {
            var hash1 = new HashDto {Key = "Hash1"};
            _database.JobGraph.InsertOne(hash1);

            var hash2 = new HashDto {Key = "Hash2"};
            _database.JobGraph.InsertOne(hash2);

            Commit(x => x.ExpireHash(hash1.Key, TimeSpan.FromDays(1)));

            var testHash1 = GetTestHash(_database, hash1.Key);
            Assert.True(DateTime.UtcNow.AddMinutes(-1) < testHash1.ExpireAt &&
                        testHash1.ExpireAt <= DateTime.UtcNow.AddDays(1));

            var testHash2 = GetTestHash(_database, hash2.Key);
            Assert.Null(testHash2.ExpireAt);
        }


        [Fact]
        [CleanDatabase]
        public void PersistSet_ClearsTheSetExpirationData()
        {
            var set1Val1 = new SetDto {Key = "Set1<value1>", Value = "value1", SetType = "Set1", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1Val1);

            var set1Val2 = new SetDto {Key = "Set1<value2>", Value = "value2", SetType = "Set1", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1Val2);

            var set2 = new SetDto {Key = "Set2<value1>", SetType = "Set2", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set2);

            Commit(x => x.PersistSet("Set1"));

            var testSet1 = GetTestSet(_database, "Set1");
            Assert.All(testSet1, x => Assert.Null(x.ExpireAt));

            var testSet2 = GetTestSet(_database, "Set2").Single();
            Assert.NotNull(testSet2.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void PersistSet_ClearsTheSetExpirationData_WhenKeyContainsRegexSpecialChars()
        {
            var key = "some+-[regex]?-#set";


            var set1 = new SetDto {Key = $"{key}<value1>", SetType = key, ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1);

            Commit(x => x.PersistSet(key));

            var testSet1 = GetTestSet(_database, key).First();
            Assert.Null(testSet1.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void PersistList_ClearsTheListExpirationData()
        {
            var list1 = new ListDto {Item = "List1", Value = "value1", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(list1);

            var list2 = new ListDto {Item = "List2", Value = "value2", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(list2);

            Commit(x => x.PersistList(list1.Item));

            var testList1 = GetTestList(_database, list1.Item);
            Assert.Null(testList1.ExpireAt);

            var testList2 = GetTestList(_database, list2.Item);
            Assert.NotNull(testList2.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void PersistHash_ClearsTheHashExpirationData()
        {
            var hash1 = new HashDto {Key = "Hash1", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(hash1);

            var hash2 = new HashDto {Key = "Hash2", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(hash2);

            Commit(x => x.PersistHash(hash1.Key));

            var testHash1 = GetTestHash(_database, hash1.Key);
            Assert.Null(testHash1.ExpireAt);

            var testHash2 = GetTestHash(_database, hash2.Key);
            Assert.NotNull(testHash2.ExpireAt);
        }

        [Fact]
        [CleanDatabase]
        public void AddRangeToSet_AddToExistingSetData()
        {
            // ASSERT
            var set1Val1 = new SetDto {Key = "Set1<value1>", Value = "value1", SetType = "Set1", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1Val1);

            var set1Val2 = new SetDto {Key = "Set1<value2>", Value = "value2", SetType = "Set1",  ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1Val2);

            var set2 = new SetDto {Key = "Set2<value2>", Value = "value2",SetType = "Set2",  ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set2);

            var values = new[] {"test1", "test2", "test3"};

            // ACT
            Commit(x => x.AddRangeToSet("Set1", values));

            var testSet1 = GetTestSet(_database, "Set1");
            var valuesToTest = new List<string>(values) {"value1", "value2"};

            Assert.NotNull(testSet1);
            // verify all values are present in testSet1
            Assert.True(testSet1.Select(s => s.Value).All(value => valuesToTest.Contains(value)));
            Assert.Equal(5, testSet1.Count);

            var testSet2 = GetTestSet(_database, "Set2");
            Assert.NotNull(testSet2);
            Assert.Equal(1, testSet2.Count);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveSet_ClearsTheSetData()
        {
            var set1Val1 = new SetDto {Key = "Set1<value1>", Value = "value1", SetType = "Set1", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1Val1);

            var set1Val2 = new SetDto {Key = "Set1<value2>", Value = "value2", SetType = "Set1", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1Val2);

            var set2 = new SetDto {Key = "Set2<value2>", Value = "value2", SetType = "Set2", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set2);

            Commit(x => x.RemoveSet("Set1"));

            var testSet1 = GetTestSet(_database, "Set1");
            Assert.Equal(0, testSet1.Count);

            var testSet2 = GetTestSet(_database, "Set2");
            Assert.Equal(1, testSet2.Count);
        }

        [Fact]
        [CleanDatabase]
        public void RemoveSet_ClearsTheSetData_WhenKeyContainsRegexSpecialChars()
        {
            var key = "some+-[regex]?-#set";


            var set1Val1 = new SetDto {Key = $"{key}<value1>", Value = "value1", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1Val1);

            var set1Val2 = new SetDto {Key = $"{key}<value2>", Value = "value2", ExpireAt = DateTime.UtcNow};
            _database.JobGraph.InsertOne(set1Val2);

            Commit(x => x.RemoveSet(key));

            var testSet1 = GetTestSet(_database, key);
            Assert.Equal(0, testSet1.Count);
        }

        private static JobDto GetTestJob(HangfireDbContext database, string jobId)
        {
            return database.JobGraph.OfType<JobDto>().Find(Builders<JobDto>.Filter.Eq(_ => _.Id, ObjectId.Parse(jobId)))
                .FirstOrDefault();
        }

        private static IList<SetDto> GetTestSet(HangfireDbContext database, string key)
        {
            return database.JobGraph.OfType<SetDto>()
                .Find(Builders<SetDto>.Filter.Eq(_ => _.SetType, key)).ToList();
        }

        private static ListDto GetTestList(HangfireDbContext database, string key)
        {
            return database.JobGraph.OfType<ListDto>().Find(Builders<ListDto>.Filter.Eq(_ => _.Item, key))
                .FirstOrDefault();
        }

        private static HashDto GetTestHash(HangfireDbContext database, string key)
        {
            return database.JobGraph.OfType<HashDto>().Find(Builders<HashDto>.Filter.Eq(_ => _.Key, key))
                .FirstOrDefault();
        }

        private void Commit(Action<MongoWriteOnlyTransaction> action)
        {
            using (var transaction = new MongoWriteOnlyTransaction(_database, new MongoStorageOptions()))
            {
                action(transaction);
                transaction.Commit();
            }
        }
    }
#pragma warning restore 1591
}