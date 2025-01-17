﻿using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ray.Core.Channels;
using Ray.Core.Event;
using Ray.Core.Serialization;
using Ray.Core.Storage;
using Ray.Storage.SQLCore;
using Ray.Storage.SQLCore.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ray.Storage.MySQL
{
    public class EventStorage<PrimaryKey> : IEventStorage<PrimaryKey>
    {
        readonly StorageOptions config;
        readonly IMpscChannel<AsyncInputEvent<BatchAppendTransport<PrimaryKey>, bool>> mpscChannel;
        readonly ILogger<EventStorage<PrimaryKey>> logger;
        readonly ISerializer serializer;
        public EventStorage(IServiceProvider serviceProvider, StorageOptions config)
        {
            logger = serviceProvider.GetService<ILogger<EventStorage<PrimaryKey>>>();
            serializer = serviceProvider.GetService<ISerializer>();
            mpscChannel = serviceProvider.GetService<IMpscChannel<AsyncInputEvent<BatchAppendTransport<PrimaryKey>, bool>>>().BindConsumer(BatchInsertExecuter);
            this.config = config;
        }
        public async Task<IList<IFullyEvent<PrimaryKey>>> GetList(PrimaryKey stateId, long latestTimestamp, long startVersion, long endVersion)
        {
            var list = new List<IFullyEvent<PrimaryKey>>((int)(endVersion - startVersion));
            await Task.Run(async () =>
            {
                var getTableListTask = config.GetSubTables();
                if (!getTableListTask.IsCompletedSuccessfully)
                    await getTableListTask;
                var stateIdStr = typeof(PrimaryKey) == typeof(long) ? stateId.ToString() : $"'{stateId.ToString()}'";
                using var conn = config.CreateConnection();
                await conn.OpenAsync();
                foreach (var table in getTableListTask.Result.Where(t => t.EndTime >= latestTimestamp))
                {
                    var sql = $"SELECT TypeCode,Data,Version,Timestamp from {table.SubTable} WHERE StateId=@StateId and Version>=@StartVersion and Version<=@EndVersion order by Version asc";
                    var originList = await conn.QueryAsync<EventModel>(sql, new
                    {
                        StateId = stateId,
                        StartVersion = startVersion,
                        EndVersion = endVersion
                    });
                    foreach (var item in originList)
                    {
                        if (serializer.Deserialize(TypeContainer.GetType(item.TypeCode), Encoding.UTF8.GetBytes(item.Data)) is IEvent evt)
                        {
                            list.Add(new FullyEvent<PrimaryKey>
                            {
                                StateId = stateId,
                                Event = evt,
                                Base = new EventBase(item.Version, item.Timestamp)
                            });
                        }
                    }
                }
            });
            return list.OrderBy(e => e.Base.Version).ToList();
        }
        public async Task<IList<IFullyEvent<PrimaryKey>>> GetListByType(PrimaryKey stateId, string typeCode, long startVersion, int limit)
        {
            var type = TypeContainer.GetType(typeCode);
            var list = new List<IFullyEvent<PrimaryKey>>(limit);
            await Task.Run(async () =>
            {
                var getTableListTask = config.GetSubTables();
                if (!getTableListTask.IsCompletedSuccessfully)
                    await getTableListTask;
                using var conn = config.CreateConnection();
                await conn.OpenAsync();
                foreach (var table in getTableListTask.Result)
                {
                    var sql = $"SELECT Data,Version,Timestamp from {table.SubTable} WHERE StateId=@StateId and TypeCode=@TypeCode and Version>=@StartVersion order by Version asc limit @Limit";
                    var originList = await conn.QueryAsync<EventModel>(sql, new
                    {
                        StateId = stateId,
                        TypeCode = typeCode,
                        StartVersion = startVersion,
                        Limit = limit
                    });
                    foreach (var item in originList)
                    {
                        if (serializer.Deserialize(type, Encoding.UTF8.GetBytes(item.Data)) is IEvent evt)
                        {
                            list.Add(new FullyEvent<PrimaryKey>
                            {
                                StateId = stateId,
                                Event = evt,
                                Base = new EventBase(item.Version, item.Timestamp)
                            });
                        }
                    }
                    if (list.Count >= limit)
                        break;
                }
            });
            return list.OrderBy(e => e.Base.Version).ToList();
        }

        static readonly ConcurrentDictionary<string, string> saveSqlDict = new ConcurrentDictionary<string, string>();
        public Task<bool> Append(IFullyEvent<PrimaryKey> fullyEvent, in EventBytesTransport bytesTransport, string unique)
        {
            var input = new BatchAppendTransport<PrimaryKey>(fullyEvent, in bytesTransport, unique);
            return Task.Run(async () =>
            {
                var wrap = new AsyncInputEvent<BatchAppendTransport<PrimaryKey>, bool>(input);
                var writeTask = mpscChannel.WriteAsync(wrap);
                if (!writeTask.IsCompletedSuccessfully)
                    await writeTask;
                return await wrap.TaskSource.Task;
            });
        }
        private string GetSaveSql(string tableName)
        {
            return saveSqlDict.GetOrAdd(tableName,
                             key => $"INSERT ignore INTO {key}(StateId,UniqueId,TypeCode,Data,Version,Timestamp) VALUES(@StateId,@UniqueId,@TypeCode,@Data,@Version,@Timestamp)");
        }
        private async Task BatchInsertExecuter(List<AsyncInputEvent<BatchAppendTransport<PrimaryKey>, bool>> wrapperList)
        {
            var minTimestamp = wrapperList.Min(t => t.Value.Event.Base.Timestamp);
            var maxTimestamp = wrapperList.Max(t => t.Value.Event.Base.Timestamp);
            var minTask = config.GetTable(minTimestamp);
            if (!minTask.IsCompletedSuccessfully)
                await minTask;
            if (minTask.Result.EndTime > maxTimestamp)
            {
                await BatchInsert(GetSaveSql(minTask.Result.SubTable), wrapperList);
            }
            else
            {
                var groups = (await Task.WhenAll(wrapperList.Select(async t =>
                {
                    var task = config.GetTable(t.Value.Event.Base.Timestamp);
                    if (!task.IsCompletedSuccessfully)
                        await task;
                    return (task.Result.SubTable, t);
                }))).GroupBy(t => t.SubTable);
                foreach (var group in groups)
                {
                    await BatchInsert(GetSaveSql(group.Key), group.Select(t => t.t).ToList());
                }
            }
            async Task BatchInsert(string saveSql, List<AsyncInputEvent<BatchAppendTransport<PrimaryKey>, bool>> list)
            {
                bool isSuccess = false;
                using var conn = config.CreateConnection();
                await conn.OpenAsync();
                using var trans = conn.BeginTransaction();
                try
                {
                    foreach (var wrapper in list)
                    {
                        wrapper.Value.ReturnValue = await conn.ExecuteAsync(saveSql, new
                        {
                            StateId = wrapper.Value.Event.StateId.ToString(),
                            wrapper.Value.UniqueId,
                            TypeCode = TypeContainer.GetTypeCode(wrapper.Value.Event.Event.GetType()),
                            Data = Encoding.UTF8.GetString(wrapper.Value.BytesTransport.EventBytes),
                            wrapper.Value.Event.Base.Version,
                            wrapper.Value.Event.Base.Timestamp
                        }, trans) > 0;
                    }
                    trans.Commit();
                    isSuccess = true;
                    list.ForEach(wrap => wrap.TaskSource.TrySetResult(wrap.Value.ReturnValue));
                }
                catch
                {
                    trans.Rollback();
                }
                if (!isSuccess)
                {
                    foreach (var wrapper in list)
                    {
                        try
                        {
                            wrapper.TaskSource.TrySetResult(await conn.ExecuteAsync(saveSql, new
                            {
                                wrapper.Value.Event.StateId,
                                wrapper.Value.UniqueId,
                                TypeCode = TypeContainer.GetTypeCode(wrapper.Value.Event.Event.GetType()),
                                Data = Encoding.UTF8.GetString(wrapper.Value.BytesTransport.EventBytes),
                                wrapper.Value.Event.Base.Version,
                                wrapper.Value.Event.Base.Timestamp
                            }) > 0);
                        }
                        catch (Exception ex)
                        {
                            wrapper.TaskSource.TrySetException(ex);
                        }
                    }
                }
            }
        }
        public async Task TransactionBatchAppend(List<EventTransport<PrimaryKey>> list)
        {
            var minTimestamp = list.Min(t => t.FullyEvent.Base.Timestamp);
            var maxTimestamp = list.Max(t => t.FullyEvent.Base.Timestamp);
            var minTask = config.GetTable(minTimestamp);
            if (!minTask.IsCompletedSuccessfully)
                await minTask;
            var groups = (await Task.WhenAll(list.Select(async t =>
            {
                var task = config.GetTable(t.FullyEvent.Base.Timestamp);
                if (!task.IsCompletedSuccessfully)
                    await task;
                return (task.Result.SubTable, t);
            }))).GroupBy(t => t.SubTable);
            using var conn = config.CreateConnection();
            await conn.OpenAsync();
            using var trans = conn.BeginTransaction();
            try
            {
                foreach (var group in groups)
                {
                    await conn.ExecuteAsync(GetSaveSql(group.Key), group.Select(g => new
                    {
                        g.t.FullyEvent.StateId,
                        g.t.UniqueId,
                        TypeCode = TypeContainer.GetTypeCode(g.t.FullyEvent.Event.GetType()),
                        Data = Encoding.UTF8.GetString(g.t.BytesTransport.EventBytes),
                        g.t.FullyEvent.Base.Version,
                        g.t.FullyEvent.Base.Timestamp
                    }), trans);
                }
                trans.Commit();
            }
            catch (Exception ex)
            {
                trans.Rollback();
                logger.LogError(ex, nameof(TransactionBatchAppend));
                throw;
            }
        }

        public Task DeletePrevious(PrimaryKey stateId, long toVersion, long startTimestamp)
        {
            return Task.Run(async () =>
            {
                var getTableListTask = config.GetSubTables();
                if (!getTableListTask.IsCompletedSuccessfully)
                    await getTableListTask;
                var tableList = getTableListTask.Result.Where(t => t.EndTime >= startTimestamp);
                using var conn = config.CreateConnection();
                await conn.OpenAsync();
                using var trans = conn.BeginTransaction();
                try
                {
                    foreach (var table in tableList)
                    {
                        var sql = $"delete from {table.SubTable} WHERE StateId=@StateId and Version<=@EndVersion";
                        await conn.ExecuteAsync(sql, new { StateId = stateId, EndVersion = toVersion }, transaction: trans);
                    }
                    trans.Commit();
                }
                catch
                {
                    trans.Rollback();
                    throw;
                }
            });
        }

        public Task DeleteAfter(PrimaryKey stateId, long fromVersion, long startTimestamp)
        {
            return Task.Run(async () =>
            {
                var getTableListTask = config.GetSubTables();
                if (!getTableListTask.IsCompletedSuccessfully)
                    await getTableListTask;
                var tableList = getTableListTask.Result.Where(t => t.EndTime >= startTimestamp);
                using var conn = config.CreateConnection();
                await conn.OpenAsync();
                using var trans = conn.BeginTransaction();
                try
                {
                    foreach (var table in tableList)
                    {
                        var sql = $"delete from {table.SubTable} WHERE StateId=@StateId and Version>=@StartVersion";
                        await conn.ExecuteAsync(sql, new { StateId = stateId, StartVersion = fromVersion });
                    }
                    trans.Commit();
                }
                catch
                {
                    trans.Rollback();
                    throw;
                }
            });
        }
        public Task DeleteByVersion(PrimaryKey stateId, long version, long timestamp)
        {
            return Task.Run(async () =>
            {
                var getTableListTask = config.GetSubTables();
                if (!getTableListTask.IsCompletedSuccessfully)
                    await getTableListTask;
                var table = getTableListTask.Result.SingleOrDefault(t => t.StartTime <= timestamp && t.EndTime >= timestamp);
                using var conn = config.CreateConnection();
                var sql = $"delete from {table.SubTable} WHERE StateId=@StateId and Version=@Version";
                await conn.ExecuteAsync(sql, new { StateId = stateId, Version = version });
            });
        }
    }
}
