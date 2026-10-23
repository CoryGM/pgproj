using System.Globalization;
using System.Text.RegularExpressions;

namespace PgProj.Integration.Tests;

/// <summary>
/// Runs a test or test class only against the PostgreSQL major versions it is for (G-269, the user's request of 2026-10-13), and shows it as skipped, with the reason,
/// on any other: <c>[PostgreSqlVersion(Minimum = 18)]</c> for a feature 18 added, <c>[PostgreSqlVersion(Maximum = 17)]</c> for the refusal on a server older than 18.
/// The version is the major version of the image the suite runs against (<see cref="PostgresContainerFixture.Image"/>), which <see cref="ServerVersionTests"/> checks
/// is the server's, so deciding needs no connection. It replaces the inline <c>Assert.Inconclusive</c> version checks the tests had.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed partial class PostgreSqlVersionAttribute : ConditionBaseAttribute
{
    private int _minimum;
    private int _maximum = int.MaxValue;

    public PostgreSqlVersionAttribute()
        : base(ConditionMode.Include)
    {
        IgnoreMessage = Describe();
    }

    /// <summary>The oldest major version the test runs on; none when not set.</summary>
    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            IgnoreMessage = Describe();
        }
    }

    /// <summary>The newest major version the test runs on; none when not set.</summary>
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            IgnoreMessage = Describe();
        }
    }

    /// <summary>The major version the suite runs against, from the image's tag (<c>postgres:15-alpine</c> is 15).</summary>
    public static int ImageMajorVersion { get; } = ReadImageMajorVersion(PostgresContainerFixture.Image);

    public override bool IsConditionMet => ImageMajorVersion >= _minimum && ImageMajorVersion <= _maximum;

    public override string GroupName => nameof(PostgreSqlVersionAttribute);

    /// <summary>The major version an image tag names, or 0 when it names none (then a test with a minimum is skipped and <see cref="ServerVersionTests"/> fails).</summary>
    internal static int ReadImageMajorVersion(string image)
        => TagMajor().Match(image) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    private string Describe()
        => (_minimum, _maximum) switch
        {
            (> 0, int.MaxValue) => $"Needs PostgreSQL {_minimum} or later; the suite runs against {ImageMajorVersion}.",
            (0, < int.MaxValue) => $"Only for PostgreSQL {_maximum} or older; the suite runs against {ImageMajorVersion}.",
            _ => $"Only for PostgreSQL {_minimum} to {_maximum}; the suite runs against {ImageMajorVersion}.",
        };

    [GeneratedRegex(@":(\d+)(?:[.\-]|$)")]
    private static partial Regex TagMajor();
}
