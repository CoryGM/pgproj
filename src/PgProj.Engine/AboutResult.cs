namespace PgProj.Engine;

/// <summary>What <see cref="PgProjEngine.AboutAsync"/> found at the request's project file.</summary>
public enum AboutProjectStatus
{
    /// <summary>The request named no project file.</summary>
    NotRequested,

    /// <summary>The project was read: <see cref="AboutResult.Project"/> describes it.</summary>
    Found,

    /// <summary>There is no project file at <see cref="AboutResult.ProjectFilePath"/>.</summary>
    NotFound,

    /// <summary>The project file or one it names is not valid: <see cref="AboutResult.ProjectMessage"/> says why.</summary>
    Invalid,
}

/// <summary>Whether a database described by <see cref="PgProjEngine.AboutAsync"/> was reached.</summary>
public enum AboutConnectionStatus
{
    /// <summary>Connected: the server's version and the role's privileges were read.</summary>
    Connected,

    /// <summary>The connection string is not valid, or the database could not be reached or refused the login: <see cref="AboutConnection.Message"/> says why.</summary>
    Failed,

    /// <summary>Not connected to, because the request said not to (<see cref="AboutRequest.Connect"/>).</summary>
    NotTried,
}

/// <summary>
/// pgproj's environment and how it reads a project and its connections (G-332): what a bug report needs, in one result. Nothing in it is a secret: a
/// connection is described by its host, port, database, and user, never its password, and every message is masked. Host names and paths are as they are.
/// </summary>
public sealed record AboutResult
{
    /// <summary>The engine's version, as the release that built it was tagged (<c>0.1.0+&lt;commit&gt;</c>).</summary>
    public required string EngineVersion { get; init; }

    /// <summary>The operating system, as .NET describes it (<c>Microsoft Windows 10.0.26300</c>, <c>Ubuntu 24.04.1 LTS</c>).</summary>
    public required string OperatingSystem { get; init; }

    /// <summary>The operating system's architecture (<c>X64</c>, <c>Arm64</c>).</summary>
    public required string OperatingSystemArchitecture { get; init; }

    /// <summary>The architecture this process runs as, which differs from <see cref="OperatingSystemArchitecture"/> under emulation.</summary>
    public required string ProcessArchitecture { get; init; }

    /// <summary>The .NET runtime (<c>.NET 10.0.13</c>).</summary>
    public required string Runtime { get; init; }

    /// <summary>The runtime identifier .NET gives this platform (<c>win-x64</c>, <c>linux-arm64</c>, <c>linux-musl-x64</c>).</summary>
    public required string RuntimeIdentifier { get; init; }

    /// <summary>Whether PostgreSQL's parser (libpg_query) loads and parses on this machine. pgproj cannot read SQL without it.</summary>
    public required bool ParserLoaded { get; init; }

    /// <summary>Why the parser did not load, when <see cref="ParserLoaded"/> is false.</summary>
    public string? ParserMessage { get; init; }

    /// <summary>What was found at the project file.</summary>
    public required AboutProjectStatus ProjectStatus { get; init; }

    /// <summary>The full path of the project file looked for, or null when none was named.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>Why the project could not be read, when <see cref="ProjectStatus"/> is <see cref="AboutProjectStatus.Invalid"/>.</summary>
    public string? ProjectMessage { get; init; }

    /// <summary>The project, when <see cref="ProjectStatus"/> is <see cref="AboutProjectStatus.Found"/>.</summary>
    public AboutProject? Project { get; init; }

    /// <summary>The database <c>diff</c> and <c>publish</c> would compare with and change, or null when none is configured.</summary>
    public AboutConnection? Target { get; init; }

    /// <summary>The database <c>init</c> and <c>document</c> would read, or null when none is configured.</summary>
    public AboutConnection? Source { get; init; }
}

/// <summary>A project as <see cref="PgProjEngine.AboutAsync"/> read it, without building it.</summary>
public sealed record AboutProject
{
    /// <summary>The project's <c>name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The project's <c>version</c>.</summary>
    public required string Version { get; init; }

    /// <summary>The folder its SQL files are read from (<c>source</c>), as a full path.</summary>
    public required string SourcePath { get; init; }

    /// <summary>How many SQL files are in it (not counting the tenant template's).</summary>
    public required int SqlFileCount { get; init; }

    /// <summary>The schemas the project lists (<c>schemas</c>); empty when it lists none.</summary>
    public required IReadOnlyList<string> Schemas { get; init; }

    /// <summary>How unqualified names resolve (<c>searchPath</c>); <c>public</c> unless the project says otherwise.</summary>
    public required IReadOnlyList<string> SearchPath { get; init; }

    /// <summary>The pre-deployment script's path, or null.</summary>
    public string? PreDeployScript { get; init; }

    /// <summary>The post-deployment script's path, or null.</summary>
    public string? PostDeployScript { get; init; }

    /// <summary>Whether grants are left out of the comparison (<c>excludeGrants</c>).</summary>
    public bool ExcludeGrants { get; init; }

    /// <summary>Whether role memberships are left out (<c>excludeRoleMemberships</c>).</summary>
    public bool ExcludeRoleMemberships { get; init; }

    /// <summary>Whether owners are left out (<c>excludeOwners</c>).</summary>
    public bool ExcludeOwners { get; init; }

    /// <summary>The project's tenants, described as <c>build</c> describes them; empty for a project without tenants.</summary>
    public IReadOnlyList<string> TenantLines { get; init; } = [];
}

/// <summary>A database <see cref="PgProjEngine.AboutAsync"/> described: where its connection string came from, what it names, and what the server said.</summary>
public sealed record AboutConnection
{
    /// <summary>Where the connection string came from: the shell's words for it, or <c>targetConnectionString in pgproj.json</c> (<c>sourceConnectionString</c>).</summary>
    public required string Origin { get; init; }

    /// <summary>Whether it was reached.</summary>
    public required AboutConnectionStatus Status { get; init; }

    /// <summary>The host (or socket directory) the connection string names, or null when it names none.</summary>
    public string? Host { get; init; }

    /// <summary>The port the connection string names (5432 when it names none).</summary>
    public int? Port { get; init; }

    /// <summary>The database: the server's name for it once connected, otherwise the one the connection string names (null when it names none).</summary>
    public string? Database { get; init; }

    /// <summary>The user: the role the server logged in once connected, otherwise the one the connection string names (null when it names none).</summary>
    public string? Username { get; init; }

    /// <summary>Why it was not reached, when <see cref="Status"/> is <see cref="AboutConnectionStatus.Failed"/>: masked, one line.</summary>
    public string? Message { get; init; }

    /// <summary>The server's version (<c>server_version</c>: <c>17.2</c>, or <c>17.2 (Debian 17.2-1.pgdg120+1)</c>), when connected.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>Whether the role is a superuser, when connected. Some statements <c>publish</c> writes need one.</summary>
    public bool? IsSuperuser { get; init; }

    /// <summary>
    /// Whether the role may create temporary objects in the database, when connected. Without it, <c>diff</c> compares views, defaults, and other
    /// expressions as text instead of as the server renders them, which can show changes that are not there.
    /// </summary>
    public bool? CanCreateTemporaryObjects { get; init; }
}
