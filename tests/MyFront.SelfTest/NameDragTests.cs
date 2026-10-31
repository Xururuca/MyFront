using MyFront.Core.Db;
using MyFront.UI;

namespace MyFront.SelfTest;

/// <summary>Names dragged from the tree into the SQL editor: quoted only when they need it.</summary>
internal static class NameDragTests
{
    public static void Offline(TestRunner t)
    {
        t.Check("names: backticks only when needed — other characters, a leading digit, a reserved word", () =>
        {
            foreach (var plain in new[] { "prices", "Area_Code", "FAO", "$tmp", "t1", "Year", "status", "date", "value", "name", "_x" })
                Assert.Equal(plain, Ident.QuoteIfNeeded(plain), "plain: " + plain);
            var quoted = new (string Name, string Sql)[]
            {
                ("order", "`order`"), ("ORDER", "`ORDER`"), ("Key", "`Key`"), ("rank", "`rank`"), ("values", "`values`"), ("select", "`select`"),
                ("1abc", "`1abc`"), ("ülke", "`ülke`"), ("İl Adı", "`İl Adı`"), ("my-table", "`my-table`"), ("a`b", "`a``b`"), ("", "``"),
            };
            foreach (var (name, sql) in quoted)
                Assert.Equal(sql, Ident.QuoteIfNeeded(name), "quoted: " + name);
        });

        t.Check("names: what a dragged tree node inserts — database, table, view, field; keys, routines and the server aren't dragged", () =>
        {
            ServerSession s = null!; // not used by the name
            Assert.Equal("satis_db", NavigatorTree.DragText(new NodeInfo(NodeKind.Database, s, "satis_db")), "database");
            Assert.Equal("FAO", NavigatorTree.DragText(new NodeInfo(NodeKind.Table, s, "d", "FAO")), "table");
            Assert.Equal("`ürün_görünüm`", NavigatorTree.DragText(new NodeInfo(NodeKind.View, s, "d", "ürün_görünüm")), "view");
            Assert.Equal("Area_Code", NavigatorTree.DragText(new NodeInfo(NodeKind.Field, s, "d", "FAO", "Area_Code")), "field: its own name, not the table's");
            Assert.Equal("`Order`", NavigatorTree.DragText(new NodeInfo(NodeKind.Field, s, "d", "FAO", "Order", InView: true)), "a view's field, reserved word");
            Assert.True(NavigatorTree.DragText(new NodeInfo(NodeKind.Key, s, "d", "FAO", "PRIMARY")) is null, "key");
            Assert.True(NavigatorTree.DragText(new NodeInfo(NodeKind.Procedure, s, "d", "p")) is null && NavigatorTree.DragText(new NodeInfo(NodeKind.Server, s)) is null, "routine / server");
        });
    }
}
