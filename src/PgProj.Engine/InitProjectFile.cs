using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace PgProj.Engine;

/// <summary>The <c>pgproj.json</c> that <c>init</c> writes (G-82), and what it says about the database it came from.</summary>
internal static partial class InitProjectFile
{
    /// <summary>
    /// What <c>init</c> can't read: every kind of object a live database can hold that the live loader does not read (G-152). One entry per
    /// kind, so that the gap that models a kind (G-146 to G-151, G-132, G-133, G-135) removes its entry here, and so a test can check that each
    /// one is shown. Row-level security and domain structure were listed until G-76 and G-77 made both work end to end (G-108); typed tables,
    /// aggregates, operators, casts, rules, event triggers, and publications are read and are not listed.
    /// </summary>
    internal static readonly string[] UnreadObjectKinds =
    [
        "roles and their settings (role memberships are scaffolded)",
        "text search parsers and templates (a text search configuration and a dictionary are scaffolded)",
        "tablespaces (a table's TABLESPACE clause is scaffolded)",
        "foreign data wrappers, servers, user mappings, and foreign tables",
        "subscriptions",
        "operator classes and families",
        "conversions",
        "base types (CREATE TYPE with INPUT and OUTPUT functions), with their input and output functions",
        "languages",
        "transforms",
        "access methods",
        "security labels",
        "database-level settings",
        "comments on event triggers, rules, operators, aggregates, casts, publications, and a domain's constraints (the objects themselves are scaffolded)",
        "indexes PostgreSQL marks INVALID, left by a failed CREATE INDEX CONCURRENTLY (each is named)",
    ];

    public static string GetDatabaseNameOrDefault(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return string.IsNullOrWhiteSpace(builder.Database) ? "pgproj" : builder.Database;
        }
        catch (Exception)
        {
            return "pgproj";
        }
    }

    /// <summary>
    /// Replaces the value of a <c>Password</c>/<c>Pwd</c> key with <c>&lt;password&gt;</c>, leaving every other
    /// part of the connection string exactly as given (spacing, key casing, key order). Matches the placeholder
    /// style already used in this repository (<c>samples/MultiSchema/pgproj.json</c>).
    /// </summary>
    public static string RedactPassword(string connectionString)
        => PasswordKeyValueRegex().Replace(connectionString, static match => $"{match.Groups["key"].Value}={match.Groups["separator"].Value}<password>{match.Groups["separator"].Value}");

    [GeneratedRegex(@"(?<key>password|pwd)\s*=\s*(?<separator>['""]?)[^;'""]*\k<separator>", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordKeyValueRegex();

    /// <summary>The <c>tenants</c> block <c>init --tenant-prefix</c> writes (G-290d).</summary>
    public sealed record TenantsJson(string Template, string Prefix, string? File, string? Query);

    public static string Build(string name, string source, string connectionString, bool excludeGrants, bool excludeRoleMemberships, bool excludeOwners, IReadOnlyList<string>? schemas = null, IReadOnlyList<string>? searchPath = null, TenantsJson? tenants = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("version", "1.0");
            writer.WriteString("source", source);
            writer.WriteString("sourceConnectionString", connectionString);

            // Only when --schemas or --exclude-schemas chose the schemas (G-157): the ones that were modeled, so a later diff or publish reads just those.
            if (schemas is not null)
            {
                writer.WriteStartArray("schemas");
                foreach (var schema in schemas)
                {
                    writer.WriteStringValue(schema);
                }

                writer.WriteEndArray();
            }

            // Only when a function body or a type names something in another schema without a schema (G-251): the schemas that find those names, then public.
            if (searchPath is { Count: > 0 })
            {
                writer.WriteStartArray("searchPath");
                foreach (var schema in searchPath)
                {
                    writer.WriteStringValue(schema);
                }

                writer.WriteStringValue("public");
                writer.WriteEndArray();
            }

            // Only written when true (G-87): otherwise this matches the default, and the field is omitted the
            // same way schemas/predeploy/postdeploy already are for a value init doesn't have an opinion on.
            if (excludeGrants)
            {
                writer.WriteBoolean("excludeGrants", true);
            }

            if (excludeRoleMemberships)
            {
                writer.WriteBoolean("excludeRoleMemberships", true);
            }

            if (excludeOwners)
            {
                writer.WriteBoolean("excludeOwners", true);
            }

            // A database with a schema per tenant (G-290d): the template, the prefix, and where the tenants come from.
            if (tenants is not null)
            {
                writer.WriteStartObject("tenants");
                writer.WriteString("template", tenants.Template);
                writer.WriteString("prefix", tenants.Prefix);
                if (tenants.File is not null)
                {
                    writer.WriteString("file", tenants.File);
                }

                if (tenants.Query is not null)
                {
                    writer.WriteString("query", tenants.Query);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
