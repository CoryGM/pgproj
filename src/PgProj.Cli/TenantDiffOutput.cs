using PgProj.Engine;

namespace PgProj.Cli;

/// <summary>
/// Writes <c>diff</c>'s result for a project with tenants (G-290, the user's choice of 2026-10-14): the shared changes, then the tenants grouped by
/// identical changes, each group shown once in the template's stand-in schema; tenants whose schema does not exist yet, as a count; tenants with nothing
/// to change, by name; and the totals, counting each tenant, in the <c>Added: n</c>/<c>Removed: n</c>/<c>Modified: n</c> lines <c>diff</c> always ends with.
/// The result (<see cref="CompareResult"/>) holds the grouping; this is its text form. The JSON form is the result's own (<see cref="CompareResult.ToJson"/>).
/// </summary>
public static class TenantDiffOutput
{
    /// <summary>How many tenant names a line of text names before it says how many more; the JSON form names them all.</summary>
    internal const int NamesShown = 10;

    public static void WriteText(CompareResult result, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(writer);
        var tenants = result.Tenants ?? throw new ArgumentException("The result has no tenants.", nameof(result));

        writer.WriteLine("Shared schemas:");
        if (TenantsComparison.Count(result.Changes) == 0)
        {
            writer.WriteLine("  No changes.");
        }
        else
        {
            DiffOutputWriter.WriteChanges(result.Changes, writer);
        }

        writer.WriteLine();
        if (tenants.List.Unavailable is not null)
        {
            writer.WriteLine("Tenants: not read (see the warning).");
        }
        else if (tenants.Tenants.Count == 0)
        {
            writer.WriteLine("Tenants: none.");
        }
        else
        {
            writer.WriteLine($"Tenants: {tenants.Tenants.Count}, their objects shown in the template's stand-in schema, {tenants.StandInSchema}.");
            var groups = tenants.Groups;
            for (var index = 0; index < groups.Count; index++)
            {
                var members = groups[index].Members;
                var differs = index > 0 ? ", changed differently from the tenants above" : string.Empty;
                writer.WriteLine($"{members.Count} tenant(s){differs}: {Names(members.Select(static member => member.Tenant.Schema))}");
                DiffOutputWriter.WriteChanges(members[0].Changes, writer, TenantsComparison.StandInDisplay(members[0].Tenant.Schema, tenants.StandInSchema));
            }

            if (tenants.NewTenants.Count > 0)
            {
                var objects = tenants.NewTenants[0].Changes.Added.Count;
                writer.WriteLine($"{tenants.NewTenants.Count} new tenant(s), with no schema yet; publish creates each with the template's {objects} object(s): {Names(tenants.NewTenants.Select(static tenant => tenant.Tenant.Schema))}");
            }

            if (tenants.UpToDate.Count > 0)
            {
                writer.WriteLine($"{tenants.UpToDate.Count} tenant(s) up to date: {Names(tenants.UpToDate.Select(static tenant => tenant.Tenant.Schema))}");
            }
        }

        writer.WriteLine($"Added: {result.AddedCount}");
        writer.WriteLine($"Removed: {result.RemovedCount}");
        writer.WriteLine($"Modified: {result.ModifiedCount}");
    }

    /// <summary>The warning for schemas named as tenants' are that the query or file no longer lists (G-290, the user's wording of 2026-10-14).</summary>
    public static string DescribeFormerTenants(IReadOnlyList<string> schemas, bool forPublish)
        => PublishTexts.DescribeFormerTenants(schemas, forPublish);

    private static string Names(IEnumerable<string> names)
    {
        var all = names.ToArray();
        return all.Length <= NamesShown
            ? string.Join(", ", all)
            : $"{string.Join(", ", all.Take(NamesShown))}, and {all.Length - NamesShown} more (--format json lists them all)";
    }
}
