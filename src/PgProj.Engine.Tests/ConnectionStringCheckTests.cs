namespace PgProj.Engine.Tests;

/// <summary>
/// G-285: a malformed connection string is said in words, not Npgsql's (moved from <c>OutputRoughEdgesTests</c> with <c>ConnectionStringCheck</c>, G-316).
/// </summary>
[TestClass]
public sealed class ConnectionStringCheckTests
{
    [TestMethod]
    [DataRow("Host=localhost;Port=notaport", "the value of 'port' is not one it takes")]
    [DataRow("Hots=localhost;Database=x", "'hots' is not a setting PostgreSQL connection strings have")]
    [DataRow("Host=localhost;SslMode=sometimes", "the value of 'sslmode' is not one it takes")]
    [DataRow("Host=localhost;Password", "it is not a list of key=value settings separated by semicolons (the problem starts at character 16)")]
    public void AMalformedConnectionStringIsSaidInWords(string connectionString, string reason)
    {
        Assert.AreEqual("The connection string is not valid: ", ConnectionStringCheck.Describe(connectionString)![..36]);
        StringAssert.Contains(ConnectionStringCheck.Describe(connectionString), reason);
    }

    /// <summary>Moved from the CLI's <c>OutputRoughEdgesTests</c> with the forward it called (G-320).</summary>
    [TestMethod]
    public async Task OpeningAConnectionWithAMalformedStringSaysWhatIsWrong()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LiveComparison.OpenConnectionAsync("Host=localhost;Port=notaport", CancellationToken.None));

        StringAssert.StartsWith(exception.Message, "The connection string is not valid: the value of 'port'");
    }

    [TestMethod]
    public void AValidConnectionStringIsAccepted()
    {
        Assert.IsNull(ConnectionStringCheck.Describe("Host=localhost;Port=5432;Database=x;Username=postgres;Password=secret"));
    }

    [TestMethod]
    public void APasswordIsNotRepeated()
    {
        var message = ConnectionStringCheck.Describe("Host=localhost;Password=hunter2;Port=x")!;

        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }
}
