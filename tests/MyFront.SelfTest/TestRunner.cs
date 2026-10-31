using System.Diagnostics;

namespace MyFront.SelfTest;

/// <summary>Tiny test runner: prints PASS/FAIL with timings, no test framework needed.</summary>
internal sealed class TestRunner
{
    private int _passed, _failed;
    private readonly List<string> _failures = new();

    public async Task Check(string name, Func<Task<string?>> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var note = await body();
            _passed++;
            Console.WriteLine($"PASS  {name}  ({sw.ElapsedMilliseconds} ms){(note is null ? "" : "  " + note)}");
        }
        catch (Exception ex)
        {
            _failed++;
            _failures.Add(name);
            Console.WriteLine($"FAIL  {name}  ({sw.ElapsedMilliseconds} ms)\n      {ex.GetType().Name}: {ex.Message}");
            if (ex is not Exception { Message: var m } || !m.Contains("expected"))
                foreach (var line in (ex.StackTrace ?? "").Split('\n').Take(8)) Console.WriteLine("        " + line.Trim());
        }
    }

    public Task Check(string name, Func<Task> body) => Check(name, async () => { await body(); return null; });

    public Task Check(string name, Action body) => Check(name, () => { body(); return Task.FromResult<string?>(null); });

    public Task Check(string name, Func<string?> body) => Check(name, () => Task.FromResult(body()));

    public int Summary()
    {
        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        foreach (var f in _failures) Console.WriteLine("  failed: " + f);
        return _failed == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected <{expected}>, got <{actual}>");
    }

    public static async Task<Exception> Throws(Func<Task> body, string what)
    {
        try { await body(); }
        catch (Exception ex) { return ex; }
        throw new Exception(what + ": expected an exception");
    }
}
