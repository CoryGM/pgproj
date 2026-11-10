using System.Collections;
using PgProj.Engine;

namespace PgProj.Cli;

/// <summary>
/// The values of the deploy scripts' variables (G-336, the user's design of 2026-11-09): each environment variable <c>PGPROJ_VAR_&lt;name&gt;</c> is the value of
/// <c>:'name'</c> and <c>:"name"</c>. Reading the environment is the shell's (as for the connection variables); the engine gets them as
/// <see cref="PublishRequest.Variables"/>, fills them in only for <c>--execute</c>, and masks them.
/// </summary>
public static class ScriptVariableSettings
{
    /// <summary>The prefix, as the engine words it.</summary>
    public const string Prefix = PublishTexts.VariablePrefix;

    /// <summary>The variables of this process's environment.</summary>
    public static IReadOnlyDictionary<string, string> Read() => Read(Environment.GetEnvironmentVariables());

    /// <summary>
    /// The variables of <paramref name="environment"/>: the name after the prefix as written (the prefix itself in any case, since Windows does not keep
    /// it), the value as it is; a variable with nothing after the prefix is left out.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && key.Length > Prefix.Length && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && entry.Value is string value)
            {
                variables[key[Prefix.Length..]] = value;
            }
        }

        return variables;
    }
}
