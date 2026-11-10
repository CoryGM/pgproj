using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// An enum type that loses or reorders a label has no in-place form, and PostgreSQL refuses to drop it while a column uses it. G-190 refused such a change;
/// G-191 (the user's decisions of 2026-11-09) replaces that test with these: the type is rebuilt under its columns (the old one renamed out of the way, the
/// new one created under its own name, each column moved through its text, the old one dropped), a removed label is destructive, a label marked
/// <c>-- @renamed-label</c> is renamed in place, and a type a domain, a composite type, a routine, or a partition key uses stays refused. A type nothing uses,
/// and a label that is only inserted, are unchanged.
/// </summary>
[TestClass]
public sealed class EnumLabelRemovalInUseDeployTests
{
    private const string Use = " CREATE TABLE public.m (id int, cur public.mood);";

    [TestMethod]
    [DataRow("('sad', 'ok', 'happy')", "('sad', 'happy')")]
    [DataRow("('sad', 'ok', 'happy')", "('sad', 'happy', 'ok')")]
    [DataRow("('sad', 'happy')", "('sad', 'glad')")]
    public void ARemovedOrReorderedLabelOfATypeInUseIsRebuiltUnderItsColumns(string before, string after)
    {
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM {before};{Use}"), Build($"CREATE TYPE public.mood AS ENUM {after};{Use}"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        var sql = batch.Operations.Select(static operation => operation.GenerateSql()).ToList();
        var rename = sql.FindIndex(static text => text == "ALTER TYPE \"public\".\"mood\" RENAME TO \"mood_pgproj_old\";");
        var create = sql.FindIndex(static text => text.StartsWith("CREATE TYPE \"public\".\"mood\"", StringComparison.Ordinal));
        var move = sql.FindIndex(static text => text.Contains("ALTER TABLE \"public\".\"m\" ALTER COLUMN \"cur\" TYPE \"public\".\"mood\" USING \"cur\"::text::\"public\".\"mood\";", StringComparison.Ordinal));
        Assert.IsTrue(rename >= 0 && create > rename && move > create, string.Join(Environment.NewLine, sql));
        StringAssert.EndsWith(sql[move], "DROP TYPE \"public\".\"mood_pgproj_old\";");
        Assert.IsFalse(batch.Operations.Any(static operation => operation is DropOperation), "the old type is renamed, not dropped first");
    }

