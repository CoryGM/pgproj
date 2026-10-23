using System.Globalization;
using System.Text;
using PgProj.Core;

namespace PgProj.Engine;

/// <summary>What the report of <c>pgproj init --report</c> says (G-157): how the command was run, what it modeled, and what it left out.</summary>
/// <param name="Arguments">The command's arguments as they were used, in order, with any secret already masked.</param>
/// <param name="ProjectDirectory">The directory of the new <c>pgproj.json</c>; file paths in the report are relative to it.</param>
/// <param name="SourceDirectory">The directory the SQL files were written to (<c>--output</c>), also where the report is written.</param>
/// <param name="SchemasOptionName">The option that chose the schemas (<c>--schemas</c>, <c>--exclude-schemas</c>), or <see langword="null"/>.</param>
internal sealed record InitReportInput(
    DateTime StartedUtc,
    DateTime EndedUtc,
    IReadOnlyList<(string Name, string Value)> Arguments,
    string Host,
    string Port,
    string ServerVersion,
    string Database,
    string ProjectDirectory,
    string SourceDirectory,
    string? SchemasOptionName,
    ScaffoldResult Result,
    IReadOnlyList<string> UnreadKinds,
    bool ExcludeGrants,
    bool ExcludeRoleMemberships,
    bool ExcludeOwners,
    string? ErdRelativePath = null,
    ErdPlacement? ErdPlacement = null,
    InitTenantSummary? Tenants = null);

/// <summary>What <c>init --tenant-prefix</c> found and did (G-290d), for the report.</summary>
/// <param name="TemplateSchema">The tenant's schema the template was scaffolded from.</param>
/// <param name="TenantSchemas">Every schema named <c>prefix_...</c>.</param>
/// <param name="Differing">The other tenants that differ from the template, with how many objects each would have added, removed, and modified.</param>
/// <param name="LeftOut">Files of the tenant's scaffold that belong to no schema (an extension, a cast, ...), left out of the template.</param>
/// <param name="BodiesNamingSchema">Template files whose function bodies still name the tenant's schema.</param>
internal sealed record InitTenantSummary(
    string Prefix,
    string TemplateSchema,
    IReadOnlyList<string> TenantSchemas,
    IReadOnlyList<(string Schema, int Added, int Removed, int Modified)> Differing,
    IReadOnlyList<string> LeftOut,
    IReadOnlyList<string> BodiesNamingSchema);

/// <summary>Writes the Markdown report of <c>pgproj init --report</c> (G-157).</summary>
internal static class InitReport
{
    private const string NoSchema = "(no schema)";

    /// <summary>The report's file name: <c>init-report-YYYY-MM-DD-HH-MM-SS.md</c>, from the time the command started, in UTC.</summary>
    public static string FileName(DateTime startedUtc)
        => $"init-report-{startedUtc.ToUniversalTime().ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}.md";

    public static string Build(InitReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = input.Result;
        var headings = new List<(int Level, string Text, string Anchor)>();
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        var body = new StringBuilder();

        void Heading(int level, string text)
        {
            var anchor = MakeAnchor(text);
            if (anchors.TryGetValue(anchor, out var seen))
            {
                anchors[anchor] = seen + 1;
                anchor = $"{anchor}-{seen}";
            }
            else
            {
                anchors[anchor] = 1;
            }

            headings.Add((level, text, anchor));
            if (body.Length > 0 && !body.ToString().EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal))
            {
                body.AppendLine();
            }

            body.Append(new string('#', level)).Append(' ').AppendLine(text).AppendLine();
        }

        void Row(params string[] cells) => body.Append("| ").AppendJoin(" | ", cells.Select(Cell)).AppendLine(" |");
        void Header(params string[] cells)
        {
            Row(cells);
            body.Append('|').Append(string.Concat(Enumerable.Repeat("---|", cells.Length))).AppendLine();
        }

        string Relative(string relativeSourcePath)
            => Path.GetRelativePath(input.ProjectDirectory, Path.Combine(input.SourceDirectory, relativeSourcePath.Replace('/', Path.DirectorySeparatorChar))).Replace('\\', '/');

        // Arguments
        Heading(2, "Arguments");
        Header("Argument", "Value");
        foreach (var (name, value) in input.Arguments)
        {
            Row(name, Code(value));
        }

