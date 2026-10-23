using PgProj.Parser;

namespace PgProj.Semantics;

/// <summary>
/// The parts of a function's or procedure's signature in the form they are compared in (G-66). The signature as written stays on
/// the routine for deployment (see <see cref="RoutineSignature"/>); these are canonical, so that a project and a live database
/// that spell the same routine differently (<c>integer</c> and <c>int4</c>, <c>varchar(10)</c> and <c>character varying</c>, a
/// default written <c>1</c> or <c>'1'::integer</c>) do not differ, while a real change still does.
/// </summary>
public static class RoutineSignatureComparison
{
    /// <summary>
    /// One entry per parameter, in declared order: the order carries meaning (it is the call order), so it is kept. Each entry is
    /// the mode, the name, the canonical type, and the canonical default.
    /// </summary>
    public static IReadOnlyList<string> Parameters(RoutineSignature? signature)
        => signature is null
            ? []
            : signature.Parameters.Select(static parameter =>
            {
                var mode = string.IsNullOrWhiteSpace(parameter.Mode) ? "IN" : parameter.Mode.Trim().ToUpperInvariant();
                var type = RoutineTypeNames.Canonicalize(parameter.DataType);
                var defaultExpression = DefaultExpressionCanonicalizer.Canonicalize(parameter.DefaultExpression, parameter.DataType, 0);
                return $"{mode} {parameter.Name ?? string.Empty} {type}{(defaultExpression is null ? string.Empty : " DEFAULT " + defaultExpression)}";
            }).ToArray();

