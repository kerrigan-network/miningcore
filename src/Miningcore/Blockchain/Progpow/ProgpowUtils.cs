using System.Globalization;
using System.Numerics;

namespace Miningcore.Blockchain.Progpow;

public static class ProgpowUtils
{
    public static string EncodeTarget(double difficulty)
    {
        difficulty = 1.0 / difficulty;

        BigInteger NewTarget;
        BigInteger DecimalDiff;
        BigInteger DecimalTarget;

        NewTarget = BigInteger.Multiply(ProgpowConstants.Diff1B, new BigInteger(difficulty));

        string StringDiff = difficulty.ToString(CultureInfo.InvariantCulture);
        int DecimalOffset = StringDiff.IndexOf(".");
        if(DecimalOffset > -1)
        {
            int Precision = (StringDiff.Length - 1) - DecimalOffset;
            DecimalDiff = BigInteger.Parse(StringDiff.Substring(DecimalOffset + 1));
            DecimalTarget = BigInteger.Multiply(ProgpowConstants.Diff1B, DecimalDiff);

            string s = DecimalTarget.ToString();
            s = s.Substring(0, s.Length - Precision);

            DecimalTarget = BigInteger.Parse(s);
            NewTarget += DecimalTarget;
        }

        return string.Format("{0:x64}", NewTarget);
    }
}
