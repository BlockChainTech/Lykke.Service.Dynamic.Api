﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AzureStorage;
using AzureStorage.Tables;
using Common.Log;
using Lykke.SettingsReader;
using Lykke.Service.Dash.Api.Core.Repositories;
using Lykke.Service.Dash.Api.Core.Domain.BroadcastInProgress;

namespace Lykke.Service.Dash.Api.AzureRepositories.BroadcastInProgress
{
    public class BroadcastInProgressRepository : IBroadcastInProgressRepository
    {
        private readonly INoSQLTableStorage<BroadcastInProgressEntity> _table;
        private static string GetPartitionKey() => "";
        private static string GetRowKey(Guid operationId) => operationId.ToString();

        public BroadcastInProgressRepository(IReloadingManager<string> connectionStringManager, ILog log)
        {
            _table = AzureTableStorage<BroadcastInProgressEntity>.Create(connectionStringManager, "BroadcastsInProgress", log);
        }

        public async Task<IEnumerable<IBroadcastInProgress>> GetAllAsync()
        {
            return await _table.GetDataAsync(GetPartitionKey());
        }

        public async Task AddAsync(Guid operationId, string hash)
        {
            await _table.InsertAsync(new BroadcastInProgressEntity
            {
                PartitionKey = GetPartitionKey(),
                RowKey = GetRowKey(operationId),
                Hash = hash
            });
        }

        public async Task DeleteAsync(Guid operationId)
        {
            await _table.DeleteAsync(GetPartitionKey(), GetRowKey(operationId));
        }
    }
}
