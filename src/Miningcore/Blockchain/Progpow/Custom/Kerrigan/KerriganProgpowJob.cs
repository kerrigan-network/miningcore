using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Extensions;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using NLog;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.Progpow.Custom.Kerrigan;

/// <summary>
/// Kerrigan KawPoW job. Kerrigan is a Dash-fork with KawPoW as one of its PoW algorithms.
/// Key differences from standard KawPoW (Ravencoin):
/// - Header nonce field is always 0 (height is in a separate nHeight field)
/// - Block serialization inserts nHeight (uint32 LE) between header and nNonce64
/// - Uses daemon's pre-built coinbasetxn (includes pool payout + all treasury outputs)
/// </summary>
public class KerriganProgpowJob : ProgpowJob
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private bool useDaemonCoinbase;

    protected override void BuildCoinbase()
    {
        // Try to use daemon's pre-built coinbasetxn (via rpcalgoport + pooladdress GBT param).
        // This includes all required treasury outputs (AI 40%, MN 20%, dev 15%, founders 5%).
        string coinbasetxnHex = null;

        if(BlockTemplate.Extra != null)
        {
            // Try SafeExtensionDataAs first
            coinbasetxnHex = BlockTemplate.Extra.SafeExtensionDataAs<string>("coinbasetxn", "data");

            // Fallback: access directly if SafeExtensionDataAs returned null
            if(string.IsNullOrEmpty(coinbasetxnHex) && BlockTemplate.Extra.ContainsKey("coinbasetxn"))
            {
                var cbtxn = BlockTemplate.Extra["coinbasetxn"];
                logger.Info(() => $"coinbasetxn type: {cbtxn?.GetType().Name}, value preview: {cbtxn?.ToString()?[..Math.Min(100, cbtxn?.ToString()?.Length ?? 0)]}");

                if(cbtxn is Newtonsoft.Json.Linq.JObject jo)
                    coinbasetxnHex = jo["data"]?.ToString();
                else if(cbtxn is IDictionary<string, object> dict && dict.ContainsKey("data"))
                    coinbasetxnHex = dict["data"]?.ToString();
            }
        }

        if(!string.IsNullOrEmpty(coinbasetxnHex))
        {
            coinbaseInitial = coinbasetxnHex.HexToByteArray();
            coinbaseFinal = Array.Empty<byte>();
            useDaemonCoinbase = true;

            // Use coinbasevalue_miner (pool share) instead of coinbasevalue (total block reward)
            // Fallback to CoinbaseValue for older daemon versions
            var minerReward = BlockTemplate.Extra?.SafeExtensionDataAs<long>("coinbasevalue_miner") ?? 0;
            rewardToPool = new Money(minerReward > 0 ? minerReward : BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);

            logger.Info(() => $"Using daemon coinbasetxn at height {BlockTemplate.Height} ({coinbaseInitial.Length} bytes)");
        }
        else
        {
            var extraKeys = BlockTemplate.Extra != null ? string.Join(", ", BlockTemplate.Extra.Keys) : "null";
            logger.Warn(() => $"No coinbasetxn from daemon at height {BlockTemplate.Height} (Extra keys: {extraKeys}) — falling back to constructed coinbase");
            base.BuildCoinbase();
        }
    }

    protected override byte[] SerializeCoinbase(string extraNonce1)
    {
        if(useDaemonCoinbase)
            return coinbaseInitial; // Complete coinbase from daemon, no extranonce needed

        return base.SerializeCoinbase(extraNonce1);
    }

    protected override byte[] SerializeHeader(Span<byte> coinbaseHash)
    {
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());
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
            Nonce = 0 // Kerrigan KawPoW: nNonce=0; height is in separate nHeight field
        };

        return blockHeader.ToBytes();
    }

    protected override byte[] SerializeBlock(byte[] header, byte[] coinbase, ulong nonce, byte[] mixHash)
    {
        var rawTransactionBuffer = BuildRawTransactionBuffer();
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1;

        using var stream = new MemoryStream();

        stream.Write(header, 0, header.Length);          // 80-byte header

        // Kerrigan KawPoW: nHeight (uint32 LE) between header and nNonce64
        var nHeightBytes = BitConverter.GetBytes((uint) BlockTemplate.Height);
        stream.Write(nHeightBytes, 0, 4);

        var nonceBytes = BitConverter.GetBytes(nonce);   // nNonce64 (uint64 LE)
        stream.Write(nonceBytes, 0, 8);

        stream.Write(mixHash, 0, mixHash.Length);        // mix_hash (32 bytes)

        WriteCompactSize(stream, transactionCount);      // varint tx count
        stream.Write(coinbase, 0, coinbase.Length);      // coinbase tx
        stream.Write(rawTransactionBuffer, 0, rawTransactionBuffer.Length); // remaining txs

        return stream.ToArray();
    }

    private static void WriteCompactSize(Stream stream, uint value)
    {
        if(value < 0xFD)
            stream.WriteByte((byte) value);
        else if(value <= 0xFFFF)
        {
            stream.WriteByte(0xFD);
            stream.Write(BitConverter.GetBytes((ushort) value), 0, 2);
        }
        else
        {
            stream.WriteByte(0xFE);
            stream.Write(BitConverter.GetBytes(value), 0, 4);
        }
    }

    // CreateMasternodeOutputs not needed — daemon's coinbasetxn handles all treasury outputs
}
