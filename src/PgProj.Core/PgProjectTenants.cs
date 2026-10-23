namespace PgProj.Core;

/// <summary>Where a project's tenants come from (G-290).</summary>
public enum TenantSource
{
    /// <summary>The database's schemas named <c>prefix_...</c>. pgproj creates no tenant schema: the application does.</summary>
    Schemas,

    /// <summary>The identifiers a query returns; pgproj creates a tenant schema the database does not have yet.</summary>
    Query,

    /// <summary>The identifiers a CSV file lists; pgproj creates a tenant schema the database does not have yet.</summary>
    File,
}

/// <summary>One tenant a CSV file lists (G-290): its identifier, exactly as written, and the line it is on.</summary>
public sealed record TenantFileEntry(string Identifier, int Line);

/// <summary>
/// The <c>tenants</c> block of <c>pgproj.json</c> (G-290): a database with a schema per tenant, each holding the same objects. The template folder holds those
/// objects once, written without a schema; each tenant's schema is <c>prefix_identifier</c>. The template is read once per tenant with that schema as the
/// default schema and first on the search path, and checked by <c>build</c> in the stand-in schema, the prefix itself, which no tenant's schema can be.
/// </summary>
public sealed record class PgProjectTenants(string TemplatePath, string Prefix, TenantSource Source)
{
    /// <summary>The <c>*.sql</c> files under <see cref="TemplatePath"/>, in the order they are read.</summary>
    public IReadOnlyList<string> TemplateFiles { get; init; } = [];

    /// <summary>The query that returns the tenants' identifiers, for <see cref="TenantSource.Query"/>.</summary>
    public string? Query { get; init; }

    /// <summary>The CSV file that lists the tenants' identifiers, for <see cref="TenantSource.File"/>.</summary>
    public string? FilePath { get; init; }

    /// <summary>The tenants <see cref="FilePath"/> lists, in file order, for <see cref="TenantSource.File"/>; empty otherwise.</summary>
    public IReadOnlyList<TenantFileEntry> FileTenants { get; init; } = [];

    /// <summary>
    /// <c>tenants.predeploy</c> (G-290e): run in each tenant's transaction before the template's changes, with its variables and <c>run when=</c> markers.
    /// Null when there is none.
    /// </summary>
    public ExpandedScript? PreDeploy { get; init; }

    /// <summary><c>tenants.postdeploy</c> (G-290e): run in each tenant's transaction after the template's changes. Null when there is none.</summary>
    public ExpandedScript? PostDeploy { get; init; }

    /// <summary>The <c>run when=</c> values a tenant's script file may have (G-290e); without one it runs on every publish.</summary>
    public static readonly IReadOnlyList<string> TenantRunWhen = [TenantChanged, TenantCreated];

    /// <summary>Runs when this tenant has changes in the publish.</summary>
    public const string TenantChanged = "tenant-changed";

    /// <summary>Runs when pgproj creates the tenant's schema, or fills it for the first time (it has no objects yet).</summary>
    public const string TenantCreated = "tenant-created";

    /// <summary>The variables a tenant's scripts have, and their values for <paramref name="identifier"/> (G-290e).</summary>
    public IReadOnlyDictionary<string, string> VariablesFor(string identifier)
        => new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant_schema"] = SchemaFor(identifier), ["tenant_id"] = identifier };

    /// <summary>The schema the template is checked in by <c>build</c>, and that a statement about the tenant's schema names in the template: the prefix.</summary>
    public string StandInSchema => Prefix;

    /// <summary>The schema of the tenant <paramref name="identifier"/>: <c>prefix_identifier</c>.</summary>
    public string SchemaFor(string identifier) => Prefix + "_" + identifier;

    /// <summary>True when <paramref name="schema"/> is named as a tenant's schema is, <c>prefix_...</c>.</summary>
    public bool IsTenantSchemaName(string schema) => schema.Length > Prefix.Length + 1 && schema.StartsWith(Prefix + "_", StringComparison.Ordinal);

    /// <summary>PostgreSQL's limit on a name, in bytes (<c>NAMEDATALEN</c> - 1); a longer one is cut short, so two tenants could share a schema.</summary>
    public const int MaxSchemaNameBytes = 63;
}