        // Summary
        var objectsBySchema = result.ModeledObjects
            .GroupBy(static item => item.SchemaName ?? NoSchema, StringComparer.Ordinal)
            .OrderBy(static group => group.Key == NoSchema ? 1 : 0)
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Heading(2, "Summary");
        Header("", "");
        Row("Started (UTC)", Utc(input.StartedUtc));
        Row("Ended (UTC)", Utc(input.EndedUtc));
        Row("Elapsed", $"{(input.EndedUtc - input.StartedUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        Row("Server", Code($"{input.Host}:{input.Port}"));
        Row("PostgreSQL version", input.ServerVersion);
        Row("Database", Code(input.Database));
        Row("Schemas modeled", result.ModeledSchemas.Count == 0 ? "none" : string.Join(", ", result.ModeledSchemas.Select(Code)));
        Row("Objects modeled", result.ModeledObjects.Count.ToString(CultureInfo.InvariantCulture));
        if (input.ErdRelativePath is { } erdPath)
        {
            Row("ERD", $"[{Path.GetFileName(erdPath)}]({EscapeLink(erdPath)})");
            if (input.ErdPlacement is { } placement)
            {
                Row("ERD action", placement.Action);
                Row("ERD action reason", placement.Reason);
            }
        }

        Heading(3, "Objects modeled by schema");
        if (objectsBySchema.Length == 0)
        {
            body.AppendLine("Nothing was modeled.");
        }

        foreach (var group in objectsBySchema)
        {
            Heading(4, $"{group.Key} ({Count(group.Count(), "object")})");
            Header("Type", "Count");
            foreach (var type in group.GroupBy(static item => item.ObjectType, StringComparer.Ordinal).OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                Row(type.Key, type.Count().ToString(CultureInfo.InvariantCulture));
            }
        }

        // Modeled objects
        Heading(2, "Modeled objects");
        if (objectsBySchema.Length == 0)
        {
            body.AppendLine("Nothing was modeled.");
        }

        foreach (var group in objectsBySchema)
        {
            Heading(3, group.Key == NoSchema ? "Objects that belong to no schema" : $"Schema {group.Key}");
            Header("Type", "Name", "File");
            foreach (var item in group.OrderBy(static item => item.ObjectType, StringComparer.Ordinal).ThenBy(static item => item.Name, StringComparer.Ordinal))
            {
                Row(item.ObjectType, Code(item.Name), Code(Relative(item.RelativePath)));
            }
        }

        // Not supported
        if (result.UnsupportedObjects.Count > 0 || input.UnreadKinds.Count > 0)
        {
            Heading(2, "Not modeled: not supported");
            body.AppendLine("pgproj cannot read these kinds of object, so they are not in the new project even if the database has them:").AppendLine();
            foreach (var kind in input.UnreadKinds)
            {
                body.Append("- ").AppendLine(kind);
            }

            if (result.UnsupportedObjects.Count > 0)
            {
                body.AppendLine().AppendLine("Found in the database:").AppendLine();
                Header("Schema", "Type", "Name", "Reason");
                foreach (var item in result.UnsupportedObjects
                    .OrderBy(static item => item.SchemaName is null ? 1 : 0)
                    .ThenBy(static item => item.SchemaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.ObjectType, StringComparer.Ordinal)
                    .ThenBy(static item => item.Name, StringComparer.Ordinal))
                {
                    Row(item.SchemaName is null ? "—" : Code(item.SchemaName), item.ObjectType, Code(item.Name), item.ObjectType == UnsupportedObject.InvalidIndex ? "INVALID (a failed CREATE INDEX CONCURRENTLY)" : item.ObjectType == UnsupportedObject.MixedInternalTriggerStates ? "written as the most common state" : "not supported");
                }
            }
        }

        // Excluded
        var categories = new List<(string Option, string LeftOut)>();
        if (input.ExcludeOwners)
        {
            categories.Add(("--exclude-owners", Count(result.ExcludedOwnerCount, "owner")));
        }

        if (input.ExcludeGrants)
        {
            categories.Add(("--exclude-grants", Count(result.ExcludedGrantCount, "grant")));
        }

        if (input.ExcludeRoleMemberships)
        {
            categories.Add(("--exclude-role-memberships", Count(result.ExcludedRoleMembershipCount, "role membership")));
        }

        if (result.ExcludedSchemas.Count > 0 || categories.Count > 0)
        {
            Heading(2, "Not modeled: excluded");
            if (result.ExcludedSchemas.Count > 0)
            {
                Heading(3, "Schemas");
                Header("Schema", "Reason");
                var reason = input.SchemasOptionName == "--schemas" ? "not named in --schemas" : "excluded (--exclude-schemas)";
                foreach (var schema in result.ExcludedSchemas.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
                {
                    Row(Code(schema), reason);
                }

                body.AppendLine().AppendLine("The objects in these schemas are not listed.");
                if (result.DanglingReferences.Count > 0)
                {
                    body.AppendLine().AppendLine("Files that refer to an excluded schema (a foreign key, a column type, a view's source); `pgproj build` cannot resolve these in the new project:").AppendLine();
                    Header("File", "Excluded schema", "References");
                    foreach (var reference in result.DanglingReferences.OrderBy(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.ExcludedSchema, StringComparer.OrdinalIgnoreCase))
                    {
                        Row(Code(Relative(reference.RelativePath)), Code(reference.ExcludedSchema), reference.Count.ToString(CultureInfo.InvariantCulture));
                    }

                    body.AppendLine().AppendLine("This is a search of the generated SQL for `schema.` and `\"schema\".`. PostgreSQL prints names in `public` without the schema, so a reference into an excluded `public` is not found.");
                }
            }

            if (categories.Count > 0)
            {
                Heading(3, "Categories");
                Header("Option", "Left out");
                foreach (var (option, leftOut) in categories)
                {
                    Row(option, leftOut);
                }
            }
        }

        // Tenants (G-290d)
        if (input.Tenants is { } tenants)
        {
            Heading(2, "Tenants");
            body.AppendLine($"The database has {tenants.TenantSchemas.Count} tenant schema(s), named `{tenants.Prefix}_...`. The template (the `Tenant` folder) was scaffolded from `{tenants.TemplateSchema}`, with its schema name taken out; where it names the schema itself, it names the stand-in `{tenants.Prefix}`.").AppendLine();
            body.AppendLine(tenants.Differing.Count == 0
                ? "Every other tenant matches the template."
                : $"{tenants.Differing.Count} tenant(s) differ from the template. `pgproj diff` shows how, and `pgproj publish` would make each match it:").AppendLine();
            if (tenants.Differing.Count > 0)
            {
                Header("Tenant", "Added", "Removed", "Modified");
                foreach (var (schema, added, removed, modified) in tenants.Differing)
                {
                    Row(Code(schema), added.ToString(System.Globalization.CultureInfo.InvariantCulture), removed.ToString(System.Globalization.CultureInfo.InvariantCulture), modified.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                body.AppendLine();
            }

            if (tenants.LeftOut.Count > 0)
            {
                body.AppendLine("Left out of the template, since they belong to the whole database rather than a schema: " + string.Join(", ", tenants.LeftOut.Select(Code)) + ".").AppendLine();
            }

            if (tenants.BodiesNamingSchema.Count > 0)
            {
                body.AppendLine($"Function bodies that still name `{tenants.TemplateSchema}` (a body is kept as written): " + string.Join(", ", tenants.BodiesNamingSchema.Select(Code)) + ".").AppendLine();
            }

            body.AppendLine("Tenant schemas: " + string.Join(", ", tenants.TenantSchemas.Select(Code)) + ".").AppendLine();
        }

        // Cluster-wide
        if (result.ClusterWideObjects.Count > 0)
        {
            Heading(2, "Not modeled: cluster-wide");
            body.AppendLine("Roles, tablespaces, and subscriptions belong to the whole server, not to a schema; pgproj reports them and does not model them. Roles are the ones that own, or hold a privilege or membership on, an object that was modeled.").AppendLine();
            Header("Type", "Name");
            foreach (var item in result.ClusterWideObjects.OrderBy(static item => item.ObjectType, StringComparer.Ordinal).ThenBy(static item => item.Name, StringComparer.Ordinal))
            {
                Row(item.ObjectType, Code(item.Name));
            }
        }

        var report = new StringBuilder();
        report.AppendLine("# pgproj init report").AppendLine().AppendLine("## Contents").AppendLine();
        foreach (var (level, text, anchor) in headings)
        {
            report.Append(' ', (level - 2) * 2).Append("- [").Append(text).Append("](#").Append(anchor).AppendLine(")");
        }

        if (input.ErdRelativePath is { } erdLink)
        {
            report.Append("- [Entity relationship diagram](").Append(EscapeLink(erdLink)).AppendLine(")");
        }

        report.Append(body);
        return report.ToString();
    }

    /// <summary>A heading's anchor as GitHub and VS Code make it: lower case, spaces as hyphens, punctuation other than <c>-</c> and <c>_</c> dropped.</summary>
    internal static string MakeAnchor(string heading)
    {
        var builder = new StringBuilder();
        foreach (var character in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
            }
            else if (character == ' ')
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    /// <summary>A relative path as a Markdown link target: each segment percent-encoded, the separators kept.</summary>
    private static string EscapeLink(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string Count(int count, string noun) => $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Code(string value) => value.Contains('`', StringComparison.Ordinal) ? Cell(value) : $"`{value}`";

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
