using MyFront.Core.Accounts;
using MyFront.Core.Db;

namespace MyFront.SelfTest;

/// <summary>Offline: tables hidden in the tree — kept per connection and database, saved, exported, in the tree's order.</summary>
internal static class HiddenTablesTests
{
    public static void Offline(TestRunner t)
    {
        t.Check("hidden tables: per database, by exact name; Unhide of the last one leaves nothing; a rename stays hidden; a copy is its own", () =>
        {
            var a = new Account { Name = "app server" };
            a.SetHidden("app", "django_celery_beat_clockedschedule", true);
            a.SetHidden("app", "auth_group", true);
            a.SetHidden("app", "auth_group", true); // twice: once
            a.SetHidden("other", "auth_group", true);
            Assert.True(a.IsHidden("app", "auth_group") && a.IsHidden("other", "auth_group") && !a.IsHidden("app", "Auth_Group")
                        && !a.IsHidden("app", "auth_user") && !a.IsHidden("nope", "auth_group"), "per database, exact name");
            Assert.Equal(2, a.HiddenTables!["app"].Count, "no doubles");

            var copy = a.Clone();
            copy.SetHidden("app", "auth_user", true);
            Assert.True(!a.IsHidden("app", "auth_user"), "the copy has its own list");

            a.RenameHidden("app", "auth_group", "auth_groups");
            a.RenameHidden("app", "auth_user", "x"); // not hidden: nothing
            Assert.True(a.IsHidden("app", "auth_groups") && !a.IsHidden("app", "auth_group") && !a.IsHidden("app", "x"), "renamed");

            a.SetHidden("app", "auth_groups", false);
            a.SetHidden("app", "django_celery_beat_clockedschedule", false);
            a.SetHidden("other", "auth_group", false);
            a.SetHidden("other", "never_hidden", false);
            Assert.True(a.HiddenTables is null, "all unhidden: nothing left");
        });

        t.Check("hidden tables: saved into the connection's saved copy only; in its Export… file and back with Import…", () =>
        {
            var before = AccountStore.Load();
            var path = Path.Combine(Path.GetTempPath(), $"myfront-hidden-{Guid.NewGuid():N}.json");
            try
            {
                var saved = new Account { Name = "hide saved", Host = "db.example" };
                AccountStore.Save(before.Append(saved));
                var open = saved.Clone(); // the open connection's own copy
                open.SetHidden("app", "auth_group", true);
                AccountStore.SaveHiddenTables(open);
                var stored = AccountStore.Load().Single(x => x.Id == saved.Id);
                Assert.True(stored.IsHidden("app", "auth_group") && stored.Name == "hide saved", "saved");

                var notSaved = new Account { Name = "not saved" };
                notSaved.SetHidden("app", "x", true);
                AccountStore.SaveHiddenTables(notSaved);
                Assert.True(AccountStore.Load().All(x => x.Id != notSaved.Id), "a connection that isn't saved isn't added");

                ConnectionFile.Export(path, stored, Array.Empty<MyFront.Core.Quick.QuickQuery>());
                Assert.True(File.ReadAllText(path).Contains("auth_group"), "in the file");
                var accounts = new List<Account>();
                var added = ConnectionFile.Read(path).AddTo(accounts, new List<MyFront.Core.Quick.QuickQuery>());
                Assert.True(added.IsHidden("app", "auth_group"), "imported with it");
            }
            finally
            {
                AccountStore.Save(before);
                File.Delete(path);
            }
        });

        t.Check("hidden tables: a table goes back where the server's list has it — tables then views, by name ignoring case (_ after letters)", () =>
        {
            var names = new[] { "background_task_completed", "Bot_bots", "auth_group", "authtoken_token", "background_task", "auth_user" };
            var list = names.Select(n => new DbObject("app", n, DbObjectType.Table))
                .Append(new DbObject("app", "a_view", DbObjectType.View)).Append(new DbObject("app", "a_proc", DbObjectType.Procedure)).ToList();
            list.Sort(DbObject.Compare);
            var order = string.Join(",", list.Select(o => o.Name));
            Assert.Equal("authtoken_token,auth_group,auth_user,background_task,background_task_completed,Bot_bots,a_view,a_proc", order, "order");
            return order;
        });
    }
}