    /// <summary>The canonical result type, <c>SETOF</c> included; empty when there is none (a procedure).</summary>
    public static string Result(RoutineSignature? signature)
    {
        if (signature is null)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(signature.ReturnType))
        {
            // A function with OUT parameters and no RETURNS returns its one output's type, or record for several; a live database prints that
            // RETURNS (G-187). The same on both sides, so a procedure with OUT parameters, which has none, still compares equal to itself.
            var outputs = signature.Parameters
                .Where(static parameter => string.Equals(parameter.Mode, "OUT", StringComparison.OrdinalIgnoreCase) || string.Equals(parameter.Mode, "INOUT", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return outputs.Length switch
            {
                0 => string.Empty,
                1 => RoutineTypeNames.Canonicalize(outputs[0].DataType),
                _ => "record",
            };
        }

        return (signature.ReturnsSet ? "SETOF " : string.Empty) + RoutineTypeNames.Canonicalize(signature.ReturnType);
    }

    /// <summary>
    /// The attributes that <c>CREATE OR REPLACE</c> can change and PostgreSQL reports back: volatility (absent means
    /// <c>VOLATILE</c>, which is what PostgreSQL reports for it), <c>STRICT</c>, <c>SECURITY DEFINER</c>, and (G-197) <c>LEAKPROOF</c>,
    /// <c>PARALLEL</c>, <c>COST</c>, and <c>ROWS</c>. A value equal to PostgreSQL's default is the same as none: <c>PARALLEL UNSAFE</c>, a
    /// <c>COST</c> of 100 (1 for a C or internal routine), and a set-returning function's <c>ROWS</c> of 1000. <c>WINDOW</c> is listed too;
    /// <see cref="IsWindow"/> tells deployment that <c>CREATE OR REPLACE</c> cannot change it.
    /// </summary>
    public static string Attributes(RoutineSignature? signature)
    {
        if (signature is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder()
            .Append(string.IsNullOrWhiteSpace(signature.Volatility) ? "VOLATILE" : signature.Volatility.Trim().ToUpperInvariant());
        if (signature.IsStrict)
        {
            builder.Append(" STRICT");
        }

        if (signature.IsSecurityDefiner)
        {
            builder.Append(" SECURITY DEFINER");
        }

        if (signature.IsLeakproof)
        {
            builder.Append(" LEAKPROOF");
        }

        if (signature.Parallel is { } parallel && !string.Equals(parallel.Trim(), "unsafe", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(" PARALLEL ").Append(parallel.Trim().ToUpperInvariant());
        }

        var language = signature.Language?.Trim();
        var defaultCost = string.Equals(language, "c", StringComparison.OrdinalIgnoreCase) || string.Equals(language, "internal", StringComparison.OrdinalIgnoreCase) ? 1d : 100d;
        if (CanonicalNumber(signature.Cost) is { } cost && cost != CanonicalNumber(defaultCost))
        {
            builder.Append(" COST ").Append(cost);
        }

        if (signature.ReturnsSet && CanonicalNumber(signature.Rows) is { } rows && rows != CanonicalNumber(1000d))
        {
            builder.Append(" ROWS ").Append(rows);
        }

        if (signature.IsWindow)
        {
            builder.Append(" WINDOW");
        }

        // A C function's link symbol is part of what it runs (G-211).
        if (signature.LinkSymbol is { } linkSymbol)
        {
            builder.Append(" SYMBOL ").Append(linkSymbol);
        }

        // G-205: the support function without a public or pg_catalog schema, which pg_get_functiondef may leave off, and the transforms' types as a set.
        if (signature.SupportFunction is { } support)
        {
            builder.Append(" SUPPORT ").Append(WithoutSearchPathSchema(support.ToLowerInvariant()));
        }

        if (signature.TransformTypes.Count > 0)
        {
            builder.Append(" TRANSFORM ").Append(string.Join(",", signature.TransformTypes.Select(RoutineTypeNames.Canonicalize).Order(StringComparer.Ordinal)));
        }

        return builder.ToString();
    }

    private static string WithoutSearchPathSchema(string name)
        => name.StartsWith("pg_catalog.", StringComparison.Ordinal) ? name["pg_catalog.".Length..]
            : name.StartsWith("public.", StringComparison.Ordinal) ? name["public.".Length..]
            : name;

    /// <summary>True for a window function (G-197): <c>CREATE OR REPLACE</c> cannot make an ordinary function one, or the reverse.</summary>
    public static bool IsWindow(RoutineSignature? signature)
        => signature?.IsWindow == true;

    /// <summary>
    /// The routine's <c>SET</c> clauses as compared (G-197): one entry per setting, by name, in name order (PostgreSQL keeps one value per
    /// name, and the order they are written in changes nothing). The values are joined with <c>", "</c>, so a list written as names
    /// (<c>search_path = pg_catalog, public</c>) and as PostgreSQL prints it (<c>search_path TO 'pg_catalog', 'public'</c>) are the same. A
    /// <c>FROM CURRENT</c> setting is compared by name alone: PostgreSQL stores the value it had when the routine was created and prints
    /// that value back, so a setting that is <c>FROM CURRENT</c> in <paramref name="other"/> (the routine this one is compared with) is
    /// <c>FROM CURRENT</c> here too.
    /// </summary>
    public static string Configuration(RoutineSignature? signature, RoutineSignature? other = null)
    {
        if (signature is null || signature.Settings.Count == 0)
        {
            return string.Empty;
        }

        var byName = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in signature.Settings)
        {
            var name = setting.Name.ToLowerInvariant();
            var fromCurrent = setting.IsFromCurrent
                || other?.Settings.Any(otherSetting => otherSetting.IsFromCurrent && string.Equals(otherSetting.Name, name, StringComparison.OrdinalIgnoreCase)) == true;
            byName[name] = fromCurrent ? "FROM CURRENT" : string.Join(", ", setting.Values);
        }

        return string.Join("; ", byName.Select(static entry => entry.Value == "FROM CURRENT" ? entry.Key + " FROM CURRENT" : entry.Key + " = " + entry.Value));
    }

    /// <summary>
    /// <c>COST</c> and <c>ROWS</c> are stored as <c>real</c> and printed back with six significant digits, so a number is compared that way.
    /// </summary>
    private static string? CanonicalNumber(string? text)
        => double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? CanonicalNumber(value)
            : null;

    private static string CanonicalNumber(double value)
        => ((double)(float)value).ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
}
