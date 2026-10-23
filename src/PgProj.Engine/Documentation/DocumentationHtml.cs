using System.Globalization;
using System.Net;
using System.Text;
using PgProj.Engine.Documentation;

namespace PgProj.Engine;

/// <summary>
/// Renders <see cref="DatabaseDocumentation"/> as one web page (G-159): a contents list beside the data dictionary, with the diagram drawn by Mermaid, which the page loads from a CDN (so it needs internet
/// access to draw the diagram; everything else is in the file, with its own styles and no other script). Everything written in a comment is HTML-encoded. A description is shown as text, with its blank lines
/// as paragraph breaks.
/// </summary>
internal static class DocumentationHtml
{
    private const string Style = """
        :root { --bg: #ffffff; --fg: #1f2328; --muted: #59636e; --line: #d1d9e0; --soft: #f6f8fa; --accent: #0969da; --warn: #9a6700; --bad: #cf222e; }
        @media (prefers-color-scheme: dark) { :root { --bg: #0d1117; --fg: #e6edf3; --muted: #9198a1; --line: #3d444d; --soft: #151b23; --accent: #4493f8; --warn: #d29922; --bad: #f85149; } }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, sans-serif; display: flex; min-height: 100vh; }
        nav { width: 280px; flex: none; padding: 1rem; border-right: 1px solid var(--line); background: var(--soft); position: sticky; top: 0; height: 100vh; overflow: auto; font-size: 13px; }
        nav ul { list-style: none; margin: 0; padding-left: 0.9rem; } nav > ul { padding-left: 0; } nav li { margin: 0.15rem 0; }
        nav .group { color: var(--muted); margin-top: 0.4rem; }
        main { flex: 1; min-width: 0; padding: 1.5rem 2rem; max-width: 1200px; }
        a { color: var(--accent); text-decoration: none; } a:hover { text-decoration: underline; }
        h1 { margin-top: 0; } h2 { border-bottom: 1px solid var(--line); padding-bottom: 0.3rem; margin-top: 2.2rem; } h3 { margin-top: 1.6rem; } h4 { margin: 1.6rem 0 0.2rem; font-size: 1.1rem; }
        .kind { color: var(--muted); font-size: 13px; margin: 0 0 0.5rem; }
        .badge { display: inline-block; border: 1px solid var(--line); border-radius: 1rem; padding: 0 0.5rem; font-size: 12px; color: var(--muted); margin-left: 0.4rem; }
        .badge.deprecated { color: var(--warn); border-color: var(--warn); } .badge.pii { color: var(--bad); border-color: var(--bad); }
        table { border-collapse: collapse; margin: 0.5rem 0 1rem; display: block; overflow-x: auto; max-width: 100%; }
        th, td { border: 1px solid var(--line); padding: 0.3rem 0.6rem; text-align: left; vertical-align: top; } th { background: var(--soft); }
        code, pre { font: 13px/1.4 ui-monospace, Consolas, monospace; } code { background: var(--soft); padding: 0 0.25rem; border-radius: 3px; }
        pre { background: var(--soft); padding: 0.75rem; overflow: auto; border-radius: 6px; } pre.mermaid { background: transparent; }
        .attributes { margin: 0.3rem 0 0.8rem; padding-left: 1.2rem; } .error { color: var(--bad); } .warning { color: var(--warn); }
        .description p { margin: 0.4rem 0; }
        @media (max-width: 800px) { body { display: block; } nav { position: static; width: auto; height: auto; border-right: 0; border-bottom: 1px solid var(--line); } main { padding: 1rem; } }
        """;

