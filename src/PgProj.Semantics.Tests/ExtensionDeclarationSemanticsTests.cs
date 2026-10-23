using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-46: extensions are declared in the project with <c>CREATE EXTENSION</c>; a quoted name (<c>"uuid-ossp"</c>) must be
/// recognized, and the built-in <c>plpgsql</c> extension is never modeled.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExtensionDeclarationSemanticsTests
{
    [TestMethod]
    [DataRow("CREATE EXTENSION citext SCHEMA public;", "extension:citext")]
    [DataRow("CREATE EXTENSION \"uuid-ossp\" SCHEMA public;", "extension:uuid-ossp")]
    [DataRow("CREATE EXTENSION IF NOT EXISTS citext SCHEMA public;", "extension:citext")]
    [DataRow("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\" WITH SCHEMA extensions;", "extension:uuid-ossp")]
    [DataRow("create extension hstore schema app", "extension:hstore")]
    [DataRow("CREATE EXTENSION citext;", "extension:citext")]
    [DataRow("CREATE EXTENSION \"uuid-ossp\";", "extension:uuid-ossp")]
    public void DeclaredExtensionsAreModeledAsExtensionTypes(string sql, string expectedKey)
    {
        var model = BuildModel(sql);

        Assert.IsTrue(model.Types.ContainsKey(expectedKey), $"Expected '{expectedKey}' in: {string.Join(", ", model.Types.Keys)}");
        Assert.AreEqual("extension", model.Types[expectedKey].TypeKind);
    }

    [TestMethod]
    public void SeveralExtensionsInOneFileAreAllRecognized()
    {
        var model = BuildModel("""
            CREATE EXTENSION citext SCHEMA public;
            CREATE EXTENSION "uuid-ossp" SCHEMA public;
            CREATE EXTENSION pgcrypto SCHEMA public;
            """);

        CollectionAssert.AreEquivalent(new[] { "extension:citext", "extension:uuid-ossp", "extension:pgcrypto" }, model.Types.Keys.ToArray());
    }

    [TestMethod]
    [DataRow("CREATE EXTENSION plpgsql SCHEMA pg_catalog;")]
    [DataRow("CREATE EXTENSION IF NOT EXISTS plpgsql;")]
    [DataRow("CREATE EXTENSION \"plpgsql\" SCHEMA pg_catalog;")]
    [DataRow("CREATE EXTENSION PLPGSQL SCHEMA pg_catalog;")]
    public void BuiltInPlpgsqlIsNeverModeled(string sql)
    {
        var model = BuildModel(sql);

        Assert.IsEmpty(model.Types, string.Join(", ", model.Types.Keys));
        Assert.IsFalse(model.HasErrors, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void CommentOnABuiltInExtensionRaisesNoError()
    {
        var model = BuildModel("""
            CREATE EXTENSION plpgsql SCHEMA pg_catalog;
            COMMENT ON EXTENSION plpgsql IS 'PL/pgSQL procedural language';
            CREATE EXTENSION citext SCHEMA public;
            COMMENT ON EXTENSION citext IS 'data type for case-insensitive character strings';
            """);

        Assert.IsFalse(model.HasErrors, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEqual(new[] { "extension:citext" }, model.Types.Keys.ToArray());
    }

    [TestMethod]
    [DataRow("plpgsql", true)]
    [DataRow("PLPGSQL", true)]
    [DataRow("\"plpgsql\"", true)]
    [DataRow(" plpgsql ", true)]
    [DataRow("citext", false)]
    [DataRow("uuid-ossp", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void BuiltInExtensionNamesAreRecognized(string? name, bool expected)
    {
        Assert.AreEqual(expected, BuiltInExtensions.IsBuiltIn(name));
    }

    [TestMethod]
    public void AColumnOfAnExtensionTypeStillResolves()
    {
        var model = BuildModel("""
            CREATE EXTENSION citext SCHEMA public;
            CREATE TABLE public.people (id bigint PRIMARY KEY, email public.citext NOT NULL);
            """);

        Assert.IsFalse(model.HasErrors, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
