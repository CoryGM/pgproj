namespace PgProj.Semantics;

public sealed record ExpressionNode(
    string Kind,
    string Text,
    IReadOnlyList<ExpressionNode> Children)
{
    public static ExpressionNode Identifier(string text)
        => new("Identifier", text, []);

    public static ExpressionNode Literal(string text)
        => new("Literal", text, []);

    public static ExpressionNode Operator(string text, IReadOnlyList<ExpressionNode> children)
        => new("Operator", text, children);

    public static ExpressionNode FunctionCall(string text, IReadOnlyList<ExpressionNode> children)
        => new("FunctionCall", text, children);

    public static ExpressionNode Simple(string text, IReadOnlyList<ExpressionNode> children)
        => new("SimpleExpression", text, children);
}
