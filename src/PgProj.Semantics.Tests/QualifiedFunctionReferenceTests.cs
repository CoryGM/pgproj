using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-54: a schema-qualified call to a project function (<c>DEFAULT public.bump(1)</c>) was reported as a missing function
/// while the unqualified <c>bump(1)</c> resolved. A routine's key is overload-qualified (<c>public.bump(integer)</c>), and the
/// name-based fallbacks compared the bare declared name with the whole, qualified reference.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class QualifiedFunctionReferenceTests
{
    private const string Bump = "create function public.bump(x integer) returns integer language sql as $$ select x + 1 $$; ";

    [TestMethod]
    [DataRow("create table public.t (n integer default public.bump(1));")]
    [DataRow("create table public.t (n integer default bump(1));")]
    [DataRow("create table public.t (n integer default PUBLIC.Bump(1));")]
    [DataRow("create table other.t (n integer default public.bump(1));")]
    [DataRow("create table public.t (n integer, constraint n_ok check (public.bump(n) > 0));")]
    [DataRow("create table public.t (n integer, constraint n_ok check (bump(n) > 0));")]
    [DataRow("create table public.t (n integer); create index t_n on public.t (public.bump(n));")]
    [DataRow("create table public.t (n integer); create view public.v as select public.bump(n) as m from public.t;")]
    [DataRow("create table public.t (n integer); create view public.v as select bump(n) as m from public.t;")]
    [DataRow("create table public.t (n integer); create view public.v as select n from public.t where public.bump(n) > 1;")]
    public void CallToAProjectFunctionIsNotMissing(string sql)
    {
        var model = Build(Bump + sql);

        var missing = model.Diagnostics.Where(static diagnostic => diagnostic.Message.Contains("missing function", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.IsEmpty(missing, sql + ": " + string.Join("; ", missing.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void CallToAnOverloadedProjectFunctionIsNotMissing()
    {
        var model = Build(
            "create function public.f(a integer) returns integer language sql as $$ select a $$; "
            + "create function public.f(a text) returns integer language sql as $$ select 1 $$; "
            + "create table public.t (n integer default public.f(1));");

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("missing function", StringComparison.OrdinalIgnoreCase)),
            string.Join("; ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    [DataRow("create table public.t (n integer default public.nosuch(1));", "public.nosuch")]
    [DataRow("create table public.t (n integer default other.bump(1));", "other.bump")]
    [DataRow("create table public.t (n integer default nosuch(1));", "nosuch")]
    public void CallToAFunctionThatDoesNotExistIsStillMissing(string sql, string name)
    {
        var model = Build(Bump + sql);

        Assert.IsTrue(
            model.Diagnostics.Any(diagnostic => diagnostic.Message.Contains($"missing function '{name}'", StringComparison.OrdinalIgnoreCase)),
            string.Join("; ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    /// <summary>A qualified reference must match that schema: another schema's function of the same name is not it.</summary>
    [TestMethod]
    public void QualifiedReferenceResolvesToTheNamedSchemaNotAnotherWithTheSameName()
    {
        var model = Build(
            "create schema a; create schema b; "
            + "create function a.bump(x integer) returns integer language sql as $$ select x $$; "
            + "create table public.t (n integer default b.bump(1));");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("missing function 'b.bump'", StringComparison.OrdinalIgnoreCase)),
            string.Join("; ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    /// <summary>An unqualified name prefers the owner's own schema when several schemas define it.</summary>
    [TestMethod]
    public void UnqualifiedReferenceIsResolvedFromTheOwnersSchemaFirst()
    {
        var model = Build(
            "create schema a; create schema b; "
            + "create function a.bump(x integer) returns integer language sql as $$ select x $$; "
            + "create function b.bump(x integer) returns integer language sql as $$ select x $$; "
            + "create table b.t (n integer default bump(1));");

        var resolved = FunctionReferences.Resolve(model, "b", "bump");

        Assert.IsNotNull(resolved);
        Assert.AreEqual("b", resolved.SchemaName);
    }

    [TestMethod]
    public void ResolverHandlesExactKeysQualifiedNamesAndQuotes()
    {
        var model = Build(Bump);

        // The exact, overload-qualified key (whatever spelling the model gives the argument type) resolves without any fallback.
        Assert.IsNotNull(FunctionReferences.Resolve(model, null, model.Functions.Keys.Single()));
        Assert.IsNotNull(FunctionReferences.Resolve(model, null, "public.bump"));
        // A reference in the model is already folded (G-206): PUBLIC.BUMP can only be the quoted "PUBLIC"."BUMP", another function.
        Assert.IsNull(FunctionReferences.Resolve(model, null, "PUBLIC.BUMP"));
        Assert.IsNotNull(FunctionReferences.Resolve(model, null, "\"public\".\"bump\""));
        Assert.IsNotNull(FunctionReferences.Resolve(model, "public", "bump"));
        Assert.IsNull(FunctionReferences.Resolve(model, null, "other.bump"));
        Assert.IsNull(FunctionReferences.Resolve(model, null, "public.nosuch"));
        Assert.IsNull(FunctionReferences.Resolve(model, null, ""));
        Assert.IsNull(FunctionReferences.Resolve(model, null, null));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
