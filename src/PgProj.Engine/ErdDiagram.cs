using System.Text;
using System.Text.RegularExpressions;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// The entity relationship diagram of a project, in Mermaid (G-158). It is built from the semantic model of the project's SQL files (their parse trees), never from a database:
/// one entity per table with its columns, types, and primary, foreign, and unique key markers, and one relationship per foreign key. Views are not drawn. A foreign key to a
/// table the project does not define is drawn to a stub entity marked <c>(external)</c>. When the project's tables are in one schema, names are written without it.
/// </summary>
public static partial class ErdDiagram
{
    /// <summary>The CDN script a web page that draws a diagram loads Mermaid from (a pinned major version): <c>--erd-html</c>'s page and <c>document</c>'s HTML.</summary>
    internal const string MermaidScript = "https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs";

    /// <summary>The Mermaid <c>erDiagram</c> text for the tables of <paramref name="model"/>, in schema, table, and column order, ending with a newline.</summary>
    public static string Build(DatabaseModel model) => Build(model, ErdFilter.None);

    /// <summary>
    /// <see cref="Build(DatabaseModel)"/> for the tables <paramref name="filter"/> keeps. A foreign key from a table that is drawn to one that is not (in a schema the filter
    /// leaves out) is drawn to an <c>(external)</c> stub, as a table the project lacks is; a foreign key from a table that is not drawn is not drawn.
    /// </summary>
    public static string Build(DatabaseModel model, ErdFilter filter)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(filter);

