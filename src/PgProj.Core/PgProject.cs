namespace PgProj.Core;

public sealed record class PgProject(
    string Name,
    string Version,
    IReadOnlyList<string> Schemas,
    string SourcePath,
    string? PreDeployScript,
    string? PostDeployScript,
    IReadOnlyList<string> SqlFiles)
{
    /// <summary>
    /// <c>sourceConnectionString</c> from <c>pgproj.json</c> (G-304a): the database <c>init</c> scaffolded the project from, written by <c>init</c> with a password placeholder.
    /// <c>document --source-connection</c> given with no value (and no <c>PGPROJ_SOURCE_CONNECTION</c>) reads it. A command uses it only when it was told to read a source database.
    /// </summary>
    public string? SourceConnectionString { get; init; }

    /// <summary>
    /// <c>targetConnectionString</c> from <c>pgproj.json</c> (G-304a): the database <c>diff</c> and <c>publish</c> compare the project with and change when neither
    /// <c>--target-connection</c> nor <c>PGPROJ_TARGET_CONNECTION</c> is given. No tool writes it.
    /// </summary>
    public string? TargetConnectionString { get; init; }

    public string? PreDeployScriptContent { get; init; }

    public string? PostDeployScriptContent { get; init; }

    /// <summary>The files that make up the pre-deployment script: itself first, then every file it includes with <c>:r</c> (G-124). Empty when there is none.</summary>
    public IReadOnlyList<ScriptSourceFile> PreDeployScriptFiles { get; init; } = [];

    /// <summary>The files that make up the post-deployment script (G-124), as <see cref="PreDeployScriptFiles"/>.</summary>
    public IReadOnlyList<ScriptSourceFile> PostDeployScriptFiles { get; init; } = [];

    /// <summary>The pre-deployment script as a tree of its files, each with its <c>run when=</c> marker (G-290e); null when there is none.</summary>
    public ScriptPart? PreDeployScriptParts { get; init; }

    /// <summary>The post-deployment script as a tree of its files (G-290e); null when there is none.</summary>
    public ScriptPart? PostDeployScriptParts { get; init; }

    /// <summary>The marker a project's own pre- and post-deploy file may have (G-290e): it runs only when the publish has changes.</summary>
    public const string RunWhenChanged = "changed";

    /// <summary>
    /// <c>excludeGrants</c> from <c>pgproj.json</c> (G-87): when true, object/schema/column privilege grants are
    /// not modeled, diffed, or deployed for this project, the same as if they were never written in source.
    /// Defaults to <see langword="false"/> (today's behavior: grants are included). A command's own
    /// <c>--exclude-grants</c> option overrides this per run, in either direction.
    /// </summary>
    public bool ExcludeGrants { get; init; }

    /// <summary>
    /// <c>excludeRoleMemberships</c> from <c>pgproj.json</c> (G-87): the same opt-out as <see cref="ExcludeGrants"/>,
    /// scoped to role membership (<c>GRANT role TO grantee</c>) only. The two are independent: a project may
    /// exclude one without the other.
    /// </summary>
    public bool ExcludeRoleMemberships { get; init; }

    /// <summary>
    /// <c>excludeOwners</c> from <c>pgproj.json</c> (G-146): when true, the owners a project states (<c>ALTER ... OWNER TO</c>) are not modeled, diffed, or deployed, and
    /// the database's owners are not read, the same opt-out as <see cref="ExcludeGrants"/>. For a project that runs against databases whose roles are named differently.
    /// </summary>
    public bool ExcludeOwners { get; init; }

    /// <summary>
    /// <c>searchPath</c> from <c>pgproj.json</c> (G-93): the schemas an unqualified name is looked up in, in order, the way PostgreSQL's
    /// <c>search_path</c> does (<c>pg_catalog</c> is always searched first, as PostgreSQL does). <c>build</c> reports an unqualified reference
    /// that is found only outside it, an unqualified declaration is created in its first schema, and when it is set <c>publish</c> runs
    /// under it. Defaults to <c>public</c> alone, PostgreSQL's default less <c>"$user"</c>.
    /// </summary>
    public IReadOnlyList<string> SearchPath { get; init; } = [DefaultSearchPath];

    /// <summary>True when <c>pgproj.json</c> sets <c>searchPath</c>; only then does <c>publish</c> pin the path (G-93).</summary>
    public bool HasConfiguredSearchPath { get; init; }

    /// <summary>The schema an unqualified declaration is created in: the first entry of <see cref="SearchPath"/> (G-93, G-114).</summary>
    public string DefaultSchema => SearchPath.Count > 0 ? SearchPath[0] : DefaultSearchPath;

    /// <summary>
    /// <c>tenants</c> from <c>pgproj.json</c> (G-290): the template every tenant's schema holds, the prefix of their schemas' names, and where the list of
    /// tenants comes from. Null for a project without tenants.
    /// </summary>
    public PgProjectTenants? Tenants { get; init; }

    public const string DefaultSearchPath = "public";
}
