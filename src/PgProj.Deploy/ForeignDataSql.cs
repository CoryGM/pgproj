using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy;

/// <summary>
/// The SQL for foreign data wrappers, servers, and user mappings (G-132). An option's value is written as the project wrote it, a variable as <c>:'name'</c>, which only
/// <c>publish --execute</c> fills in (G-336), so a printed or written script keeps it.
/// </summary>
public static class ForeignDataSql
{
    public static string CreateWrapper(ForeignDataWrapperDefinition wrapper)
        => $"CREATE FOREIGN DATA WRAPPER {Quote(wrapper.Name)}"
            + (wrapper.Handler is { } handler ? $" HANDLER {handler}" : string.Empty)
            + (wrapper.Validator is { } validator ? $" VALIDATOR {validator}" : string.Empty)
            + Options(wrapper.Options) + ";"
            + Comment("FOREIGN DATA WRAPPER", wrapper.Name, wrapper.Comment);

    public static string CreateServer(ForeignServerDefinition server)
        => $"CREATE SERVER {Quote(server.Name)}"
            + (server.ServerType is { } type ? $" TYPE {Literal(type)}" : string.Empty)
            + (server.Version is { } version ? $" VERSION {Literal(version)}" : string.Empty)
            + $" FOREIGN DATA WRAPPER {Quote(server.Wrapper)}" + Options(server.Options) + ";"
            + Comment("SERVER", server.Name, server.Comment);

    public static string CreateMapping(UserMappingDefinition mapping)
        => $"CREATE USER MAPPING FOR {Role(mapping.Role)} SERVER {Quote(mapping.Server)}{Options(mapping.Options)};";

    public static string DropWrapper(ForeignDataWrapperDefinition wrapper) => $"DROP FOREIGN DATA WRAPPER {Quote(wrapper.Name)};";

    public static string DropServer(ForeignServerDefinition server) => $"DROP SERVER {Quote(server.Name)};";

    public static string DropMapping(UserMappingDefinition mapping) => $"DROP USER MAPPING FOR {Role(mapping.Role)} SERVER {Quote(mapping.Server)};";

    /// <summary>A wrapper changed in place: its handler, validator, options, and comment.</summary>
    public static string AlterWrapper(ForeignDataWrapperDefinition current, ForeignDataWrapperDefinition desired)
    {
        var statements = new List<string>();
        var target = $"ALTER FOREIGN DATA WRAPPER {Quote(desired.Name)}";
        if (!string.Equals(current.Handler, desired.Handler, StringComparison.Ordinal))
        {
            statements.Add($"{target} {(desired.Handler is { } handler ? $"HANDLER {handler}" : "NO HANDLER")};");
        }

        if (!string.Equals(current.Validator, desired.Validator, StringComparison.Ordinal))
        {
            statements.Add($"{target} {(desired.Validator is { } validator ? $"VALIDATOR {validator}" : "NO VALIDATOR")};");
        }

        AddOptionChanges(statements, target, current.Options, desired.Options);
        AddCommentChange(statements, "FOREIGN DATA WRAPPER", desired.Name, current.Comment, desired.Comment);
        return string.Join("\n", statements);
    }

    /// <summary>A server changed in place: its version and options, and its comment. A changed wrapper or <c>TYPE</c> drops and creates it instead.</summary>
    public static string AlterServer(ForeignServerDefinition current, ForeignServerDefinition desired)
    {
        var statements = new List<string>();
        var target = $"ALTER SERVER {Quote(desired.Name)}";
        if (desired.Version is { } version && !string.Equals(current.Version, version, StringComparison.Ordinal))
        {
            statements.Add($"{target} VERSION {Literal(version)};");
        }

        AddOptionChanges(statements, target, current.Options, desired.Options);
        AddCommentChange(statements, "SERVER", desired.Name, current.Comment, desired.Comment);
        return string.Join("\n", statements);
    }

    /// <summary>A user mapping's options changed in place.</summary>
    public static string AlterMapping(UserMappingDefinition current, UserMappingDefinition desired)
    {
        var statements = new List<string>();
        AddOptionChanges(statements, $"ALTER USER MAPPING FOR {Role(desired.Role)} SERVER {Quote(desired.Server)}", current.Options, desired.Options);
        return string.Join("\n", statements);
    }

    /// <summary>
    /// <c>publish --apply-secrets</c> (G-336 rotation (c)): the options of <paramref name="desired"/> that name one of <paramref name="variables"/> (all of its variables
    /// when null), sent again to a mapping that exists, <c>SET</c> where it has the option and <c>ADD</c> where it does not; null when it names none of them.
    /// </summary>
    public static string? ApplySecrets(UserMappingDefinition current, UserMappingDefinition desired, IReadOnlySet<string>? variables)
    {
        var options = desired.Options.Where(option => option.Variable is { } variable && (variables is null || variables.Contains(variable))).ToArray();
        if (options.Length == 0)
        {
            return null;
        }

        var changes = options.Select(option => $"{(current.Options.Any(existing => existing.Name == option.Name) ? "SET" : "ADD")} {QuoteOption(option.Name)} {option.ValueSql}");
        return $"ALTER USER MAPPING FOR {Role(desired.Role)} SERVER {Quote(desired.Server)} OPTIONS ({string.Join(", ", changes)});";
    }

    private static void AddOptionChanges(List<string> statements, string target, IReadOnlyList<ForeignOptionDefinition> current, IReadOnlyList<ForeignOptionDefinition> desired)
    {
        var differences = ForeignOptions.Differences(current, desired);
        if (differences.Count == 0)
        {
            return;
        }

        var desiredByName = desired.ToDictionary(static option => option.Name, StringComparer.Ordinal);
        var changes = differences.Select(difference => difference.Action == "DROP"
            ? $"DROP {QuoteOption(difference.Name)}"
            : $"{difference.Action} {QuoteOption(difference.Name)} {desiredByName[difference.Name].ValueSql}");
        statements.Add($"{target} OPTIONS ({string.Join(", ", changes)});");
    }

    private static void AddCommentChange(List<string> statements, string kind, string name, string? current, string? desired)
    {
        if (!string.Equals(current, desired, StringComparison.Ordinal))
        {
            statements.Add($"COMMENT ON {kind} {Quote(name)} IS {(desired is null ? "NULL" : Literal(desired))};");
        }
    }

    private static string Options(IReadOnlyList<ForeignOptionDefinition> options)
        => options.Count == 0 ? string.Empty : $" OPTIONS ({string.Join(", ", options.Select(static option => $"{QuoteOption(option.Name)} {option.ValueSql}"))})";

    private static string Comment(string kind, string name, string? comment)
        => comment is null ? string.Empty : $"\nCOMMENT ON {kind} {Quote(name)} IS {Literal(comment)};";

    /// <summary><c>PUBLIC</c> and <c>CURRENT_USER</c> as keywords; a role by its name, quoted.</summary>
    public static string Role(string role)
        => role is "PUBLIC" or UserMappingNode.CurrentUser ? role : Quote(role);

    private static string QuoteOption(string name) => SqlIdentifiers.QuotePlain(name);

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
