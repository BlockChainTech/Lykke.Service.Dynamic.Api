﻿using Common;
using Common.Log;
using Lykke.Service.Dash.Api.Core.Services;
using Lykke.Service.Dash.Api.Core.Repositories;
using Lykke.Service.Dash.Api.Services.Helpers;
using Lykke.Service.Dash.Api.Core.Domain.InsightClient;
using System.Threading.Tasks;
using System.Linq;

namespace Lykke.Service.Dash.Job.Services
{
    public class PeriodicalService : IPeriodicalService
    {
        private ILog _log;
        private readonly IDashInsightClient _dashInsightClient;
        private readonly IBroadcastRepository _broadcastRepository;
        private readonly IBroadcastInProgressRepository _broadcastInProgressRepository;
        private readonly IBalanceRepository _balanceRepository;
        private readonly IBalancePositiveRepository _balancePositiveRepository;
        private readonly int _minConfirmations;

        public PeriodicalService(ILog log,
            IDashInsightClient dashInsightClient,
            IBroadcastRepository broadcastRepository,
            IBroadcastInProgressRepository broadcastInProgressRepository,
            IBalanceRepository balanceRepository,
            IBalancePositiveRepository balancePositiveRepository,
            int minConfirmations)
        {
            _log = log;
            _dashInsightClient = dashInsightClient;
            _broadcastRepository = broadcastRepository;
            _broadcastInProgressRepository = broadcastInProgressRepository;
            _balanceRepository = balanceRepository;
            _balancePositiveRepository = balancePositiveRepository;
            _minConfirmations = minConfirmations;
        }

        public async Task UpdateBroadcasts()
        {
            var list = await _broadcastInProgressRepository.GetAllAsync();

            foreach (var item in list)
            {
                var tx = await _dashInsightClient.GetTx(item.Hash);
                if (tx != null && tx.Confirmations >= _minConfirmations)
                {
                    await _log.WriteInfoAsync(nameof(PeriodicalService), nameof(UpdateBroadcasts),
                        new { operationId = item.OperationId, amount = tx.GetAmount(), fees = tx.Fees, blockHeight = tx.BlockHeight }.ToJson(),
                        $"Brodcast update is detected");

                    await _broadcastRepository.SaveAsCompletedAsync(item.OperationId, tx.GetAmount(),
                        tx.Fees, tx.BlockHeight);
                    await _broadcastInProgressRepository.DeleteAsync(item.OperationId);

                    await RefreshBalances(tx);
                }
            }
        }

        public async Task UpdateBalances()
        {
            var positiveBalances = await _balancePositiveRepository.GetAllAsync();
            var continuation = "";

            while (true)
            {
                var balances = await _balanceRepository.GetAsync(100, continuation);

                foreach (var balance in balances.Entities)
                {
                    var deleteZeroBalance = positiveBalances.Any(f => f.Address == balance.Address);

                    await RefreshAddressBalance(balance.Address, deleteZeroBalance);
                }

                if (string.IsNullOrEmpty(balances.ContinuationToken))
                {
                    break;
                }

                continuation = balances.ContinuationToken;
            }
        }

        private async Task RefreshBalances(Tx tx)
        {
            foreach (var address in tx.GetAddresses())
            {
                var balance = await _balanceRepository.GetAsync(address);
                if (balance != null)
                {
                    await RefreshAddressBalance(address, true);
                }
            }
        }

        private async Task<decimal> RefreshAddressBalance(string address, bool deleteZeroBalance)
        {
            var balance = await _dashInsightClient.GetBalance(address, _minConfirmations);

            if (balance > 0)
            {
                var block = await _dashInsightClient.GetLatestBlockHeight();

                var balancePositive = await _balancePositiveRepository.GetAsync(address);
                if (balancePositive == null)
                {
                    await _log.WriteInfoAsync(nameof(PeriodicalService), nameof(RefreshAddressBalance),
                        new { address = address, balance = balance, block = block }.ToJson(),
                        $"Positive balance is detected");
                }
                if (balancePositive != null && balancePositive.Amount != balance)
                {
                    await _log.WriteInfoAsync(nameof(PeriodicalService), nameof(RefreshAddressBalance),
                        new { address = address, balance = balance, oldBalance = balancePositive.Amount, block = block }.ToJson(),
                        $"Change in positive balance is detected");
                }

                await _balancePositiveRepository.SaveAsync(address, balance, block);
            }

            if (balance == 0 && deleteZeroBalance)
            {
                await _log.WriteInfoAsync(nameof(PeriodicalService), nameof(RefreshAddressBalance),
                    new { address = address }.ToJson(),
                    $"Zero balance is detected");

                await _balancePositiveRepository.DeleteAsync(address);
            }

            return balance;
        }
    }
}
