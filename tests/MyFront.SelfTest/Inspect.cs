using System.Diagnostics;
using MiniExcelLibs;

namespace MyFront.SelfTest;

/// <summary>Mode "inspect": sheets, headers, value types and row counts of Excel files (no import).</summary>
internal static class Inspect
{
    public static void Run(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            Console.WriteLine($"== {Path.GetFileName(path)}  ({new FileInfo(path).Length / 1024.0:0.0} KB)");
            foreach (var sheet in MiniExcel.GetSheetNames(path))
            {
                var sw = Stopwatch.StartNew();
                var rows = MiniExcel.Query(path, useHeaderRow: false, sheetName: sheet).Cast<IDictionary<string, object?>>();
                int n = 0;
                List<string>? header = null;
                var types = new Dictionary<string, HashSet<string>>();
                var samples = new List<IDictionary<string, object?>>();
                foreach (var r in rows)
                {
                    n++;
                    if (header is null) { header = r.Select(kv => $"{kv.Key}={kv.Value}").ToList(); continue; }
                    if (samples.Count < 2) samples.Add(r);
                    foreach (var kv in r)
                    {
                        if (!types.TryGetValue(kv.Key, out var set)) types[kv.Key] = set = new HashSet<string>();
                        if (set.Count < 4) set.Add(kv.Value?.GetType().Name ?? "null");
                    }
                }
                Console.WriteLine($"  sheet '{sheet}': {n} rows incl. header, read in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine("  header: " + string.Join(" | ", header ?? new()));
                Console.WriteLine("  types:  " + string.Join(" | ", types.Select(kv => kv.Key + ":" + string.Join("/", kv.Value))));
                foreach (var s in samples)
                    Console.WriteLine("  sample: " + string.Join(" | ", s.Select(kv => kv.Value is null ? "∅" : kv.Value is string str ? '"' + (str.Length > 25 ? str[..25] + "…" : str) + '"' : kv.Value.ToString())));
            }
        }
    }
}
