using System.Security.Cryptography;
using MyFront.Core;

namespace MyFront.UI;

/// <summary>
/// Scintilla.dll / Lexilla.dll are embedded in the exe. On first editor use they are extracted to
/// %LOCALAPPDATA%\MyFront\native\{hash}\ (verified by SHA-256) and Scintilla.NET is pointed there.
/// </summary>
internal static class ScintillaLoader
{
    private static bool _done;
    private static readonly string[] Files = { "Scintilla.dll", "Lexilla.dll" };

    public static void Ensure()
    {
        if (_done) return;
        var asm = typeof(ScintillaLoader).Assembly;
        var blobs = Files.Select(f =>
        {
            using var s = asm.GetManifestResourceStream("MyFront.native." + f)
                          ?? throw new InvalidOperationException($"Embedded {f} missing.");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }).ToArray();

        var hash = Convert.ToHexString(SHA256.HashData(blobs[0].Concat(blobs[1]).ToArray()))[..16];
        var dir = Path.Combine(AppPaths.LocalDir, "native", hash);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < Files.Length; i++)
        {
            var path = Path.Combine(dir, Files[i]);
            if (File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(blobs[i])))
                continue;
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, blobs[i]);
            File.Move(tmp, path, overwrite: true);
        }
        ScintillaNET.ScintillaNativeLibrary.SatelliteDirectory = dir;
        _done = true;
    }
}
