using Npgsql;
using PgProj.Semantics;

namespace PgProj.Core;

/// <summary>
/// Resolves a project base type's <c>LIKE</c> through the server it is compared with (G-267, the user's design of 2026-11-09, item 5). <c>LIKE = t</c> copies
/// <c>t</c>'s <c>INTERNALLENGTH</c>, <c>PASSEDBYVALUE</c>, <c>ALIGNMENT</c>, and <c>STORAGE</c>, and a database stores those four values, never the <c>LIKE</c>,
/// so the project's type gets them from the server's <c>pg_type</c> for <c>t</c>, each one the definition does not state beside the <c>LIKE</c>. Only reads
/// (<c>to_regtype</c>, which does not fail): a type the server does not have, one this publish has yet to create, is left as written and compared so.
/// Between two projects nothing is resolved: <c>LIKE</c> is compared as written.
/// </summary>
public static class ServerBaseTypeLikeResolver
{
    public static async Task ResolveAsync(NpgsqlConnection connection, DatabaseModel projectModel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(projectModel);

        var withLike = projectModel.Types.Values.Where(static type => type.BaseTypeOptions?.WrittenLike is not null).ToArray();
        if (withLike.Length == 0)
        {
            return;
        }

        var types = new Dictionary<string, TypeDefinition>(projectModel.Types, PgProj.Parser.SqlIdentifiers.NameComparer);
        foreach (var type in withLike)
        {
            var options = type.BaseTypeOptions!;
            await using var command = new NpgsqlCommand("select typlen, typbyval, typalign::text, typstorage::text from pg_catalog.pg_type where oid = pg_catalog.to_regtype(@name)", connection);
            command.Parameters.AddWithValue("name", options.WrittenLike!);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                continue;
            }

            types[type.Key] = type with
            {
                BaseTypeOptions = options with
                {
                    InternalLength = options.Stated.Contains("INTERNALLENGTH") ? options.InternalLength : reader.GetInt16(0),
                    PassedByValue = options.Stated.Contains("PASSEDBYVALUE") ? options.PassedByValue : reader.GetBoolean(1),
                    Alignment = options.Stated.Contains("ALIGNMENT") ? options.Alignment : reader.GetString(2),
                    Storage = options.Stated.Contains("STORAGE") ? options.Storage : reader.GetString(3),
                    Like = null,
                },
            };
        }

        projectModel.Types = types;
    }
}
