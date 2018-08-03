using Common.Log;
using Lykke.Service.Dynamic.Api.Core.Domain;
using Lykke.Service.Dynamic.Api.Core.Domain.Broadcast;
using Lykke.Service.Dynamic.Api.Core.Services;
using Lykke.Service.Dynamic.Api.Core.Repositories;
using Lykke.Service.Dynamic.Api.Core.Domain.InsightClient;
using NBitcoin;
//using NBitcoin.;
using NBitcoin.JsonConverters;
using NBitcoin.Policy;
using System;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace Lykke.Service.Dynamic.Api.Services
{
    public class DynamicService : IDynamicService
    {
        private readonly ILog _log;
        private readonly IDynamicInsightClient _dynamicInsightClient;
        private readonly IBroadcastRepository _broadcastRepository;
        private readonly IBroadcastInProgressRepository _broadcastInProgressRepository;
        private readonly Network _network;
        private readonly decimal _fee;
        private readonly int _minConfirmations;

        public DynamicService(ILog log,
            IDynamicInsightClient dynamicInsightClient,
            IBroadcastRepository broadcastRepository,
            IBroadcastInProgressRepository broadcastInProgressRepository,
            IBalanceRepository balanceRepository,
            IBalancePositiveRepository balancePositiveRepository,
            string network,
            decimal fee,
            int minConfirmations)
        {
            
            NBitcoinDynamic.DynamicNetworks.Register();

            _log = log;
            _dynamicInsightClient = dynamicInsightClient;
            _broadcastRepository = broadcastRepository;
            _broadcastInProgressRepository = broadcastInProgressRepository;
            _network = Network.GetNetwork(network);
            _fee = fee;
            _minConfirmations = minConfirmations;
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
            var sendAmount = Money.FromUnit(amount, Asset.Dynamic.Unit);
            var txsUnspent = await _dynamicInsightClient.GetTxsUnspentAsync(fromAddress.ToString(), _minConfirmations);

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

            var feeMoney = Money.FromUnit(_fee, Asset.Dynamic.Unit);

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
                response = await _dynamicInsightClient.BroadcastTxAsync(transaction.ToHex());

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
                await _log.WriteErrorAsync(nameof(DynamicService), nameof(BroadcastAsync),
                    $"transaction: {transaction}, operationId: {operationId}", ex);

                throw;
            }

            var block = await _dynamicInsightClient.GetLatestBlockHeight();

            await _broadcastRepository.AddAsync(operationId, response.Txid, block);
            await _broadcastInProgressRepository.AddAsync(operationId, response.Txid);
        }

        public async Task<IBroadcast> GetBroadcastAsync(Guid operationId)
        {
            return await _broadcastRepository.GetAsync(operationId);
        }

        public async Task DeleteBroadcastAsync(IBroadcast broadcast)
        {
            await _broadcastInProgressRepository.DeleteAsync(broadcast.OperationId);
            await _broadcastRepository.DeleteAsync(broadcast.OperationId);
        }

        public async Task<decimal> GetAddressBalance(string address)
        {
            return await _dynamicInsightClient.GetBalance(address, _minConfirmations);
        }

        public decimal GetFee()
        {
            return _fee;
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
            var txs = await _dynamicInsightClient.GetAddressTxs(fromAddress, continuation);

            return txs?.ToArray() ?? new Tx[] { };
        }
    }
}
