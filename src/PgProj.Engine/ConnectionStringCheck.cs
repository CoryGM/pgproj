using System.Text.RegularExpressions;
using Npgsql;

namespace PgProj.Engine;

/// <summary>
/// What is wrong with a connection string, in words (G-285), before Npgsql's own text (<c>Couldn't set port (Parameter 'port')</c>) reaches the user: a setting
/// Npgsql does not know, a value a setting does not take, or text that is not <c>key=value</c> pairs. A password's value is never shown.
/// </summary>
internal static partial class ConnectionStringCheck
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase) { "password", "pwd", "passfile", "sslpassword", "ssl password" };

    /// <summary>Null when Npgsql accepts <paramref name="connectionString"/>; otherwise the message, starting <c>The connection string is not valid: </c>.</summary>
    public static string? Describe(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
            return null;
        }
        catch (ArgumentException exception)
        {
            return "The connection string is not valid: " + Reason(exception);
        }
    }

    private static string Reason(ArgumentException exception)
    {
        if (string.IsNullOrEmpty(exception.ParamName))
        {
            // "Format of the initialization string does not conform to specification starting at index 15."
            return FormatIndex().Match(exception.Message) is { Success: true } match
                ? $"it is not a list of key=value settings separated by semicolons (the problem starts at character {int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 1})."
                : "it is not a list of key=value settings separated by semicolons.";
        }

        var key = exception.ParamName;
        if (!new NpgsqlConnectionStringBuilder().ContainsKey(key))
        {
            return $"'{key}' is not a setting PostgreSQL connection strings have (for example Host, Port, Database, Username, Password).";
        }

        // The inner exception says why the value was refused, and may quote it; a secret's value is not repeated.
        return SecretKeys.Contains(key) || exception.InnerException is null
            ? $"the value of '{key}' is not one it takes."
            : $"the value of '{key}' is not one it takes: {exception.InnerException.Message}";
    }

    [GeneratedRegex(@"at index (\d+)")]
    private static partial Regex FormatIndex();
}
