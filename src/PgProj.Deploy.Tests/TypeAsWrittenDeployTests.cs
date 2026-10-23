using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-207 step 3: a type is deployed as the project wrote it (G-88's normalized spellings are reversed): a column's, a routine parameter's and
/// result's, a domain's base type, and the types of a cast, an operator, and an aggregate. A type that needs quotes keeps them, so <c>"char"</c>
/// is no longer deployed as <c>char</c> (which PostgreSQL reads as <c>character(1)</c>) and <c>"My S"."My T"</c> or <c>"any"</c> no longer fail
/// (G-201).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeAsWrittenDeployTests
{
    [TestMethod]
    public void AColumnTypeIsDeployedAsWritten()
    {
        var sql = Script("CREATE TABLE t (a INT, b VARCHAR(10), c \"char\", d serial);");

        StringAssert.Contains(sql, "\"a\" INT,");
        StringAssert.Contains(sql, "\"b\" VARCHAR(10),");
        StringAssert.Contains(sql, "\"c\" \"char\",");
        StringAssert.Contains(sql, "\"d\" integer");
    }

    [TestMethod]
    public void ARoutinesTypesAreDeployedAsWrittenWithTheirQuotes()
    {
        var sql = Script("CREATE SCHEMA \"My S\"; CREATE TYPE \"My S\".\"My T\" AS (a int); CREATE FUNCTION f(a \"char\", b \"My S\".\"My T\") RETURNS SETOF \"char\" LANGUAGE sql AS $$ SELECT a $$;");

        StringAssert.Contains(sql, "\"public\".\"f\"(a \"char\", b \"My S\".\"My T\")\nRETURNS SETOF \"char\"\n");
    }

    [TestMethod]
    public void ADomainsBaseTypeIsDeployedAsWritten()
    {
        StringAssert.Contains(Script("CREATE DOMAIN d AS VARCHAR(10);"), "CREATE DOMAIN \"public\".\"d\" AS VARCHAR(10);");
    }

    [TestMethod]
    public void ACastOperatorAndAggregateAreDeployedWithTheirTypesAsWritten()
    {
        var sql = Script("""
            CREATE SCHEMA "My S";
            CREATE TYPE "My S"."My T" AS (a int);
            CREATE FUNCTION tocharf("My S"."My T") RETURNS "char" LANGUAGE sql IMMUTABLE AS $$ SELECT 'x'::"char" $$;
            CREATE CAST ("My S"."My T" AS "char") WITH FUNCTION tocharf("My S"."My T");
            CREATE FUNCTION eqf("My S"."My T", "My S"."My T") RETURNS bool LANGUAGE sql IMMUTABLE AS $$ SELECT true $$;
            CREATE OPERATOR === (LEFTARG = "My S"."My T", RIGHTARG = "My S"."My T", FUNCTION = eqf);
            CREATE FUNCTION accf(int, "char") RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT $1 $$;
            CREATE AGGREGATE agg("char") (SFUNC = accf, STYPE = INTEGER);
            """);

        StringAssert.Contains(sql, "CREATE CAST (\"My S\".\"My T\" AS \"char\")\n    WITH FUNCTION \"tocharf\"(\"My S\".\"My T\");");
        StringAssert.Contains(sql, "LEFTARG = \"My S\".\"My T\",");
        StringAssert.Contains(sql, "\"agg\" (\"char\")");
        StringAssert.Contains(sql, "STYPE = INTEGER");
    }

    private static string Script(string sql)
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(DatabaseModel.Empty, model), new DependencyAnalyzer().Analyze(DatabaseModel.Empty, model));
        return string.Join("\n", batch.Operations.Select(static operation => operation.GenerateSql()));
    }
}
