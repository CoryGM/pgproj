namespace PgProj.Core;

/// <summary>What <c>pgproj init</c> reads and leaves out of a database (the options of <see cref="LiveDatabaseModelLoader.ScaffoldAsync"/>).</summary>
/// <param name="CaseSensitivePaths">True when the directory the files are written to tells <c>T.sql</c> and <c>t.sql</c> apart (<see cref="FileSystemCase"/>, G-212):
/// two objects whose names differ only by case then get files of those names; otherwise the second gets a <c>~2</c> suffix.</param>
/// <param name="IncludeSchemas">When not <see langword="null"/>, only these schemas are scaffolded (<c>--schemas</c>); each read as PostgreSQL reads a name, folded to lower case unless double-quoted (G-206).</param>
/// <param name="ExcludeSchemas">When not <see langword="null"/>, every schema but these is scaffolded (<c>--exclude-schemas</c>). At most one of the two lists is given.</param>
public sealed record ScaffoldOptions(
    IReadOnlyList<string>? IncludeSchemas = null,
    IReadOnlyList<string>? ExcludeSchemas = null,
    bool ExcludeGrants = false,
    bool ExcludeRoleMemberships = false,
    bool ExcludeOwners = false,
    bool CaseSensitivePaths = false)
{
    /// <summary>True when the project is scoped to some schemas (either list was given), so its <c>pgproj.json</c> states them.</summary>
    public bool IsScoped => IncludeSchemas is not null || ExcludeSchemas is not null;
}

/// <summary>An object <c>init</c> modeled, and the file it is in (relative to the project's <c>source</c> directory).</summary>
/// <param name="SchemaName">The schema, or <see langword="null"/> for an object that belongs to none (an event trigger, a cast, a publication).</param>
/// <param name="ObjectType">A display name: <c>Table</c>, <c>Materialized view</c>, <c>Text search dictionary</c>, ...</param>
public sealed record ScaffoldedObject(string? SchemaName, string ObjectType, string Name, string RelativePath);

/// <summary>An object in the database that pgproj cannot read, so <c>init</c> left it out (<see cref="SchemaName"/> is null for one that belongs to no schema).</summary>
public sealed record UnsupportedObject(string? SchemaName, string ObjectType, string Name)
{
    /// <summary>The <see cref="ObjectType"/> of an index PostgreSQL marks <c>INVALID</c> (a failed <c>CREATE INDEX CONCURRENTLY</c>), left out as <c>pg_dump</c> leaves it out (G-271).</summary>
    public const string InvalidIndex = "Invalid index";

    /// <summary>The <see cref="ObjectType"/> of a table whose internal (foreign-key) triggers are in mixed states, which <c>ALTER TABLE ... TRIGGER ALL</c> cannot state: <c>init</c> writes the most common one (G-261).</summary>
    public const string MixedInternalTriggerStates = "Mixed internal trigger states";
}

/// <summary>Something that belongs to the whole server, not to a schema: a role, a tablespace, a subscription. pgproj reports these and does not model them.</summary>
public sealed record ClusterWideObject(string ObjectType, string Name);

/// <summary>A scaffolded file that names a schema <c>init</c> left out (<see cref="Count"/> times), so a build of the new project cannot resolve it.</summary>
public sealed record DanglingReference(string RelativePath, string ExcludedSchema, int Count);

/// <summary>Everything <c>pgproj init</c> scaffolded and everything it left out, for its files and for its report.</summary>
public sealed record ScaffoldResult(
    IReadOnlyList<ScaffoldedFile> Files,
    IReadOnlyList<string> ModeledSchemas,
    IReadOnlyList<ScaffoldedObject> ModeledObjects,
    IReadOnlyList<UnsupportedObject> UnsupportedObjects,
    IReadOnlyList<string> ExcludedSchemas,
    IReadOnlyList<ClusterWideObject> ClusterWideObjects,
    IReadOnlyList<DanglingReference> DanglingReferences,
    int ExcludedOwnerCount,
    int ExcludedGrantCount,
    int ExcludedRoleMembershipCount,
    IReadOnlyList<string>? SearchPath = null);
