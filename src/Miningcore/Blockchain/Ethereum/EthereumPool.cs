using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Ethereum
{
    [CoinFamily(CoinFamily.Ethereum)]
    public class EthereumPool : PoolBase
    {
        public EthereumPool(IComponentContext ctx,
            JsonSerializerSettings serializerSettings,
            IConnectionFactory cf,
            IStatsRepository statsRepo,
            IMapper mapper,
            IMasterClock clock,
            IMessageBus messageBus,
            NicehashService nicehashService) :
            base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, nicehashService)
        {
        }

        private object currentJobParams;
        private EthereumJobManager manager;
        private EthereumCoinTemplate coin;

        private async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;
            var context = connection.ContextAs<EthereumWorkerContext>();

            if(request.Id == null)
                throw new StratumException(StratumError.Other, "missing request id");

            var requestParams = request.ParamsAs<string[]>();

            if(requestParams == null || requestParams.Length < 2 || requestParams.Any(string.IsNullOrEmpty))
                throw new StratumException(StratumError.MinusOne, "invalid request");

            manager.PrepareWorker(connection);

            var data = new object[]
                {
                    new object[]
                    {
                        EthereumStratumMethods.MiningNotify,
                        connection.ConnectionId,
                        EthereumConstants.EthereumStratumVersion
                    },
                    context.ExtraNonce1
                }
                .ToArray();

            await connection.RespondAsync(data, request.Id);

            // setup worker context
            context.IsSubscribed = true;
            context.UserAgent = requestParams[0].Trim();
        }

        private async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;
            var context = connection.ContextAs<EthereumWorkerContext>();

            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var requestParams = request.ParamsAs<string[]>();
            var workerValue = requestParams?.Length > 0 ? requestParams[0] : "0";
            var password = requestParams?.Length > 1 ? requestParams[1] : null;
            var passParts = password?.Split(PasswordControlVarsSeparator);

            // extract worker/miner
            var workerParts = workerValue?.Split('.');
            var minerName = workerParts?.Length > 0 ? workerParts[0].Trim() : null;
            var workerName = workerParts?.Length > 1 ? workerParts[1].Trim() : "0";

            // assumes that workerName is an address
            context.IsAuthorized = manager.ValidateAddress(minerName);

            // respond
            await connection.RespondAsync(context.IsAuthorized, request.Id);

            if(context.IsAuthorized)
            {
                context.Miner = minerName.ToLower();
                context.Worker = workerName;

                // extract control vars from password
                var staticDiff = GetStaticDiffFromPassparts(passParts);

                // Nicehash support
                var nicehashDiff = await GetNicehashStaticMinDiff(connection, context.UserAgent, coin.Name, coin.GetAlgorithmName());

                if(nicehashDiff.HasValue)
                {
                    if(!staticDiff.HasValue || nicehashDiff > staticDiff)
                    {
                        logger.Info(() => $"[{connection.ConnectionId}] Nicehash detected. Using API supplied difficulty of {nicehashDiff.Value}");

                        staticDiff = nicehashDiff;
                    }

                    else
                        logger.Info(() => $"[{connection.ConnectionId}] Nicehash detected. Using miner supplied difficulty of {staticDiff.Value}");
                }

                // Static diff
                if(staticDiff.HasValue &&
                   (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                    context.VarDiff == null && staticDiff.Value > context.Difficulty))
                {
                    context.VarDiff = null; // disable vardiff
                    context.SetDifficulty(staticDiff.Value);

                    logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");
                }

                await EnsureInitialWorkSent(connection);

                logger.Info(() => $"[{connection.ConnectionId}] Authorized worker {workerValue}");
            }

            else
            {
                logger.Info(() => $"[{connection.ConnectionId}] Banning unauthorized worker {minerName} for {loginFailureBanTimeout.TotalSeconds} sec");

                banManager.Ban(connection.RemoteEndpoint.Address, loginFailureBanTimeout);

                CloseConnection(connection);
            }
        }

        private async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
        {
            var request = tsRequest.Value;
            var context = connection.ContextAs<EthereumWorkerContext>();

            try
            {
                if(request.Id == null)
                    throw new StratumException(StratumError.MinusOne, "missing request id");

                // check age of submission (aged submissions are usually caused by high server load)
                var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

                if(requestAge > maxShareAge)
                {
                    logger.Warn(() => $"[{connection.ConnectionId}] Dropping stale share submission request (server overloaded?)");
                    return;
                }

                // validate worker
                if(!context.IsAuthorized)
                    throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized worker");
                else if(!context.IsSubscribed)
                    throw new StratumException(StratumError.NotSubscribed, "not subscribed");

                // check request
                var submitRequest = request.ParamsAs<string[]>();

                if(submitRequest.Length != 3 ||
                    submitRequest.Any(string.IsNullOrEmpty))
                    throw new StratumException(StratumError.MinusOne, "malformed PoW result");

                // recognize activity
                context.LastActivity = clock.Now;

                var poolEndpoint = poolConfig.Ports[connection.LocalEndpoint.Port];

                var share = await manager.SubmitShareAsync(connection, submitRequest, ct);

                await connection.RespondAsync(true, request.Id);

                // publish
                messageBus.SendMessage(new StratumShare(connection, share));

                // telemetry
                PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

                logger.Info(() => $"[{connection.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty / EthereumConstants.Pow2x32, 3)}");
                await EnsureInitialWorkSent(connection);

                // update pool stats
                if(share.IsBlockCandidate)
                    poolStats.LastPoolBlockTime = clock.Now;

                // update client stats
                context.Stats.ValidShares++;
                await UpdateVarDiffAsync(connection);
            }

            catch(StratumException ex)
            {
                // telemetry
                PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

                // update client stats
                context.Stats.InvalidShares++;
                logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");

                // banning
                ConsiderBan(connection, context, poolConfig.Banning);

                throw;
            }
        }

        private async Task EnsureInitialWorkSent(StratumConnection connection)
        {
            var context = connection.ContextAs<EthereumWorkerContext>();
            var sendInitialWork = false;

            lock(context)
            {
                if(context.IsSubscribed && context.IsAuthorized && !context.IsInitialWorkSent)
                {
                    context.IsInitialWorkSent = true;
                    sendInitialWork = true;
                }
            }

            if(sendInitialWork)
            {
                // send intial update
                await connection.NotifyAsync(EthereumStratumMethods.SetDifficulty, new object[] { context.Difficulty });
                await connection.NotifyAsync(EthereumStratumMethods.MiningNotify, currentJobParams);
            }
        }

        protected virtual Task OnNewJobAsync(object jobParams)
        {
            currentJobParams = jobParams;

            logger.Info(() => "Broadcasting job");

            return Guard(()=> Task.WhenAll(ForEachConnection(async connection =>
            {
                if(!connection.IsAlive)
                    return;

                var context = connection.ContextAs<EthereumWorkerContext>();

                if(!context.IsSubscribed || !context.IsAuthorized || CloseIfDead(connection, context))
                    return;

                // varDiff: if the client has a pending difficulty change, apply it now
                if(context.ApplyPendingDifficulty())
                    await connection.NotifyAsync(EthereumStratumMethods.SetDifficulty, new object[] { context.Difficulty });

                // send job
                await connection.NotifyAsync(EthereumStratumMethods.MiningNotify, currentJobParams);
            })), ex=> logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"));
        }

        #region Overrides

        public override void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            coin = poolConfig.Template.As<EthereumCoinTemplate>();

            base.Configure(poolConfig, clusterConfig);
        }

        protected override async Task SetupJobManager(CancellationToken ct)
        {
            manager = ctx.Resolve<EthereumJobManager>(
                new TypedParameter(typeof(IExtraNonceProvider), new EthereumExtraNonceProvider(poolConfig.Id, clusterConfig.InstanceId)));

            manager.Configure(poolConfig, clusterConfig);

            await manager.StartAsync(ct);

            if(poolConfig.EnableInternalStratum == true)
            {
                disposables.Add(manager.Jobs
                    .Select(job => Observable.FromAsync(() =>
                        Guard(()=> OnNewJobAsync(job),
                            ex=> logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                    .Concat()
                    .Subscribe(_ => { }, ex =>
                    {
                        logger.Debug(ex, nameof(OnNewJobAsync));
                    }));

                // start with initial blocktemplate
                await manager.Jobs.Take(1).ToTask(ct);
            }

            else
            {
                // keep updating NetworkStats
                disposables.Add(manager.Jobs.Subscribe());
            }
        }

        protected override async Task InitStatsAsync()
        {
            await base.InitStatsAsync();

            blockchainStats = manager.BlockchainStats;
        }

        protected override WorkerContextBase CreateWorkerContext()
        {
            return new EthereumWorkerContext();
        }

        protected override async Task OnRequestAsync(StratumConnection client,
            Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
        {
            var request = tsRequest.Value;

            try
            {
                switch(request.Method)
                {
                    case EthereumStratumMethods.Subscribe:
                        await OnSubscribeAsync(client, tsRequest);
                        break;

                    case EthereumStratumMethods.Authorize:
                        await OnAuthorizeAsync(client, tsRequest);
                        break;

                    case EthereumStratumMethods.SubmitShare:
                        await OnSubmitAsync(client, tsRequest, ct);
                        break;

                    case EthereumStratumMethods.ExtraNonceSubscribe:
                        // Pretend to support it even though we actually do not. Some miners drop the connection upon receiving an error from this
                        await client.RespondAsync(true, request.Id);
                        break;

                    default:
                        logger.Info(() => $"[{client.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                        await client.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                        break;
                }
            }

            catch(StratumException ex)
            {
                await client.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
            }
        }

        public override double HashrateFromShares(double shares, double interval)
        {
            var result = shares / interval;
            return result;
        }

        public override double ShareMultiplier => 1;

        protected override async Task OnVarDiffUpdateAsync(StratumConnection client, double newDiff)
        {
            await base.OnVarDiffUpdateAsync(client, newDiff);

            // apply immediately and notify client
            var context = client.ContextAs<EthereumWorkerContext>();

            if(context.HasPendingDifficulty)
            {
                context.ApplyPendingDifficulty();

                // send job
                await client.NotifyAsync(EthereumStratumMethods.SetDifficulty, new object[] { context.Difficulty });
                await client.NotifyAsync(EthereumStratumMethods.MiningNotify, currentJobParams);
            }
        }

        #endregion // Overrides
    }
}