        var tables = model.Tables.Values
            .Where(table => filter.Keeps(table.SchemaName))
            .OrderBy(static table => table.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static table => table.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schemas = tables.Select(static table => table.SchemaName ?? string.Empty).Distinct(PgProj.Parser.SqlIdentifiers.NameComparer).ToArray();
        var soleSchema = schemas.Length == 1 ? schemas[0] : null;

        string DisplayName(string? schema, string table, bool external = false)
        {
            var name = soleSchema is not null && string.Equals(schema ?? string.Empty, soleSchema, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(schema)
                ? table
                : $"{schema}.{table}";
            return external ? $"{name} (external)" : name;
        }

        var constraintsByTable = model.Constraints.Values
            .Where(static constraint => !string.IsNullOrWhiteSpace(constraint.TableName))
            .GroupBy(static constraint => DatabaseModel.CreateKey(constraint.SchemaName, constraint.TableName!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), PgProj.Parser.SqlIdentifiers.NameComparer);

        var builder = new StringBuilder();
        builder.AppendLine("erDiagram");

        var relationships = new List<string>();
        var externals = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            constraintsByTable.TryGetValue(table.Key, out var constraints);
            constraints ??= [];
            var primaryKey = constraints.SelectMany(static constraint => constraint.PrimaryKeyColumns).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
            var unique = constraints.Where(static constraint => string.Equals(constraint.ConstraintType, "UNIQUE", StringComparison.OrdinalIgnoreCase))
                .SelectMany(static constraint => constraint.UniqueColumns).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);
            var foreign = constraints.SelectMany(static constraint => constraint.ForeignKeyColumns).ToHashSet(PgProj.Parser.SqlIdentifiers.NameComparer);

            builder.Append("    ").Append(Entity(DisplayName(table.SchemaName, table.Name))).AppendLine(" {");
            foreach (var column in table.Columns)
            {
                var keys = new List<string>();
                if (primaryKey.Contains(column.Name))
                {
                    keys.Add("PK");
                }

                if (foreign.Contains(column.Name))
                {
                    keys.Add("FK");
                }

                if (unique.Contains(column.Name))
                {
                    keys.Add("UK");
                }

                builder.Append("        ").Append(TypeName(column.RawType ?? column.DataType)).Append(' ').Append(Word(column.Name));
                if (keys.Count > 0)
                {
                    builder.Append(' ').Append(string.Join(", ", keys));
                }

                builder.AppendLine();
            }

            builder.AppendLine("    }");

            foreach (var constraint in constraints.Where(static constraint => string.Equals(constraint.ConstraintType, "FOREIGN KEY", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(constraint.ForeignKeyReferencedTable))
                .OrderBy(static constraint => constraint.Name, StringComparer.OrdinalIgnoreCase))
            {
                var (targetSchema, targetTable) = SplitReferenced(constraint);
                var targetKey = DatabaseModel.CreateKey(targetSchema ?? constraint.SchemaName, targetTable);
                var defined = model.Tables.ContainsKey(targetKey) && filter.Keeps(model.Tables[targetKey].SchemaName);
                var parent = DisplayName(targetSchema ?? constraint.SchemaName, targetTable, external: !defined);
                if (!defined)
                {
                    externals[parent] = parent;
                }

                // A foreign key whose columns are all NOT NULL always has a parent; one with a nullable column may not.
                var optionalParent = constraint.ForeignKeyColumns.Any(name => table.Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.Ordinal))?.IsNullable != false);
                relationships.Add($"    {Entity(parent)} {(optionalParent ? "|o" : "||")}--o{{ {Entity(DisplayName(table.SchemaName, table.Name))} : {Label(constraint.Name)}");
            }
        }

        foreach (var external in externals.Values)
        {
            builder.Append("    ").AppendLine(Entity(external));
        }

        foreach (var relationship in relationships)
        {
            builder.AppendLine(relationship);
        }

        return builder.ToString();
    }

    private static (string? Schema, string Table) SplitReferenced(ConstraintDefinition constraint)
    {
        var referenced = constraint.ForeignKeyReferencedTable!.Replace("\"", string.Empty, StringComparison.Ordinal);
        var dot = referenced.LastIndexOf('.');
        return dot < 0
            ? (constraint.ForeignKeyReferencedSchemaName, referenced)
            : (referenced[..dot], referenced[(dot + 1)..]);
    }

    /// <summary>An entity name: bare when it is one Mermaid word, in double quotes otherwise (a schema-qualified name, an external stub).</summary>
    private static string Entity(string name) => SimpleWord().IsMatch(name) ? name : $"\"{name.Replace("\"", "'", StringComparison.Ordinal)}\"";

    /// <summary>An attribute name as one Mermaid word: anything that is not a letter, digit, or underscore becomes an underscore.</summary>
    private static string Word(string name) => NotWordCharacter().Replace(name, "_");

    /// <summary>A column type as one Mermaid word: no spaces, commas, or quotes (<c>numeric(10,2)</c> is <c>numeric(10_2)</c>, <c>timestamp with time zone</c> is <c>timestamp_with_time_zone</c>).</summary>
    private static string TypeName(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "unknown";
        }

        // pg_catalog is always on the path and public is where an extension's types usually are (citext), and PostgreSQL's internal names are not what people write: int8 is bigint.
        var name = type.Trim();
        foreach (var prefix in new[] { "pg_catalog.", "public." })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        foreach (var (internalName, friendly) in new[] { ("int8", "bigint"), ("int4", "integer"), ("int2", "smallint"), ("bool", "boolean"), ("float8", "double precision"), ("float4", "real") })
        {
            if (name.StartsWith(internalName, StringComparison.OrdinalIgnoreCase) && (name.Length == internalName.Length || !char.IsLetterOrDigit(name[internalName.Length])))
            {
                name = friendly + name[internalName.Length..];
                break;
            }
        }

        return NotTypeCharacter().Replace(name, "_");
    }

    /// <summary>A relationship label: always quoted, so a constraint name with anything in it is safe.</summary>
    private static string Label(string name) => $"\"{name.Replace("\"", "'", StringComparison.Ordinal)}\"";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex SimpleWord();

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotWordCharacter();

    [GeneratedRegex(@"[^A-Za-z0-9_()\[\]-]")]
    private static partial Regex NotTypeCharacter();
}
