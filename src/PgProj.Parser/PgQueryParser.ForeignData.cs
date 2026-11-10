using System.Text.Json;

namespace PgProj.Parser;

/// <summary>Foreign data wrappers, servers, and user mappings (G-132).</summary>
public sealed partial class PgQueryParser
{
    /// <summary>
    /// A variable, <c>:'name'</c>, in a project's SQL (marked by <see cref="ScriptVariables.MarkSlots"/>) is allowed only as an option value of <c>CREATE SERVER</c> or
    /// <c>CREATE USER MAPPING</c> (G-336's designated slots, G-132); anywhere else it is an error, since pgproj would deploy the text as a value.
    /// </summary>
    private static void ReportMisplacedVariables(string sql, JsonElement rawStatement, JsonElement statementBody, ICollection<SqlDiagnostic> diagnostics)
    {
        var location = TryGetDirectInt32Property(rawStatement, "stmt_location", out var statementLocation) ? statementLocation : 0;
        var length = TryGetDirectInt32Property(rawStatement, "stmt_len", out var statementLength) ? statementLength : 0;
        var text = GetStatementSql(sql, rawStatement);
        var inText = text.Count(static character => character == ScriptVariables.SlotMarker);
        if (inText == 0)
        {
            return;
        }

        var inSlots = 0;
        foreach (var property in statementBody.EnumerateObject().Where(static property => property.Name is "CreateForeignServerStmt" or "CreateUserMappingStmt"))
        {
            inSlots = ReadOptions(property.Value).Count(static option => option.Variable is not null);
        }

        if (inSlots == inText)
        {
            return;
        }

        var range = UnmodeledStatementReader.DescribeWithRange(sql, location, length);
        diagnostics.Add(new SqlDiagnostic(
            SqlDiagnosticSeverity.Error,
            $"A variable (:'name') is allowed only as an OPTIONS value of CREATE SERVER or CREATE USER MAPPING, where it names a value given at publish time; here it would be deployed as text: {range.Snippet.Replace(ScriptVariables.SlotMarker.ToString(), ":", StringComparison.Ordinal)}",
            nameof(PgQueryParser),
            range.Line)
        {
            Column = range.Column,
            EndLine = range.EndLine,
            EndColumn = range.EndColumn,
        });
    }

    /// <summary>A <c>CreateFdwStmt</c>: <c>fdwname</c>, <c>func_options</c> (<c>handler</c> and <c>validator</c>, each a name list, or no argument for <c>NO ...</c>), and <c>options</c>.</summary>
    private static ForeignDataWrapperNode? TryCreateForeignDataWrapperNode(JsonElement element)
    {
        if (TryGetDirectStringProperty(element, "fdwname") is not { Length: > 0 } name)
        {
            return null;
        }

        string? handler = null;
        string? validator = null;
        if (TryGetDirectProperty(element, "func_options", out var functions) && functions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in functions.EnumerateArray())
            {
                if (!item.TryGetProperty("DefElem", out var option) || TryGetDirectStringProperty(option, "defname") is not { } optionName)
                {
                    continue;
                }

                var function = option.TryGetProperty("arg", out var argument) && argument.TryGetProperty("List", out var list) && list.TryGetProperty("items", out var items)
                    ? string.Join('.', items.EnumerateArray().Select(static part => part.TryGetProperty("String", out var value) && value.TryGetProperty("sval", out var text) ? text.GetString() : null).OfType<string>())
                    : null;
                if (optionName == "handler")
                {
                    handler = function;
                }
                else if (optionName == "validator")
                {
                    validator = function;
                }
            }
        }

        return new ForeignDataWrapperNode(name) { Handler = handler, Validator = validator, Options = ReadOptions(element) };
    }

    /// <summary>A <c>CreateForeignServerStmt</c>: <c>servername</c>, <c>servertype</c>, <c>version</c>, <c>fdwname</c>, and <c>options</c>.</summary>
    private static ForeignServerNode? TryCreateForeignServerNode(JsonElement element)
        => TryGetDirectStringProperty(element, "servername") is { Length: > 0 } name && TryGetDirectStringProperty(element, "fdwname") is { Length: > 0 } wrapper
            ? new ForeignServerNode(name, wrapper)
            {
                ServerType = TryGetDirectStringProperty(element, "servertype"),
                Version = TryGetDirectStringProperty(element, "version"),
                Options = ReadOptions(element),
            }
            : null;

    /// <summary>A <c>CreateUserMappingStmt</c>: <c>user</c> (a <c>RoleSpec</c>), <c>servername</c>, and <c>options</c>.</summary>
    private static UserMappingNode? TryCreateUserMappingNode(JsonElement element)
    {
        if (TryGetDirectStringProperty(element, "servername") is not { Length: > 0 } server || !TryGetDirectProperty(element, "user", out var user))
        {
            return null;
        }

        var role = TryGetDirectStringProperty(user, "roletype") switch
        {
            "ROLESPEC_PUBLIC" => "PUBLIC",
            "ROLESPEC_CURRENT_USER" or "ROLESPEC_CURRENT_ROLE" or "ROLESPEC_SESSION_USER" => UserMappingNode.CurrentUser,
            _ => TryGetDirectStringProperty(user, "rolename"),
        };
        return role is null ? null : new UserMappingNode(role, server) { Options = ReadOptions(element) };
    }

    /// <summary>The <c>options</c> of a foreign data statement, each a <c>DefElem</c> whose argument is a string, in the order written.</summary>
    private static List<ForeignOptionNode> ReadOptions(JsonElement element)
    {
        var options = new List<ForeignOptionNode>();
        if (!TryGetDirectProperty(element, "options", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return options;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("DefElem", out var option) && TryGetDirectStringProperty(option, "defname") is { } name)
            {
                var value = option.TryGetProperty("arg", out var argument) && argument.TryGetProperty("String", out var text) && text.TryGetProperty("sval", out var sval) ? sval.GetString() : null;
                options.Add(new ForeignOptionNode(name, value));
            }
        }

        return options;
    }
}
