using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Extensions;
using NBitcoin;
using Newtonsoft.Json.Linq;
using MasterNodeExtra = Miningcore.Blockchain.Bitcoin.DaemonResponses.MasterNodeBlockTemplateExtra;
using Masternode = Miningcore.Blockchain.Bitcoin.DaemonResponses.Masternode;

namespace Miningcore.Blockchain.Equihash.Custom.Kerrigan;

public class KerriganEquihashJob : EquihashJob
{
    protected override void BuildCoinbase()
    {
        // Equihash puts the extranonce in the block header, not the coinbase,
        // so a pre-built coinbase from the daemon works without modification.
        if(BlockTemplate.CoinbaseTx != null && !string.IsNullOrEmpty(BlockTemplate.CoinbaseTx.Data))
        {
            coinbaseInitial = BlockTemplate.CoinbaseTx.Data.HexToByteArray();

            coinbaseInitialHash = new byte[32];
            sha256D.Digest(coinbaseInitial, coinbaseInitialHash);

            // Equihash RPC ports return full block value (all outputs) as coinbasevalue,
            // not just the miner's share. Use Subsidy.Miner for the correct pool reward.
            if(BlockTemplate.Subsidy != null)
                rewardToPool = new Money((long)(BlockTemplate.Subsidy.Miner * BitcoinConstants.SatoshisPerBitcoin));
            else
                rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);

            return;
        }

        base.BuildCoinbase();
    }

    protected override Transaction CreateOutputTransaction()
    {
        var txNetwork = Network.GetNetwork(networkParams.CoinbaseTxNetwork);
        var tx = Transaction.Create(txNetwork);
        tx.Version = txVersion;

        if(!hasMasterNodes && isOverwinterActive)
        {
            overwinterField.SetValue(tx, true);
            versionGroupField.SetValue(tx, txVersionGroupId);
        }

        // Pool output at index 0 (Dash-fork convention)
        if(BlockTemplate.Subsidy != null)
            rewardToPool = new Money((long)(BlockTemplate.Subsidy.Miner * BitcoinConstants.SatoshisPerBitcoin) + (long)rewardFees);
        else
            rewardToPool = new Money(blockReward + rewardFees, MoneyUnit.Satoshi);

        tx.Outputs.Add(rewardToPool, poolAddressDestination);

        // Masternode outputs after pool
        if(hasMasterNodes && masterNodeParameters != null)
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
                            rewardToPool -= payeeReward;
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
                    rewardToPool -= payeeReward;
                }
            }

            tx.Outputs[0].Value = rewardToPool;
        }

        tx.Inputs.Add(TxIn.CreateCoinbase((int) BlockTemplate.Height));
        return tx;
    }
}
