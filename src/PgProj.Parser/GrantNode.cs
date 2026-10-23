namespace PgProj.Parser;

/// <summary>The shape of a privilege statement, which decides how its targets are read.</summary>
public enum GrantForm
{
    /// <summary><c>GRANT ... ON [kind] name[, name]</c>: privileges on named objects.</summary>
    Object,

    /// <summary><c>GRANT ... ON ALL TABLES|SEQUENCES|FUNCTIONS|PROCEDURES|ROUTINES IN SCHEMA s</c>.</summary>
    AllInSchema,

    /// <summary><c>ALTER DEFAULT PRIVILEGES [FOR ROLE r] [IN SCHEMA s] GRANT ... ON TABLES|SEQUENCES|FUNCTIONS|TYPES|SCHEMAS</c>.</summary>
    DefaultPrivileges,

    /// <summary><c>GRANT role[, role] TO role[, role]</c>: role membership.</summary>
    RoleMembership,
}

/// <summary>
/// An object a privilege statement names. <see cref="ArgumentTypes"/> is null for anything that is not a function or
/// procedure, and the declared argument types (as pg_query resolved them, for example <c>pg_catalog.int4</c>, with
/// <c>[]</c> for an array) for one, so an overload can be told from its siblings.
/// </summary>
public sealed record GrantTarget(string? SchemaName, string Name, IReadOnlyList<string>? ArgumentTypes = null)
{
    /// <summary>True when <see cref="SchemaName"/> was not written but defaulted (G-93); see <see cref="SqlNode.IsSchemaDefaulted"/>.</summary>
    public bool IsSchemaDefaulted { get; init; }
}

/// <summary>One privilege of a statement. <see cref="Name"/> is upper case, or <c>ALL PRIVILEGES</c>; <see cref="Columns"/> is empty unless the privilege is limited to columns.</summary>
public sealed record GrantPrivilege(string Name, IReadOnlyList<string> Columns);

/// <summary>
/// A <c>GRANT</c>, <c>REVOKE</c>, <c>GRANT role</c>, or <c>ALTER DEFAULT PRIVILEGES</c> statement, read from pg_query's AST
/// (G-65). It says what the statement contains and nothing about what it means: the semantic layer expands it into grants.
/// </summary>
public sealed record GrantNode(GrantForm Form, string ObjectKind) : SqlNode("Grant", null, null)
{
    /// <summary>False for <c>REVOKE</c>.</summary>
    public bool IsGrant { get; init; } = true;

    public IReadOnlyList<GrantTarget> Targets { get; init; } = [];

    /// <summary>Empty means <c>ALL PRIVILEGES</c> (the statement named none).</summary>
    public IReadOnlyList<GrantPrivilege> Privileges { get; init; } = [];

    /// <summary>The grantees; <c>PUBLIC</c> is spelled <c>PUBLIC</c>.</summary>
    public IReadOnlyList<string> Grantees { get; init; } = [];

    /// <summary><c>WITH GRANT OPTION</c>, or <c>WITH ADMIN OPTION</c> for role membership.</summary>
    public bool WithGrantOption { get; init; }

    /// <summary>The <c>FOR ROLE</c> roles of default privileges, or the roles being granted for role membership.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The <c>IN SCHEMA</c> schemas of default privileges.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [];

    /// <summary>Parts of the statement the parser saw but the model has no place for (<c>CURRENT_USER</c> as a grantee, <c>GRANTED BY</c>, ...).</summary>
    public IReadOnlyList<string> UnsupportedParts { get; init; } = [];
}
