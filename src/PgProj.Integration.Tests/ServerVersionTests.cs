using System.Text.RegularExpressions;
using Npgsql;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-120: the suite runs against every supported PostgreSQL major version, one run per image (<see cref="PostgresContainerFixture.ImageVariable"/>).
/// This proves each run really talks to the version its image names, so a green run on "18" is a green run on PostgreSQL 18.
/// </summary>
[TestClass]
public sealed partial class ServerVersionTests
{
    [TestMethod]
    public async Task TheServerIsTheMajorVersionTheImageNames()
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::int / 10000", connection);
        var serverMajor = (int)(await command.ExecuteScalarAsync())!;

        var tag = TagMajor().Match(PostgresContainerFixture.Image);
        Assert.IsTrue(tag.Success, $"The image '{PostgresContainerFixture.Image}' names no major version; use a tag such as postgres:17-alpine.");
        Assert.AreEqual(int.Parse(tag.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), serverMajor, PostgresContainerFixture.Image);
    }

    [GeneratedRegex(@":(\d+)(?:[.\-]|$)")]
    private static partial Regex TagMajor();
}
