using System.Globalization;

namespace Miningcore.Blockchain.Equihash;

public class EquihashConstants
{
    public const int TargetPaddingLength = 32;

    /// <summary>
    /// Precision scale factor for BigInteger division (10^18).
    /// Used to preserve decimal precision when dividing large BigInteger values.
    /// The result is divided by this value (as double 1e18) to restore the decimal point.
    /// </summary>
    private const int PrecisionScaleExponent = 18;
    private static readonly System.Numerics.BigInteger PrecisionScale =
        System.Numerics.BigInteger.Pow(10, PrecisionScaleExponent);
    private const double PrecisionScaleDouble = 1e18;

    // ============================================================================
    // POOL SHARE VALIDATION CONSTANTS (Diff1BValue)
    // These are used for validating miner shares, NOT for network difficulty.
    // Each Equihash variant has a specific Diff1 value based on the coin's
    // protocol and S-NOMP standards.
    // ============================================================================

    // Equihash 200,9 (Zcash, Komodo, Buck, etc.) - ASIC-friendly
    // S-NOMP standard for Zcash-family: 0x0007ffff... (same as MaxTarget)
    // This matches the diff1 in coin templates (coins/zcash.json, coins/buck.json)
    // Effort = shareDiff * (MaxTarget/Diff1) / networkDiff = shareDiff / networkDiff when Diff1 == MaxTarget
    public static readonly System.Numerics.BigInteger Equihash200_9_Diff1 =
        System.Numerics.BigInteger.Parse("0007ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", NumberStyles.HexNumber);

    // Equihash 192,7 (Zero, etc.) - GPU-friendly
    // Uses Zero's diff1 from coin template (coins/zero.json).
    // This gives EffortMultiplier ≈ 1.03 (MaxTarget ≈ Diff1).
    // Note: ShareRecorder uses GetEffortMultiplierFromPoolConfig() which reads
    // dynamically from coin templates, so this constant is primarily for unit tests.
    public static readonly System.Numerics.BigInteger Equihash192_7_Diff1 =
        System.Numerics.BigInteger.Parse("0A5FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEB4", NumberStyles.HexNumber);

