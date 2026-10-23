using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// Gives a tenant's copy of the template its own schema where the template names the schema itself (G-290): <c>CREATE SCHEMA tenant</c>,
/// <c>GRANT ... ON SCHEMA tenant</c>, <c>GRANT ... ON ALL TABLES IN SCHEMA tenant</c>, <c>ALTER DEFAULT PRIVILEGES IN SCHEMA tenant</c>,
/// <c>ALTER SCHEMA tenant OWNER TO</c>, and <c>COMMENT ON SCHEMA tenant</c> name the stand-in schema (the prefix), which stands for each tenant's.
/// So does a routine's <c>search_path</c> setting: an entry naming the stand-in (<c>SET search_path TO tenant, platform</c>) is the tenant's schema.
/// Everything else in the template is written without a schema and already takes the tenant's, as the default schema it was read with.
/// </summary>
public static class TenantStandIn
{
    public static IReadOnlyList<SqlSyntaxTree> Replace(IReadOnlyList<SqlSyntaxTree> trees, string standIn, string schema)
    {
        ArgumentNullException.ThrowIfNull(trees);
        ArgumentException.ThrowIfNullOrWhiteSpace(standIn);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        string Map(string? name) => string.Equals(name, standIn, StringComparison.Ordinal) ? schema : name!;

        SqlNode Apply(SqlNode node)
            => node switch
            {
                SchemaNode created when string.Equals(created.Name, standIn, StringComparison.Ordinal) => new SchemaNode(schema) { IsSchemaDefaulted = created.IsSchemaDefaulted },
                OwnerNode { ObjectKind: "Schema" } owner => owner with { ObjectName = Map(owner.ObjectName), SchemaName = owner.SchemaName is null ? null : Map(owner.SchemaName) },
                ObjectCommentNode { ObjectKind: "Schema" } comment => comment with { SchemaName = Map(comment.SchemaName), TableName = Map(comment.TableName) },
                GrantNode { Form: GrantForm.DefaultPrivileges } grant => grant with { Schemas = grant.Schemas.Select(Map).ToArray() },
                GrantNode { Form: GrantForm.AllInSchema } grant => grant with { Targets = grant.Targets.Select(target => target with { Name = Map(target.Name) }).ToArray() },
                GrantNode grant when string.Equals(grant.ObjectKind, "schema", StringComparison.OrdinalIgnoreCase)
                    => grant with { Targets = grant.Targets.Select(target => target with { Name = Map(target.Name) }).ToArray() },
                FunctionNode function when function.Signature is { } signature => function with { Signature = MapSearchPath(signature) },
                ProcedureNode procedure when procedure.Signature is { } signature => procedure with { Signature = MapSearchPath(signature) },
                _ => node,
            };

        RoutineSignature MapSearchPath(RoutineSignature signature)
            => signature.Settings.Any(static setting => setting.Name == "search_path")
                ? signature with
                {
                    Settings = signature.Settings
                        .Select(setting => setting.Name != "search_path"
                            ? setting
                            : setting with
                            {
                                Values = setting.Values.Select(Map).ToArray(),
                                WrittenText = setting.WrittenText is null ? null : TenantSchemaRewriter.MapStandIn(setting.WrittenText, standIn, schema),
                            })
                        .ToArray(),
                }
                : signature;

        return trees.Select(tree => tree with { Nodes = tree.Nodes.Select(Apply).ToArray() }).ToArray();
    }
}
