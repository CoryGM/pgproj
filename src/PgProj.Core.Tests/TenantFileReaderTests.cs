namespace PgProj.Core.Tests;

/// <summary>
/// G-290: the CSV file of tenants has a header naming a <c>tenant</c> column, which holds each tenant's identifier exactly as written; its schema is
/// <c>prefix_identifier</c>. Every problem is reported with its line, all together.
/// </summary>
[TestClass]
public sealed class TenantFileReaderTests
{
    [TestMethod]
    public void TheTenantColumnIsReadWithItsLinesAndOtherColumnsAreIgnored()
    {
        var entries = TenantFileReader.Parse("plan,tenant,region\ngold,acme,eu\nsilver,globex,us\n", "t.csv", "tenant");

        CollectionAssert.AreEqual(new[] { "acme", "globex" }, entries.Select(static entry => entry.Identifier).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 3 }, entries.Select(static entry => entry.Line).ToArray());
    }

    [TestMethod]
    public void AnIdentifierKeepsItsCaseAndTheHeaderNameIgnoresCase()
    {
        var entries = TenantFileReader.Parse("Tenant\nAcme\nacme\n", "t.csv", "tenant");

        CollectionAssert.AreEqual(new[] { "Acme", "acme" }, entries.Select(static entry => entry.Identifier).ToArray());
    }

    [TestMethod]
    public void QuotedFieldsHoldCommasQuotesAndLineBreaksAndTheLinesStayRight()
    {
        var entries = TenantFileReader.Parse("tenant,note\r\n\"a,b\",x\r\n\"say \"\"hi\"\"\",\"two\r\nlines\"\r\nlast,y", "t.csv", "tenant");

        CollectionAssert.AreEqual(new[] { "a,b", "say \"hi\"", "last" }, entries.Select(static entry => entry.Identifier).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 3, 5 }, entries.Select(static entry => entry.Line).ToArray());
    }

    [TestMethod]
    public void ABomAndBlankLinesAreSkipped()
    {
        var entries = TenantFileReader.Parse("﻿\ntenant\n\nacme\n\n", "t.csv", "tenant");

        Assert.AreEqual("acme", entries.Single().Identifier);
        Assert.AreEqual(4, entries.Single().Line);
    }

    [TestMethod]
    public void AFileWithOnlyAHeaderListsNoTenants()
        => Assert.AreEqual(0, TenantFileReader.Parse("tenant\n", "t.csv", "tenant").Count);

    [TestMethod]
    [DataRow("", "is empty")]
    [DataRow("name\nacme\n", "has no 'tenant' column")]
    [DataRow("tenant\n\"acme\n", "t.csv:2: a quoted field is never closed")]
    [DataRow("tenant\nac\"me\n", "t.csv:2: a double quote inside a field")]
    [DataRow("tenant\n\"acme\"x\n", "t.csv:2: text after a quoted field")]
    public void AFileThatCannotBeReadIsRefused(string text, string expected)
    {
        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => TenantFileReader.Parse(text, "t.csv", "tenant"));

        StringAssert.Contains(exception.Message, expected);
    }

    [TestMethod]
    public void EveryBadLineIsReportedWithItsLine()
    {
        var longIdentifier = new string('x', 57);
        var text = $"plan,tenant\ngold,acme\nsilver\nbronze,\ngold,acme\ngold, padded\ngold,{longIdentifier}\n";

        var exception = Assert.ThrowsExactly<PgProjectValidationException>(() => TenantFileReader.Parse(text, "t.csv", "tenant"));

        StringAssert.Contains(exception.Message, "5 problem(s)");
        StringAssert.Contains(exception.Message, "t.csv:3: the line has no 'tenant' value");
        StringAssert.Contains(exception.Message, "t.csv:4: the 'tenant' value is empty");
        StringAssert.Contains(exception.Message, "t.csv:5: the tenant 'acme' is listed again (first on line 2)");
        StringAssert.Contains(exception.Message, "t.csv:6: the tenant ' padded' starts or ends with a space");
        StringAssert.Contains(exception.Message, "t.csv:7: the tenant '" + longIdentifier + "' makes the schema name 'tenant_" + longIdentifier + "', 64 bytes long");
    }

    [TestMethod]
    public void ASchemaNameOfExactlySixtyThreeBytesIsAccepted()
        => Assert.AreEqual(1, TenantFileReader.Parse("tenant\n" + new string('x', 56) + "\n", "t.csv", "tenant").Count);

    [TestMethod]
    public void TheLimitCountsBytesNotCharacters()
    {
        // 'é' is two bytes in UTF-8: 7 + 28 * 2 = 63 bytes fits, one more does not.
        Assert.AreEqual(1, TenantFileReader.Parse("tenant\n" + new string('é', 28) + "\n", "t.csv", "tenant").Count);
        Assert.ThrowsExactly<PgProjectValidationException>(() => TenantFileReader.Parse("tenant\n" + new string('é', 29) + "\n", "t.csv", "tenant"));
    }
}