    // Equihash 144,5 (Bitcoin Gold, Flux, etc.) - GPU-friendly
    // Smaller parameters = faster solving
    public static readonly System.Numerics.BigInteger Equihash144_5_Diff1 =
        System.Numerics.BigInteger.Parse("0AB1EF0000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

    // Equihash 125,4 (Legacy ZelCash, etc.) - GPU-friendly
    // Smallest common Equihash variant
    public static readonly System.Numerics.BigInteger Equihash125_4_Diff1 =
        System.Numerics.BigInteger.Parse("0AB1EF0000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

    // Legacy alias for backward compatibility - points to 192,7 as before
    public static readonly System.Numerics.BigInteger ZCashDiff1b = Equihash192_7_Diff1;

    // ============================================================================
    // PROTOCOL-DEFINED MAXTARGET CONSTANTS
    // These represent the maximum target (minimum difficulty) defined in each
    // blockchain's consensus rules. Used for NETWORK difficulty calculation.
    // Source: Protocol chainparams.cpp consensus.powLimit values
    //
    // Network Difficulty = MaxTarget / CurrentBlockTarget
    // ============================================================================

    // Kerrigan protocol MaxTarget (from kerrigan/src/chainparams.cpp)
    // powLimit for equihash200 = 0x0000020000...
    public static readonly System.Numerics.BigInteger Equihash200_9_MaxTarget =
        System.Numerics.BigInteger.Parse("0000020000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

    // Kerrigan protocol MaxTarget (from kerrigan/src/chainparams.cpp)
    // powLimit for equihash192 = 0x0010000000...
    public static readonly System.Numerics.BigInteger Equihash192_7_MaxTarget =
        System.Numerics.BigInteger.Parse("0010000000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

    // Bitcoin Gold protocol MaxTarget (from BTCGPU/src/chainparams.cpp)
    // powLimit for equihash200 = 0x0000020000...
    public static readonly System.Numerics.BigInteger Equihash144_5_MaxTarget =
        System.Numerics.BigInteger.Parse("0007ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", NumberStyles.HexNumber);

    // ZelCash/Flux 125,4 MaxTarget (legacy variant)
    // Similar to BTG parameters
    public static readonly System.Numerics.BigInteger Equihash125_4_MaxTarget =
        System.Numerics.BigInteger.Parse("0007ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", NumberStyles.HexNumber);

    // Equihash 96,3 MaxTarget (Test/experimental variant)
    // Used for testing - similar parameters to BTG
    public static readonly System.Numerics.BigInteger Equihash96_3_MaxTarget =
        System.Numerics.BigInteger.Parse("0007ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", NumberStyles.HexNumber);

    // Equihash 96,3 Diff1 (Test/experimental variant)
    public static readonly System.Numerics.BigInteger Equihash96_3_Diff1 =
        System.Numerics.BigInteger.Parse("0AB1EF0000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

    // Effort multiplier for Equihash 96,3 (Test variant)
    public static readonly double EffortMultiplier_96_3 = CalculateEffortMultiplier(Equihash96_3_MaxTarget, Equihash96_3_Diff1);

    // ============================================================================
    // SOL/DIFF RATIOS (Decoupled from Bitcoin's 2^32)
    // These constants define how many solutions correspond to difficulty 1.
    // They are used for hashrate calculation ONLY, not share validation.
    //
    // IMPORTANT: Equihash does NOT use Bitcoin's 2^32 relationship!
    // See docs/EQUIHASH-DECOUPLING-PLAN.md for full technical analysis.
    // ============================================================================

    /// <summary>
    /// Equihash 200,9 Sol/diff ratio (Zcash, Komodo, etc.)
    /// Difficulty 1 = 8192 solutions (2^13)
    /// S-NOMP standard ratio for all Equihash variants using S-NOMP diff1.
    /// </summary>
    public const double SolPerDiff_200_9 = 8192.0;

    /// <summary>
    /// Equihash 192,7 Sol/diff ratio (Zero, etc.)
    /// Difficulty 1 = 8192 solutions (2^13)
    ///
    /// Uses S-NOMP standard diff1 (0x0007ffff...) which has 28 leading zero bits.
    /// Hashes per diff1 share = 2^256 / diff1 = 2^256 / 2^243 = 2^13 = 8192
    /// Same as Zcash 200,9 because both use the same S-NOMP standard diff1.
    /// </summary>
    public const double SolPerDiff_192_7 = 8192.0;

    /// <summary>
    /// Equihash 144,5 Sol/diff ratio (Bitcoin Gold, Flux, etc.)
    /// Difficulty 1 = 32 solutions (2^5)
    /// </summary>
    public const double SolPerDiff_144_5 = 32.0;

    /// <summary>
    /// Equihash 125,4 Sol/diff ratio (Legacy ZelCash, etc.)
    /// Difficulty 1 = 8 solutions (2^3)
    /// </summary>
    public const double SolPerDiff_125_4 = 8.0;

    // ============================================================================
    // EFFORT MULTIPLIERS (ShareDiff to NetworkDiff normalization)
    // ============================================================================
    // Effort = SUM(shareDiff * effortMultiplier / networkDiff)
    //
    // ShareDiff uses Diff1BValue for scaling, NetworkDiff uses MaxTarget.
    // These multipliers normalize the two scales so effort ≈ 1.0 at expected work.
    //
    // Formula: EffortMultiplier = MaxTarget / Diff1BValue
    //
    // Pre-calculated ratios (using BigInteger arithmetic at class init):
    // - Zcash 200,9: MaxTarget/Diff1 = 0x0007ffff.../0x0007ffff... = 1.0 (Diff1 == MaxTarget)
    // - Zero 192,7:  MaxTarget/Diff1 = 0x0AB1Efff.../0x0A5Fffff... ≈ 1.03 (MaxTarget ≈ Diff1)
    // - BTG 144,5:   MaxTarget/Diff1 = 0x0007ffff.../0x0AB1EF00... ≈ 0.00291
    // - ZelCash 125,4: Same as BTG ≈ 0.00291
    //
    // See docs/EQUIHASH-DECOUPLING-PLAN.md for derivation.
    // ============================================================================

    /// <summary>
    /// Effort multiplier for Equihash 200,9 (Zcash, Komodo, etc.)
    /// Normalizes share difficulty to network difficulty scale.
    /// </summary>
    public static readonly double EffortMultiplier_200_9 = CalculateEffortMultiplier(Equihash200_9_MaxTarget, Equihash200_9_Diff1);

    /// <summary>
    /// Effort multiplier for Equihash 192,7 (Zero, etc.)
    /// Hardcoded to 0.8192 based on S-NOMP algorithm normalization:
    /// - S-NOMP uses worker_diff × 8192 × 0.25 for 192,7 (vs × 8192 × 1.0 for 200,9)
    /// - The 0.25 factor (2048/8192) accounts for 192,7's 2048 solutions
    /// - Empirically validated over 2825 blocks: without this, average effort = 1.26
    /// Note: CalculateEffortMultiplier gives ~1.03, but S-NOMP normalization requires ~0.8192
    /// </summary>
    public const double EffortMultiplier_192_7 = 0.8192;

    /// <summary>
    /// Effort multiplier for Equihash 144,5 (Bitcoin Gold, Flux, etc.)
    /// </summary>
    public static readonly double EffortMultiplier_144_5 = CalculateEffortMultiplier(Equihash144_5_MaxTarget, Equihash144_5_Diff1);

    /// <summary>
    /// Effort multiplier for Equihash 125,4 (Legacy ZelCash, etc.)
    /// </summary>
    public static readonly double EffortMultiplier_125_4 = CalculateEffortMultiplier(Equihash125_4_MaxTarget, Equihash125_4_Diff1);

    /// <summary>
    /// Calculate effort multiplier from MaxTarget and Diff1BValue.
    /// EffortMultiplier = MaxTarget / Diff1BValue
    /// </summary>
    private static double CalculateEffortMultiplier(System.Numerics.BigInteger maxTarget, System.Numerics.BigInteger diff1)
    {
        if(diff1 == 0)
            return 1.0; // Safe default

        // Use BigInteger division with scaling for precision.
        // Multiply maxTarget by PrecisionScale (10^18) before dividing to preserve decimal places,
        // then divide result by PrecisionScaleDouble to restore the decimal point.
        var scaledResult = (maxTarget * PrecisionScale) / diff1;
        return (double)scaledResult / PrecisionScaleDouble;
    }
}

public class VeruscoinConstants
{
    public const int SolutionSlice = 6;
    public const string HashVersion2b2 = "2b2";
    public const string HashVersion2b1 = "2b1";
    public const string HashVersion2b = "2b";
    public const string HashVersion2 = "2";
}

public enum ZOperationStatus
{
    Queued,
    Executing,
    Success,
    Cancelled,
    Failed
}

public static class EquihashCommands
{
    public const string ZGetBalance = "z_getbalance";
    public const string ZGetTotalBalance = "z_gettotalbalance";
    public const string ZGetListAddresses = "z_listaddresses";
    public const string ZValidateAddress = "z_validateaddress";
    public const string ZShieldCoinbase = "z_shieldcoinbase";
    
    /// <summary>
    /// Some projects like Veruscoin does not require shielding before being able to spend coins.
    /// They can also sends coins from a t-address to t-addresses and z-addresses
    /// Returns an operation-id. You use the operationid value with z_getoperationstatus and
    /// z_getoperationresult to obtain the result of sending funds, which if successful, will be a txid.
    /// </summary>
    public const string SendCurrency = "sendcurrency";

    /// <summary>
    /// Returns an operationid. You use the operationid value with z_getoperationstatus and
    /// z_getoperationresult to obtain the result of sending funds, which if successful, will be a txid.
    /// </summary>
    public const string ZSendMany = "z_sendmany";

    public const string ZGetOperationStatus = "z_getoperationstatus";
    public const string ZGetOperationResult = "z_getoperationresult";
}

public static class VeruscoinCommands
{
    public const string SubmitMergedBlock = "submitmergedblock";
}
