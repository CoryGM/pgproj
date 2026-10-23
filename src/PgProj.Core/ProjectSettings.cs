namespace PgProj.Core;

/// <summary>
/// The properties of <c>pgproj.json</c> as an object (G-309a), for a caller with no such file to hand over: a web front end that receives a
/// project's settings and files in a request. Validated by the same code as the file (<see cref="PgProjectLoader"/>), so every rule and every
/// message is the file's; relative paths are resolved against the root directory the caller names. The meaning of each property is that of
/// the file's (<c>pgproj.json.md</c>).
/// </summary>
public sealed record class ProjectSettings
{
    public string? Name { get; init; }

    public string? Version { get; init; }

    public IReadOnlyList<string>? Schemas { get; init; }

    public string? Source { get; init; }

    public string? SourceConnectionString { get; init; }

    public string? TargetConnectionString { get; init; }

    public string? PreDeploy { get; init; }

    public string? PostDeploy { get; init; }

    public bool? ExcludeGrants { get; init; }

    public bool? ExcludeRoleMemberships { get; init; }

    public bool? ExcludeOwners { get; init; }

    public IReadOnlyList<string>? SearchPath { get; init; }

    public TenantSettings? Tenants { get; init; }
}

/// <summary>The <c>tenants</c> block of <see cref="ProjectSettings"/> (G-290).</summary>
public sealed record class TenantSettings
{
    public string? Template { get; init; }

    public string? Prefix { get; init; }

    public string? Query { get; init; }

    public string? File { get; init; }

    public string? PreDeploy { get; init; }

    public string? PostDeploy { get; init; }
}
