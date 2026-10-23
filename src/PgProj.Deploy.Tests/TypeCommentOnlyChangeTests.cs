using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-53: a modified enum, composite, or range type was always dropped and recreated, so changing only its comment failed while any
/// column used the type and was needlessly destructive otherwise. A change whose only difference is the comment is now deployed as
/// <c>COMMENT ON TYPE</c> alone; every other change to a type is redeployed as before.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeCommentOnlyChangeTests
{
    private const string Enum = "create type public.mood as enum ('sad', 'ok');";
    private const string Composite = "create type public.money_amount as (amount numeric, currency text);";
    private const string Range = "create type public.int_span as range (subtype = integer);";

    [TestMethod]
    [DataRow(Enum, "mood")]
    [DataRow(Composite, "money_amount")]
    [DataRow(Range, "int_span")]
    public void ChangingOnlyTheCommentEmitsCommentOnTypeAlone(string type, string name)
    {
        var statements = Changed(type + $"comment on type public.{name} is 'first';", type + $"comment on type public.{name} is 'second';");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual($"COMMENT ON TYPE \"public\".\"{name}\" IS 'second';", statements[0]);
    }

    [TestMethod]
    [DataRow(Enum, "mood")]
    [DataRow(Composite, "money_amount")]
    [DataRow(Range, "int_span")]
    public void AddingACommentEmitsCommentOnTypeAlone(string type, string name)
    {
        var statements = Changed(type, type + $"comment on type public.{name} is 'new';");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual($"COMMENT ON TYPE \"public\".\"{name}\" IS 'new';", statements[0]);
    }

    [TestMethod]
    [DataRow(Enum, "mood")]
    [DataRow(Composite, "money_amount")]
    [DataRow(Range, "int_span")]
    public void RemovingTheCommentClearsItWithoutRecreatingTheType(string type, string name)
    {
        var statements = Changed(type + $"comment on type public.{name} is 'old';", type);

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual($"COMMENT ON TYPE \"public\".\"{name}\" IS NULL;", statements[0]);
    }

    [TestMethod]
    public void ACommentWithAQuoteIsEscaped()
    {
        var statements = Changed(Enum, Enum + "comment on type public.mood is 'it''s a mood';");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual("COMMENT ON TYPE \"public\".\"mood\" IS 'it''s a mood';", statements[0]);
    }

    [TestMethod]
    public void ACommentOnlyChangeToATypeAColumnUsesDoesNotTouchTheTable()
    {
        const string table = "create table public.t (m public.mood);";

        var statements = Changed(Enum + table + "comment on type public.mood is 'a';", Enum + table + "comment on type public.mood is 'b';");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual("COMMENT ON TYPE \"public\".\"mood\" IS 'b';", statements[0]);
    }

    [TestMethod]
    public void AppendedEnumLabelsAndANewCommentAreBothDeployedInPlace()
    {
        var statements = Changed(
            Enum + "comment on type public.mood is 'a';",
            "create type public.mood as enum ('sad', 'ok', 'happy'); comment on type public.mood is 'b';");

        Assert.HasCount(1, statements, Dump(statements));
        StringAssert.Contains(statements[0], "ALTER TYPE \"public\".\"mood\" ADD VALUE 'happy';");
        StringAssert.Contains(statements[0], "COMMENT ON TYPE \"public\".\"mood\" IS 'b';");
        Assert.DoesNotContain("DROP", statements[0]);
    }

    [TestMethod]
    public void ARemovedEnumLabelWithANewCommentIsStillDroppedAndRecreated()
    {
        var statements = Changed(
            "create type public.mood as enum ('sad', 'ok', 'happy'); comment on type public.mood is 'a';",
            "create type public.mood as enum ('sad', 'happy'); comment on type public.mood is 'b';");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "DROP TYPE");
        StringAssert.StartsWith(statements[1], "CREATE TYPE");
        StringAssert.Contains(statements[1], "COMMENT ON TYPE \"public\".\"mood\" IS 'b';");
    }

    [TestMethod]
    public void AChangedCompositeFieldWithANewCommentIsAlteredInPlaceWithTheComment()
    {
        // Since G-111 a composite type's fields change in place; the comment still follows the field change rather than hiding it.
        var statements = Changed(
            Composite + "comment on type public.money_amount is 'a';",
            "create type public.money_amount as (amount numeric, currency varchar); comment on type public.money_amount is 'b';");

        Assert.HasCount(1, statements, Dump(statements));
        Assert.AreEqual(
            "ALTER TYPE \"public\".\"money_amount\" ALTER ATTRIBUTE \"currency\" TYPE varchar;\nCOMMENT ON TYPE \"public\".\"money_amount\" IS 'b';",
            statements[0]);
    }

    [TestMethod]
    public void AChangedRangeOptionWithANewCommentIsStillDroppedAndRecreated()
    {
        var statements = Changed(
            Range + "comment on type public.int_span is 'a';",
            "create type public.int_span as range (subtype = bigint); comment on type public.int_span is 'b';");

        Assert.IsTrue(statements.Any(static statement => statement.StartsWith("DROP TYPE", StringComparison.Ordinal)), Dump(statements));
    }

    [TestMethod]
    [DataRow(Enum)]
    [DataRow(Composite)]
    [DataRow(Range)]
    public void AnUnchangedCommentEmitsNothing(string type)
    {
        var name = type.Contains("mood", StringComparison.Ordinal) ? "mood" : type.Contains("money_amount", StringComparison.Ordinal) ? "money_amount" : "int_span";
        var sql = type + $"comment on type public.{name} is 'same';";

        Assert.IsEmpty(Changed(sql, sql));
    }

    [TestMethod]
    public void ACommentOnlyModificationWithNoReportedDetailsIsNotAssumedToBeCommentOnly()
    {
        var oldType = Build(Enum).Types["public.mood"];
        var newType = Build(Enum + "comment on type public.mood is 'x';").Types["public.mood"];

        var batch = new ScriptGenerator().Generate(
            new ChangeSet
            {
                Added = [],
                Removed = [],
                Modified = [new ModifiedChange { NodeId = newType.Key, NodeKind = ChangeNodeKind.Type, OldNode = oldType, NewNode = newType, Details = [] }],
            },
            new DependencyGraph());

        Assert.HasCount(2, batch.Operations);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
    }

    private static List<string> Changed(string oldSql, string newSql)
    {
        var oldModel = Build(oldSql);
        var newModel = Build(newSql);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();
    }

    private static string Dump(IEnumerable<string> statements) => string.Join(Environment.NewLine, statements);

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
