using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>The keys of foreign data wrappers, servers, and user mappings (G-132): none is in a schema, so each kind has a prefix of its own, as a publication has.</summary>
public static class ForeignDataKeys
{
    public static string Wrapper(string name) => $"foreign data wrapper:{name}";

    public static string Server(string name) => $"server:{name}";

    /// <summary>A user mapping is its role (<c>PUBLIC</c> for every role) and its server.</summary>
    public static string UserMapping(string role, string server) => $"user mapping:{role} on {server}";
}

/// <summary>
/// An option of a foreign data wrapper, server, or user mapping (G-132): its name, and its value as written, or the variable that names it (<c>:'name'</c>, G-336's
/// designated slots). A value named by a variable is never compared (G-336 decision 3), and neither is a <c>password</c>, however it is written (the user's decision of
/// 2026-11-10): only whether the option is there is.
/// </summary>
public sealed record ForeignOptionDefinition(string Name, string? Value, string? Variable)
{
    /// <summary>True when the value is compared: written out, and not a password.</summary>
    public bool IsValueCompared => Variable is null && !IsPassword;

    /// <summary>True for the <c>password</c> option, which is a secret whatever the wrapper.</summary>
    public bool IsPassword => string.Equals(Name, "password", StringComparison.Ordinal);

    /// <summary>The option as it is compared: its name, and its value when that is compared.</summary>
    public string Comparable => IsValueCompared ? $"{Name}={Value}" : $"{Name}=<not compared>";

    /// <summary>The option's value as SQL: the variable, or the value quoted.</summary>
    public string ValueSql => Variable is { } variable ? $":'{variable}'" : $"'{(Value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)}'";

    public static IReadOnlyList<ForeignOptionDefinition> From(IEnumerable<ForeignOptionNode> options)
        => options.Select(static option => new ForeignOptionDefinition(option.Name, option.Variable is null ? option.Value : null, option.Variable)).ToArray();
}

/// <summary>
/// A <c>CREATE FOREIGN DATA WRAPPER</c> (G-132): its handler and validator functions (null for none) and its options. Only a superuser creates, alters, or drops one. A
/// wrapper an extension installs (<c>postgres_fdw</c>) is the extension's, not the project's.
/// </summary>
public sealed record ForeignDataWrapperDefinition(string Key, string Name) : ForeignDataNode
{
    public override string Kind => "ForeignDataWrapper";

    /// <summary>The handler function as written (<c>app.h</c>), or null for <c>NO HANDLER</c> or none.</summary>
    public string? Handler { get; init; }

    /// <summary>The validator function as written, or null for <c>NO VALIDATOR</c> or none.</summary>
    public string? Validator { get; init; }
}

/// <summary>A <c>CREATE SERVER</c> (G-132): its wrapper, <c>TYPE</c>, <c>VERSION</c>, and options. It is created after its wrapper, and before its user mappings.</summary>
public sealed record ForeignServerDefinition(string Key, string Name, string Wrapper) : ForeignDataNode
{
    public override string Kind => "ForeignServer";

    public string? ServerType { get; init; }

    public string? Version { get; init; }
}

/// <summary>
/// A <c>CREATE USER MAPPING</c> (G-132): a role (<c>PUBLIC</c> for every role; the deploying role for <c>CURRENT_USER</c>) and a server, and its options, which hold the
/// role's credentials. Its secret is sent when it is created, and re-sent only by <c>publish --apply-secrets</c> (G-336, rotation option (c)).
/// </summary>
public sealed record UserMappingDefinition(string Key, string Name, string Role, string Server) : ForeignDataNode
{
    public override string Kind => "UserMapping";

    /// <summary>True for a mapping read from a database whose options the connected role may not read (<c>pg_user_mappings</c> shows them as null): they are not compared.</summary>
    public bool OptionsUnreadable { get; init; }
}

/// <summary>What the three foreign data kinds share: no schema, options, a comment, and no references a semantic pass resolves.</summary>
public abstract record ForeignDataNode : ISemanticNode
{
    public abstract string Kind { get; }

    public abstract string Key { get; init; }

    public abstract string Name { get; init; }

    public string? SchemaName => null;

    /// <summary>The options, in the order written.</summary>
    public IReadOnlyList<ForeignOptionDefinition> Options { get; init; } = [];

    /// <summary>The comment (<c>COMMENT ON FOREIGN DATA WRAPPER</c> or <c>SERVER</c>); a user mapping has none.</summary>
    public string? Comment { get; init; }

    public IReadOnlyList<string> ReferencedTables { get; init; } = [];

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public IReadOnlyList<string> ReferencedTypes { get; init; } = [];

    public IReadOnlyList<string> ReferencedFunctions { get; init; } = [];

    public IReadOnlyList<TableDefinition> ResolvedTables { get; init; } = [];

    public IReadOnlyList<ColumnDefinition> ResolvedColumns { get; init; } = [];

    public IReadOnlyList<TypeDefinition> ResolvedTypes { get; init; } = [];

    public IReadOnlyList<FunctionDefinition> ResolvedFunctions { get; init; } = [];

    public SqlNode? DeclarationNode { get; init; }

    public bool IsRecovered { get; set; }

    public bool HasErrors { get; set; }

    /// <summary>The options as they are compared, sorted by name (PostgreSQL keeps them as a list, but their order means nothing).</summary>
    public IReadOnlyList<string> ComparableOptions => Options.Select(static option => option.Comparable).Order(StringComparer.Ordinal).ToArray();

    private readonly PgProj.Parser.PlaceSlot<SemanticLocation> _location = PgProj.Parser.PlaceSlot<SemanticLocation>.Empty;

    /// <summary>
    /// Where the definition was written: its file and range (G-309e), for the diagnostics about it; null for one read from a database. Never part of the
    /// definition's equality, never compared, and not in <c>--dump-model</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemanticLocation? Location
    {
        get => _location.Value;
        init => _location = new PgProj.Parser.PlaceSlot<SemanticLocation>(value);
    }
}
