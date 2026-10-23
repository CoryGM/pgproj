using PgProj.Parser;

namespace PgProj.Semantics;

public sealed class SemanticBuilder
{
    private readonly SemanticPipeline _pipeline = new();

    public DatabaseModel Build(
        IEnumerable<SqlSyntaxTree> syntaxTrees,
        IReadOnlyList<string>? schemas = null,
        bool excludeGrants = false,
        bool excludeRoleMemberships = false,
        IReadOnlyList<string>? searchPath = null,
        bool excludeOwners = false,
        IReadOnlyList<SqlSyntaxTree>? likeSourceTrees = null)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        return _pipeline.Execute(syntaxTrees, schemas, excludeGrants, excludeRoleMemberships, searchPath, excludeOwners, likeSourceTrees);
    }
}
