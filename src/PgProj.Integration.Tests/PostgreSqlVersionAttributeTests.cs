namespace PgProj.Integration.Tests;

/// <summary>G-269: <see cref="PostgreSqlVersionAttribute"/> reads the major version from the image tag and runs a test only inside its range.</summary>
[TestClass]
public sealed class PostgreSqlVersionAttributeTests
{
    [TestMethod]
    [DataRow("postgres:18-alpine", 18)]
    [DataRow("postgres:14-alpine", 14)]
    [DataRow("postgres:15.8", 15)]
    [DataRow("postgres:16", 16)]
    [DataRow("postgres:latest", 0)]
    public void TheMajorVersionIsReadFromTheImageTag(string image, int expected)
        => Assert.AreEqual(expected, PostgreSqlVersionAttribute.ReadImageMajorVersion(image));

    [TestMethod]
    public void ARangeIncludesTheVersionTheSuiteRunsAgainst()
    {
        var version = PostgreSqlVersionAttribute.ImageMajorVersion;

        Assert.IsTrue(new PostgreSqlVersionAttribute { Minimum = version }.IsConditionMet);
        Assert.IsTrue(new PostgreSqlVersionAttribute { Maximum = version }.IsConditionMet);
        Assert.IsFalse(new PostgreSqlVersionAttribute { Minimum = version + 1 }.IsConditionMet);
        Assert.IsFalse(new PostgreSqlVersionAttribute { Maximum = version - 1 }.IsConditionMet);
        Assert.AreEqual($"Needs PostgreSQL {version + 1} or later; the suite runs against {version}.", new PostgreSqlVersionAttribute { Minimum = version + 1 }.IgnoreMessage);
        Assert.AreEqual($"Only for PostgreSQL {version - 1} or older; the suite runs against {version}.", new PostgreSqlVersionAttribute { Maximum = version - 1 }.IgnoreMessage);
    }
}
