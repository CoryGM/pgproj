namespace PgProj.Semantics;

public interface ISemanticNode
{
    string Key { get; }

    string Kind { get; }

    string Name { get; }

    string? SchemaName { get; }

    IReadOnlyList<string> ReferencedTables { get; }

    IReadOnlyList<string> ReferencedColumns { get; }

    IReadOnlyList<string> ReferencedTypes { get; }

    IReadOnlyList<string> ReferencedFunctions { get; }

    /// <summary>The keys of the project's operators (G-129) that the object's expressions use, so it is created after them and dropped before them (G-138).</summary>
    IReadOnlyList<string> ReferencedOperators => [];

    IReadOnlyList<TableDefinition> ResolvedTables { get; }

    IReadOnlyList<ColumnDefinition> ResolvedColumns { get; }

    IReadOnlyList<TypeDefinition> ResolvedTypes { get; }

    IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; }

    /// <summary>Where the object was written (G-309e), for the diagnostics about it; null when it was read from a database or the place is unknown.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    SemanticLocation? Location => null;

    bool IsRecovered { get; set; }

    bool HasErrors { get; set; }
}