    public static string Render(DatabaseDocumentation document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var title = $"Database documentation: {document.Source.Name}";
        var html = new StringBuilder();
        html.Line("<!doctype html>");
        html.Line($"<!-- {DocumentationFiles.Marker} on {DocAttributes.Utc(document.GeneratedUtc)} -->");
        html.Line("<html lang=\"en\">");
        html.Line("<head>");
        html.Line("<meta charset=\"utf-8\">");
        html.Line("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Line($"<title>{E(title)}</title>");
        html.Line("<style>");
        html.Line(Style);
        html.Line("</style>");
        html.Line("</head>");
        html.Line("<body>");

        html.Line("<nav>");
        html.Line("<ul>");
        html.Line("<li><a href=\"#summary\">Summary</a></li>");
        if (document.Problems.Count > 0)
        {
            html.Line("<li><a href=\"#problems\">Documentation problems</a></li>");
        }

        html.Line("<li><a href=\"#erd\">Entity relationship diagram</a></li>");
        foreach (var schema in document.Schemas)
        {
            html.Line($"<li><a href=\"#schema-{E(DocAttributes.Anchor(schema.Name))}\">Schema {E(schema.Name)}</a>");
            html.Line("<ul>");
            foreach (var group in DocAttributes.Objects(schema).GroupBy(static entry => entry.Group))
            {
                html.Line($"<li class=\"group\">{E(group.Key)}<ul>");
                foreach (var entry in group)
                {
                    html.Line($"<li><a href=\"#{E(DocAttributes.Anchor(entry.Item.QualifiedName))}\">{E(entry.Item.Name)}</a></li>");
                }

                html.Line("</ul></li>");
            }

            html.Line("</ul></li>");
        }

        html.Line("</ul>");
        html.Line("</nav>");

        html.Line("<main>");
        html.Line($"<h1>{E(title)}</h1>");
        var scope = document.SchemasOnly.Count > 0
            ? $" Limited to the schemas: {string.Join(", ", document.SchemasOnly.Select(static name => $"<code>{E(name)}</code>"))}."
            : document.SchemasExcluded.Count > 0 ? $" All schemas except: {string.Join(", ", document.SchemasExcluded.Select(static name => $"<code>{E(name)}</code>"))}." : string.Empty;
        html.Line($"<p>Generated {E(DocAttributes.Utc(document.GeneratedUtc))} from the {E(document.Source.Kind)} <code>{E(document.Source.Name)}</code> by <code>pgproj document</code>.{scope}</p>");

        var counts = document.Counts;
        html.Line("<h2 id=\"summary\">Summary</h2>");
        html.Line("<table><tbody>");
        foreach (var (label, value) in new[]
        {
            ("Schemas", counts.Schemas), ("Tables", counts.Tables), ("Columns", counts.Columns), ("Views", counts.Views), ("Functions and procedures", counts.Routines),
            ("Types", counts.Types), ("Sequences", counts.Sequences), ("Indexes", counts.Indexes), ("Constraints", counts.Constraints), ("Triggers", counts.Triggers), ("Policies", counts.Policies),
            ("Documentation errors", counts.Errors), ("Documentation warnings", counts.Warnings),
        })
        {
            html.Line($"<tr><th>{E(label)}</th><td>{value.ToString(CultureInfo.InvariantCulture)}</td></tr>");
        }

        html.Line("</tbody></table>");

        if (document.Problems.Count > 0)
        {
            html.Line("<h2 id=\"problems\">Documentation problems</h2>");
            html.Line("<table><thead><tr><th>Object</th><th>Severity</th><th>Tag</th><th>Problem</th></tr></thead><tbody>");
            foreach (var problem in document.Problems)
            {
                var severity = problem.Severity.ToString().ToLowerInvariant();
                html.Line($"<tr><td>{E(problem.Object)}</td><td class=\"{severity}\">{severity}</td><td><code>@{E(problem.Tag)}</code></td><td>{E(problem.Message)}</td></tr>");
            }

            html.Line("</tbody></table>");
        }

        html.Line("<h2 id=\"erd\">Entity relationship diagram</h2>");
        html.Line("<pre class=\"mermaid\">");
        html.Line(E(document.Erd.TrimEnd()));
        html.Line("</pre>");
        html.Line("<noscript><p>Turn on JavaScript to see the diagram.</p></noscript>");

        foreach (var schema in document.Schemas)
        {
            html.Line($"<h2 id=\"schema-{E(DocAttributes.Anchor(schema.Name))}\"><span id=\"{E(DocAttributes.Anchor(schema.QualifiedName))}\"></span>Schema {E(schema.Name)}</h2>");
            var schemaLabel = string.Join(" · ", new[] { schema.BusinessName, schema.Classification }.Where(static part => !string.IsNullOrWhiteSpace(part)).Select(E));
            if (schemaLabel.Length > 0)
            {
                html.Line($"<p class=\"kind\">{schemaLabel}</p>");
            }

            Describe(html, schema);
            Section(html, "Tables", schema.Tables, table => Table(html, table));
            Section(html, "Views", schema.Views, view => View(html, view));
            Section(html, "Functions and procedures", schema.Routines, routine => Routine(html, routine));
            Section(html, "Types", schema.Types, type => Type(html, type));
            Section(html, "Sequences", schema.Sequences, sequence => Sequence(html, sequence));
        }

        html.Line("</main>");
        html.Line("<script type=\"module\">");
        html.Line($"import mermaid from \"{ErdDiagram.MermaidScript}\";");
        html.Line($"mermaid.initialize({{ startOnLoad: true, maxTextSize: 10000000 }});");
        html.Line("</script>");
        html.Line("</body>");
        html.Line("</html>");
        return html.ToString();
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static void Section<T>(StringBuilder html, string title, IReadOnlyList<T> items, Action<T> render)
    {
        if (items.Count == 0)
        {
            return;
        }

        html.Line($"<h3>{E(title)}</h3>");
        foreach (var item in items)
        {
            render(item);
        }
    }

    private static string Links(IEnumerable<DocLink> links)
        => string.Join(", ", links.Select(static link => link.Target is null
            ? E(link.Value)
            : $"<a href=\"#{E(DocAttributes.Anchor(link.Target))}\">{E(link.Value)}</a>"));

    /// <summary>A description as paragraphs: blank lines separate them, and a single line break stays a line break.</summary>
    private static string Paragraphs(string text)
        => string.Join(string.Empty, text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(static paragraph => $"<p>{E(paragraph.Trim()).Replace("\n", "<br>", StringComparison.Ordinal)}</p>"));

    private static void Heading(StringBuilder html, DocItem item, string kind)
    {
        var badges = new StringBuilder();
        if (item.Deprecated is { Flag: true })
        {
            badges.Append("<span class=\"badge deprecated\">deprecated</span>");
        }

        if (item.Pii is true)
        {
            badges.Append("<span class=\"badge pii\">PII</span>");
        }

        if (!string.IsNullOrWhiteSpace(item.Classification))
        {
            badges.Append($"<span class=\"badge\">{E(item.Classification)}</span>");
        }

        html.Line($"<h4 id=\"{E(DocAttributes.Anchor(item.QualifiedName))}\">{E(item.QualifiedName)}{badges}</h4>");
        html.Line($"<p class=\"kind\">{E(kind)}{(item.BusinessName is null ? string.Empty : $" · {E(item.BusinessName)}")}</p>");
        Describe(html, item);
    }

    /// <summary>The description, the labelled tags, the links, and the problems of an object (the part of its heading after the title).</summary>
    private static void Describe(StringBuilder html, DocItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            html.Line($"<div class=\"description\">{Paragraphs(item.Description)}</div>");
        }

        var attributes = DocAttributes.For(item)
            .Where(static attribute => attribute.Label is not "Business name" and not "Classification")
            .ToArray();
        if (attributes.Length > 0 || item.SeeAlso is { Count: > 0 } || item.InvalidTags is { Count: > 0 })
        {
            html.Line("<ul class=\"attributes\">");
            foreach (var attribute in attributes)
            {
                html.Line($"<li><strong>{E(attribute.Label)}:</strong> {string.Join("; ", attribute.Values.Select(static value => E(value).Replace("\n", "<br>", StringComparison.Ordinal)))}</li>");
            }

            if (item.SeeAlso is { Count: > 0 })
            {
                html.Line($"<li><strong>See also:</strong> {Links(item.SeeAlso)}</li>");
            }

            if (item.InvalidTags is { Count: > 0 })
            {
                foreach (var (name, value) in item.InvalidTags)
                {
                    html.Line($"<li class=\"error\"><strong>Problem with <code>@{E(name)}</code>:</strong> <code>{E(value.Replace('\n', ' '))}</code></li>");
                }
            }

            html.Line("</ul>");
        }
    }

    private static void Table(StringBuilder html, DocTable table)
    {
        Heading(html, table, table.Kind);
        if (table.PartitionedBy is not null)
        {
            html.Line($"<p>Partitioned by {E(table.PartitionedBy)}.</p>");
        }

        if (table.PartitionOf is not null)
        {
            html.Line($"<p>A partition of <code>{E(table.PartitionOf)}</code>.</p>");
        }

        html.Line("<table><thead><tr><th>#</th><th>Column</th><th>Type</th><th>Null</th><th>Default</th><th>Keys</th><th>Description</th></tr></thead><tbody>");
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

            var notes = new StringBuilder();
            void Note(string markup)
            {
                if (notes.Length > 0)
                {
                    notes.Append("<br>");
                }

                notes.Append(markup);
            }

            if (!string.IsNullOrWhiteSpace(column.Description))
            {
                Note(E(column.Description).Replace("\n", "<br>", StringComparison.Ordinal));
            }

            if (column.BusinessName is not null)
            {
                Note($"<strong>Business name:</strong> {E(column.BusinessName)}");
            }

            if (column.Deprecated is { Flag: true } deprecated)
            {
                Note($"<span class=\"badge deprecated\">deprecated</span>{(string.IsNullOrWhiteSpace(deprecated.Reason) ? string.Empty : " " + E(deprecated.Reason))}");
            }

            if (column.References is { Count: > 0 })
            {
                Note($"References {E(string.Join(", ", column.References))}.");
            }

            if (column.Generated is not null)
            {
                Note($"Generated: <code>{E(column.Generated)}</code>.");
            }

            if (column.Identity is not null)
            {
                Note($"Identity: {E(column.Identity)}.");
            }

            foreach (var attribute in DocAttributes.For(column).Where(static attribute => attribute.Label is not "Business name" and not "Deprecated"))
            {
                Note($"<strong>{E(attribute.Label)}:</strong> {string.Join("; ", attribute.Values.Select(static value => E(value).Replace("\n", " ", StringComparison.Ordinal)))}");
            }

            if (column.SeeAlso is { Count: > 0 })
            {
                Note($"<strong>See also:</strong> {Links(column.SeeAlso)}");
            }

            if (column.InvalidTags is { Count: > 0 })
            {
                foreach (var (name, value) in column.InvalidTags)
                {
                    Note($"<span class=\"error\">Problem with <code>@{E(name)}</code>: <code>{E(value.Replace('\n', ' '))}</code></span>");
                }
            }

            html.Line($"<tr id=\"{E(DocAttributes.Anchor(column.QualifiedName))}\"><td>{column.Position.ToString(CultureInfo.InvariantCulture)}</td><td><code>{E(column.Name)}</code></td><td>{E(column.Type)}</td><td>{DocAttributes.YesNo(column.Nullable)}</td><td>{(column.Default is null ? string.Empty : $"<code>{E(column.Default)}</code>")}</td><td>{E(string.Join(", ", keys))}</td><td>{notes}</td></tr>");
        }

        html.Line("</tbody></table>");

        if (table.Constraints.Count > 0)
        {
            html.Line("<p><strong>Constraints</strong></p>");
            html.Line("<table><thead><tr><th>Name</th><th>Type</th><th>Columns</th><th>Definition</th><th>Description</th></tr></thead><tbody>");
            foreach (var constraint in table.Constraints)
            {
                var actions = string.Join(" ", new[] { constraint.OnUpdate is null ? null : $"ON UPDATE {constraint.OnUpdate}", constraint.OnDelete is null ? null : $"ON DELETE {constraint.OnDelete}", constraint.Deferrable ? "DEFERRABLE" : null }.Where(static part => part is not null));
                html.Line($"<tr><td><code>{E(constraint.Name)}</code></td><td>{E(constraint.Type)}</td><td>{E(string.Join(", ", constraint.Columns))}</td><td>{(constraint.Definition is null ? string.Empty : $"<code>{E(constraint.Definition)}</code>")}{(actions.Length > 0 ? " " + E(actions) : string.Empty)}</td><td>{Notes(constraint)}</td></tr>");
            }

            html.Line("</tbody></table>");
        }

        if (table.Triggers.Count > 0)
        {
            html.Line("<p><strong>Triggers</strong></p>");
            html.Line("<table><thead><tr><th>Name</th><th>When</th><th>Level</th><th>Runs</th><th>Description</th></tr></thead><tbody>");
            foreach (var trigger in table.Triggers)
            {
                var when = $"{trigger.Timing} {string.Join(" OR ", trigger.Events)}{(trigger.When is null ? string.Empty : $" WHEN ({trigger.When})")}".Trim();
                html.Line($"<tr id=\"{E(DocAttributes.Anchor(trigger.QualifiedName))}\"><td><code>{E(trigger.Name)}</code></td><td>{E(when)}</td><td>{E(trigger.Level)}{(trigger.ConstraintTrigger ? " (constraint trigger)" : string.Empty)}</td><td><code>{E(trigger.Function)}</code></td><td>{Notes(trigger)}</td></tr>");
            }

            html.Line("</tbody></table>");
        }

        if (table.Policies.Count > 0)
        {
            html.Line($"<p><strong>Row-level security policies</strong>{(table.RowSecurity ? string.Empty : " (row-level security is not enabled on the table)")}</p>");
            html.Line("<table><thead><tr><th>Name</th><th>Applies to</th><th>Roles</th><th>Using</th><th>With check</th><th>Description</th></tr></thead><tbody>");
            foreach (var policy in table.Policies)
            {
                html.Line($"<tr id=\"{E(DocAttributes.Anchor(policy.QualifiedName))}\"><td><code>{E(policy.Name)}</code></td><td>{(policy.Permissive ? "permissive" : "restrictive")} {E(policy.Command)}</td><td>{E(string.Join(", ", policy.Roles))}</td><td>{(policy.Using is null ? string.Empty : $"<code>{E(policy.Using)}</code>")}</td><td>{(policy.WithCheck is null ? string.Empty : $"<code>{E(policy.WithCheck)}</code>")}</td><td>{Notes(policy)}</td></tr>");
            }

            html.Line("</tbody></table>");
        }

        if (table.Indexes.Count > 0)
        {
            html.Line("<p><strong>Indexes</strong></p>");
            html.Line("<table><thead><tr><th>Name</th><th>Method</th><th>Unique</th><th>Keys</th><th>Where</th><th>Description</th></tr></thead><tbody>");
            foreach (var index in table.Indexes)
            {
                html.Line($"<tr id=\"{E(DocAttributes.Anchor(index.QualifiedName))}\"><td><code>{E(index.Name)}</code></td><td>{E(index.Method)}</td><td>{DocAttributes.YesNo(index.Unique)}</td><td>{E(string.Join(", ", index.Keys))}{(index.Include is { Count: > 0 } ? E($" INCLUDE ({string.Join(", ", index.Include)})") : string.Empty)}</td><td>{(index.Predicate is null ? string.Empty : $"<code>{E(index.Predicate)}</code>")}</td><td>{Notes(index)}</td></tr>");
            }

            html.Line("</tbody></table>");
        }
    }

