using System.Text;
using Npgsql;
using PgProj.Core;
using PgProj.Parser;

namespace PgProj.Engine;

/// <summary>
/// <c>init</c> for a database with a schema per tenant (G-290d, the user's decision 5 on #168): the tenants are the schemas named <c>prefix_...</c>; the
/// shared schemas are scaffolded as usual without them; one tenant (the first by name, or the one named) is scaffolded into the template folder with its
/// schema name taken out; and the other tenants are compared with the template, so the ones that differ are named.
/// </summary>
internal static class InitTenants
{
    /// <summary>The template folder under the output directory.</summary>
    public const string TemplateFolder = "Tenant";

    /// <summary>The tenants found, and the one the template is scaffolded from.</summary>
    public sealed record Choice(string Prefix, IReadOnlyList<string> TenantSchemas, string TemplateSchema)
    {
        public IEnumerable<string> Identifiers => TenantSchemas.Select(schema => schema[(Prefix.Length + 1)..]);
    }

    /// <summary>The template scaffolded from a tenant: its files (under <see cref="TemplateFolder"/>), and what was left out or still names the tenant.</summary>
    public sealed record Template(IReadOnlyList<ScaffoldedFile> Files, IReadOnlyList<string> LeftOut, IReadOnlyList<string> BodiesNamingSchema);

    /// <summary>The prefix (folded as a name typed outside SQL is, G-206), the database's tenant schemas, and the template's tenant; an error when there is none.</summary>
    public static async Task<Choice> ChooseAsync(NpgsqlConnection connection, string prefixOption, string? fromIdentifier, CancellationToken cancellationToken)
    {
        var prefix = SqlIdentifiers.FoldName(prefixOption.Trim());
        if (prefix.Length == 0 || prefix.StartsWith("pg_", StringComparison.Ordinal))
        {
            throw new RefusedException($"--tenant-prefix '{prefixOption}' is not a prefix pgproj can use: it is empty or starts with pg_, which PostgreSQL keeps for its own schemas.");
        }

        var schemas = await TenantDiscovery.FindTenantSchemasAsync(connection, prefix, cancellationToken);
        if (schemas.Count == 0)
        {
            throw new RefusedException($"The database has no schema named {prefix}_<identifier>, so there is no tenant to scaffold the template from. Check --tenant-prefix.");
        }

        var template = fromIdentifier is null ? schemas[0] : prefix + "_" + fromIdentifier.Trim();
        if (!schemas.Contains(template, StringComparer.Ordinal))
        {
            throw new RefusedException($"--tenant-from names '{fromIdentifier}', but the database has no schema {template}. The tenants are: {string.Join(", ", schemas.Take(10))}{(schemas.Count > 10 ? ", ..." : string.Empty)}.");
        }

        return new Choice(prefix, schemas, template);
    }

    /// <summary>
    /// The tenant's scaffold as the template: each file under <c>Schema/&lt;tenant&gt;/</c> moved under <see cref="TemplateFolder"/> (the schema's own file
    /// named for the stand-in), its text rewritten by <see cref="TenantSchemaRewriter"/>; a file outside the schema (an extension, a cast, an event trigger,
    /// a publication) belongs to the whole database and is left out.
    /// </summary>
    public static Template ToTemplate(IReadOnlyList<ScaffoldedFile> tenantFiles, string schema, string standIn)
    {
        var prefix = $"Schema/{schema}/";
        var files = new List<ScaffoldedFile>();
        var leftOut = new List<string>();
        var bodies = new List<string>();
        foreach (var file in tenantFiles)
        {
            var path = file.RelativePath.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                leftOut.Add(path);
                continue;
            }

            var rest = path[prefix.Length..];
            var target = string.Equals(rest, schema + ".sql", StringComparison.Ordinal) ? standIn + ".sql" : rest;
            var rewritten = TenantSchemaRewriter.Rewrite(file.Content, schema, standIn);
            if (rewritten.NamesSchemaInBody)
            {
                bodies.Add($"{TemplateFolder}/{target}");
            }

            files.Add(new ScaffoldedFile($"{TemplateFolder}/{target}", rewritten.Text));
        }

        return new Template(files, leftOut, bodies);
    }

    /// <summary>The CSV file of tenants (G-290a's format): a <c>tenant</c> header, then each identifier, quoted when it needs to be.</summary>
    public static string BuildCsv(IEnumerable<string> identifiers)
    {
        var csv = new StringBuilder("tenant\n");
        foreach (var identifier in identifiers)
        {
            csv.Append(identifier.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : identifier).Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>
    /// The tenants other than the template's that differ from the template, by comparing each with the new project as it was read back, as <c>diff</c> does
    /// (G-290b). With how many objects <c>publish</c> would add, remove, and change in each.
    /// </summary>
    public static async Task<IReadOnlyList<(string Schema, int Added, int Removed, int Modified)>> FindDifferingAsync(
        LiveComparison live,
        NpgsqlConnection connection,
        ModelBuild built,
        Choice choice,
        bool excludeGrants,
        bool excludeRoleMemberships,
        bool excludeOwners,
        CancellationToken cancellationToken)
    {
        var (project, workspace, trees, model) = built;
        if (model.HasErrors)
        {
            return [];
        }

        await using (var read = new NpgsqlCommand("select pg_catalog.current_setting('search_path')", connection))
        {
            var previous = (string?)await read.ExecuteScalarAsync(cancellationToken);
            try
            {
                var sharedTrees = trees.Where(static tree => tree.SearchPath is null).ToArray();
                var differing = new List<(string, int, int, int)>();
                foreach (var schema in choice.TenantSchemas.Where(schema => !string.Equals(schema, choice.TemplateSchema, StringComparison.Ordinal)))
                {
                    var tenant = new TenantTarget(schema[(choice.Prefix.Length + 1)..], schema, Exists: true);
                    var (_, _, changes) = await TenantComparison.CompareTenantAsync(live, connection, project, workspace, sharedTrees, tenant, excludeGrants, excludeRoleMemberships, excludeOwners, cancellationToken);
                    if (changes.Added.Count + changes.Removed.Count + changes.Modified.Count > 0)
                    {
                        differing.Add((schema, changes.Added.Count, changes.Removed.Count, changes.Modified.Count));
                    }
                }

                return differing;
            }
            finally
            {
                await using var restore = new NpgsqlCommand("select pg_catalog.set_config('search_path', @path, false)", connection);
                restore.Parameters.AddWithValue("path", previous ?? "public");
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }
}
