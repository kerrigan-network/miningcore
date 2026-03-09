using System.Globalization;
using System.Numerics;
using Miningcore.Util;

namespace Miningcore.Blockchain.Progpow;

public class ProgpowConstants
{
    public const int EpochLength = 7500;
    public static BigInteger BigMaxValue = BigInteger.Pow(2, 256);
    public static readonly BigInteger Diff1B = BigInteger.Parse("00000000ff000000000000000000000000000000000000000000000000000000", NumberStyles.AllowHexSpecifier, null);
    public static readonly BigInteger Diff1 = BigInteger.Parse("00000000ff000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public const int ExtranoncePlaceHolderLength = 2;
    public static double Multiplier = (double) new BigRational(BigMaxValue, Diff1);
}