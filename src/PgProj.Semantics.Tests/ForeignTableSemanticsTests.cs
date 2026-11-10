using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-133: a foreign table is a table with its server and options, its columns with theirs, its comment and owner; <c>build</c> reports what PostgreSQL refuses on one (an
/// index, or a constraint other than <c>CHECK</c> and <c>NOT NULL</c>).
/// </summary>
[TestClass]
public sealed class ForeignTableSemanticsTests
{
    private const string Server = "CREATE FOREIGN DATA WRAPPER w; CREATE SERVER remote FOREIGN DATA WRAPPER w;\n";

    [TestMethod]
    public void TheTableCarriesItsServerOptionsCommentAndOwner()
    {
        var model = Build(Server + """
            CREATE FOREIGN TABLE public.ft (a integer OPTIONS (column_name 'x') NOT NULL CHECK (a > 0), b text) SERVER remote OPTIONS (table_name 't');
            COMMENT ON FOREIGN TABLE public.ft IS 'remote rows';
            ALTER FOREIGN TABLE public.ft OWNER TO app;
            """);

        var table = model.Tables["public.ft"];
        Assert.IsTrue(table.IsForeign);
        Assert.AreEqual("remote", table.ForeignServer);
        Assert.AreEqual("table_name=t", string.Join(",", ForeignOptions.Describe(table.ForeignOptions)));
        Assert.AreEqual("column_name=x", string.Join(",", ForeignOptions.Describe(table.Columns.Single(static column => column.Name == "a").ForeignOptions)));
        Assert.AreEqual("remote rows", table.Comment);
        Assert.AreEqual("table app", $"{model.Owners.Values.Single().ObjectKind} {model.Owners.Values.Single().OwnerRole}");
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity is SemanticDiagnosticSeverity.Error or SemanticDiagnosticSeverity.Warning), string.Join(" | ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnOrdinaryTableIsNotForeign()
    {
        Assert.IsFalse(Build("CREATE TABLE public.t (a integer);").Tables["public.t"].IsForeign);
    }

    [TestMethod]
    [DataRow("CREATE FOREIGN TABLE public.ft (a integer PRIMARY KEY) SERVER remote;", "primary key constraint ft_pkey")]
    [DataRow("CREATE FOREIGN TABLE public.ft (a integer) SERVER remote; ALTER TABLE public.ft ADD CONSTRAINT u UNIQUE (a);", "unique constraint u")]
    [DataRow("CREATE FOREIGN TABLE public.ft (a integer) SERVER remote; CREATE INDEX ft_a ON public.ft (a);", "index ft_a")]
    public void WhatPostgreSqlRefusesOnAForeignTableIsAnError(string sql, string what)
    {
        var error = Build(Server + sql).Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error);

        StringAssert.Contains(error.Message, $"The foreign table public.ft cannot have the {what}");
    }

    [TestMethod]
    public void ACheckAndANotNullAreAllowed()
    {
        var model = Build(Server + "CREATE FOREIGN TABLE public.ft (a integer NOT NULL, CONSTRAINT positive CHECK (a > 0)) SERVER remote;");

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error), string.Join(" | ", model.Diagnostics.Select(static d => d.Message)));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
