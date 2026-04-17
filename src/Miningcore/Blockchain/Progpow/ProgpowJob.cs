using System.Globalization;
using System.Text;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Progpow;

public class ProgpowJobParams
{
    public ulong Height { get; init; }
    public bool CleanJobs { get; set; }
}

public class ProgpowJob : BitcoinJob
{
    protected IProgpowCache progpowHasher;
    private new ProgpowJobParams jobParams;

    protected virtual byte[] SerializeHeader(Span<byte> coinbaseHash)
    {
        // build merkle-root
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

        // Build version
        var version = BlockTemplate.Version;

#pragma warning disable 618
        var blockHeader = new BlockHeader
#pragma warning restore 618
        {
            Version = unchecked((int) version),
            Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits)),
            HashPrevBlock = uint256.Parse(BlockTemplate.PreviousBlockhash),
            HashMerkleRoot = new uint256(merkleRoot),
            BlockTime = DateTimeOffset.FromUnixTimeSeconds(BlockTemplate.CurTime),
            Nonce = BlockTemplate.Height
        };

        return blockHeader.ToBytes();
    }

    public virtual (Share Share, string BlockHex) ProcessShareInternal(ILogger logger,
        StratumConnection worker, ulong nonce, string inputHeaderHash, string mixHash)
    {
        var context = worker.ContextAs<ProgpowWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        // build coinbase
        var coinbase = SerializeCoinbase(extraNonce1);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // hash block-header
        var headerBytes = SerializeHeader(coinbaseHash);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash);
        headerHash.Reverse();

        var headerHashHex = headerHash.ToHexString();

        if(headerHashHex != inputHeaderHash)
            throw new StratumException(StratumError.MinusOne, $"bad header-hash");

        if(!progpowHasher.Compute(logger, (int) BlockTemplate.Height, headerHash.ToArray(), nonce, out var mixHashOut, out var resultBytes))
            throw new StratumException(StratumError.MinusOne, "bad hash");

        if(mixHash != mixHashOut.ToHexString())
            throw new StratumException(StratumError.MinusOne, $"bad mix-hash");

        resultBytes.ReverseInPlace();
        mixHashOut.ReverseInPlace();

        var resultValue = new uint256(resultBytes);
        var resultValueBig = resultBytes.AsSpan().ToBigInteger();
        // calc share-diff using coin-specific Diff1
        var diff1 = coin.Symbol == "FIRO" ? FiroConstants.Diff1 : RavencoinConstants.Diff1;
        var shareDiff = (double) new BigRational(diff1, resultValueBig) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // check if the share meets the much harder block difficulty (block candidate)
        var isBlockCandidate = resultValue <= blockTargetValue;

        // DEBUG: Log high-diff shares that might be close to block candidates
        if(shareDiff >= 100)
            logger.Info(() => $"High diff share: D={shareDiff:F1}, result={resultValue}, target={blockTargetValue}, isCandidate={isBlockCandidate}");

        // test if share meets at least workers current difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            // check if share matched the previous difficulty from before a vardiff retarget
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                // use previous difficulty
                stratumDifficulty = context.PreviousDifficulty.Value;
            }

            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var result = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier,
        };

        if(!isBlockCandidate)
        {
            return (result, null);
        }

        result.IsBlockCandidate = true;
        result.BlockReward = rewardToPool?.ToDecimal(MoneyUnit.BTC) ?? 0;

        // Use configured blockHasher (coins.json) for block identity. Kerrigan
        // uses X11 for all block identity regardless of mining algo; Ravencoin
        // uses reverse(sha256d). See the blockHasher coin config field.
        Span<byte> identityHash = stackalloc byte[32];
        blockHasher.Digest(headerBytes, identityHash);
        result.BlockHash = identityHash.ToHexString();

        var blockBytes = SerializeBlock(headerBytes, coinbase, nonce, mixHashOut);
        var blockHex = blockBytes.ToHexString();

        return (result, blockHex);
    }

    protected virtual byte[] SerializeCoinbase(string extraNonce1)
    {
        var extraNonce1Bytes = extraNonce1.HexToByteArray();

        using var stream = new MemoryStream();
        {
            stream.Write(coinbaseInitial);
            stream.Write(extraNonce1Bytes);
            stream.Write(coinbaseFinal);

            return stream.ToArray();
        }
    }

    protected virtual byte[] SerializeBlock(byte[] header, byte[] coinbase, ulong nonce, byte[] mixHash)
    {
        var rawTransactionBuffer = BuildRawTransactionBuffer();
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1; // +1 for prepended coinbase tx

        // BIP 141: When default_witness_commitment is present, the coinbase transaction
        // must be serialized in segwit format with a witness nonce (32 zero bytes).
        // The non-witness coinbase (used for txid/merkle root) stays unchanged.
        // The block body must contain the witness-serialized coinbase.
        var withWitnessCommitment = !string.IsNullOrEmpty(BlockTemplate.DefaultWitnessCommitment);
        var coinbaseToWrite = withWitnessCommitment ? BuildWitnessCoinbase(coinbase) : coinbase;

        using var stream = new MemoryStream();
        {
            var bs = new BitcoinStream(stream, true);

            bs.ReadWrite(header);
            bs.ReadWrite(ref nonce);
            bs.ReadWrite(mixHash);
            bs.ReadWriteAsVarInt(ref transactionCount);

            bs.ReadWrite(coinbaseToWrite);
            bs.ReadWrite(rawTransactionBuffer);

            return stream.ToArray();
        }
    }

    /// <summary>
    /// Converts a non-witness coinbase transaction to segwit format per BIP 141.
    /// Inserts marker+flag bytes after version and appends witness stack before locktime.
    ///
    /// Non-witness format:
    ///   [version:4][inputs...][outputs...][locktime:4][extra_payload?]
    ///
    /// Witness format:
    ///   [version:4][marker:0x00][flag:0x01][inputs...][outputs...][witness_count:1][item_len:32][nonce:32 zeros][locktime:4][extra_payload?]
    /// </summary>
    private byte[] BuildWitnessCoinbase(byte[] coinbase)
    {
        // The coinbase has this structure:
        //   bytes 0-3:  version (4 bytes)
        //   bytes 4..N: inputs + outputs (variable)
        //   bytes N-3..N: locktime (4 bytes)
        //   bytes N+1..: optional extra_payload (DIP3 coinbase_payload as varstring)
        //
        // For DIP3 special transactions (txVersion with type bits set), the coinbase_payload
        // follows locktime as a var-length string. We need to find where locktime starts
        // to insert the witness stack before it.
        //
        // Strategy: We know the coinbase structure because we built it.
        // The locktime + optional coinbase_payload are at the end of coinbaseFinal.
        // We can compute the split point: everything before locktime = version + inputs + outputs,
        // and locktime + extra_payload come after.

        using var stream = new MemoryStream();

        // 1. Write version (first 4 bytes)
        stream.Write(coinbase, 0, 4);

        // 2. Write segwit marker (0x00) and flag (0x01) per BIP 141/144
        stream.WriteByte(0x00);
        stream.WriteByte(0x01);

        // 3. Write everything between version and locktime (inputs + outputs)
        //    The locktime is 4 bytes. After locktime, there may be a coinbase_payload (DIP3).
        //    We need to figure out where the "tail" starts (locktime + extra_payload).
        //
        //    coinbaseFinal structure (built in BuildCoinbase):
        //      [scriptsig_final][sequence:4][outputs...][locktime:4][coinbase_payload?]
        //
        //    The locktime (4 zero bytes) + coinbase_payload are the last part of coinbaseFinal.
        //    We know the tail length: 4 bytes for locktime + coinbase_payload bytes.

        var hasCoinbasePayload = coin.HasMasterNodes && !string.IsNullOrEmpty(masterNodeParameters?.CoinbasePayload);
        var hasTxComment = !string.IsNullOrEmpty(txComment);

        // Calculate tail size: locktime(4) + txComment(varstring) + coinbase_payload(varstring)
        var tailSize = 4; // locktime

        if(hasTxComment)
        {
            var commentBytes = Encoding.ASCII.GetBytes(txComment);
            tailSize += VarIntSize((uint) commentBytes.Length) + commentBytes.Length;
        }

        if(hasCoinbasePayload)
        {
            var payloadBytes = masterNodeParameters.CoinbasePayload.HexToByteArray();
            tailSize += VarIntSize((uint) payloadBytes.Length) + payloadBytes.Length;
        }

        // The "body" is everything after version and before the tail
        var bodyLength = coinbase.Length - 4 - tailSize;
        stream.Write(coinbase, 4, bodyLength);

        // 4. Write witness stack: 1 item, 32 bytes, all zeros (witness reserved value)
        stream.WriteByte(0x01);  // witness count: 1 stack item for the single input
        stream.WriteByte(0x20);  // item length: 32 bytes
        stream.Write(new byte[32], 0, 32); // witness nonce: 32 zero bytes

        // 5. Write the tail (locktime + optional extra_payload)
        var tailStart = coinbase.Length - tailSize;
        stream.Write(coinbase, tailStart, tailSize);

        return stream.ToArray();
    }

    /// <summary>
    /// Returns the byte size of a Bitcoin varint encoding for the given value.
    /// </summary>
    private static int VarIntSize(uint value)
    {
        if(value < 0xFD) return 1;
        if(value <= 0xFFFF) return 3;
        if(value <= 0xFFFFFFFF) return 5;
        return 9;
    }

    #region API-Surface

    public virtual void Init(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher, IProgpowCache progpowHasher)
    {
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(poolAddressDestination);
        Contract.RequiresNonNull(coinbaseHasher);
        Contract.RequiresNonNull(headerHasher);
        Contract.RequiresNonNull(blockHasher);
        Contract.RequiresNonNull(progpowHasher);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        this.coin = pc.Template.As<ProgpowCoinTemplate>();
        this.txVersion = coin.CoinbaseTxVersion;
        this.network = network;
        this.clock = clock;
        this.poolAddressDestination = poolAddressDestination;
        this.BlockTemplate = blockTemplate;
        this.JobId = jobId;

        var coinbaseString = !string.IsNullOrEmpty(cc.PaymentProcessing?.CoinbaseString) ?
            cc.PaymentProcessing?.CoinbaseString.Trim() : "Miningcore";

        if(!string.IsNullOrEmpty(coinbaseString))
            this.scriptSigFinalBytes = new Script(Op.GetPushOp(Encoding.UTF8.GetBytes(coinbaseString))).ToBytes();

        this.Difficulty = new Target(System.Numerics.BigInteger.Parse(BlockTemplate.Target, NumberStyles.HexNumber)).Difficulty;

        this.extraNoncePlaceHolderLength = RavencoinConstants.ExtranoncePlaceHolderLength;
        this.shareMultiplier = shareMultiplier;
        
        if(coin.HasMasterNodes)
        {
            masterNodeParameters = BlockTemplate.Extra.SafeExtensionDataAs<MasterNodeBlockTemplateExtra>();

            if(coin.HasSmartNodes)
            {
                if(masterNodeParameters.Extra?.ContainsKey("smartnode") == true)
                {
                    masterNodeParameters.Masternode = JToken.FromObject(masterNodeParameters.Extra["smartnode"]);
                }
            }

            // Firo uses "znode" instead of "smartnode"
            if(coin.Symbol == "FIRO")
            {
                if(masterNodeParameters.Extra?.ContainsKey("znode") == true)
                {
                    masterNodeParameters.Masternode = JToken.FromObject(masterNodeParameters.Extra["znode"]);
                }
            }

            if(!string.IsNullOrEmpty(masterNodeParameters.CoinbasePayload))
            {
                txVersion = 3;
                const uint txType = 5;
                txVersion += txType << 16;
            }
        }
        
        if(coin.HasPayee)
            payeeParameters = BlockTemplate.Extra.SafeExtensionDataAs<PayeeBlockTemplateExtra>();

        if (coin.HasFounderFee)
            founderParameters = BlockTemplate.Extra.SafeExtensionDataAs<FounderBlockTemplateExtra>();

        if (coin.HasMinerFund)
            minerFundParameters = BlockTemplate.Extra.SafeExtensionDataAs<MinerFundTemplateExtra>("coinbasetxn", "minerfund");

        this.coinbaseHasher = coinbaseHasher;
        this.headerHasher = headerHasher;
        this.blockHasher = blockHasher;
        this.progpowHasher = progpowHasher;
        
        if(!string.IsNullOrEmpty(BlockTemplate.Target))
            this.blockTargetValue = new uint256(BlockTemplate.Target);
        else
        {
            var tmp = new Target(BlockTemplate.Bits.HexToByteArray());
            this.blockTargetValue = tmp.ToUInt256();
        }

        BuildMerkleBranches();
        BuildCoinbase();

        this.jobParams = new ProgpowJobParams
        {
            Height = BlockTemplate.Height,
            CleanJobs = false
        };
    }

    public new object GetJobParams(bool isNew)
    {
        jobParams.CleanJobs = isNew;
        return jobParams;
    }

    public void PrepareWorkerJob(ProgpowWorkerJob workerJob, out string headerHash)
    {
        workerJob.Job = this;
        workerJob.Height = BlockTemplate.Height;
        workerJob.Bits = BlockTemplate.Bits;
        workerJob.SeedHash = progpowHasher.SeedHash.ToHexString();
        headerHash = CreateHeaderHash(workerJob);
    }

    private string CreateHeaderHash(ProgpowWorkerJob workerJob)
    {
        var headerHasher = coin.HeaderHasherValue;
        var coinbaseHasher = coin.CoinbaseHasherValue;
        var extraNonce1 = workerJob.ExtraNonce1;

        var coinbase = SerializeCoinbase(extraNonce1);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        var headerBytes = SerializeHeader(coinbaseHash);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash);
        headerHash.Reverse();

        return headerHash.ToHexString();
    }


    #endregion // API-Surface
}