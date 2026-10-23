using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-137: a routine's parameter or result type written <c>public.x</c> is the same type as <c>x</c> (a live read is pinned to the <c>public</c> search
/// path and writes <c>x</c>), so neither spelling is a change, in either direction; a genuinely different type still is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutinePublicQualifiedTypeDiffTests
{
    private const string Type = "CREATE TYPE public.mood AS ENUM ('a', 'b'); ";

    [TestMethod]
    [DataRow("RETURNS public.mood", "RETURNS mood")]
    [DataRow("RETURNS SETOF public.mood", "RETURNS SETOF mood")]
    [DataRow("RETURNS public.mood[]", "RETURNS mood[]")]
    public void AResultTypeWithAndWithoutPublicIsNotAChange(string first, string second)
    {
        Assert.IsTrue(IsEmpty(Diff(Function(first, "text"), Function(second, "text"))));
        Assert.IsTrue(IsEmpty(Diff(Function(second, "text"), Function(first, "text"))));
    }

    [TestMethod]
    public void AParameterTypeWithAndWithoutPublicIsNotAChange()
    {
        Assert.IsTrue(IsEmpty(Diff(Function("RETURNS integer", "public.mood"), Function("RETURNS integer", "mood"))));
        Assert.IsTrue(IsEmpty(Diff(Function("RETURNS integer", "mood"), Function("RETURNS integer", "public.mood"))));
    }

    [TestMethod]
    public void ADifferentResultTypeIsStillAChange()
    {
        Assert.IsFalse(IsEmpty(Diff(Function("RETURNS public.mood", "text"), Function("RETURNS text", "text"))));
    }

    private static string Function(string returns, string parameter)
        => Type + $"CREATE FUNCTION public.f({parameter}) {returns} LANGUAGE sql AS $$ SELECT NULL $$;";

    private static ChangeSet Diff(string oldSql, string newSql) => new DiffEngine().Diff(Build(oldSql), Build(newSql));

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
