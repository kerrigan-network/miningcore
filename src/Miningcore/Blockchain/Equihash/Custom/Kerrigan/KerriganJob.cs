using Miningcore.Blockchain.Equihash.DaemonResponses;
using Miningcore.Extensions;
using NBitcoin;
using NLog;

namespace Miningcore.Blockchain.Equihash.Custom.Kerrigan;

/// <summary>
/// Kerrigan equihash job. Uses the daemon's pre-built coinbasetxn directly.
/// The daemon (via rpcalgoport + pooladdress GBT param) builds the coinbase with
/// all required treasury outputs (AI 40%, MN 20%, dev 15%, founders 5%).
/// Equihash extranonce is in the block header, not coinbase, so this is safe.
/// </summary>
public class KerriganJob : EquihashJob
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    protected override void BuildCoinbase()
    {
        var equihashTemplate = BlockTemplate as EquihashBlockTemplate;

        if(equihashTemplate?.CoinbaseTx?.Data != null)
        {
            // Use daemon's pre-built coinbase (includes pool payout + all treasury outputs)
            coinbaseInitial = equihashTemplate.CoinbaseTx.Data.HexToByteArray();
            coinbaseInitialHash = new byte[32];
            sha256D.Digest(coinbaseInitial, coinbaseInitialHash);

            blockReward = BlockTemplate.CoinbaseValue;
            rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);

            logger.Info(() => $"Using daemon coinbasetxn at height {BlockTemplate.Height} ({coinbaseInitial.Length} bytes)");
        }
        else
        {
            // Fallback to parent class coinbase construction
            logger.Warn(() => $"No coinbasetxn from daemon at height {BlockTemplate.Height} — falling back to stock equihash coinbase");
            base.BuildCoinbase();
        }
    }

    protected override byte[] SerializeBlock(Span<byte> header, Span<byte> coinbase, Span<byte> solution)
    {
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1;
        var rawTransactionBuffer = BuildRawTransactionBuffer();

        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            bs.ReadWrite(header);
            bs.ReadWrite(solution);
            bs.ReadWriteAsVarInt(ref transactionCount);
            bs.ReadWrite(coinbase);
            bs.ReadWrite(rawTransactionBuffer);

            return stream.ToArray();
        }
    }
}
