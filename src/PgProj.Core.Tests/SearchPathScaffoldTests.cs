using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-251: a function body or a type may name another function or type without a schema, because the session that created it had that schema on its <c>search_path</c>. A scaffold of such a
/// database needs a <c>searchPath</c> in its <c>pgproj.json</c>, or it does not build; <c>init</c> chooses the smallest set of schemas that finds every such name.
/// </summary>
[TestClass]
public sealed class SearchPathScaffoldTests
{
    private static LiveDatabaseModelLoader CreateLoader() => new(new PgQueryParser(), new SemanticBuilder());

    private static ScaffoldedFile File(string path, string content) => new(path, content);

    [TestMethod]
    public void ADatabaseWhoseNamesAreAllQualifiedOrInPublicNeedsNoSearchPath()
    {
        var path = CreateLoader().FindNeededSearchPath(
        [
            File("a.sql", "CREATE SCHEMA s;\nCREATE FUNCTION s.f(a integer) RETURNS integer LANGUAGE sql AS 'SELECT a';\nCREATE FUNCTION s.g(a integer) RETURNS integer LANGUAGE sql AS 'SELECT s.f(a)';"),
            File("b.sql", "CREATE FUNCTION public.h(a integer) RETURNS integer LANGUAGE sql AS 'SELECT a';\nCREATE FUNCTION public.i(a integer) RETURNS integer LANGUAGE sql AS 'SELECT h(a)';"),
        ]);

        Assert.IsEmpty(path);
    }

    [TestMethod]
    public void AFunctionThatCallsASiblingWithoutASchemaNeedsItsSchemaOnTheSearchPath()
    {
        var path = CreateLoader().FindNeededSearchPath(
        [
            File("a.sql", "CREATE SCHEMA s;\nCREATE FUNCTION s.f(a integer) RETURNS integer LANGUAGE sql AS 'SELECT a';\nCREATE FUNCTION s.g(a integer) RETURNS integer LANGUAGE sql AS 'SELECT f(a)';"),
        ]);

        CollectionAssert.AreEqual(new[] { "s" }, path.ToArray());
    }

    [TestMethod]
    public void TheSchemaThatFindsTheMostNamesIsChosenAndOnlyEnoughSchemasAre()
    {
        var path = CreateLoader().FindNeededSearchPath(
        [
            File("a.sql", """
                CREATE SCHEMA a;
                CREATE SCHEMA b;
                CREATE FUNCTION a.f1(x integer) RETURNS integer LANGUAGE sql AS 'SELECT x';
                CREATE FUNCTION a.f2(x integer) RETURNS integer LANGUAGE sql AS 'SELECT f1(x)';
                CREATE FUNCTION a.f3(x integer) RETURNS integer LANGUAGE sql AS 'SELECT f1(x) + f2(x)';
                CREATE FUNCTION b.g1(x integer) RETURNS integer LANGUAGE sql AS 'SELECT x';
                CREATE FUNCTION b.g2(x integer) RETURNS integer LANGUAGE sql AS 'SELECT g1(x)';
                """),
        ]);

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, path.ToArray());
        Assert.AreEqual("a", path[0]);
    }
}
