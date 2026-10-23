namespace PgProj.Parser.Tests;

/// <summary>
/// G-95: a SQL-standard routine body (<c>BEGIN ATOMIC ... END</c>, or <c>RETURN expression</c>) is SQL, not a string, so it has no
/// <c>AS</c> or delimiter. The routine carries it from its first word to the end of the statement, marked as such, so it can be deployed as
/// written; its text is also what the body is compared by.
/// </summary>
[TestClass]
public sealed class SqlStandardRoutineBodyParsingTests
{
    [TestMethod]
    [DataRow(
        "CREATE FUNCTION s.total(p int) RETURNS bigint LANGUAGE sql STABLE BEGIN ATOMIC SELECT CASE WHEN p > 0 THEN 1 ELSE 0 END; SELECT 2; END;",
        "BEGIN ATOMIC SELECT CASE WHEN p > 0 THEN 1 ELSE 0 END; SELECT 2; END",
        DisplayName = "BEGIN ATOMIC")]
    [DataRow(
        "CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql IMMUTABLE RETURN x * 2;",
        "RETURN x * 2",
        DisplayName = "RETURN")]
    [DataRow(
        "CREATE OR REPLACE FUNCTION s.a()\n RETURNS integer\n LANGUAGE sql\nBEGIN ATOMIC\n SELECT 1;\nEND;",
        "BEGIN ATOMIC\n SELECT 1;\nEND",
        DisplayName = "as pg_get_functiondef prints it")]
    public void AFunctionCarriesItsSqlStandardBodyAsWritten(string sql, string body)
    {
        var function = new PgQueryParser().Parse(sql).Functions.Single();

        Assert.IsTrue(function.IsSqlStandardBody);
        Assert.AreEqual(body, function.RawBody);
        Assert.IsNull(function.BodyDelimiter);
        Assert.AreEqual(body, function.RawBodyExpression);
    }

    [TestMethod]
    public void AProcedureCarriesItsSqlStandardBodyAsWritten()
    {
        var procedure = new PgQueryParser().Parse("CREATE PROCEDURE s.clear() LANGUAGE sql BEGIN ATOMIC DELETE FROM s.t; END;").Procedures.Single();

        Assert.IsTrue(procedure.IsSqlStandardBody);
        Assert.AreEqual("BEGIN ATOMIC DELETE FROM s.t; END", procedure.RawBody);
        Assert.IsNull(procedure.BodyDelimiter);
    }

    [TestMethod]
    public void AStringBodyIsNotSqlStandard()
    {
        var function = new PgQueryParser().Parse("CREATE FUNCTION s.f() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;").Functions.Single();

        Assert.IsFalse(function.IsSqlStandardBody);
        Assert.AreEqual(" SELECT 1 ", function.RawBody);
        Assert.AreEqual("$$", function.BodyDelimiter);
    }
}
