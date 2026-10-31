using MyFront.Core;
using MyFront.SelfTest;

// Usage: MyFront.SelfTest [core|ui|probe ...]   (default: core)
// Uses a temp data folder so the real %APPDATA%\MyFront is never touched.
AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "MyFront.SelfTest", "data");
Directory.CreateDirectory(AppPaths.DataDir);

var mode = args.Length > 0 ? args[0] : "core";
var db = TestDb.Load();
var t = new TestRunner();

switch (mode)
{
    case "probe":
        await Probe.RunAsync(db);
        return 0;
    case "bench":
        await Bench.RunAsync(db);
        return 0;
    case "sshbench":
        await SshBench.RunAsync(db);
        return 0;
    case "inspect":
        Inspect.Run(args.Skip(1));
        return 0;
    case "import":
    {
        using var session = await MyFront.Core.Db.ServerSession.ConnectAsync(db.ToAccount(), db.Password);
        await ImportTests.RunAsync(t, db, session);
        break;
    }
    case "export":
    {
        using var session = await MyFront.Core.Db.ServerSession.ConnectAsync(db.ToAccount(), db.Password);
        await ExportTests.RunAsync(t, db, session);
        break;
    }
    case "ssh":
        await SshTests.RunAsync(t);
        await SshTests.RunLiveAsync(t, db);
        break;
    case "sshlive":
        await SshTests.RunLiveAsync(t, db);
        break;
    case "props":
    {
        EditorPropertyTests.Offline(t);
        using var session = await MyFront.Core.Db.ServerSession.ConnectAsync(db.ToAccount(), db.Password);
        await EditorPropertyTests.PropertiesAsync(t, session, db);
        break;
    }
    case "ddl":
    {
        DdlTests.Offline(t);
        DdlTests.OfflineKeys(t);
        using var session = await MyFront.Core.Db.ServerSession.ConnectAsync(db.ToAccount(), db.Password);
        await DdlTests.LiveAsync(t, session, db);
        break;
    }
    case "core":
        await CoreTests.RunAsync(t, db);
        break;
    case "ui":
        UiTests.Run(t, db);
        break;
    case "uiimport":
        UiTests.Run(t, db, importOnly: true);
        break;
    case "uiquick":
        UiTests.Run(t, db, quickOnly: true);
        break;
    case "uicur": // only the tests of the work in progress (offline first, then the UI part, quietly)
        ConnectionFileTests.Offline(t);
        HiddenTablesTests.Offline(t);
        UiTests.Run(t, db, only: "current");
        break;
    case "quick":
    {
        using var session = await MyFront.Core.Db.ServerSession.ConnectAsync(db.ToAccount(), db.Password);
        await QuickTests.RunAsync(t, db, session);
        break;
    }
    case "all":
    {
        await CoreTests.RunAsync(t, db);
        Console.WriteLine("\n== export ==");
        using (var session = await MyFront.Core.Db.ServerSession.ConnectAsync(db.ToAccount(), db.Password))
            await ExportTests.RunAsync(t, db, session);
        Console.WriteLine("\n== import ==");
        using (var session = await MyFront.Core.Db.ServerSession.ConnectAsync(db.ToAccount(), db.Password))
            await ImportTests.RunAsync(t, db, session);
        Console.WriteLine("\n== quick queries ==");
        using (var session = await MyFront.Core.Db.ServerSession.ConnectAsync(db.ToAccount(), db.Password))
            await QuickTests.RunAsync(t, db, session);
        Console.WriteLine("\n== ui ==");
        UiTests.Run(t, db);
        break;
    }
    default:
        Console.WriteLine("unknown mode " + mode);
        return 2;
}
var exitCode = t.Summary();
TestFiles.Cleanup();
return exitCode;
