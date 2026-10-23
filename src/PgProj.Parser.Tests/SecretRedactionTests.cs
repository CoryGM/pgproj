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
    [DataRow("CREATE USER MAPPING FOR bob SERVER srv OPTIONS (user 'u', password 'hunter2');", "CREATE USER MAPPING", "password '*****'")]
    [DataRow("ALTER USER MAPPING FOR bob SERVER srv OPTIONS (SET password 'hunter2');", "ALTER USER MAPPING", "SET password '*****'")]
    [DataRow("CREATE SERVER s FOREIGN DATA WRAPPER w OPTIONS (host 'h', PASSWORD 'hunter2', dbname 'd');", "CREATE SERVER", "PASSWORD '*****'")]
    [DataRow("CREATE ROLE app LOGIN ENCRYPTED PASSWORD 'hunter2';", "CREATE ROLE", "ENCRYPTED PASSWORD '*****'")]
    [DataRow("ALTER ROLE app WITH PASSWORD 'it''s hunter2';", "ALTER ROLE", "WITH PASSWORD '*****'")]
    [DataRow("CREATE SERVER s FOREIGN DATA WRAPPER w OPTIONS (dsn 'postgresql://bob:hunter2@host/db');", "CREATE SERVER", "bob:*****@host")]
    [DataRow("CREATE SERVER s FOREIGN DATA WRAPPER w OPTIONS (api_key 'hunter2', token 'hunter2');", "CREATE SERVER", "api_key '*****', token '*****'")]
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
