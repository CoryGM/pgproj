namespace PgProj.Parser;

/// <summary>
/// An option of a foreign data wrapper, server, or user mapping (<c>OPTIONS (host 'db', password :'pw')</c>, G-132): its name as pg_query read it (an identifier,
/// folded), and its value, which may be a variable (<see cref="Variable"/>) for a server or a user mapping.
/// </summary>
public sealed record ForeignOptionNode(string Name, string? Value)
{
    /// <summary>The variable that names the value (<c>:'pw'</c> is <c>pw</c>), or null for a value written out (G-336's designated slots).</summary>
    public string? Variable => ScriptVariables.SlotVariable(Value);
}

/// <summary>
/// <c>CREATE FOREIGN DATA WRAPPER name [HANDLER f | NO HANDLER] [VALIDATOR f | NO VALIDATOR] [OPTIONS (...)]</c> (G-132). It belongs to no schema; its name is its identity.
/// Only a superuser creates one. A function is its name parts joined with dots, as written; null for none.
/// </summary>
public sealed record ForeignDataWrapperNode(string? Name) : SqlNode("ForeignDataWrapper", Name, null)
{
    public string? Handler { get; init; }

    public string? Validator { get; init; }

    public IReadOnlyList<ForeignOptionNode> Options { get; init; } = [];
}

/// <summary><c>CREATE SERVER name [TYPE 'type'] [VERSION 'version'] FOREIGN DATA WRAPPER wrapper [OPTIONS (...)]</c> (G-132). No schema; its name is its identity.</summary>
public sealed record ForeignServerNode(string? Name, string Wrapper) : SqlNode("ForeignServer", Name, null)
{
    public string? ServerType { get; init; }

    public string? Version { get; init; }

    public IReadOnlyList<ForeignOptionNode> Options { get; init; } = [];
}

/// <summary>
/// <c>CREATE USER MAPPING FOR role SERVER server [OPTIONS (...)]</c> (G-132). Its identity is the role and the server. <see cref="Role"/> is the role as written (exact),
/// <c>PUBLIC</c> for every role, or <see cref="CurrentUser"/> for <c>CURRENT_USER</c>, <c>CURRENT_ROLE</c>, or <c>USER</c>, which PostgreSQL stores as the role that runs it.
/// </summary>
public sealed record UserMappingNode(string Role, string Server) : SqlNode("UserMapping", $"{Role} on {Server}", null)
{
    /// <summary>The <see cref="Role"/> of a mapping for the role that runs the statement.</summary>
    public const string CurrentUser = "CURRENT_USER";

    public IReadOnlyList<ForeignOptionNode> Options { get; init; } = [];
}
