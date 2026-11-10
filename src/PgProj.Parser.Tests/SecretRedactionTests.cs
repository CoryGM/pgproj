using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-142 and G-141: a warning or an error that quotes SQL masks a password in it (five asterisks) instead of printing it or
/// leaving the statement out. Pattern matching on the common shapes, by the user's decision.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SecretRedactionTests
{
    [TestMethod]
    [DataRow("CREATE SUBSCRIPTION s CONNECTION 'host=h dbname=d user=u password=secret' PUBLICATION p WITH (connect = false);", "CREATE SUBSCRIPTION", "password=*****")]
    [DataRow("ALTER SUBSCRIPTION s CONNECTION 'host=h password = secret port=5432';", "ALTER SUBSCRIPTION", "password = *****")]
    // The modeled CREATE SERVER and CREATE USER MAPPING (G-132) quote no statement; their ALTER forms are still reported, and masked.
    [DataRow("ALTER USER MAPPING FOR b SERVER s OPTIONS (ADD password 'hunter2');", "ALTER USER MAPPING", "ADD password '*****'")]
    [DataRow("ALTER USER MAPPING FOR bob SERVER srv OPTIONS (SET password 'hunter2');", "ALTER USER MAPPING", "SET password '*****'")]
    [DataRow("ALTER SERVER s OPTIONS (ADD host 'h', ADD PASSWORD 'hunter2', ADD dbname 'd');", "ALTER SERVER", "PASSWORD '*****'")]
    [DataRow("CREATE ROLE app LOGIN ENCRYPTED PASSWORD 'hunter2';", "CREATE ROLE", "ENCRYPTED PASSWORD '*****'")]
    [DataRow("ALTER ROLE app WITH PASSWORD 'it''s hunter2';", "ALTER ROLE", "WITH PASSWORD '*****'")]
    [DataRow("ALTER SERVER s OPTIONS (SET dsn 'postgresql://bob:hunter2@host/db');", "ALTER SERVER", "bob:*****@host")]
    [DataRow("ALTER SERVER s OPTIONS (ADD api_key 'hunter2', ADD token 'hunter2');", "ALTER SERVER", "api_key '*****', ADD token '*****'")]
    public void AStatementTheWarningQuotesHasItsPasswordMasked(string sql, string kind, string maskedPart)
    {
        var diagnostic = Parse(sql).Diagnostics.Single(static d => d.Source == "UnmodeledStatement");

        StringAssert.Contains(diagnostic.Message, maskedPart);
        Assert.StartsWith($"{kind} is not modeled", diagnostic.Message);
        Assert.DoesNotContain("hunter2", diagnostic.Message);
        Assert.DoesNotContain("secret", diagnostic.Message);
    }

    [TestMethod]
    public void ThePasswordIsMaskedBeforeTheStatementIsShortened()
    {
        // The secret sits where an 80-character cut would fall: masking afterwards would show its first characters.
        var sql = "CREATE SUBSCRIPTION subscription_one CONNECTION 'host=example.org password=hunter2hunter2hunter2hunter2' PUBLICATION p;";

        var message = Parse(sql).Diagnostics.Single().Message;

        Assert.DoesNotContain("hunter", message);
        StringAssert.Contains(message, "password=*****");
    }

    [TestMethod]
    [DataRow("ALTER TABLE public.t RENAME TO u;")]
    [DataRow("CREATE TEXT SEARCH TEMPLATE public.ts (INIT = a, LEXIZE = b);")]
    [DataRow("INSERT INTO public.t VALUES (1);")]
    [DataRow("CREATE TABLESPACE tbs LOCATION '/x';")]
    [DataRow("COMMENT ON LANGUAGE plpgsql IS 'the token of appreciation';")]
    public void AStatementWithoutASecretIsQuotedAsWritten(string sql)
    {
        var message = Parse(sql).Diagnostics.Single().Message;

        Assert.EndsWith($": {sql.TrimEnd(';')}", message);
    }

    [TestMethod]
    [DataRow("CREATE ROLE app PASSWORD hunter2;", "syntax error at or near \"*****\"")]
    [DataRow("ALTER ROLE app PASSWORD = 'hunter2';", "syntax error at or near \"=\"")]
    [DataRow("CREATE SUBSCRIPTION s CONNECTION 'host=h password=hunter2' 'x' PUBLICATION p;", "syntax error at or near \"'x'\"")]
    [DataRow("CREATE SERVER srv FOREIGN DATA WRAPPER w OPTIONS (password hunter2 x);", "syntax error at or near \"*****\"")]
    [DataRow("CREATE TABLE t (a int) FROM FROM;", "syntax error at or near \"FROM\"")]
    public void ASyntaxErrorDoesNotQuoteASecretTheParserStoppedAt(string sql, string expected)
    {
        var error = Parse(sql).Diagnostics.Single(static d => d.Severity == SqlDiagnosticSeverity.Error);

        Assert.AreEqual(expected, error.Message);
        Assert.DoesNotContain("hunter2", error.Message);
        Assert.IsNotNull(error.LineNumber);
        Assert.IsNotNull(error.Column);
    }

    [TestMethod]
    public void ASyntaxErrorAfterNonAsciiTextStillFindsTheSecretBeforeTheCursor()
    {
        var error = Parse("-- é 😀\nCREATE ROLE app PASSWORD hunter2;").Diagnostics.Single(static d => d.Severity == SqlDiagnosticSeverity.Error);

        Assert.AreEqual("syntax error at or near \"*****\"", error.Message);
        Assert.AreEqual(2, error.LineNumber);
    }

    [TestMethod]
    [DataRow("x")]
    [DataRow("averyveryverylongsecretvalue")]
    public void TheMaskIsFiveAsterisksWhateverTheSecretsLength(string secret)
    {
        var message = Parse($"CREATE SUBSCRIPTION s CONNECTION 'password={secret}' PUBLICATION p;").Diagnostics.Single().Message;

        StringAssert.Contains(message, "'password=*****'");
    }

    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);
}
