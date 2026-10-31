using System.Runtime.InteropServices;
using System.Text;

namespace MyFront.Core.Accounts;

/// <summary>
/// Windows DPAPI (CurrentUser scope) via crypt32, so no extra package is needed.
/// Encrypted values can only be decrypted by the same Windows user on the same machine.
/// </summary>
public static class Dpapi
{
    private const string Prefix = "dpapi1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyFront/secret/v1");
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        try { return Prefix + Convert.ToBase64String(Transform(data, protect: true)); }
        finally { Array.Clear(data); }
    }

    public static string Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Unknown secret format.");
        var plain = Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
        try { return Encoding.UTF8.GetString(plain); }
        finally { Array.Clear(plain); }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var entropyBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            entropyBlob.cbData = Entropy.Length;
            entropyBlob.pbData = Marshal.AllocHGlobal(Entropy.Length);
            Marshal.Copy(Entropy, 0, entropyBlob.pbData, Entropy.Length);

            bool ok = protect
                ? CryptProtectData(ref inBlob, "MyFront", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero)
            {
                // wipe the plaintext copy before freeing it
                Marshal.Copy(new byte[inBlob.cbData], 0, inBlob.pbData, inBlob.cbData);
                Marshal.FreeHGlobal(inBlob.pbData);
            }
            if (entropyBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
