using System.Text;
using NBitcoin.DataEncoders;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Progpow.Custom.Kerrigan;

public class KerriganProgpowJob : ProgpowJob
{
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
            Nonce = 0  // Kerrigan KawPoW uses nNonce=0; height is in separate nHeight field
        };

        return blockHeader.ToBytes();
    }

    protected override byte[] SerializeBlock(byte[] header, byte[] coinbase, ulong nonce, byte[] mixHash)
    {
        var rawTransactionBuffer = BuildRawTransactionBuffer();
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1;

        using var stream = new MemoryStream();
        {
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

    protected override Money CreateMasternodeOutputs(NBitcoin.Transaction tx, Money reward)
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
                    if(!string.IsNullOrEmpty(masterNode.Payee))
                    {
                        var payeeDestination = BitcoinUtils.AddressToDestination(masterNode.Payee, network);
                        var payeeReward = masterNode.Amount;
                        tx.Outputs.Add(payeeReward, payeeDestination);
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
