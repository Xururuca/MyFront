using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace MyFront.Core.Export;

/// <summary>
/// MiniExcel has no option for a bold header, so after it finishes we replace xl/styles.xml.
/// MiniExcel writes styles.xml after the (big) sheet parts, so only the few small parts behind it
/// (workbook, rels, content types) are moved; sheet data is never read or rewritten.
/// The result is a normal, gap-free zip (Excel rejects orphaned entries).
/// </summary>
internal static class XlsxStylePatcher
{
    private const uint EocdSig = 0x06054b50, CentralSig = 0x02014b50, LocalSig = 0x04034b50, Zip64LocatorSig = 0x07064b50;
    private const string StylesName = "xl/styles.xml";
    private const int MaxTailBytes = 16 * 1024 * 1024;
    private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <returns>True when the header style was made bold; false (file untouched) if the layout was unexpected.</returns>
    public static bool MakeHeaderBold(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long len = fs.Length;
        int tailLen = (int)Math.Min(len, 22 + 0xFFFF);
        var end = new byte[tailLen];
        fs.Position = len - tailLen;
        fs.ReadExactly(end);
        int eocd = -1;
        for (int i = tailLen - 22; i >= 0; i--)
            if (U32(end, i) == EocdSig) { eocd = i; break; }
        if (eocd < 0) return false;
        if (eocd >= 20 && U32(end, eocd - 20) == Zip64LocatorSig) return false; // zip64 (>4 GB): leave as written

        int entries = U16(end, eocd + 10);
        uint cdSize = U32(end, eocd + 12);
        uint cdOffset = U32(end, eocd + 16);
        var eocdBytes = end.AsSpan(eocd).ToArray();

        var cd = new byte[cdSize];
        fs.Position = cdOffset;
        fs.ReadExactly(cd);

        var records = new List<int>(entries);
        int rec = -1, pos = 0;
        for (int n = 0; n < entries; n++)
        {
            if (pos + 46 > cd.Length || U32(cd, pos) != CentralSig) return false;
            records.Add(pos);
            int nameLen = U16(cd, pos + 28), extraLen = U16(cd, pos + 30), commentLen = U16(cd, pos + 32);
            if (U32(cd, pos + 20) == uint.MaxValue || U32(cd, pos + 42) == uint.MaxValue) return false; // zip64 fields
            if (Encoding.UTF8.GetString(cd, pos + 46, nameLen) == StylesName) rec = pos;
            pos += 46 + nameLen + extraLen + commentLen;
        }
        if (rec < 0) return false;

        int flags = U16(cd, rec + 8), method = U16(cd, rec + 10);
        uint cSize = U32(cd, rec + 20), localOffset = U32(cd, rec + 42);
        if ((flags & 0x0008) != 0 || method is not (0 or 8)) return false; // data descriptor / unknown method

        // the existing entry and everything behind it up to the central directory
        var local = new byte[30];
        fs.Position = localOffset;
        fs.ReadExactly(local);
        if (U32(local, 0) != LocalSig) return false;
        long dataStart = localOffset + 30 + U16(local, 26) + U16(local, 28);
        long oldEnd = dataStart + cSize;
        long tailBytes = cdOffset - oldEnd;
        if (tailBytes < 0 || tailBytes > MaxTailBytes) return false;

        var data = new byte[cSize];
        fs.Position = dataStart;
        fs.ReadExactly(data);
        var tail = new byte[tailBytes];
        fs.ReadExactly(tail);

        byte[] xmlBytes = method == 8 ? Inflate(data) : data;
        bool bom = xmlBytes.Length >= 3 && xmlBytes[0] == 0xEF && xmlBytes[1] == 0xBB && xmlBytes[2] == 0xBF;
        var xml = Encoding.UTF8.GetString(xmlBytes, bom ? 3 : 0, xmlBytes.Length - (bom ? 3 : 0));
        var patched = AddBoldHeader(xml);
        if (patched is null) return false;
        var newXml = (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(patched)).ToArray();
        var newData = method == 8 ? Deflate(newXml) : newXml;
        uint crc = Crc32(newXml);

        // same local header, new sizes / CRC
        var header = new byte[dataStart - localOffset];
        fs.Position = localOffset;
        fs.ReadExactly(header);
        W32(header, 14, crc);
        W32(header, 18, (uint)newData.Length);
        W32(header, 22, (uint)newXml.Length);

        long shift = (header.Length + newData.Length) - (oldEnd - localOffset);
        fs.Position = localOffset;
        fs.Write(header);
        fs.Write(newData);
        fs.Write(tail);

        W32(cd, rec + 16, crc);
        W32(cd, rec + 20, (uint)newData.Length);
        W32(cd, rec + 24, (uint)newXml.Length);
        foreach (var r in records)
        {
            uint off = U32(cd, r + 42);
            if (off > localOffset) W32(cd, r + 42, (uint)(off + shift));
        }
        long newCdOffset = fs.Position;
        if (newCdOffset > uint.MaxValue) throw new IOException("Zip grew past 4 GB while patching styles.");
        fs.Write(cd);
        W32(eocdBytes, 16, (uint)newCdOffset);
        fs.Write(eocdBytes);
        fs.SetLength(fs.Position);
        return true;
    }

    /// <summary>Adds a bold font and points the header cell style (index 1, as MiniExcel writes it) at it.</summary>
    internal static string? AddBoldHeader(string xml)
    {
        var doc = XDocument.Parse(xml);
        var fonts = doc.Root?.Element(X + "fonts");
        var xfs = doc.Root?.Element(X + "cellXfs")?.Elements(X + "xf").ToList();
        if (fonts is null || xfs is null || xfs.Count < 2) return null;
        var baseFont = fonts.Elements(X + "font").FirstOrDefault();
        var bold = baseFont is null ? new XElement(X + "font") : new XElement(baseFont);
        bold.AddFirst(new XElement(X + "b"));
        fonts.Add(bold);
        int boldId = fonts.Elements(X + "font").Count() - 1;
        fonts.SetAttributeValue("count", boldId + 1);
        xfs[1].SetAttributeValue("fontId", boldId);
        xfs[1].SetAttributeValue("applyFont", 1);
        var decl = doc.Declaration?.ToString() ?? "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
        return decl + doc.Root!.ToString(SaveOptions.DisableFormatting);
    }

    private static byte[] Inflate(byte[] data)
    {
        using var inflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        inflate.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);
        return ms.ToArray();
    }

    private static int U16(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));
    private static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));
    private static void W32(byte[] b, int i, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i), v);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
