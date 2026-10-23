using System.Globalization;
using System.Text;
using Npgsql;
using PgProj.Core;

namespace PgProj.Engine;

/// <summary>A tenant a run works on (G-290): its identifier, its schema (<c>prefix_identifier</c>), and whether the database has that schema yet.</summary>
public sealed record TenantTarget(string Identifier, string Schema, bool Exists);

/// <summary>
/// The tenants of one run (G-290): those the project lists (narrowed by <c>--tenants</c>), the schemas named as tenants' are that the query or file no
/// longer lists (former tenants, which pgproj never drops), and, when the query could not run, why the tenants were not read.
/// </summary>
public sealed record TenantList(IReadOnlyList<TenantTarget> Tenants, IReadOnlyList<string> FormerSchemas, string? Unavailable);

/// <summary>
/// Finds the tenants in the connected database (G-290): with the prefix alone, its schemas named <c>prefix_...</c>; with a query, the identifiers the
/// query returns, run in a read-only transaction; with a CSV file, the identifiers it lists. In query and file mode, a schema named as a tenant's is that
/// is not listed is a former tenant.
/// </summary>
internal static class TenantDiscovery
{
    public static async Task<TenantList> FindAsync(NpgsqlConnection connection, PgProjectTenants tenants, IReadOnlyList<string> only, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(only);

        var existing = await ReadTenantSchemasAsync(connection, tenants.Prefix, cancellationToken);
        IReadOnlyList<string> identifiers;
        switch (tenants.Source)
        {
            case TenantSource.File:
                identifiers = [.. tenants.FileTenants.Select(static entry => entry.Identifier)];
                break;
            case TenantSource.Query:
                var (returned, unavailable) = await RunQueryAsync(connection, tenants, cancellationToken);
                if (unavailable is not null)
                {
                    return new TenantList([], [], unavailable);
                }

                identifiers = returned;
                break;
            default:
                identifiers = [.. existing.Select(schema => schema[(tenants.Prefix.Length + 1)..])];
                break;
        }

        var listedSchemas = identifiers.Select(tenants.SchemaFor).ToHashSet(StringComparer.Ordinal);
        var former = tenants.Source == TenantSource.Schemas
            ? []
            : existing.Where(schema => !listedSchemas.Contains(schema)).Order(StringComparer.Ordinal).ToArray();

        if (only.Count > 0)
        {
            var known = identifiers.ToHashSet(StringComparer.Ordinal);
            var unknown = only.Where(identifier => !known.Contains(identifier)).ToArray();
            if (unknown.Length > 0)
            {
                var where = tenants.Source switch
                {
                    TenantSource.File => $"the tenant file {tenants.FilePath} does not list",
                    TenantSource.Query => "the tenant query does not return",
                    _ => $"the database has no schema {tenants.Prefix}_<identifier> for",
                };
                throw new RefusedException(
                    $"--tenants names {string.Join(", ", unknown.Select(static identifier => $"'{identifier}'"))}, which {where}. --tenants takes tenant identifiers (acme, not {tenants.Prefix}_acme) "
                    + "of tenants the project already has.");
            }

            identifiers = [.. identifiers.Where(only.Contains)];
        }

        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var targets = identifiers
            .Select(identifier => new TenantTarget(identifier, tenants.SchemaFor(identifier), existingSet.Contains(tenants.SchemaFor(identifier))))
            .OrderBy(static target => target.Schema, StringComparer.Ordinal)
            .ToArray();
        return new TenantList(targets, former, null);
    }

    /// <summary>
    /// The schemas a project's shared side reads when it sets no <c>schemas</c> (G-290): every schema of the database except the system ones, the tenants'
    /// (<c>prefix_...</c>), and one named as the stand-in, so a tenant's objects are never compared, or proposed for removal, as shared ones.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadSharedSchemasAsync(NpgsqlConnection connection, PgProjectTenants tenants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tenants);

        const string sql = """
            select nspname from pg_catalog.pg_namespace
            where nspname !~ '^pg_' and nspname <> 'information_schema' and nspname <> @standIn
              and not (left(nspname, @length) = @tenantPrefix and length(nspname) > @length)
            order by nspname
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("standIn", tenants.StandInSchema);
        command.Parameters.AddWithValue("tenantPrefix", tenants.Prefix + "_");
        command.Parameters.AddWithValue("length", tenants.Prefix.Length + 1);
        var schemas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas;
    }

    /// <summary>The database's schemas named as a tenant's: <c>prefix_</c> and at least one more character, by name.</summary>
    public static Task<IReadOnlyList<string>> FindTenantSchemasAsync(NpgsqlConnection connection, string prefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return ReadTenantSchemasAsync(connection, prefix, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ReadTenantSchemasAsync(NpgsqlConnection connection, string prefix, CancellationToken cancellationToken)
    {
        const string sql = """
            select nspname from pg_catalog.pg_namespace
            where left(nspname, @length) = @tenantPrefix and length(nspname) > @length
            order by nspname
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantPrefix", prefix + "_");
        command.Parameters.AddWithValue("length", prefix.Length + 1);
        var schemas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas;
    }

    /// <summary>
    /// Runs the tenant query in a read-only transaction, never committed (disposing it rolls it back). A query whose table or schema does not exist yet (the shared project that
    /// creates it is not published) gives the reason instead of identifiers; any other failure, or a result that is not one column of usable identifiers, is an error.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Identifiers, string? Unavailable)> RunQueryAsync(NpgsqlConnection connection, PgProjectTenants tenants, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(tenants.Query, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (reader.FieldCount != 1)
            {
                throw new RefusedException($"The tenant query returns {reader.FieldCount} columns; it must return one column, each tenant's identifier.");
            }

            var identifiers = new List<string>();
            var problems = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var row = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                row++;
                var value = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(value))
                {
                    problems.Add($"row {row}: the identifier is {(value is null ? "NULL" : "empty")}.");
                }
                else if (!seen.Add(value))
                {
                    problems.Add($"row {row}: the tenant '{value}' is returned again.");
                }
                else if (Encoding.UTF8.GetByteCount(tenants.SchemaFor(value)) > PgProjectTenants.MaxSchemaNameBytes)
                {
                    problems.Add($"row {row}: the tenant '{value}' makes the schema name '{tenants.SchemaFor(value)}', longer than PostgreSQL's {PgProjectTenants.MaxSchemaNameBytes} bytes.");
                }
                else if (value.Trim().Length != value.Length || value.Contains('\0', StringComparison.Ordinal))
                {
                    problems.Add($"row {row}: the tenant '{value}' starts or ends with a space, or holds a NUL character.");
                }
                else
                {
                    identifiers.Add(value);
                }
            }

            if (problems.Count > 0)
            {
                throw new RefusedException($"The tenant query returned {problems.Count} unusable row(s):" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(static problem => "  " + problem)));
            }

            return (identifiers, null);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            return ([], $"The tenant query could not run ({exception.SqlState}: {exception.MessageText}), most likely because the shared table it reads has not been published yet, so the tenants were not read.");
        }
        catch (PostgresException exception)
        {
            throw new RefusedException($"The tenant query failed: {exception.SqlState}: {exception.MessageText}", exception);
        }
    }
}
