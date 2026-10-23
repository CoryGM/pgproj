using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-286: a domain whose base type is a quoted name with a capital letter recorded it in lower case, so <c>build</c> reported <c>CREATE DOMAIN dm AS "Mytype"</c>'s base
/// type as missing, and took a domain over <c>"Name"</c> for one over the built-in <c>name</c>. The base type is now the name as PostgreSQL reads it.
/// </summary>
[TestClass]
public sealed class DomainQuotedBaseTypeTests
{
    private const string Sql = """
        CREATE DOMAIN public."Mytype" AS int;
        CREATE DOMAIN public.dm AS "Mytype";
        CREATE DOMAIN public."Name" AS character varying(50);
        CREATE DOMAIN public.short_name AS "Name" CHECK (length(VALUE) < 10);
        """;

    [TestMethod]
    public void TheBaseTypeKeepsItsCaseAndIsTheProjectsType()
    {
        var model = Build(Sql);

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("missing type", StringComparison.Ordinal)),
            string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var dm = model.Types.Values.Single(static type => type.Name == "dm");
        var shortName = model.Types.Values.Single(static type => type.Name == "short_name");
        StringAssert.EndsWith(dm.DomainBaseType, "Mytype");
        StringAssert.EndsWith(shortName.DomainBaseType, "Name");
        Assert.IsTrue(dm.ReferencedTypes.Any(static type => type.EndsWith("Mytype", StringComparison.Ordinal)), string.Join(", ", dm.ReferencedTypes));
        Assert.IsTrue(shortName.ReferencedTypes.Any(static type => type.EndsWith("Name", StringComparison.Ordinal)), string.Join(", ", shortName.ReferencedTypes));
    }

    [TestMethod]
    public void AnUnquotedNameIsStillFolded()
    {
        var model = Build("CREATE DOMAIN public.mytype AS int; CREATE DOMAIN public.dm AS MyType;");

        StringAssert.EndsWith(model.Types.Values.Single(static type => type.Name == "dm").DomainBaseType, "mytype");
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("missing type", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AQuotedNameTheProjectDoesNotDeclareIsMissing()
    {
        var model = Build("CREATE DOMAIN public.mytype AS int; CREATE DOMAIN public.dm AS \"MyType\";");

        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("MyType", StringComparison.Ordinal) && diagnostic.Message.Contains("missing type", StringComparison.Ordinal)),
            string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
