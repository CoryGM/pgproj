namespace PgProj.Semantics;

public enum ScriptPhase
{
    PrePhase,
    MainPhase,
    PostPhase,
}

public sealed record ScriptStatement
{
    public required string Sql { get; init; }

    public string? ObjectKey { get; init; }

    public string? ObjectKind { get; init; }

    public ScriptPhase Phase { get; init; } = ScriptPhase.MainPhase;

    public bool IsNoOp { get; init; }

    public bool IsDestructive { get; init; }

    public bool IsReversible { get; init; }
}

public sealed class ScriptBuilder
{
    private List<ScriptStatement> _statements = [];
    private List<SemanticDiagnostic> _diagnostics = [];

    public IReadOnlyList<ScriptStatement> Statements => _statements;

    public IReadOnlyList<SemanticDiagnostic> Diagnostics => _diagnostics;

    public void ReplaceStatements(IEnumerable<ScriptStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        _statements = statements.ToList();
    }

    public void AddStatement(ScriptStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        _statements.Add(statement);
    }

    public void AddDiagnostic(SemanticDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _diagnostics.Add(diagnostic);
    }
}

internal sealed class ScriptGeneratorOptimizationPass
{
    public void Execute(DatabaseModel model, ScriptBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(builder);

        GroupStatements(builder);
        RemoveNoOps(builder);
        DetectDestructiveOperations(builder);
        DetectReversibleOperations(builder);
        OrderStatements(builder);
    }

    private void GroupStatements(ScriptBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var groupedStatements = builder.Statements
            .Select(statement => statement with
            {
                Phase = InferPhase(statement),
            })
            .ToArray();

        // TODO: Detect and group parallelizable operations once dependency-safe script segments are modeled.
        // TODO: Group compatible statements into explicit execution batches once batch boundaries are represented in the builder.
        builder.ReplaceStatements(groupedStatements);
    }

    private void RemoveNoOps(ScriptBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var statements = builder.Statements;
        if (statements.Count == 0)
        {
            return;
        }

        var filteredStatements = statements
            .Where(static statement => !statement.IsNoOp)
            .Where(static statement => !string.IsNullOrWhiteSpace(statement.Sql))
            .Select(statement => statement with
            {
                Sql = NormalizeSql(statement.Sql),
            })
            .DistinctBy(static statement => CreateStatementIdentity(statement), StringComparer.Ordinal)
            .ToArray();

        builder.ReplaceStatements(filteredStatements);
    }

    private void DetectDestructiveOperations(ScriptBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var statements = builder.Statements
            .Select(statement =>
            {
                var normalizedSql = NormalizeSql(statement.Sql);
                var isDestructive = IsDestructiveOperation(normalizedSql);

                if (isDestructive)
                {
                    builder.AddDiagnostic(SemanticDiagnostics.Warning(
                        $"Script optimization detected destructive operation: {normalizedSql}",
                        source: nameof(ScriptGeneratorOptimizationPass)));
                }

                return statement with
                {
                    Sql = normalizedSql,
                    IsDestructive = isDestructive,
                };
            })
            .ToArray();

        builder.ReplaceStatements(statements);
    }

    private void DetectReversibleOperations(ScriptBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var statements = builder.Statements
            .Select(statement =>
            {
                var normalizedSql = NormalizeSql(statement.Sql);
                var isReversible = IsReversibleOperation(normalizedSql);

                if (isReversible)
                {
                    builder.AddDiagnostic(SemanticDiagnostics.Info(
                        $"Script optimization detected potentially reversible operation: {normalizedSql}",
                        source: nameof(ScriptGeneratorOptimizationPass)));
                }

                return statement with
                {
                    Sql = normalizedSql,
                    IsReversible = isReversible,
                };
            })
            .ToArray();

        // TODO: Group statements into transaction-safe units once transaction semantics are modeled in the script builder.
        // TODO: Add savepoint-based recovery hints once script execution rollback metadata is available.
        builder.ReplaceStatements(statements);
    }

    private void OrderStatements(ScriptBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var orderedStatements = builder.Statements
            .OrderBy(static statement => statement.Phase)
            .ThenBy(static statement => GetObjectKindOrder(statement.ObjectKind))
            .ThenBy(static statement => NormalizeToken(statement.ObjectKey), StringComparer.Ordinal)
            .ThenBy(static statement => NormalizeSql(statement.Sql), StringComparer.Ordinal)
            .ToArray();

        builder.ReplaceStatements(orderedStatements);
    }

    private static ScriptPhase InferPhase(ScriptStatement statement)
    {
        var sql = NormalizeSql(statement.Sql);
        var kind = NormalizeToken(statement.ObjectKind);

        if (sql.StartsWith("CREATE TYPE", StringComparison.OrdinalIgnoreCase) ||
            sql.StartsWith("CREATE FUNCTION", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "type", StringComparison.Ordinal))
        {
            return ScriptPhase.PrePhase;
        }

        if (sql.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase) ||
            sql.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase) ||
            sql.StartsWith("DROP INDEX", StringComparison.OrdinalIgnoreCase) ||
            sql.StartsWith("DROP FUNCTION", StringComparison.OrdinalIgnoreCase) ||
            sql.StartsWith("DROP TYPE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "index", StringComparison.Ordinal) ||
            string.Equals(kind, "constraint", StringComparison.Ordinal))
        {
            return ScriptPhase.PostPhase;
        }

        return ScriptPhase.MainPhase;
    }

    private static bool IsDestructiveOperation(string sql)
        => sql.StartsWith("DROP TABLE", StringComparison.OrdinalIgnoreCase)
            || sql.Contains(" DROP COLUMN ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DROP TYPE", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DROP FUNCTION", StringComparison.OrdinalIgnoreCase);

    private static bool IsReversibleOperation(string sql)
        => sql.Contains("ALTER COLUMN", StringComparison.OrdinalIgnoreCase)
            && sql.Contains(" TYPE ", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase)
            && sql.Contains(" DEFAULT ", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("ADD CONSTRAINT", StringComparison.OrdinalIgnoreCase);

    private static string CreateStatementIdentity(ScriptStatement statement)
        => string.Join('|',
            statement.Phase.ToString(),
            NormalizeToken(statement.ObjectKind),
            NormalizeToken(statement.ObjectKey),
            NormalizeSql(statement.Sql));

    private static int GetObjectKindOrder(string? objectKind)
        => NormalizeToken(objectKind) switch
        {
            "type" => 0,
            "function" => 1,
            "table" => 2,
            "view" => 3,
            "index" => 4,
            "constraint" => 5,
            _ => 6,
        };

    private static string NormalizeSql(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return string.Join(' ', sql.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string NormalizeToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}