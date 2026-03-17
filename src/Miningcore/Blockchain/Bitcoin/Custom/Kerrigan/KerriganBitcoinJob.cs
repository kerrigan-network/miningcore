using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Extensions;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.Custom.Kerrigan;

/// <summary>
/// X11 stratum puts the extranonce in the coinbase, so we cannot use the daemon's
/// pre-built coinbasetxn here. Instead we build the coinbase ourselves with:
/// - pool output at index 0 (Dash-fork convention)
/// - raw script bytes for masternode outputs (avoids P2PKH/P2SH mismatch)
/// </summary>
public class KerriganBitcoinJob : BitcoinJob
{
    protected override Transaction CreateOutputTransaction()
    {
        rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
        var tx = Transaction.Create(network);

        // Pool output at index 0
        tx.Outputs.Add(rewardToPool, poolAddressDestination);

        if(coin.HasMasterNodes)
        {
            var deducted = CreateMasternodeOutputs(tx, rewardToPool);
            tx.Outputs[0].Value = deducted;
            rewardToPool = deducted;
        }

        return tx;
    }

    protected override Money CreateMasternodeOutputs(Transaction tx, Money reward)
    {
        if(masterNodeParameters.Masternode != null)
        {
            Masternode[] masternodes;

            if(masterNodeParameters.Masternode.Type == JTokenType.Array)
                masternodes = masterNodeParameters.Masternode.ToObject<Masternode[]>();
            else
                masternodes = new[] { masterNodeParameters.Masternode.ToObject<Masternode>() };

            if(masternodes != null)
            {
                foreach(var masterNode in masternodes)
                {
                    if(!string.IsNullOrEmpty(masterNode.Script))
                    {
                        var payeeScript = new Script(masterNode.Script.HexToByteArray());
                        var payeeReward = masterNode.Amount;
                        tx.Outputs.Add(payeeReward, payeeScript);
                        reward -= payeeReward;
                    }
                }
            }
        }

        if(masterNodeParameters.SuperBlocks is { Length: > 0 })
        {
            foreach(var superBlock in masterNodeParameters.SuperBlocks)
            {
                var payeeAddress = BitcoinUtils.AddressToDestination(superBlock.Payee, network);
                var payeeReward = superBlock.Amount;
                tx.Outputs.Add(payeeReward, payeeAddress);
                reward -= payeeReward;
            }
        }

        return reward;
    }
}
