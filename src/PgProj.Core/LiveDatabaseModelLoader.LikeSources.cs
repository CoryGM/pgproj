using Npgsql;
using PgProj.Parser;

namespace PgProj.Core;

public sealed partial class LiveDatabaseModelLoader
{
    /// <summary>
    /// The tables a project's <c>LIKE</c> clauses copy from but the project does not declare (G-221; <see cref="Semantics.DatabaseModel.ExternalLikeSources"/>),
    /// read from the database as the statements that would make them: each table, its constraints, indexes, and statistics, and their comments. The project is
    /// then built again with this tree as a source of <c>LIKE</c> (<see cref="TableLikeExpander"/>), so a table that copies from a schema the project does not
    /// model has the columns PostgreSQL gave it. Null when there is nothing to read, or none of the tables exists.
    /// </summary>
    public async Task<SqlSyntaxTree?> ReadLikeSourcesAsync(NpgsqlConnection connection, IReadOnlyCollection<string> tableKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableKeys);

        var wanted = tableKeys
            .Select(static key => key.Split('.', 2))
            .Where(static parts => parts.Length == 2)
            .Select(static parts => (Schema: parts[0], Name: parts[1]))
            .ToHashSet();
        if (wanted.Count == 0)
        {
            return null;
        }

        var schemaFilter = wanted.Select(static table => table.Schema).Distinct(StringComparer.Ordinal).ToArray();
        var previousSearchPath = await LiveReadSearchPath.PinAsync(connection, null, cancellationToken);
        string? previousJit = null;
        try
        {
            previousJit = await LiveReadJit.PinAsync(connection, cancellationToken);
            var (_, tables) = AttachIdentitySequences(
                await ReadSequencesAsync(connection, schemaFilter, cancellationToken),
                await ReadTablesAsync(connection, schemaFilter, cancellationToken));
            bool Wanted(string schemaName, string tableName) => wanted.Contains((schemaName, tableName));

            var sourceTables = tables.Where(table => Wanted(table.SchemaName, table.TableName)).ToArray();
            if (sourceTables.Length == 0)
            {
                return null;
            }

            var constraints = (await ReadConstraintsAsync(connection, schemaFilter, cancellationToken)).Where(constraint => Wanted(constraint.SchemaName, constraint.TableName)).ToArray();
            var indexes = (await ReadIndexesAsync(connection, schemaFilter, cancellationToken)).Where(index => Wanted(index.SchemaName, index.TableName)).ToArray();
            var statistics = (await ReadStatisticsAsync(connection, schemaFilter, cancellationToken)).Where(item => Wanted(item.TableSchemaName, item.TableName)).ToArray();
            return _parser.Parse(GenerateSnapshotSql([], sourceTables, constraints, indexes, statistics: statistics));
        }
        finally
        {
            await LiveReadJit.RestoreAsync(connection, previousJit, CancellationToken.None);
            await LiveReadSearchPath.RestoreAsync(connection, null, previousSearchPath, CancellationToken.None);
        }
    }
}
