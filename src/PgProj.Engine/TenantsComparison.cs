using System.Text.RegularExpressions;
using PgProj.Diff;

namespace PgProj.Engine;

/// <summary>One tenant's changes (G-290): what in its schema differs from the template read for it.</summary>
public sealed record TenantDifference(TenantTarget Tenant, ChangeSet Changes);

/// <summary>Tenants with the same changes, which are shown once (in the template's stand-in schema).</summary>
public sealed record TenantGroup(IReadOnlyList<TenantDifference> Members);

/// <summary>
/// A project with tenants compared with a database (G-290), beyond the shared side (<see cref="CompareResult.Changes"/>): each tenant's changes, the
/// tenants grouped by identical changes, the new ones, the up-to-date ones, and the schemas that were tenants' once.
/// </summary>
public sealed class TenantsComparison
{
    /// <summary>A comparison of the tenants in <paramref name="list"/>, each tenant's changes in <paramref name="tenants"/>, shown in <paramref name="standInSchema"/>.</summary>
    public TenantsComparison(string standInSchema, TenantList list, IReadOnlyList<TenantDifference> tenants)
    {
        StandInSchema = standInSchema;
        List = list;
        Tenants = tenants;
        Groups = [.. tenants
            .Where(static tenant => tenant.Tenant.Exists && Count(tenant.Changes) > 0)
            .GroupBy(tenant => Render(tenant, standInSchema), StringComparer.Ordinal)
            .Select(static group => new TenantGroup([.. group]))
            .OrderByDescending(static group => group.Members.Count)
            .ThenBy(static group => group.Members[0].Tenant.Schema, StringComparer.Ordinal)];
        NewTenants = [.. tenants.Where(static tenant => !tenant.Tenant.Exists)];
        UpToDate = [.. tenants.Where(static tenant => tenant.Tenant.Exists && Count(tenant.Changes) == 0)];
    }

    /// <summary>The schema the template's objects are shown in (the prefix).</summary>
    public string StandInSchema { get; }

    /// <summary>The tenants found, the schemas that were tenants', and why the tenants were not read, when they were not.</summary>
    public TenantList List { get; }

    /// <summary>Every tenant compared, with its changes.</summary>
    public IReadOnlyList<TenantDifference> Tenants { get; }

    /// <summary>The changed tenants whose schema exists, grouped by identical changes, the largest group first.</summary>
    public IReadOnlyList<TenantGroup> Groups { get; }

    /// <summary>The tenants whose schema does not exist yet.</summary>
    public IReadOnlyList<TenantDifference> NewTenants { get; }

    /// <summary>The tenants whose schema exists and has nothing to change.</summary>
    public IReadOnlyList<TenantDifference> UpToDate { get; }

    /// <summary>The added, removed, and modified objects of every tenant.</summary>
    public (int Added, int Removed, int Modified) Totals
        => (Tenants.Sum(static tenant => tenant.Changes.Added.Count), Tenants.Sum(static tenant => tenant.Changes.Removed.Count), Tenants.Sum(static tenant => tenant.Changes.Modified.Count));

    /// <summary>Shows a tenant's schema as the stand-in, in names and values (<c>tenant_acme.orders</c> as <c>tenant.orders</c>), so tenants with the same changes read alike.</summary>
    public static Func<string, string> StandInDisplay(string schema, string standIn)
    {
        var quoted = new Regex($"\"{Regex.Escape(schema.Replace("\"", "\"\"", StringComparison.Ordinal))}\"", RegexOptions.CultureInvariant);
        var bare = new Regex($"(?<![A-Za-z0-9_$\"]){Regex.Escape(schema)}(?![A-Za-z0-9_$\"])", RegexOptions.CultureInvariant);
        return text => bare.Replace(quoted.Replace(text, standIn), standIn);
    }

    internal static int Count(ChangeSet changes) => changes.Added.Count + changes.Removed.Count + changes.Modified.Count;

    private static string Render(TenantDifference tenant, string standIn)
    {
        using var writer = new StringWriter();
        ChangeListText.Write(tenant.Changes, writer, StandInDisplay(tenant.Tenant.Schema, standIn));
        return writer.ToString();
    }
}
