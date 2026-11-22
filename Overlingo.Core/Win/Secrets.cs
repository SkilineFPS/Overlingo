using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Overlingo.Core;

public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedB64);
}

/// <summary>Ochrana klíčů přes Windows DPAPI (vázáno na uživatelský účet), P/Invoke bez NuGet.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB pIn, string? desc, IntPtr entropy, IntPtr res, IntPtr prompt, int flags, out DATA_BLOB pOut);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DATA_BLOB pIn, IntPtr desc, IntPtr entropy, IntPtr res, IntPtr prompt, int flags, out DATA_BLOB pOut);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
    const int UI_FORBIDDEN = 1;

    static byte[] Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var inb = new DATA_BLOB { cbData = data.Length, pbData = h.AddrOfPinnedObject() };
            bool ok = protect ? CryptProtectData(ref inb, "Overlingo", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, out var o)
                              : CryptUnprotectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, out o);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var res = new byte[o.cbData];
            Marshal.Copy(o.pbData, res, 0, o.cbData);
            LocalFree(o.pbData);
            return res;
        }
        finally { h.Free(); }
    }

    public string Protect(string plain) => Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plain), true));
    public string Unprotect(string b64) => Encoding.UTF8.GetString(Run(Convert.FromBase64String(b64), false));
}

/// <summary>Jen pro testy / nevybavené platformy – NENÍ šifrování.</summary>
public sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plain) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
    public string Unprotect(string b64) => Encoding.UTF8.GetString(Convert.FromBase64String(b64));
}
