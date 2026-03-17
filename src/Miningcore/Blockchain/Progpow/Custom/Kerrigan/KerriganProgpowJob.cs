using System.Text;
using NBitcoin.DataEncoders;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Extensions;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Progpow.Custom.Kerrigan;

public class KerriganProgpowJob : ProgpowJob
{
    private bool useDaemonCoinbase;

    protected override void BuildCoinbase()
    {
        // If the daemon provided a pre-built coinbase (via pooladdress + rpcalgoport),
        // use it directly. KawPoW puts the extranonce in the block header (nonce64),
        // not the coinbase, so no injection is needed.
        if(BlockTemplate.CoinbaseTx != null && !string.IsNullOrEmpty(BlockTemplate.CoinbaseTx.Data))
        {
            useDaemonCoinbase = true;
            coinbaseInitial = BlockTemplate.CoinbaseTx.Data.HexToByteArray();
            coinbaseFinal = Array.Empty<byte>();
            rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
            return;
        }

        // Fallback: build coinbase ourselves (pre-masternode or missing pooladdress)
        base.BuildCoinbase();
    }

    protected override byte[] SerializeCoinbase(string extraNonce1)
    {
        if(useDaemonCoinbase)
            return coinbaseInitial;

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
            Nonce = 0
        };

        return blockHeader.ToBytes();
    }

    protected override byte[] SerializeBlock(byte[] header, byte[] coinbase, ulong nonce, byte[] mixHash)
    {
        var rawTransactionBuffer = BuildRawTransactionBuffer();
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1;

        using var stream = new MemoryStream();
        {
            stream.Write(header, 0, header.Length);

            // nHeight (uint32 LE) between header and nNonce64
            var nHeightBytes = BitConverter.GetBytes((uint) BlockTemplate.Height);
            stream.Write(nHeightBytes, 0, 4);

            var nonceBytes = BitConverter.GetBytes(nonce);
            stream.Write(nonceBytes, 0, 8);

            stream.Write(mixHash, 0, mixHash.Length);

            WriteCompactSize(stream, transactionCount);
            stream.Write(coinbase, 0, coinbase.Length);
            stream.Write(rawTransactionBuffer, 0, rawTransactionBuffer.Length);

            return stream.ToArray();
        }
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

    protected override Transaction CreateOutputTransaction()
    {
        rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
        var tx = Transaction.Create(network);

        // Pool output at index 0 (Dash-fork convention)
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
