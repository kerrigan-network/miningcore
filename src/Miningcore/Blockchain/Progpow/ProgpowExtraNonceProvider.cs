namespace Miningcore.Blockchain.Progpow;

public class ProgpowExtraNonceProvider : ExtraNonceProviderBase
{
    public ProgpowExtraNonceProvider(string poolId, byte? clusterInstanceId) : base(poolId, ProgpowConstants.ExtranoncePlaceHolderLength, clusterInstanceId)
    {
    }
}
