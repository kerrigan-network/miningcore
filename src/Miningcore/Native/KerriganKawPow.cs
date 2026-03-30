using System.Runtime.InteropServices;

// ReSharper disable FieldCanBeMadeReadOnly.Local
// ReSharper disable MemberCanBePrivate.Local
// ReSharper disable InconsistentNaming

namespace Miningcore.Native;

public static unsafe class KerriganKawPow
{
    [DllImport("libkerrigankawpow", EntryPoint = "ethash_create_epoch_context", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr CreateContext(int epoch_number);

    [DllImport("libkerrigankawpow", EntryPoint = "ethash_destroy_epoch_context", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DestroyContext(IntPtr context);

    [DllImport("libkerrigankawpow", EntryPoint = "hash", CallingConvention = CallingConvention.Cdecl)]
    public static extern KawPow.Ethash_result hash(IntPtr context, int block_number, ref KawPow.Ethash_hash256 header_hash, ulong nonce);

    [DllImport("libkerrigankawpow", EntryPoint = "hashext", CallingConvention = CallingConvention.Cdecl)]
    public static extern KawPow.Ethash_result hashext(IntPtr context, int block_number, ref KawPow.Ethash_hash256 header_hash, ulong nonce, ref KawPow.Ethash_hash256 mix_hash, ref KawPow.Ethash_hash256 boundary1, ref KawPow.Ethash_hash256 boundary2, out int retcode);

    [DllImport("libkerrigankawpow", EntryPoint = "calculate_epoch_seed", CallingConvention = CallingConvention.Cdecl)]
    public static extern KawPow.Ethash_hash256 calculate_epoch_seed(int epoch_number);
}
