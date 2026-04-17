using System.Globalization;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Equihash.Custom.BitcoinGold;
using Miningcore.Blockchain.Equihash.Custom.Kerrigan;
using Miningcore.Blockchain.Equihash.Custom.Minexcoin;
using Miningcore.Blockchain.Equihash.Custom.Piratechain;
using Miningcore.Blockchain.Equihash.Custom.Veruscoin;
using Miningcore.Blockchain.Equihash.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto.Hashing.Equihash;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using NLog;

namespace Miningcore.Blockchain.Equihash;

public class EquihashJobManager : BitcoinJobManagerBase<EquihashJob>
{
    public EquihashJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider) : base(ctx, clock, messageBus, extraNonceProvider)
    {
        // HybridShareRouter removed - fractional splitting now handled by ShareRecorder
    }

    private EquihashCoinTemplate coin;
    private EquihashSolver solver;

    public EquihashCoinTemplate.EquihashNetworkParams ChainConfig { get; private set; }

    protected override void PostChainIdentifyConfigure()
    {
        ChainConfig = coin.GetNetwork(network.ChainName);
        solver = EquihashSolverFactory.GetSolver(ctx, ChainConfig.Solver);

        base.PostChainIdentifyConfigure();
    }

    private async Task<RpcResponse<EquihashBlockTemplate>> GetBlockTemplateAsync(CancellationToken ct)
    {
        var subsidyResponse = await rpc.ExecuteAsync<ZCashBlockSubsidy>(logger, BitcoinCommands.GetBlockSubsidy, ct);

        // Try GBTArgs from extraPoolConfig, then from raw Extra dict, then default
        var gbtArgs = extraPoolConfig?.GBTArgs;

        if(gbtArgs == null && poolConfig.Extra?.ContainsKey("GBTArgs") == true)
            gbtArgs = Newtonsoft.Json.Linq.JToken.FromObject(poolConfig.Extra["GBTArgs"]);

        // GBTArgs must be wrapped in array for positional params: "params": [{...}]
        var gbtParams = gbtArgs != null ? new object[] { gbtArgs } : GetBlockTemplateParams();

        var result = await rpc.ExecuteAsync<EquihashBlockTemplate>(logger,
            BitcoinCommands.GetBlockTemplate, ct, gbtParams);

        if(subsidyResponse.Error == null && result.Error == null && result.Response != null)
            result.Response.Subsidy = subsidyResponse.Response;
        else if(subsidyResponse.Error != null && result.Error == null)
        {
            // Log warning but continue - some coins don't support getblocksubsidy
            logger.Debug(() => $"getblocksubsidy returned error (code {subsidyResponse.Error.Code}): {subsidyResponse.Error.Message} - continuing without subsidy info");
        }

        return result;
    }

    private RpcResponse<EquihashBlockTemplate> GetBlockTemplateFromJson(string json)
    {
        var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

        return new RpcResponse<EquihashBlockTemplate>(result.ResultAs<EquihashBlockTemplate>());
    }

    protected override IDestination AddressToDestination(string address, BitcoinAddressType? addressType)
    {
        if(!coin.UsesZCashAddressFormat)
            return base.AddressToDestination(address, addressType);

        var decoded = Encoders.Base58.DecodeData(address);
        var hash = decoded.Skip(2).Take(20).ToArray();
        var result = new KeyId(hash);
        return result;
    }

    private EquihashJob CreateJob()
    {
        switch(coin.Symbol)
        {
            case "ARRR":
                return new PiratechainJob();

            case "BTG":
                return new BitcoinGoldJob();

            case "KRGN":
                return new KerriganJob();

            case "MNX":
                return new MinexcoinJob();
            
            case "VRSC":
                return new VeruscoinJob();
        }

        return new EquihashJob();
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var response = string.IsNullOrEmpty(json) ?
                await GetBlockTemplateAsync(ct) :
                GetBlockTemplateFromJson(json);

            // may happen if daemon is currently not connected to peers
            if(response.Error != null)
            {
                logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                return (false, forceUpdate);
            }

            var blockTemplate = response.Response;
            var job = currentJob;

            // Defensive: ensure blockTemplate is not null even when response.Error is null
            if(blockTemplate == null)
            {
                logger.Warn(() => $"Unable to update job. Daemon returned null block template.");
                return (false, forceUpdate);
            }

            var isNew = job == null ||
                (job.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                    blockTemplate.Height > job.BlockTemplate?.Height);

            if(isNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if(isNew || forceUpdate)
            {
                job = CreateJob();

                job.Init(blockTemplate, NextJobId(),
                    poolConfig, clusterConfig, clock, poolAddressDestination, network, solver);

                lock(jobLock)
                {
                    validJobs.Insert(0, job);

                    // trim active jobs
                    while(validJobs.Count > maxActiveJobs)
                        validJobs.RemoveAt(validJobs.Count - 1);

                    // FIX: Assign currentJob inside lock for thread safety consistency
                    currentJob = job;
                }

                if(isNew)
                {
                    if(via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height}");

                    // Debug ZeroNode information if applicable
                    if(coin.HasZeroNodes)
                    {
                        logger.Debug(() => $"coin HasZeroNodes {coin.HasZeroNodes}");
                        logger.Debug(() => $"ZeroNode Payee {blockTemplate.ZeroNodePayee}");
                        logger.Debug(() => $"ZeroNode Payee Amount {blockTemplate.ZeroNodePayeeAmount}");
                    }

                    // update stats
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }

                else
                {
                    if(via != null)
                        logger.Debug(() => $"Template update {blockTemplate.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate.Height}");
                }
            }

            return (isNew, forceUpdate);
        }

        catch(OperationCanceledException)
        {
            // ignored
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    protected override object GetJobParamsForStratum(bool isNew)
    {
        var job = currentJob;
        return job?.GetJobParams(isNew);
    }

    #region API-Surface

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<EquihashCoinTemplate>();

        base.Configure(pc, cc);
    }

    public override async Task<bool> ValidateAddressAsync(string address, CancellationToken ct)
    {
        if(string.IsNullOrEmpty(address))
            return false;

        // handle t-addr
        if(await base.ValidateAddressAsync(address, ct))
            return true;

        if(!coin.UseBitcoinPayoutHandler)
        {
            try
            {
                // handle z-addr
                var result = await rpc.ExecuteAsync<ValidateAddressResponse>(logger,
                    EquihashCommands.ZValidateAddress, ct, new[] { address });

                return result.Response is {IsValid: true};
            }
            catch(Exception ex)
            {
                logger.Error(ex, () => $"Error validating z-address {address}: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    public object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1
        };

        return responseData;
    }

    public async ValueTask<Share> SubmitShareAsync(StratumConnection worker,
        object submission, CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // extract params
        var workerValue = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var nTime = submitParams[2] as string;
        var extraNonce2 = submitParams[3] as string;
        var solution = submitParams[4] as string;

        if(string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.Other, "missing or invalid workername");

        if(string.IsNullOrEmpty(solution))
            throw new StratumException(StratumError.Other, "missing or invalid solution");

        EquihashJob job;

        lock(jobLock)
        {
            job = validJobs.FirstOrDefault(x => x.JobId == jobId);
        }

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        // validate & process
        var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, solution);

        // Log high-difficulty shares approaching block threshold for monitoring
        // This helps track pool luck and identify near-misses
        const double HIGH_SHARE_THRESHOLD = 0.01; // 1% of network difficulty = interesting share
        if(share.ActualShareDifficulty >= (share.NetworkDifficulty * HIGH_SHARE_THRESHOLD))
        {
            logger.Info(() => $"[HIGH-DIFF-SHARE] ActualDiff={share.ActualShareDifficulty:F2}, " +
                $"NetDiff={share.NetworkDifficulty:F2}, " +
                $"Ratio={share.ActualShareDifficulty / share.NetworkDifficulty:P4}, " +
                $"IsBlock={share.IsBlockCandidate}, " +
                $"Miner={context.Miner}");
        }

        // CRITICAL: Detect shares that SHOULD be blocks but aren't marked as blocks
        // This indicates a bug in share validation logic that causes lost blocks
        // For Equihash, shares use same scale as network diff when Diff1 == MaxTarget
        const double BLOCK_DETECTION_THRESHOLD = 0.99; // 99% of network difficulty
        if(share.ActualShareDifficulty >= (share.NetworkDifficulty * BLOCK_DETECTION_THRESHOLD) && !share.IsBlockCandidate)
        {
            logger.Error(() => $"[POTENTIAL-LOST-BLOCK] Actual share difficulty {share.ActualShareDifficulty:F8} meets {BLOCK_DETECTION_THRESHOLD:P0} of network difficulty {share.NetworkDifficulty:F8} " +
                $"but IsBlockCandidate = FALSE - " +
                $"Miner: {context.Miner}, " +
                $"Worker: {context.Worker}, " +
                $"ActualShare/Network Ratio: {(share.ActualShareDifficulty / share.NetworkDifficulty):P2}, " +
                $"Stratum Difficulty: {share.Difficulty:F8}, " +
                $"BlockHeight: {share.BlockHeight}, " +
                $"JobId: {jobId}, " +
                $"ExtraNonce2: {extraNonce2}, " +
                $"NTime: {nTime}, " +
                $"Solution: {solution?.Substring(0, Math.Min(100, solution?.Length ?? 0))}... " +
                $"THIS IS LIKELY A BUG IN SHARE VALIDATION - BLOCK MAY BE LOST!");

            logger.Debug(() => $"[POTENTIAL-LOST-BLOCK-DETAILS] Full solution: {solution}, BlockHex null: {string.IsNullOrEmpty(blockHex)}");

            // Send urgent notification
            messageBus.SendMessage(new AdminNotification("POTENTIAL LOST BLOCK DETECTED",
                $"Pool {poolConfig.Id} share with actual difficulty {share.ActualShareDifficulty:F8} ({(share.ActualShareDifficulty / share.NetworkDifficulty):P2} of network diff) " +
                $"was NOT marked as block candidate. Miner: {context.Miner}. This may indicate a critical bug in block detection logic!"));
        }

        // if block candidate, submit & check if accepted by network
        if(share.IsBlockCandidate)
        {
            // Critical: Check if blockHex is null (can happen if block serialization fails)
            if(string.IsNullOrEmpty(blockHex))
            {
                logger.Error(() => $"[BLOCK-SERIALIZATION-FAIL] Block candidate {share.BlockHeight} [{share.BlockHash}] has null blockHex - block serialization failed - " +
                    $"Share Difficulty: {share.Difficulty:F8}, " +
                    $"Network Difficulty: {share.NetworkDifficulty:F8}, " +
                    $"Miner: {context.Miner}, " +
                    $"ExtraNonce2: {extraNonce2}, " +
                    $"NTime: {nTime}, " +
                    $"Solution: {solution?.Substring(0, Math.Min(100, solution?.Length ?? 0))}..., " +
                    $"JobId: {jobId}");

                logger.Debug(() => $"[BLOCK-SERIALIZATION-FAIL-DETAILS] Full solution: {solution}");

                // Send urgent notification - this is silent money loss
                messageBus.SendMessage(new AdminNotification("BLOCK SERIALIZATION FAILURE",
                    $"Pool {poolConfig.Id} block candidate {share.BlockHeight} [{share.BlockHash}] has null blockHex. " +
                    $"Block serialization failed - block reward LOST. " +
                    $"Miner: {context.Miner}, Difficulty: {share.Difficulty:F8}, Network: {share.NetworkDifficulty:F8}"));

                // Return early - cannot submit without blockHex
                return share;
            }
            else
            {
                logger.Info(() => $"[BLOCK-CANDIDATE] Submitting block {share.BlockHeight} [{share.BlockHash}] - " +
                    $"Share Difficulty: {share.Difficulty:F8}, Network Difficulty: {share.NetworkDifficulty:F8}, Miner: {context.Miner}");

                SubmitResult acceptResponse;

                switch(coin.Symbol)
                {
                    case "VRSC":
                        // when PBaaS activates we must use the coinbasetxn from daemon to get proper fee pool calculations in coinbase
                        var solutionVersion = job.BlockTemplate.Solution.Substring(0, 8);
                        var reversedSolutionVersion = uint.Parse(solutionVersion.HexToReverseByteArray().ToHexString(), NumberStyles.HexNumber);
                        var isPBaaSActive = (reversedSolutionVersion > 6);

                        acceptResponse = await SubmitVeruscoinBlockAsync(share, blockHex, isPBaaSActive, ct);

                        break;
                    default:
                        acceptResponse = await SubmitBlockAsync(share, blockHex, ct);

                        break;
                }

                // is it still a block candidate?
                share.IsBlockCandidate = acceptResponse.Accepted;

                if(share.IsBlockCandidate)
                {
                    logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                    OnBlockFound();

                    // persist the coinbase transaction-hash to allow the payment processor
                    // to verify later on that the pool has received the reward for the block
                    share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
                }

                else
                {
                    // clear fields that no longer apply
                    share.TransactionConfirmationData = null;
                }
            }
        }

        // enrich share with common data
        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        share.NetworkDifficulty = job.Difficulty;
        share.Difficulty = share.Difficulty;
        share.Created = clock.Now;

        // PaymentMode will be determined by ShareRecorder's fractional splitting
        // Leave it null so HybridShareSplitter can split the share properly

        return share;
    }
    
    protected async Task<SubmitResult> SubmitVeruscoinBlockAsync(Share share, string blockHex, bool isPBaaSActive, CancellationToken ct)
    {
        var requestCommand = isPBaaSActive ? VeruscoinCommands.SubmitMergedBlock : BitcoinCommands.SubmitBlock;

        logger.Info(() => $"[VRSC-BLOCK-SUBMIT] Attempting Veruscoin block submission - Height: {share.BlockHeight}, Hash: {share.BlockHash}, " +
            $"PBaaS Active: {isPBaaSActive}, Command: {requestCommand}, BlockHex Length: {blockHex?.Length ?? 0} bytes");

        var batch = new []
        {
            new RpcRequest(requestCommand, new[] { blockHex }),
            new RpcRequest(BitcoinCommands.GetBlock, new[] { share.BlockHash })
        };

        var results = await rpc.ExecuteBatchAsync(logger, ct, batch);

        // did submission succeed?
        var submitResult = results[0];
        var submitError = submitResult.Error?.Message ??
            submitResult.Error?.Code.ToString(CultureInfo.InvariantCulture) ??
            submitResult.Response?.ToString();

        if((!isPBaaSActive && !string.IsNullOrEmpty(submitError)) || (isPBaaSActive && !submitError.Contains("accepted")))
        {
            logger.Error(() => $"[VRSC-BLOCK-SUBMIT-FAIL] Veruscoin block {share.BlockHeight} [{share.BlockHash}] submission REJECTED - " +
                $"PBaaS Active: {isPBaaSActive}, " +
                $"Command: {requestCommand}, " +
                $"Error: {submitError}, " +
                $"RPC Error Code: {submitResult.Error?.Code}, " +
                $"Share Difficulty: {share.Difficulty:F8}, " +
                $"Network Difficulty: {share.NetworkDifficulty:F8}, " +
                $"Miner: {share.Miner}, " +
                $"BlockHex Length: {blockHex?.Length ?? 0} bytes");

            logger.Debug(() => $"[VRSC-BLOCK-SUBMIT-FAIL-HEX] Block {share.BlockHeight} failed submission, BlockHex: {blockHex?.Substring(0, Math.Min(500, blockHex?.Length ?? 0))}...");

            messageBus.SendMessage(new AdminNotification("Block submission failed", $"Pool {poolConfig.Id} {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}failed to submit block {share.BlockHeight}: {submitError}"));
            return new SubmitResult(false, null);
        }

        // was it accepted?
        var acceptResult = results[1];
        var block = acceptResult.Response?.ToObject<Bitcoin.DaemonResponses.Block>();
        var accepted = acceptResult.Error == null && block?.Hash == share.BlockHash;

        if(!accepted)
        {
            var acceptError = acceptResult.Error?.Message ?? acceptResult.Error?.Code.ToString() ?? "null response";

            logger.Error(() => $"[VRSC-BLOCK-VERIFY-FAIL] Veruscoin block {share.BlockHeight} [{share.BlockHash}] submitted but NOT FOUND - " +
                $"PBaaS Active: {isPBaaSActive}, " +
                $"GetBlock Error: {acceptError}, " +
                $"Returned Hash: {block?.Hash ?? "null"}, " +
                $"Expected Hash: {share.BlockHash}, " +
                $"Share Difficulty: {share.Difficulty:F8}, " +
                $"Network Difficulty: {share.NetworkDifficulty:F8}, " +
                $"Miner: {share.Miner}");

            logger.Debug(() => $"[VRSC-BLOCK-VERIFY-FAIL-DETAILS] GetBlock response - Error: {JsonConvert.SerializeObject(acceptResult.Error)}, " +
                $"Block data: {(block != null ? JsonConvert.SerializeObject(block) : "null")}");

            messageBus.SendMessage(new AdminNotification($"[{share.PoolId.ToUpper()}]-[{share.Source}] Block submission failed", $"[{share.PoolId.ToUpper()}]-[{share.Source}] Block {share.BlockHeight} submission failed for pool {poolConfig.Id} because block was not found after submission"));
        }

        return new SubmitResult(accepted, block?.Transactions.FirstOrDefault());
    }

    #endregion // API-Surface
}
