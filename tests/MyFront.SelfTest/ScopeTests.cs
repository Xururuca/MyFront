using MyFront.Core.Accounts;
using MyFront.Core.Quick;

namespace MyFront.SelfTest;

/// <summary>Offline: which databases a connection shows, and which quick queries are on its toolbar.</summary>
internal static class ScopeTests
{
    public static void Offline(TestRunner t)
    {
        t.Check("scope: a connection with a database shows only that one (any case); without one, all", () =>
        {
            var all = new[] { "information_schema", "mf_test", "shop" };
            Assert.Equal("information_schema,mf_test,shop", string.Join(",", all.Where(new Account().ShowsDatabase)), "no database");
            Assert.Equal("information_schema,mf_test,shop", string.Join(",", all.Where(new Account { DefaultDatabase = "" }.ShowsDatabase)), "empty");
            Assert.Equal("mf_test", string.Join(",", all.Where(new Account { DefaultDatabase = "mf_test" }.ShowsDatabase)), "mf_test");
            Assert.Equal("mf_test", string.Join(",", all.Where(new Account { DefaultDatabase = "MF_Test" }.ShowsDatabase)), "case");
        });

        t.Check("scope: a quick query is on its own connection's toolbar; one without a connection on every toolbar; none without a connection", () =>
        {
            var mine = new QuickQuery { AccountId = "a" };
            var every = new QuickQuery();
            Assert.True(mine.ShowsOn("a") && !mine.ShowsOn("b") && !mine.ShowsOn(null), "bound to a");
            Assert.True(every.ShowsOn("a") && every.ShowsOn("b") && !every.ShowsOn(null), "every connection");
            var other = new QuickQuery { AccountId = "b" };
            Assert.True(mine.SharesToolbarWith(new QuickQuery { AccountId = "a" }) && mine.SharesToolbarWith(every) && every.SharesToolbarWith(other), "same toolbar");
            Assert.True(!mine.SharesToolbarWith(other), "a and b never show together, so both may have Ctrl+1");
        });
    }
}