    /// <summary>An object's description and labelled tags as the markup of one table cell, one line each.</summary>
    private static string Notes(DocItem item)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            notes.Add(E(item.Description).Replace("\n", "<br>", StringComparison.Ordinal));
        }

        if (item.Deprecated is { Flag: true } deprecated)
        {
            notes.Add($"<span class=\"badge deprecated\">deprecated</span>{(string.IsNullOrWhiteSpace(deprecated.Reason) ? string.Empty : " " + E(deprecated.Reason))}");
        }

        notes.AddRange(DocAttributes.For(item).Where(static attribute => attribute.Label is not "Deprecated")
            .Select(static attribute => $"<strong>{E(attribute.Label)}:</strong> {string.Join("; ", attribute.Values.Select(static value => E(value).Replace("\n", " ", StringComparison.Ordinal)))}"));
        if (item.SeeAlso is { Count: > 0 })
        {
            notes.Add($"<strong>See also:</strong> {Links(item.SeeAlso)}");
        }

        if (item.InvalidTags is { Count: > 0 })
        {
            notes.AddRange(item.InvalidTags.Select(static pair => $"<span class=\"error\">Problem with <code>@{E(pair.Key)}</code>: <code>{E(pair.Value.Replace('\n', ' '))}</code></span>"));
        }

        return string.Join("<br>", notes);
    }

    private static void View(StringBuilder html, DocView view)
    {
        Heading(html, view, view.Materialized ? "materialized view" : "view");
        if (view.Columns.Count > 0)
        {
            html.Line("<table><thead><tr><th>Column</th><th>Type</th><th>Description</th></tr></thead><tbody>");
            foreach (var column in view.Columns)
            {
                html.Line($"<tr><td><code>{E(column.Name)}</code></td><td>{E(column.Type)}</td><td>{E(column.Description)}</td></tr>");
            }

            html.Line("</tbody></table>");
        }

        if (!string.IsNullOrWhiteSpace(view.Definition))
        {
            html.Line($"<pre>{E(view.Definition)}</pre>");
        }
    }

    private static void Routine(StringBuilder html, DocRoutine routine)
    {
        Heading(html, routine, routine.Kind);
        html.Line($"<p><code>{E(routine.Signature)}</code>{(routine.Returns is null ? string.Empty : $" returns <code>{E(routine.Returns)}</code>")}</p>");
        var facts = new[] { routine.Language is null ? null : $"Language: {routine.Language}", routine.Volatility is null ? null : $"Volatility: {routine.Volatility}", routine.SecurityDefiner ? "Security definer" : null }.Where(static fact => fact is not null);
        var line = string.Join(" · ", facts);
        if (line.Length > 0)
        {
            html.Line($"<p>{E(line)}</p>");
        }
    }

    private static void Type(StringBuilder html, DocType type)
    {
        Heading(html, type, type.Kind ?? "type");
        if (type.BaseType is not null)
        {
            html.Line($"<p>Base type: <code>{E(type.BaseType)}</code></p>");
        }

        if (type.Labels is { Count: > 0 })
        {
            html.Line($"<p>Labels: {string.Join(", ", type.Labels.Select(static label => $"<code>{E(label)}</code>"))}</p>");
        }

        if (type.Fields is { Count: > 0 })
        {
            html.Line("<table><thead><tr><th>Field</th><th>Type</th><th>Description</th></tr></thead><tbody>");
            foreach (var field in type.Fields)
            {
                html.Line($"<tr><td><code>{E(field.Name)}</code></td><td>{E(field.Type)}</td><td>{E(field.Description)}</td></tr>");
            }

            html.Line("</tbody></table>");
        }
    }

    private static void Sequence(StringBuilder html, DocSequence sequence)
    {
        Heading(html, sequence, "sequence");
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
            html.Line($"<p>{E(line)}</p>");
        }
    }
}
