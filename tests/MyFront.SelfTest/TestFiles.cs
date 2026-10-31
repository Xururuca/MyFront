namespace MyFront.SelfTest;

/// <summary>
/// After a run, removes the files the tests made: the export / import files under tests\samples\out (Excel and CSV of up
/// to 1.2M rows) and the temp folder (test data folder with its accounts and history, SQL files, keys). The screenshots
/// and logs stay; each run overwrites them. MYFRONT_TEST_KEEP_FILES=1 keeps everything for a look.
/// </summary>
internal static class TestFiles
{
    public static void Cleanup()
    {
        if (Environment.GetEnvironmentVariable("MYFRONT_TEST_KEEP_FILES") == "1")
        {
            Console.WriteLine("\n(test files kept: MYFRONT_TEST_KEEP_FILES=1)");
            return;
        }
        var outDir = Path.Combine(TestDb.RepoRoot, "tests", "samples", "out");
        long bytes = 0;
        int inUse = 0;
        foreach (var dir in new[] { Path.Combine(outDir, "exports"), Path.Combine(outDir, "imports"), Path.Combine(Path.GetTempPath(), "MyFront.SelfTest") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    bytes += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { inUse++; }
            }
            try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Console.WriteLine($"\n(test files removed: {bytes / 1048576.0:N1} MB{(inUse > 0 ? $", {inUse} still in use" : "")}; screenshots kept in tests\\samples\\out)");
    }
}
