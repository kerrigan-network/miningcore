using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Extensions;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NLog;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.Bitcoin.Custom.Kerrigan;

/// <summary>
/// Kerrigan X11 job. Constructs coinbase with raw P2SH scripts from masternode array
/// and pool output at index 0 (Dash convention). Uses raw scripts to match
/// CMNPaymentsProcessor validation exactly.
/// </summary>
public class KerriganBitcoinJob : BitcoinJob
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    protected override Money CreateMasternodeOutputs(Transaction tx, Money reward)
    {
        if(masterNodeParameters?.Masternode != null)
        {
            Masternode[] masternodes;

            if(masterNodeParameters.Masternode.Type == JTokenType.Array)
                masternodes = masterNodeParameters.Masternode.ToObject<Masternode[]>();
            else
                masternodes = new[] { masterNodeParameters.Masternode.ToObject<Masternode>() };

            if(masternodes != null)
            {
                foreach(var mn in masternodes)
                {
                    if(!string.IsNullOrEmpty(mn.Script))
                    {
                        // Use raw script from daemon directly (avoids P2SH/P2PKH address conversion mismatch)
                        var scriptBytes = mn.Script.HexToByteArray();
                        var script = new Script(scriptBytes);
                        tx.Outputs.Add(mn.Amount, script);
                        reward -= mn.Amount;
                    }
                }
            }
        }

        if(masterNodeParameters?.SuperBlocks is { Length: > 0 })
        {
            foreach(var sb in masterNodeParameters.SuperBlocks)
            {
                if(!string.IsNullOrEmpty(sb.Payee))
                {
                    var payeeAddress = BitcoinUtils.AddressToDestination(sb.Payee, network);
                    tx.Outputs.Add(sb.Amount, payeeAddress);
                    reward -= sb.Amount;
                }
            }
        }

        return reward;
    }

    protected override Transaction CreateOutputTransaction()
    {
        var minerReward = BlockTemplate.Extra?.SafeExtensionDataAs<long>("coinbasevalue_miner") ?? 0;
        rewardToPool = new Money(minerReward > 0 ? minerReward : BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
        var tx = Transaction.Create(network);

        // Dash-fork: pool output MUST be at index 0
        tx.Outputs.Add(rewardToPool, poolAddressDestination);

        if(coin.HasMasterNodes)
        {
            // MN subtraction must use total CoinbaseValue, not coinbasevalue_miner.
            // coinbasevalue_miner is already the pool share after MN deductions.
            var mnBasis = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
            var remainder = CreateMasternodeOutputs(tx, mnBasis);

            if(minerReward <= 0)
                rewardToPool = remainder;
        }

        tx.Outputs[0].Value = rewardToPool;

        return tx;
    }
}
