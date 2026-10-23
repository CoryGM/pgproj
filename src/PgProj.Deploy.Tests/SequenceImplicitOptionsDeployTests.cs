using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-57: the diff compares sequence options with PostgreSQL's defaults applied, so an option the target leaves implicit can
/// be a reported change (live <c>INCREMENT BY 5</c>, project silent). The ALTER must then set it to its default, not skip
/// it: skipping would report a change and deploy nothing.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SequenceImplicitOptionsDeployTests
{
    [TestMethod]
    public void OptionsTheTargetLeavesImplicitAreSetToTheirDefaults()
    {
        var statements = Statements(
            "create sequence public.s as integer increment by 5 maxvalue 1000 cache 20;",
            "create sequence public.s;");

        Assert.HasCount(1, statements, string.Join("\n", statements));
        Assert.AreEqual("ALTER SEQUENCE \"public\".\"s\" AS int8 INCREMENT BY 1 NO MAXVALUE CACHE 1;", statements[0]);
    }

    [TestMethod]
    public void OnlyTheChangedOptionIsAltered()
    {
        var statements = Statements("create sequence public.s maxvalue 100;", "create sequence public.s;");

        Assert.HasCount(1, statements);
        Assert.AreEqual("ALTER SEQUENCE \"public\".\"s\" NO MAXVALUE;", statements[0]);
    }

    [TestMethod]
    public void OptionsWrittenExplicitlyAreEmittedAsWritten()
    {
        var statements = Statements("create sequence public.s;", "create sequence public.s as integer increment by 2 cache 10 cycle;");

        Assert.HasCount(1, statements);
        // The type is emitted with a friendly spelling (G-88), not pg_query's internal pg_catalog.int4. The
        // maximum changed with the type but was left implicit, so it is reset to the new type's default
        // (PostgreSQL applies AS first within one statement).
        Assert.AreEqual("ALTER SEQUENCE \"public\".\"s\" AS integer INCREMENT BY 2 NO MAXVALUE CACHE 10 CYCLE;", statements[0]);
    }

    [TestMethod]
    public void ImplicitStartIsSetToItsDefault()
    {
        var statements = Statements("create sequence public.s start with 50;", "create sequence public.s;");

        // G-220: the start is recorded, not restarted.
        Assert.HasCount(1, statements);
        Assert.AreEqual("ALTER SEQUENCE \"public\".\"s\" START WITH 1;", statements[0]);
    }

    [TestMethod]
    public void SpellingOnlyDifferencesEmitNothing()
    {
        var statements = Statements(
            "create sequence public.s as bigint increment by 1 minvalue 1 maxvalue 9223372036854775807 start with 1 cache 1;",
            "create sequence public.s;");

        Assert.IsEmpty(statements, string.Join("\n", statements));
    }

    private static IReadOnlyList<string> Statements(string oldSql, string newSql)
    {
        var oldModel = new SemanticPipeline().Execute([new PgQueryParser().Parse(oldSql)]);
        var newModel = new SemanticPipeline().Execute([new PgQueryParser().Parse(newSql)]);
        var generator = new ScriptGenerator();
        return generator.Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)).Operations
            .Select(static operation => operation.GenerateSql().Trim())
            .ToArray();
    }
}
