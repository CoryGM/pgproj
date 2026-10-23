using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-70: PostgreSQL keeps the case of a quoted identifier, so <c>CREATE TABLE "MyTable" ("Mixed" integer)</c> makes a table named
/// <c>MyTable</c>. pgproj folded every name to lower case, wrote <c>CHECK (Amount &gt; 0)</c> for <c>CHECK ("Amount" &gt; 0)</c>, left a
/// type unquoted, and did not escape a quote inside a name. Every statement below is written with the names exactly as the project
/// wrote them, and a name that is plain is written exactly as it always was.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MixedCaseIdentifierDeployTests
{
    private const string Project = """
        CREATE SCHEMA "MySchema";
        CREATE TYPE "MySchema"."Mood" AS ENUM ('sad', 'ok');
        CREATE SEQUENCE "MySchema"."TicketSeq";
        CREATE TABLE "MySchema"."Parent" ("Id" integer PRIMARY KEY, "Code" text UNIQUE, "State" "MySchema"."Mood" NOT NULL DEFAULT 'ok', "Moods" "MySchema"."Mood"[]);
        CREATE TABLE "MySchema"."Child" (
            "Id" integer,
            "ParentId" integer REFERENCES "MySchema"."Parent" ("Id"),
            "Amount" integer CHECK ("Amount" > 0),
            "Ticket" integer DEFAULT nextval('"MySchema"."TicketSeq"'),
            CONSTRAINT "Child_Pk" PRIMARY KEY ("Id"));
        CREATE INDEX "IxChild" ON "MySchema"."Child" ("Amount", lower("ParentId"::text)) WHERE "Amount" > 0;
        CREATE VIEW "MySchema"."ChildView" AS SELECT "Id", "Amount" FROM "MySchema"."Child";
        CREATE FUNCTION "MySchema"."Describe"("M" "MySchema"."Mood", "Prefix" text DEFAULT 'x') RETURNS "MySchema"."Mood" LANGUAGE sql AS $$ SELECT "M" $$;
        CREATE PROCEDURE "MySchema"."DoProc"("Arg" "MySchema"."Mood"[]) LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE FUNCTION "MySchema"."DoIt"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER "TrgChild" AFTER UPDATE OF "Amount" ON "MySchema"."Child" FOR EACH ROW EXECUTE FUNCTION "MySchema"."DoIt"();
        COMMENT ON TABLE "MySchema"."Parent" IS 'parent';
        COMMENT ON COLUMN "MySchema"."Parent"."Code" IS 'code';
        COMMENT ON FUNCTION "MySchema"."Describe"("MySchema"."Mood", text) IS 'describe';
        GRANT SELECT ON "MySchema"."Parent" TO "SomeRole";
        """;

    [TestMethod]
    public void EveryObjectIsCreatedUnderTheNameTheProjectWrote()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Project));

        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"MySchema\";", statements[0]);
        AssertContains(statements, "CREATE TYPE \"MySchema\".\"Mood\" AS ENUM ('sad', 'ok');");
        AssertContains(statements, "CREATE SEQUENCE \"MySchema\".\"TicketSeq\";");
        AssertContainsStartingWith(statements, "CREATE TABLE \"MySchema\".\"Parent\" (\n    \"Id\" integer NOT NULL,\n    \"Code\" text,\n    \"State\" \"MySchema\".\"Mood\" NOT NULL DEFAULT 'ok',\n    \"Moods\" \"MySchema\".\"Mood\"[]\n);");
        AssertContainsStartingWith(statements, "CREATE TABLE \"MySchema\".\"Child\" (\n    \"Id\" integer NOT NULL,\n    \"ParentId\" integer,\n    \"Amount\" integer,\n    \"Ticket\" integer DEFAULT nextval('\"MySchema\".\"TicketSeq\"')\n);");
        AssertContains(statements, "CREATE OR REPLACE VIEW \"MySchema\".\"ChildView\" AS\nSELECT \"Id\", \"Amount\" FROM \"MySchema\".\"Child\";");
        AssertContains(statements, "CREATE INDEX \"IxChild\" ON \"MySchema\".\"Child\" USING btree (\"Amount\", (lower(\"ParentId\"::text))) WHERE \"Amount\" > 0;");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"Child\"\nADD CONSTRAINT \"Child_Amount_check\"\nCHECK (\"Amount\" > 0);");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"Child\"\nADD CONSTRAINT \"Child_Pk\"\nPRIMARY KEY (\"Id\");");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"Child\"\nADD CONSTRAINT \"Child_ParentId_fkey\"\nFOREIGN KEY (\"ParentId\") REFERENCES \"MySchema\".\"Parent\" (\"Id\");");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"Parent\"\nADD CONSTRAINT \"Parent_Code_key\"\nUNIQUE (\"Code\");");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"Parent\"\nADD CONSTRAINT \"Parent_pkey\"\nPRIMARY KEY (\"Id\");");
        AssertContains(statements, "GRANT SELECT ON TABLE \"MySchema\".\"Parent\" TO \"SomeRole\";");
        Assert.IsFalse(statements.Any(static statement => statement.Contains("TODO", StringComparison.Ordinal)), Dump(statements));
    }

    [TestMethod]
    public void RoutinesAreCreatedWithQuotedParametersTypesAndComments()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Project));

        AssertContains(statements, "CREATE OR REPLACE FUNCTION \"MySchema\".\"Describe\"(\"M\" \"MySchema\".\"Mood\", \"Prefix\" text DEFAULT 'x')\nRETURNS \"MySchema\".\"Mood\"\nLANGUAGE sql\nAS $$ SELECT \"M\" $$;\nCOMMENT ON FUNCTION \"MySchema\".\"Describe\"(\"MySchema\".\"Mood\", text) IS 'describe';");
        AssertContains(statements, "CREATE OR REPLACE PROCEDURE \"MySchema\".\"DoProc\"(\"Arg\" \"MySchema\".\"Mood\"[])\nLANGUAGE sql\nAS $$ SELECT 1 $$;");
        AssertContains(statements, "CREATE TRIGGER \"TrgChild\" AFTER UPDATE OF \"Amount\" ON \"MySchema\".\"Child\" FOR EACH ROW EXECUTE FUNCTION \"MySchema\".\"DoIt\"();");
    }

    [TestMethod]
    public void CommentsNameTheMixedCaseObjects()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Project));

        AssertContains(statements, "COMMENT ON TABLE \"MySchema\".\"Parent\" IS 'parent';");
        AssertContains(statements, "COMMENT ON COLUMN \"MySchema\".\"Parent\".\"Code\" IS 'code';");
    }

    [TestMethod]
    public void EveryObjectIsDroppedUnderTheNameItWasCreatedWith()
    {
        var statements = Statements(Build(Project), Build("CREATE SCHEMA \"MySchema\";"));

        AssertContains(statements, "DROP TRIGGER \"TrgChild\" ON \"MySchema\".\"Child\";");
        AssertContains(statements, "DROP FUNCTION \"MySchema\".\"Describe\"(\"MySchema\".\"Mood\", text);");
        AssertContains(statements, "DROP PROCEDURE \"MySchema\".\"DoProc\"(\"MySchema\".\"Mood\"[]);");
        AssertContains(statements, "DROP FUNCTION \"MySchema\".\"DoIt\"();");
        AssertContains(statements, "DROP VIEW \"MySchema\".\"ChildView\";");
        AssertContains(statements, "DROP INDEX \"MySchema\".\"IxChild\";");
        AssertContains(statements, "DROP TABLE \"MySchema\".\"Child\";");
        AssertContains(statements, "DROP TABLE \"MySchema\".\"Parent\";");
        AssertContains(statements, "DROP TYPE \"MySchema\".\"Mood\";");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"Child\" DROP CONSTRAINT \"Child_ParentId_fkey\";");
        AssertContains(statements, "REVOKE SELECT ON TABLE \"MySchema\".\"Parent\" FROM \"SomeRole\";");
    }

    [TestMethod]
    public void TheForeignKeyIsDroppedBeforeTheKeyItDependsOnWhateverTheCase()
    {
        var statements = Statements(Build(Project), Build("CREATE SCHEMA \"MySchema\";"));

        var foreignKey = statements.FindIndex(static statement => statement.Contains("DROP CONSTRAINT \"Child_ParentId_fkey\"", StringComparison.Ordinal));
        var primaryKey = statements.FindIndex(static statement => statement.Contains("DROP CONSTRAINT \"Parent_pkey\"", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, foreignKey, Dump(statements));
        Assert.IsLessThan(primaryKey, foreignKey, Dump(statements));
    }

    [TestMethod]
    public void ChangesToAMixedCaseTableAreAlteredUnderItsName()
    {
        var before = Build("CREATE TABLE \"MySchema\".\"T\" (\"Id\" integer, \"Old Col\" text, \"Amount\" integer);");
        var after = Build("CREATE TABLE \"MySchema\".\"T\" (\"Id\" integer, \"Amount\" bigint NOT NULL DEFAULT 5, \"New Col\" \"MySchema\".\"Mood\");");

        var statements = Statements(before, after);

        AssertContains(statements, "ALTER TABLE \"MySchema\".\"T\" DROP COLUMN \"Old Col\";");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"T\" ADD COLUMN \"New Col\" \"MySchema\".\"Mood\";");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"T\" ALTER COLUMN \"Amount\" TYPE bigint USING \"Amount\"::bigint;");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"T\" ALTER COLUMN \"Amount\" SET NOT NULL;");
        AssertContains(statements, "ALTER TABLE \"MySchema\".\"T\" ALTER COLUMN \"Amount\" SET DEFAULT 5;");
    }

    [TestMethod]
    public void AColumnRenamedFromTheAnnotationIsRenamedUnderItsSpellings()
    {
        var before = Build("CREATE TABLE \"T\" (\"Old Name\" text);");
        // The annotation is read as PostgreSQL reads a name (G-287), so a quoted column's old name is written quoted.
        var after = Build("CREATE TABLE \"T\" (\n-- @renamed-from: \"Old Name\"\n\"New Name\" text);");

        AssertContains(Statements(before, after), "ALTER TABLE \"public\".\"T\" RENAME COLUMN \"Old Name\" TO \"New Name\";");
    }

    [TestMethod]
    public void AQuoteInsideANameIsEscapedInEveryStatement()
    {
        const string project = """
            CREATE SCHEMA "s""x";
            CREATE SEQUENCE "s""x"."q""seq";
            CREATE TABLE "s""x"."t""b" ("c""d" serial PRIMARY KEY, "e""f" integer UNIQUE, CONSTRAINT "k""y" CHECK ("e""f" > 0));
            CREATE INDEX "i""x" ON "s""x"."t""b" ("e""f");
            COMMENT ON TABLE "s""x"."t""b" IS 'p';
            COMMENT ON COLUMN "s""x"."t""b"."e""f" IS 'c';
            """;

        var statements = Statements(DatabaseModel.Empty, Build(project));

        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"s\"\"x\";", statements[0]);
        AssertContainsStartingWith(statements, "CREATE TABLE \"s\"\"x\".\"t\"\"b\" (\n    \"c\"\"d\" integer NOT NULL DEFAULT nextval('\"s\"\"x\".\"t\"\"b_c\"\"d_seq\"'::regclass),");
        AssertContains(statements, "CREATE INDEX \"i\"\"x\" ON \"s\"\"x\".\"t\"\"b\" USING btree (\"e\"\"f\");");
        AssertContains(statements, "ALTER TABLE \"s\"\"x\".\"t\"\"b\"\nADD CONSTRAINT \"k\"\"y\"\nCHECK (\"e\"\"f\" > 0);");
        AssertContains(statements, "COMMENT ON TABLE \"s\"\"x\".\"t\"\"b\" IS 'p';");
        AssertContains(statements, "COMMENT ON COLUMN \"s\"\"x\".\"t\"\"b\".\"e\"\"f\" IS 'c';");
        AssertContainsStartingWith(statements, "ALTER SEQUENCE \"s\"\"x\".\"t\"\"b_c\"\"d_seq\" OWNED BY \"s\"\"x\".\"t\"\"b\".\"c\"\"d\";");

        var drops = Statements(Build(project), Build("CREATE SCHEMA \"s\"\"x\";"));
        AssertContains(drops, "DROP INDEX \"s\"\"x\".\"i\"\"x\";");
        AssertContains(drops, "DROP TABLE \"s\"\"x\".\"t\"\"b\";");
    }

    [TestMethod]
    public void ReservedWordsAndSpacesStayQuotedInRegeneratedText()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.\"user\" (id integer, \"order\" integer CHECK (\"order\" > 0), \"my col\" text); CREATE INDEX ix ON public.\"user\" (\"order\", lower(\"my col\")) WHERE \"order\" > 0;"));

        AssertContains(statements, "ALTER TABLE \"public\".\"user\"\nADD CONSTRAINT \"user_order_check\"\nCHECK (\"order\" > 0);");
        AssertContains(statements, "CREATE INDEX \"ix\" ON \"public\".\"user\" USING btree (\"order\", (lower(\"my col\"))) WHERE \"order\" > 0;");
    }

    [TestMethod]
    public void APlainProjectIsWrittenExactlyAsBefore()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build("CREATE TABLE public.orders (id integer PRIMARY KEY, total numeric(10,2) CHECK (total > 0), status text DEFAULT 'new'); CREATE INDEX ix ON public.orders (lower(status)) WHERE total > 0;"));

        AssertContainsStartingWith(statements, "CREATE TABLE \"public\".\"orders\" (\n    \"id\" integer NOT NULL,\n    \"total\" numeric(10,2),\n    \"status\" text DEFAULT 'new'\n);");
        AssertContains(statements, "CREATE INDEX \"ix\" ON \"public\".\"orders\" USING btree ((lower(status))) WHERE total > 0;");
        AssertContains(statements, "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"orders_total_check\"\nCHECK (total > 0);");
    }

    private static void AssertContains(IReadOnlyList<string> statements, string expected)
        => Assert.IsTrue(string.Join("\n", statements).Contains(expected, StringComparison.Ordinal), $"Expected:\n{expected}\n\nActual:\n{Dump(statements)}");

    private static void AssertContainsStartingWith(IReadOnlyList<string> statements, string expected)
        => Assert.IsTrue(statements.Any(statement => statement.StartsWith(expected, StringComparison.Ordinal)), $"Expected a statement starting with:\n{expected}\n\nActual:\n{Dump(statements)}");

    private static string Dump(IEnumerable<string> statements) => string.Join("\n---\n", statements);

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
