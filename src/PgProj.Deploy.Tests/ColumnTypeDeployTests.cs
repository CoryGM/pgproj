using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-45: generated DDL must carry a column's full type. It used to emit only the base type, so
/// <c>numeric(10,2)</c> deployed as unconstrained <c>numeric</c> and <c>text[]</c> as <c>text</c>. G-88: a SQL-
/// standard keyword type spelling (<c>integer</c>) must deploy as a friendly name, not pg_query's internal
/// <c>pg_catalog.int4</c> - which is why every expected column type below is already the friendly form.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnTypeDeployTests
{
    [TestMethod]
    [DataRow("numeric(10,2)", "\"c\" numeric(10,2)")]
    [DataRow("numeric(5)", "\"c\" numeric(5)")]
    [DataRow("varchar(20)", "\"c\" varchar(20)")]
    [DataRow("char(3)", "\"c\" char(3)")]
    [DataRow("bit(8)", "\"c\" bit(8)")]
    [DataRow("timestamp(3) with time zone", "\"c\" timestamp(3) with time zone")]
    [DataRow("text[]", "\"c\" text[]")]
    [DataRow("numeric(10,2)[]", "\"c\" numeric(10,2)[]")]
    [DataRow("varchar(20)[]", "\"c\" varchar(20)[]")]
    [DataRow("interval day to second(2)", "\"c\" interval day to second(2)")]
    [DataRow("interval(3)", "\"c\" interval(3)")]
    [DataRow("interval year to month", "\"c\" interval year to month")]
    [DataRow("interval", "\"c\" interval")]
    [DataRow("integer", "\"c\" integer")]
    public void CreateTableCarriesTheFullType(string type, string expectedColumn)
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(DatabaseModel.Empty, BuildModel(type)));

        StringAssert.Contains(script, expectedColumn, script);
    }

    /// <summary>
    /// The full translation table (G-88), confirmed against a real server's <c>format_type()</c> where a rename
    /// applies (<c>src/PgProj.Deploy.Tests</c> is not Docker-backed, so this pins the values that lookup
    /// produced rather than re-querying a server on every run). <c>timestamp</c>/<c>timestamptz</c>/<c>time</c>/
    /// <c>timetz</c> are deliberately *not* renamed to their multi-word SQL-standard spelling even though
    /// <c>format_type()</c> would: PostgreSQL requires a precision modifier *between* the two halves there
    /// (<c>timestamp(3) with time zone</c>), which this rendering path has nowhere to insert, so they keep their
    /// short, already-valid catalog spelling instead - the <c>CreateTableCarriesTheFullType</c> case above proves
    /// this specific combination.
    /// </summary>
    [TestMethod]
    [DataRow("smallint", "\"c\" smallint")]
    [DataRow("bigint", "\"c\" bigint")]
    [DataRow("boolean", "\"c\" boolean")]
    [DataRow("real", "\"c\" real")]
    [DataRow("double precision", "\"c\" double precision")]
    [DataRow("timestamptz", "\"c\" timestamptz")]
    [DataRow("timestamp", "\"c\" timestamp")]
    [DataRow("time", "\"c\" time")]
    [DataRow("json", "\"c\" json")]
    [DataRow("bit varying(5)", "\"c\" bit varying(5)")]
    // A type that was never rewritten to a pg_catalog-qualified internal name in the first place (the user
    // already wrote its own catalog name, or a type with no SQL-standard keyword form at all) must pass through
    // completely unchanged - there is nothing to translate.
    [DataRow("int4", "\"c\" int4")]
    [DataRow("uuid", "\"c\" uuid")]
    [DataRow("jsonb", "\"c\" jsonb")]
    public void FriendlyTypeNameTranslationTable(string type, string expectedColumn)
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(DatabaseModel.Empty, BuildModel(type)));

        StringAssert.Contains(script, expectedColumn, script);
    }

    [TestMethod]
    public void AddColumnCarriesTheFullType()
    {
        var oldModel = new SemanticPipeline().Execute([new PgQueryParser().Parse("create table public.t (id bigint not null);")]);
        var newModel = new SemanticPipeline().Execute([new PgQueryParser().Parse("create table public.t (id bigint not null, price numeric(10,2));")]);

        var script = new ScriptGenerator().GenerateSqlScript(Generate(oldModel, newModel));

        StringAssert.Contains(script, "ADD COLUMN \"price\" numeric(10,2);");
    }

    [TestMethod]
    [DataRow("varchar(10)", "varchar(40)", "varchar(40)")]
    [DataRow("numeric(10,2)", "numeric(12,3)", "numeric(12,3)")]
    [DataRow("numeric(10,2)[]", "numeric(12,2)[]", "numeric(12,2)[]")]
    [DataRow("timestamptz(3)", "timestamptz(6)", "timestamptz(6)")]
    [DataRow("interval", "interval day to second(2)", "interval day to second(2)")]
    public void ChangingModifiersAltersTheColumnTypeWithTheFullTypeAndCast(string oldType, string newType, string expectedType)
    {
        var script = new ScriptGenerator().GenerateSqlScript(Generate(BuildModel(oldType), BuildModel(newType)));

        StringAssert.Contains(script, $"ALTER COLUMN \"c\" TYPE {expectedType} USING \"c\"::{expectedType};", script);
    }

    [TestMethod]
    [DataRow("numeric(10,2)", "NUMERIC( 10 , 2 )")]
    [DataRow("numeric(5)", "numeric(5,0)")]
    [DataRow("varchar(20)", "character varying(20)")]
    [DataRow("integer[]", "int[3]")]
    public void EquivalentSpellingsEmitNoAlteration(string oldType, string newType)
    {
        var batch = Generate(BuildModel(oldType), BuildModel(newType));

        Assert.IsEmpty(batch.Operations, new ScriptGenerator().GenerateSqlScript(batch));
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(
            new DiffEngine().Diff(oldModel, newModel),
            new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel BuildModel(string type)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse($"create table public.t (c {type});")]);
}
