namespace PgProj.Parser;

/// <summary>A single <c>CHECK</c> constraint of a <c>CREATE DOMAIN</c> statement (G-77).</summary>
public sealed record DomainCheckConstraintNode(string? Name, string RawExpression, string? ExpressionAst)
{
    /// <summary>True for a check added <c>NOT VALID</c> (G-233); only <c>ALTER DOMAIN ... ADD CONSTRAINT</c> can say so, <c>CREATE DOMAIN</c> cannot.</summary>
    public bool IsNotValid { get; init; }
}

/// <summary>
/// <c>ALTER DOMAIN name ADD CONSTRAINT c CHECK (...) [NOT VALID]</c> (G-233). A transient marker, like <see cref="AlterTableAddColumnNode"/>:
/// <see cref="AlterDomainCheckApplier"/> appends the check to its domain's <see cref="DomainNode.CheckConstraints"/> before the semantic passes run.
/// </summary>
public sealed record AlterDomainCheckNode(string? DomainName, string? SchemaName, DomainCheckConstraintNode Check) : SqlNode("AlterDomainCheck", DomainName, SchemaName);

public sealed record DomainNode(string? Name, string? SchemaName, string? BaseType) : SqlNode("Domain", Name, SchemaName)
{
    /// <summary>Type modifiers of the base type exactly as declared, such as <c>10</c> and <c>2</c> for <c>numeric(10,2)</c>.</summary>
    /// <summary>The base type exactly as written (<c>VARCHAR(10)</c>), which is what is deployed (G-207); null when it could not be read.</summary>
    public string? WrittenBaseType { get; init; }

    public IReadOnlyList<string> BaseTypeModifiers { get; init; } = [];

    /// <summary>Number of array dimensions of the base type (<c>text[]</c> is 1); 0 for a scalar.</summary>
    public int BaseTypeArrayDimensions { get; init; }

    /// <summary>The field restriction of an <c>interval</c> base type (for example <c>day to second</c>), or null.</summary>
    public string? BaseTypeIntervalFields { get; init; }

    /// <summary>Raw SQL text of the domain's <c>DEFAULT</c> expression, or null when it has none.</summary>
    public string? RawDefaultExpression { get; init; }

    /// <summary>True when the domain was declared <c>NOT NULL</c>.</summary>
    public bool IsNotNull { get; init; }

    /// <summary>The domain's <c>CHECK</c> constraints, in declaration order.</summary>
    public IReadOnlyList<DomainCheckConstraintNode> CheckConstraints { get; init; } = [];

    /// <summary>The domain's <c>COLLATE</c>, its name parts joined with dots (<c>C</c>, <c>app.ci</c>), or null when it states none (G-196).</summary>
    public string? CollationName { get; init; }
}