    [TestMethod]
    public void ARemovedLabelIsDestructiveAndAReorderIsNot()
    {
        var removed = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Use}"), Build($"CREATE TYPE public.mood AS ENUM ('sad', 'happy');{Use}"));
        var reordered = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Use}"), Build($"CREATE TYPE public.mood AS ENUM ('ok', 'sad', 'happy');{Use}"));

        var destructive = removed.GetDestructiveOperations().Single();
        StringAssert.Contains(destructive.Reasons.Single(), "enum label 'ok' removed while columns use the type");
        Assert.IsEmpty(reordered.GetDestructiveOperations());
    }

    [TestMethod]
    public void ALabelMarkedRenamedIsRenamedInPlace()
    {
        var batch = Generate(
            Build($"CREATE TYPE public.mood AS ENUM ('sad', 'glad');{Use}"),
            Build($"-- @renamed-label: glad -> happy\nCREATE TYPE public.mood AS ENUM ('sad', 'happy', 'ok');{Use}"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        Assert.AreEqual(
            "ALTER TYPE \"public\".\"mood\" RENAME VALUE 'glad' TO 'happy';\nALTER TYPE \"public\".\"mood\" ADD VALUE 'ok';",
            string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql())).ReplaceLineEndings("\n"));
        Assert.IsEmpty(batch.GetDestructiveOperations());
    }

    [TestMethod]
    public void ARenameAlreadyMadeIsNothingToDo()
    {
        var batch = Generate(
            Build($"CREATE TYPE public.mood AS ENUM ('sad', 'happy');{Use}"),
            Build($"-- @renamed-label: glad -> happy\nCREATE TYPE public.mood AS ENUM ('sad', 'happy');{Use}"));

        Assert.IsEmpty(batch.Operations, "the database has the new label already: the annotation is not a change, and is not compared");
    }

    [TestMethod]
    public void ADefaultAndAnArrayColumnMoveWithTheType()
    {
        const string Table = " CREATE TABLE public.m (id int, cur public.mood NOT NULL DEFAULT 'sad', past public.mood[]);";
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Table}"), Build($"CREATE TYPE public.mood AS ENUM ('sad', 'happy');{Table}"));

        var swap = batch.Operations.OfType<EnumSwapOperation>().Single().GenerateSql();
        var dropDefault = swap.IndexOf("ALTER TABLE \"public\".\"m\" ALTER COLUMN \"cur\" DROP DEFAULT;", StringComparison.Ordinal);
        var move = swap.IndexOf("ALTER COLUMN \"cur\" TYPE \"public\".\"mood\" USING", StringComparison.Ordinal);
        var setDefault = swap.IndexOf("ALTER TABLE \"public\".\"m\" ALTER COLUMN \"cur\" SET DEFAULT 'sad';", StringComparison.Ordinal);
        Assert.IsTrue(dropDefault >= 0 && move > dropDefault && setDefault > move, swap);
        StringAssert.Contains(swap, "ALTER COLUMN \"past\" TYPE \"public\".\"mood\"[] USING \"past\"::text[]::\"public\".\"mood\"[];");
        StringAssert.Contains(swap, "EXCEPTION WHEN invalid_text_representation THEN");
        Assert.HasCount(1, SqlStatementSplitter.Split(swap).Where(static statement => statement.Contains("RAISE EXCEPTION", StringComparison.Ordinal)).ToArray().Take(1));
    }

    [TestMethod]
    public void ACheckOnTheColumnIsDroppedBeforeTheSwapAndAddedAfter()
    {
        const string Table = " CREATE TABLE public.m (id int, cur public.mood, CONSTRAINT m_not_ok CHECK (cur <> 'sad'));";
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Table}"), Build($"CREATE TYPE public.mood AS ENUM ('sad', 'happy');{Table}"));

        var sql = batch.Operations.Select(static operation => operation.GenerateSql()).ToList();
        var dropCheck = sql.FindIndex(static text => text.Contains("DROP CONSTRAINT", StringComparison.Ordinal) && text.Contains("m_not_ok", StringComparison.Ordinal));
        var rename = sql.FindIndex(static text => text.Contains("RENAME TO \"mood_pgproj_old\"", StringComparison.Ordinal));
        var swap = sql.FindIndex(static text => text.Contains("DROP TYPE \"public\".\"mood_pgproj_old\"", StringComparison.Ordinal));
        var addCheck = sql.FindIndex(static text => text.Contains("ADD CONSTRAINT", StringComparison.Ordinal) && text.Contains("m_not_ok", StringComparison.Ordinal));
        Assert.IsTrue(dropCheck >= 0 && rename > dropCheck && swap > rename && addCheck > swap, string.Join(Environment.NewLine, sql));
    }

    [TestMethod]
    [DataRow("((cur = 'ok'::public.mood))")]
    [DataRow("((cur = 'ok'))")]
    [DataRow("((cur::text))")]
    public void AnIndexWithAnExpressionOnTheColumnIsDroppedBeforeTheSwapAndCreatedAfter(string expression)
    {
        // G-345: only an index's predicate counted, so PostgreSQL was left to rebuild an expression index during the swap, which fails on an expression
        // that compares the column with the old type (42883: operator does not exist: mood = mood_pgproj_old).
        var table = $" CREATE TABLE public.m (id int, cur public.mood); CREATE INDEX m_cur_x ON public.m {expression};";
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{table}"), Build($"CREATE TYPE public.mood AS ENUM ('happy', 'ok', 'sad');{table}"));

        var sql = batch.Operations.Select(static operation => operation.GenerateSql()).ToList();
        var dropIndex = sql.FindIndex(static text => text.Contains("DROP INDEX", StringComparison.Ordinal) && text.Contains("m_cur_x", StringComparison.Ordinal));
        var rename = sql.FindIndex(static text => text.Contains("RENAME TO \"mood_pgproj_old\"", StringComparison.Ordinal));
        var swap = sql.FindIndex(static text => text.Contains("DROP TYPE \"public\".\"mood_pgproj_old\"", StringComparison.Ordinal));
        var createIndex = sql.FindIndex(static text => text.Contains("CREATE INDEX", StringComparison.Ordinal) && text.Contains("m_cur_x", StringComparison.Ordinal));
        Assert.IsTrue(dropIndex >= 0 && rename > dropIndex && swap > rename && createIndex > swap, string.Join(Environment.NewLine, sql));
        Assert.IsEmpty(batch.GetDestructiveOperations(), "A reorder, and an index dropped only to be created again.");
    }

    [TestMethod]
    public void AnIndexWithAnExpressionOnAnotherColumnIsLeftAlone()
    {
        const string Table = " CREATE TABLE public.m (id int, cur public.mood); CREATE INDEX m_id_x ON public.m ((id + 1));";
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Table}"), Build($"CREATE TYPE public.mood AS ENUM ('happy', 'ok', 'sad');{Table}"));

        Assert.IsFalse(batch.Operations.Select(static operation => operation.GenerateSql()).Any(static text => text.Contains("m_id_x", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("CREATE DOMAIN public.cheer AS public.mood;", "public.cheer")]
    [DataRow("CREATE TYPE public.pair AS (a public.mood, b int);", "public.pair")]
    [DataRow("CREATE FUNCTION public.f(x public.mood) RETURNS int LANGUAGE sql RETURN 1;", "public.f")]
    public void ATypeADomainACompositeOrARoutineUsesStaysRefused(string other, string named)
    {
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Use} {other}"), Build($"CREATE TYPE public.mood AS ENUM ('sad', 'happy');{Use} {other}"));

        var blocked = batch.GetUnsupportedOperations().Single().GenerateSql();
        StringAssert.Contains(blocked, named);
        StringAssert.Contains(blocked, "pgproj rebuilds the type under the columns that use it; it cannot here");
        Assert.IsFalse(batch.Operations.Any(static operation => operation is EnumSwapOperation));
    }

    [TestMethod]
    public void APartitionKeyOfTheTypeStaysRefused()
    {
        const string Table = " CREATE TABLE public.m (id int, cur public.mood) PARTITION BY LIST (cur);";
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Table}"), Build($"CREATE TYPE public.mood AS ENUM ('sad', 'happy');{Table}"));

        StringAssert.Contains(batch.GetUnsupportedOperations().Single().GenerateSql(), "public.m.cur uses it and is a partition key");
    }

    [TestMethod]
    public void TheOldNameFitsPostgreSqlsLimit()
    {
        var longName = new string('n', 60);

        var oldName = EnumSwapRenameOperation.OldName(longName);

        Assert.AreEqual(63, oldName.Length);
        StringAssert.EndsWith(oldName, "_pgproj_old");
        Assert.AreEqual("mood_pgproj_old", EnumSwapRenameOperation.OldName("mood"));
    }

    [TestMethod]
    public void ARemovedLabelOfATypeNothingUsesIsStillDroppedAndCreated()
    {
        var batch = Generate(Build("CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');"), Build("CREATE TYPE public.mood AS ENUM ('sad', 'happy');"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        Assert.IsTrue(batch.Operations.Any(static operation => operation is DropOperation));
        Assert.IsTrue(batch.Operations.Any(static operation => operation is CreateOperation));
    }

    [TestMethod]
    public void AnInsertedLabelOfATypeInUseIsAddedInPlace()
    {
        var batch = Generate(Build($"CREATE TYPE public.mood AS ENUM ('sad', 'happy');{Use}"), Build($"CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');{Use}"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
        Assert.AreEqual("ALTER TYPE \"public\".\"mood\" ADD VALUE 'ok' AFTER 'sad';", batch.Operations.Single().GenerateSql());
    }

    private static OperationBatch Generate(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
