using System.Text;

namespace PgProj.Core;

/// <summary>
/// Reads the CSV file of tenants a project's <c>tenants.file</c> names (G-290). The first line is a header, and the column named <c>tenant</c> holds each
/// tenant's identifier; other columns are ignored, and blank lines are skipped. Fields follow RFC 4180: a field in double quotes may hold commas, line
/// breaks, and doubled quotes. An identifier is taken exactly as written, case included, and its schema is <c>prefix_identifier</c>.
/// Every problem is collected, each with the file and line, and reported together.
/// </summary>
public static class TenantFileReader
{
    /// <summary>The header of the column that holds the identifiers.</summary>
    public const string TenantColumn = "tenant";

    public static IReadOnlyList<TenantFileEntry> Read(string filePath, string prefix) => Read(filePath, prefix, DiskFileSource.Instance);

    /// <summary>Reads the tenant file at <paramref name="filePath"/> in <paramref name="source"/> (G-309a).</summary>
    public static IReadOnlyList<TenantFileEntry> Read(string filePath, string prefix, IProjectFileSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(source);

        string text;
        try
        {
            text = source.ReadAllText(filePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (DecoderFallbackException)
        {
            throw new PgProjectValidationException($"{filePath}: the tenant file is not UTF-8 text.");
        }

        return Parse(text, filePath, prefix);
    }

    public static IReadOnlyList<TenantFileEntry> Parse(string text, string filePath, string prefix)
    {
        ArgumentNullException.ThrowIfNull(text);

        var records = SplitRecords(text.StartsWith('﻿') ? text[1..] : text, filePath);
        var problems = new List<string>();
        var header = records.FirstOrDefault(static record => !record.IsBlank);
        if (header is null)
        {
            throw new PgProjectValidationException($"{filePath}: the tenant file is empty. Its first line is a header naming a '{TenantColumn}' column, followed by one tenant per line.");
        }

        var column = header.Fields.FindIndex(static field => string.Equals(field.Trim(), TenantColumn, StringComparison.OrdinalIgnoreCase));
        if (column < 0)
        {
            throw new PgProjectValidationException(
                $"{filePath}:{header.Line}: the header has no '{TenantColumn}' column (it has: {string.Join(", ", header.Fields.Select(static field => $"'{field}'"))}). "
                + $"The first line names the columns, and the '{TenantColumn}' column holds each tenant's identifier.");
        }

        var entries = new List<TenantFileEntry>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in records.SkipWhile(record => !ReferenceEquals(record, header)).Skip(1).Where(static record => !record.IsBlank))
        {
            var location = $"{filePath}:{record.Line}";
            if (column >= record.Fields.Count)
            {
                problems.Add($"{location}: the line has no '{TenantColumn}' value (column {column + 1}).");
                continue;
            }

            var identifier = record.Fields[column];
            if (identifier.Length == 0)
            {
                problems.Add($"{location}: the '{TenantColumn}' value is empty.");
                continue;
            }

            if (identifier.Trim().Length != identifier.Length)
            {
                problems.Add($"{location}: the tenant '{identifier}' starts or ends with a space, which would be part of its schema's name. Remove the space.");
                continue;
            }

            if (identifier.Contains('\0', StringComparison.Ordinal))
            {
                problems.Add($"{location}: the tenant identifier contains a NUL character, which a PostgreSQL name cannot.");
                continue;
            }

            var schema = prefix + "_" + identifier;
            var bytes = Encoding.UTF8.GetByteCount(schema);
            if (bytes > PgProjectTenants.MaxSchemaNameBytes)
            {
                problems.Add($"{location}: the tenant '{identifier}' makes the schema name '{schema}', {bytes} bytes long; PostgreSQL allows {PgProjectTenants.MaxSchemaNameBytes} "
                    + "and would cut it short, so two tenants could end up in one schema. Use a shorter identifier or prefix.");
                continue;
            }

            if (seen.TryGetValue(identifier, out var firstLine))
            {
                problems.Add($"{location}: the tenant '{identifier}' is listed again (first on line {firstLine}).");
                continue;
            }

            seen[identifier] = record.Line;
            entries.Add(new TenantFileEntry(identifier, record.Line));
        }

        if (problems.Count > 0)
        {
            throw new PgProjectValidationException($"The tenant file '{filePath}' has {problems.Count} problem(s):" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        return entries;
    }

    private sealed record CsvRecord(int Line, List<string> Fields)
    {
        public bool IsBlank => Fields.Count <= 1 && Fields.All(static value => value.Length == 0);
    }

    /// <summary>The file's records, each with the line it starts on. A line break inside a quoted field belongs to the field.</summary>
    private static List<CsvRecord> SplitRecords(string text, string filePath)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var quotedField = false;
        var quoteLine = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (character == '\n')
                    {
                        line++;
                    }

                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0 && !quotedField:
                    inQuotes = true;
                    quotedField = true;
                    quoteLine = line;
                    break;
                case '"':
                    throw new PgProjectValidationException($"{filePath}:{line}: a double quote inside a field that does not start with one. Put the whole field in double quotes and double the quote (\"a\"\"b\").");
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    quotedField = false;
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    records.Add(new CsvRecord(recordLine, [.. fields]));
                    fields.Clear();
                    field.Clear();
                    quotedField = false;
                    line++;
                    recordLine = line;
                    break;
                default:
                    if (quotedField)
                    {
                        throw new PgProjectValidationException($"{filePath}:{line}: text after a quoted field's closing quote. A quoted field ends at its closing quote, then a comma or the end of the line.");
                    }

                    field.Append(character);
                    break;
            }
        }

        if (inQuotes)
        {
            throw new PgProjectValidationException($"{filePath}:{quoteLine}: a quoted field is never closed.");
        }

        if (field.Length > 0 || fields.Count > 0 || quotedField)
        {
            fields.Add(field.ToString());
            records.Add(new CsvRecord(recordLine, [.. fields]));
        }

        return records;
    }
}
