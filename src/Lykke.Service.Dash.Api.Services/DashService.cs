﻿using Common.Log;
using Lykke.Service.Dash.Api.Core.Domain;
using Lykke.Service.Dash.Api.Core.Domain.Broadcast;
using Lykke.Service.Dash.Api.Core.Services;
using Lykke.Service.Dash.Api.Core.Repositories;
using Lykke.Service.Dash.Api.Core.Settings.ServiceSettings;
using Lykke.Service.Dash.Api.Services.Helpers;
using Lykke.Service.Dash.Api.Core.Domain.InsightClient;
using NBitcoin;
using NBitcoin.Dash;
using NBitcoin.JsonConverters;
using NBitcoin.Policy;
using System;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Common;

namespace Lykke.Service.Dash.Api.Services
{
    public class DashService : IDashService
    {
        private readonly ILog _log;
        private readonly IDashInsightClient _dashInsightClient;
        private readonly IBroadcastRepository _broadcastRepository;
        private readonly IBroadcastInProgressRepository _broadcastInProgressRepository;
        private readonly IBalanceRepository _balanceRepository;
        private readonly IBalancePositiveRepository _balancePositiveRepository;
        private readonly Network _network;
        private readonly DashApiSettings _dashApiSettings;

        public DashService(ILog log,
            IDashInsightClient dashInsightClient,
            IBroadcastRepository broadcastRepository,
            IBroadcastInProgressRepository broadcastInProgressRepository,
            IBalanceRepository balanceRepository,
            IBalancePositiveRepository balancePositiveRepository,
            DashApiSettings dashApiSettings)
        {
            DashNetworks.Register();

            _log = log;
            _dashInsightClient = dashInsightClient;
            _broadcastRepository = broadcastRepository;
            _broadcastInProgressRepository = broadcastInProgressRepository;
            _balanceRepository = balanceRepository;
            _balancePositiveRepository = balancePositiveRepository;
            _dashApiSettings = dashApiSettings;
            _network = Network.GetNetwork(_dashApiSettings.Network);
        }

        public BitcoinAddress GetBitcoinAddress(string address)
        {
            try
            {
                return BitcoinAddress.Create(address, _network);
            }
            catch
            {
                return null;
            }
        }

        public Transaction GetTransaction(string transactionHex)
        {
            try
            {
                return Transaction.Parse(transactionHex);
            }
            catch
            {
                return null;
            }
        }

        public async Task<string> BuildTransactionAsync(Guid operationId, BitcoinAddress fromAddress,
            BitcoinAddress toAddress, decimal amount, bool includeFee)
        {
            var sendAmount = Money.FromUnit(amount, Asset.Dash.Unit);
            var txsUnspent = await _dashInsightClient.GetTxsUnspentAsync(fromAddress.ToString());

            var builder = new TransactionBuilder()
                .Send(toAddress, sendAmount)
                .SetChange(fromAddress)
                .SetTransactionPolicy(new StandardTransactionPolicy
                {
                    CheckFee = false
                });

            if (includeFee)
            {
                builder.SubtractFees();
            }

            foreach (var txUnspent in txsUnspent)
            {
                var coin = new Coin(
                    fromTxHash: uint256.Parse(txUnspent.Txid),
                    fromOutputIndex: txUnspent.Vout,
                    amount: Money.Coins(txUnspent.Amount),
                    scriptPubKey: fromAddress.ScriptPubKey);

                builder.AddCoins(coin);
            }

            var feeMoney = Money.FromUnit(_dashApiSettings.Fee, Asset.Dash.Unit);

            var tx = builder
                .SendFees(feeMoney)
                .BuildTransaction(false);

            var coins = builder.FindSpentCoins(tx);

            return Serializer.ToString((tx: tx, coins: coins));
        }

        public async Task BroadcastAsync(Transaction transaction, Guid operationId)
        {
            TxBroadcast response;

            try
            {
                response = await _dashInsightClient.BroadcastTxAsync(transaction.ToHex());

                if (response == null)
                {
                    throw new ArgumentException($"{nameof(response)} can not be null");
                }
                if (string.IsNullOrEmpty(response.Txid))
                {
                    throw new ArgumentException($"{nameof(response)}{nameof(response.Txid)} can not be null or empty. Response={response}");
                }
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(nameof(DashService), nameof(BroadcastAsync),
                    $"transaction: {transaction.ToString()}, operationId: {operationId}", ex);

                throw;
            }

            var block = await GetLatestBlockHeight();

            await _broadcastRepository.AddAsync(operationId, response.Txid, block);
            await _broadcastInProgressRepository.AddAsync(operationId, response.Txid);
        }

