using System.Globalization;
using System.Text;
using PgProj.Engine.Documentation;

namespace PgProj.Engine;

/// <summary>Renders <see cref="DatabaseDocumentation"/> as one Markdown file with a table of contents (G-159). The diagram is in a <c>mermaid</c> block, which GitHub, GitLab, and VS Code's preview draw.</summary>
internal static class DocumentationMarkdown
{
    public static string Render(DatabaseDocumentation document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var text = new StringBuilder();
        text.Line($"<!-- {DocumentationFiles.Marker} on {DocAttributes.Utc(document.GeneratedUtc)} -->");
        text.Line($"# Database documentation: {document.Source.Name}");
        text.Line();
        text.Line($"Generated {DocAttributes.Utc(document.GeneratedUtc)} from the {document.Source.Kind} `{document.Source.Name}` by `pgproj document`.");
        if (document.SchemasOnly.Count > 0)
        {
            text.Line($" Limited to the schemas: {string.Join(", ", document.SchemasOnly.Select(static name => $"`{name}`"))}.");
        }
        else if (document.SchemasExcluded.Count > 0)
        {
            text.Line($" All schemas except: {string.Join(", ", document.SchemasExcluded.Select(static name => $"`{name}`"))}.");
        }

        text.Line();

        text.Line("## Contents");
        text.Line();
        text.Line("- [Summary](#summary)");
        if (document.Problems.Count > 0)
        {
            text.Line("- [Documentation problems](#documentation-problems)");
        }

        text.Line("- [Entity relationship diagram](#entity-relationship-diagram)");
        foreach (var schema in document.Schemas)
        {
            text.Line($"- [Schema {schema.Name}](#schema-{DocAttributes.Anchor(schema.Name)})");
            foreach (var group in DocAttributes.Objects(schema).GroupBy(static entry => entry.Group))
            {
                text.Line($"  - {group.Key}: {string.Join(", ", group.Select(static entry => $"[{entry.Item.Name}](#{DocAttributes.Anchor(entry.Item.QualifiedName)})"))}");
            }
        }

        text.Line();

        var counts = document.Counts;
        text.Line("## Summary");
        text.Line();
        text.Line("| | |");
        text.Line("|---|---|");
        foreach (var (label, value) in new[]
        {
            ("Schemas", counts.Schemas), ("Tables", counts.Tables), ("Columns", counts.Columns), ("Views", counts.Views), ("Functions and procedures", counts.Routines),
            ("Types", counts.Types), ("Sequences", counts.Sequences), ("Indexes", counts.Indexes), ("Constraints", counts.Constraints), ("Triggers", counts.Triggers), ("Policies", counts.Policies),
            ("Documentation errors", counts.Errors), ("Documentation warnings", counts.Warnings),
        })
        {
            text.Line($"| {label} | {value.ToString(CultureInfo.InvariantCulture)} |");
        }

        text.Line();

        if (document.Problems.Count > 0)
        {
            text.Line("## Documentation problems");
            text.Line();
            text.Line("| Object | Severity | Tag | Problem |");
            text.Line("|---|---|---|---|");
            foreach (var problem in document.Problems)
            {
                text.Line($"| {Cell(problem.Object)} | {problem.Severity.ToString().ToLowerInvariant()} | `@{problem.Tag}` | {Cell(problem.Message)} |");
            }

            text.Line();
        }

        text.Line("## Entity relationship diagram");
        text.Line();
        text.Line("```mermaid");
        text.Line(document.Erd.TrimEnd());
        text.Line("```");
        text.Line();

        foreach (var schema in document.Schemas)
        {
            text.Line($"<a id=\"{DocAttributes.Anchor(schema.QualifiedName)}\"></a>");
            text.Line($"## Schema {schema.Name}");
            text.Line();
            Describe(text, schema);
            Section(text, "Tables", schema.Tables, table => Table(text, table));
            Section(text, "Views", schema.Views, view => View(text, view));
            Section(text, "Functions and procedures", schema.Routines, routine => Routine(text, routine));
            Section(text, "Types", schema.Types, type => Type(text, type));
            Section(text, "Sequences", schema.Sequences, sequence => Sequence(text, sequence));
        }

        return text.ToString();
    }

    private static void Section<T>(StringBuilder text, string title, IReadOnlyList<T> items, Action<T> render)
    {
        if (items.Count == 0)
        {
            return;
        }

        text.Line($"### {title}");
        text.Line();
        foreach (var item in items)
        {
            render(item);
        }
    }

    private static void Heading(StringBuilder text, DocItem item, string kind)
    {
        text.Line($"<a id=\"{DocAttributes.Anchor(item.QualifiedName)}\"></a>");
        text.Line($"#### {item.QualifiedName}{(item.Deprecated is { Flag: true } ? " (deprecated)" : string.Empty)}");
        text.Line();
        text.Line($"*{kind}*{(item.BusinessName is null ? string.Empty : $" · {item.BusinessName}")}");
        text.Line();
        Describe(text, item);
    }

    /// <summary>The description and the labelled tags of an object, as a paragraph and a list.</summary>
    private static void Describe(StringBuilder text, DocItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            text.Line(item.Description);
            text.Line();
        }

        var attributes = DocAttributes.For(item).Where(static attribute => attribute.Label != "Business name").ToArray();
        foreach (var attribute in attributes)
        {
            text.Line($"- **{attribute.Label}:** {string.Join("; ", attribute.Values.Select(static value => Inline(value)))}");
        }

        if (item.SeeAlso is { Count: > 0 })
        {
            text.Line($"- **See also:** {string.Join(", ", item.SeeAlso.Select(static link => link.Target is null ? Inline(link.Value) : $"[{link.Value}](#{DocAttributes.Anchor(link.Target)})"))}");
        }

        if (item.InvalidTags is { Count: > 0 })
        {
            foreach (var (name, value) in item.InvalidTags)
            {
                text.Line($"- **Problem with `@{name}`:** `{value.Replace('\n', ' ')}`");
            }
        }

        if (attributes.Length > 0 || item.SeeAlso is { Count: > 0 } || item.InvalidTags is { Count: > 0 })
        {
            text.Line();
        }
    }

    private static void Table(StringBuilder text, DocTable table)
    {
        Heading(text, table, table.Kind);
        if (table.PartitionedBy is not null)
        {
            text.Line($"Partitioned by {table.PartitionedBy}.");
            text.Line();
        }

        if (table.PartitionOf is not null)
        {
            text.Line($"A partition of `{table.PartitionOf}`.");
            text.Line();
        }

        text.Line("| # | Column | Type | Null | Default | Keys | Description |");
        text.Line("|---|---|---|---|---|---|---|");
        foreach (var column in table.Columns)
        {
            var keys = new List<string>();
            if (column.PrimaryKey)
            {
                keys.Add("PK");
            }

            if (column.ForeignKey)
            {
                keys.Add("FK");
            }

            if (column.Unique)
            {
                keys.Add("UK");
            }

            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(column.Description))
            {
                notes.Add(column.Description);
            }

            if (column.References is { Count: > 0 })
            {
                notes.Add($"References {string.Join(", ", column.References)}.");
            }

            if (column.Generated is not null)
            {
                notes.Add($"Generated: `{column.Generated}`.");
            }

            if (column.Identity is not null)
            {
                notes.Add($"Identity: {column.Identity}.");
            }

            notes.AddRange(DocAttributes.For(column).Select(static attribute => $"{attribute.Label}: {string.Join("; ", attribute.Values)}"));
            if (column.SeeAlso is { Count: > 0 })
            {
                notes.Add($"See also: {string.Join(", ", column.SeeAlso.Select(static link => link.Target is null ? link.Value : $"[{link.Value}](#{DocAttributes.Anchor(link.Target)})"))}");
            }

            if (column.InvalidTags is { Count: > 0 })
            {
                notes.AddRange(column.InvalidTags.Select(static pair => $"Problem with @{pair.Key}: {pair.Value}"));
            }

            text.Line($"| {column.Position.ToString(CultureInfo.InvariantCulture)} | `{column.Name}` | {Cell(column.Type)} | {DocAttributes.YesNo(column.Nullable)} | {(column.Default is null ? string.Empty : $"`{Cell(column.Default)}`")} | {string.Join(", ", keys)} | {string.Join("<br>", notes.Select(Cell))} |");
        }

        text.Line();

        if (table.Constraints.Count > 0)
        {
            text.Line("**Constraints**");
            text.Line();
            text.Line("| Name | Type | Columns | Definition | Description |");
            text.Line("|---|---|---|---|---|");
            foreach (var constraint in table.Constraints)
            {
                var definition = constraint.Definition is null ? string.Empty : $"`{Cell(constraint.Definition)}`";
                var actions = string.Join(" ", new[] { constraint.OnUpdate is null ? null : $"ON UPDATE {constraint.OnUpdate}", constraint.OnDelete is null ? null : $"ON DELETE {constraint.OnDelete}", constraint.Deferrable ? "DEFERRABLE" : null }.Where(static part => part is not null));
                text.Line($"| `{constraint.Name}` | {constraint.Type} | {string.Join(", ", constraint.Columns)} | {definition}{(actions.Length > 0 ? " " + actions : string.Empty)} | {Notes(constraint)} |");
            }

            text.Line();
        }

        if (table.Triggers.Count > 0)
        {
            text.Line("**Triggers**");
            text.Line();
            text.Line("| Name | When | Level | Runs | Description |");
            text.Line("|---|---|---|---|---|");
            foreach (var trigger in table.Triggers)
            {
                var when = $"{trigger.Timing} {string.Join(" OR ", trigger.Events)}{(trigger.When is null ? string.Empty : $" WHEN ({trigger.When})")}".Trim();
                text.Line($"| `{trigger.Name}` | {Cell(when)} | {trigger.Level}{(trigger.ConstraintTrigger ? " (constraint trigger)" : string.Empty)} | `{Cell(trigger.Function)}` | {Notes(trigger)} |");
            }

            text.Line();
        }

        if (table.Policies.Count > 0)
        {
            text.Line($"**Row-level security policies**{(table.RowSecurity ? string.Empty : " (row-level security is not enabled on the table)")}");
            text.Line();
            text.Line("| Name | Applies to | Roles | Using | With check | Description |");
            text.Line("|---|---|---|---|---|---|");
            foreach (var policy in table.Policies)
            {
                text.Line($"| `{policy.Name}` | {(policy.Permissive ? "permissive" : "restrictive")} {policy.Command} | {Cell(string.Join(", ", policy.Roles))} | {(policy.Using is null ? string.Empty : $"`{Cell(policy.Using)}`")} | {(policy.WithCheck is null ? string.Empty : $"`{Cell(policy.WithCheck)}`")} | {Notes(policy)} |");
            }

            text.Line();
        }

        if (table.Indexes.Count > 0)
        {
            text.Line("**Indexes**");
            text.Line();
            text.Line("| Name | Method | Unique | Keys | Where | Description |");
            text.Line("|---|---|---|---|---|---|");
            foreach (var index in table.Indexes)
            {
                text.Line($"| `{index.Name}` | {index.Method} | {DocAttributes.YesNo(index.Unique)} | {Cell(string.Join(", ", index.Keys))}{(index.Include is { Count: > 0 } ? $" INCLUDE ({string.Join(", ", index.Include)})" : string.Empty)} | {(index.Predicate is null ? string.Empty : $"`{Cell(index.Predicate)}`")} | {Notes(index)} |");
            }

            text.Line();
        }
    }

    /// <summary>An object's description and labelled tags as the text of one table cell, one line each.</summary>
    private static string Notes(DocItem item)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            notes.Add(item.Description);
        }

        notes.AddRange(DocAttributes.For(item).Select(static attribute => $"{attribute.Label}: {string.Join("; ", attribute.Values)}"));
        if (item.SeeAlso is { Count: > 0 })
        {
            notes.Add($"See also: {string.Join(", ", item.SeeAlso.Select(static link => link.Target is null ? link.Value : $"[{link.Value}](#{DocAttributes.Anchor(link.Target)})"))}");
        }

        if (item.InvalidTags is { Count: > 0 })
        {
            notes.AddRange(item.InvalidTags.Select(static pair => $"Problem with @{pair.Key}: {pair.Value}"));
        }

        return string.Join("<br>", notes.Select(Cell));
    }

    private static void View(StringBuilder text, DocView view)
    {
        Heading(text, view, view.Materialized ? "materialized view" : "view");
        if (view.Columns.Count > 0)
        {
            text.Line("| Column | Type | Description |");
            text.Line("|---|---|---|");
            foreach (var column in view.Columns)
            {
                text.Line($"| `{column.Name}` | {Cell(column.Type)} | {Cell(column.Description)} |");
            }

            text.Line();
        }

        if (!string.IsNullOrWhiteSpace(view.Definition))
        {
            text.Line("```sql");
            text.Line(view.Definition);
            text.Line("```");
            text.Line();
        }
    }

    private static void Routine(StringBuilder text, DocRoutine routine)
    {
        Heading(text, routine, routine.Kind);
        text.Line($"`{routine.Signature}`{(routine.Returns is null ? string.Empty : $" returns `{routine.Returns}`")}");
        text.Line();
        var facts = new[] { routine.Language is null ? null : $"Language: {routine.Language}", routine.Volatility is null ? null : $"Volatility: {routine.Volatility}", routine.SecurityDefiner ? "Security definer" : null }.Where(static fact => fact is not null);
        var line = string.Join(" · ", facts);
        if (line.Length > 0)
        {
            text.Line(line);
            text.Line();
        }
    }

    private static void Type(StringBuilder text, DocType type)
    {
        Heading(text, type, type.Kind ?? "type");
        if (type.BaseType is not null)
        {
            text.Line($"Base type: `{type.BaseType}`");
            text.Line();
        }

        if (type.Labels is { Count: > 0 })
        {
            text.Line($"Labels: {string.Join(", ", type.Labels.Select(static label => $"`{label}`"))}");
            text.Line();
        }

        if (type.Fields is { Count: > 0 })
        {
            text.Line("| Field | Type | Description |");
            text.Line("|---|---|---|");
            foreach (var field in type.Fields)
            {
                text.Line($"| `{field.Name}` | {Cell(field.Type)} | {Cell(field.Description)} |");
            }

            text.Line();
        }
    }

    private static void Sequence(StringBuilder text, DocSequence sequence)
    {
        Heading(text, sequence, "sequence");
        var facts = new[]
        {
            sequence.DataType is null ? null : $"Type: {sequence.DataType}",
            sequence.Start is null ? null : $"Start: {sequence.Start}",
            sequence.Increment is null ? null : $"Increment: {sequence.Increment}",
            sequence.Minimum is null ? null : $"Minimum: {sequence.Minimum}",
            sequence.Maximum is null ? null : $"Maximum: {sequence.Maximum}",
            sequence.Cycle is true ? "Cycles" : null,
            sequence.OwnedBy is null ? null : $"Owned by {sequence.OwnedBy}",
        }.Where(static fact => fact is not null);
        var line = string.Join(" · ", facts);
        if (line.Length > 0)
        {
            text.Line(line);
            text.Line();
        }
    }

    /// <summary>A value in a table cell: a pipe would end the cell and a line break would end the row.</summary>
    private static string Cell(string? value)
        => value is null ? string.Empty : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);

    /// <summary>A tag value in a list item: a line break would end the item.</summary>
    private static string Inline(string value) => value.Replace("\n", " ", StringComparison.Ordinal);
}