        public async Task<IBroadcast> GetBroadcastAsync(Guid operationId)
        {
            return await _broadcastRepository.GetAsync(operationId);
        }

        public async Task DeleteBroadcastAsync(IBroadcast broadcast)
        {
            await _broadcastRepository.DeleteAsync(broadcast.OperationId);

            if (broadcast.State == BroadcastState.Broadcasted)
            {
                await _broadcastInProgressRepository.DeleteAsync(broadcast.OperationId);
            }
        }

        public async Task UpdateBroadcasts()
        {
            var list = await _broadcastInProgressRepository.GetAllAsync();

            foreach (var item in list)
            {
                var tx = await _dashInsightClient.GetTx(item.Hash);
                if (tx != null && tx.Confirmations >= _dashApiSettings.MinConfirmations)
                {
                    await _log.WriteInfoAsync(nameof(DashService), nameof(UpdateBroadcasts),
                        new { operationId = item.OperationId, amount = tx.GetAmount(), fees = tx.Fees, blockHeight = tx.BlockHeight }.ToJson(),
                        $"Brodcast update is detected");

                    await _broadcastRepository.SaveAsCompletedAsync(item.OperationId, tx.GetAmount(),
                        tx.Fees, tx.BlockHeight);
                    await _broadcastInProgressRepository.DeleteAsync(item.OperationId);

                    await RefreshBalances(tx);
                }
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

        public async Task UpdateBalances()
        {
            var balances = await _balanceRepository.GetAllAsync();
            var positiveBalances = await _balancePositiveRepository.GetAllAsync();

            foreach (var balance in balances)
            {
                var deleteZeroBalance = positiveBalances.Any(f => f.Address == balance.Address);

                await RefreshAddressBalance(balance.Address, deleteZeroBalance);
            }
        }

        private async Task<decimal> RefreshAddressBalance(string address, bool deleteZeroBalance)
        {
            var balance = await GetAddressBalance(address);

            if (balance > 0)
            {
                var block = await GetLatestBlockHeight();

                var balancePositive = await _balancePositiveRepository.GetAsync(address);
                if (balancePositive == null)
                {
                    await _log.WriteInfoAsync(nameof(DashService), nameof(RefreshAddressBalance),
                        new { address = address, balance = balance, block = block }.ToJson(),
                        $"Positive balance is detected");
                }
                if (balancePositive != null && balancePositive.Amount != balance)
                {
                    await _log.WriteInfoAsync(nameof(DashService), nameof(RefreshAddressBalance),
                        new { address = address, balance = balance, oldBalance = balancePositive.Amount, block = block }.ToJson(),
                        $"Change in positive balance is detected");
                }

                await _balancePositiveRepository.SaveAsync(address, balance, block);
            }

            if(balance == 0 && deleteZeroBalance)
            {
                await _log.WriteInfoAsync(nameof(DashService), nameof(RefreshAddressBalance),
                    new { address = address }.ToJson(), 
                    $"Zero balance is detected");

                await _balancePositiveRepository.DeleteAsync(address);
            }

            return balance;
        }

        private async Task<long> GetLatestBlockHeight()
        {
            return await _dashInsightClient.GetLatestBlockHeight();
        }

        public async Task<decimal> GetAddressBalance(string address)
        {
            var utxos = await _dashInsightClient.GetTxsUnspentAsync(address);
            var balance = utxos
                .Where(f => f.Confirmations >= _dashApiSettings.MinConfirmations)
                .Sum(f => f.Amount);

            return balance;
        }

        public decimal GetFee()
        {
            return _dashApiSettings.Fee;
        }

        public async Task<Tx[]> GetFromAddressTxs(string fromAddress, int take, string afterHash)
        {
            var txsFinal = new List<Tx>();
            var counter = 0;

            while (true)
            {
                var txs = await GetAddressTxs(fromAddress, counter);

                if (!txs.Any())
                {
                    return txsFinal.ToArray();
                }

                foreach (var tx in txs)
                {
                    counter++;
                    
                    if (tx.Vin != null && tx.Vin.Any(x => x.Addr == fromAddress))
                    {
                        if (tx.Txid == afterHash)
                        {
                            return txsFinal.ToArray();
                        }

                        txsFinal.Add(tx);

                        if (txsFinal.Count == take)
                        {
                            return txsFinal.ToArray();
                        }
                    }
                }
            }            
        }

        private async Task<Tx[]> GetAddressTxs(string fromAddress, int continuation)
        {
            var txs = await _dashInsightClient.GetAddressTxs(fromAddress, continuation);

            return txs == null ? new Tx[] { } : txs.ToArray();
        }
    }
}
