using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-132: foreign data wrappers, servers, and user mappings declared, with their comments, grants, and owners; a literal password warned about; options compared by
/// name, a variable's value or a password only by whether the option is there.
/// </summary>
[TestClass]
public sealed class ForeignDataSemanticsTests
{
    private const string Objects = """
        CREATE FOREIGN DATA WRAPPER w;
        CREATE SERVER s FOREIGN DATA WRAPPER w OPTIONS (host :'db_host');
        COMMENT ON SERVER s IS 'x';
        CREATE USER MAPPING FOR app SERVER s OPTIONS (user 'u', password :'pw');

        """;

    [TestMethod]
    public void EachKindIsDeclaredUnderItsKey()
    {
        var model = Build(Objects + "GRANT USAGE ON FOREIGN SERVER s TO app; GRANT ALL ON FOREIGN DATA WRAPPER w TO app; ALTER SERVER s OWNER TO app;");

        Assert.IsTrue(model.ForeignDataWrappers.ContainsKey("foreign data wrapper:w"));
        Assert.AreEqual("x", model.ForeignServers["server:s"].Comment);
        Assert.AreEqual("app", model.UserMappings["user mapping:app on s"].Role);
        CollectionAssert.AreEquivalent(new[] { "server:s", "foreign data wrapper:w" }, model.Grants.Values.Select(static grant => grant.ObjectKey).ToArray());
        Assert.AreEqual("server", model.Owners.Values.Single().ObjectKind);
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity is SemanticDiagnosticSeverity.Error or SemanticDiagnosticSeverity.Warning), string.Join(" | ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ALiteralPasswordIsAWarningThatDoesNotQuoteIt()
    {
        var model = Build("CREATE FOREIGN DATA WRAPPER w; CREATE SERVER s FOREIGN DATA WRAPPER w; CREATE USER MAPPING FOR app SERVER s OPTIONS (password 'hunter2');");

        var warning = model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning);
        StringAssert.Contains(warning.Message, "writes its password in the project");
        Assert.DoesNotContain("hunter2", warning.Message);
    }

    [TestMethod]
    public void OptionsAreComparedByNameAndASecretOnlyByPresence()
    {
        static IReadOnlyList<ForeignOptionDefinition> Options(params (string Name, string? Value, string? Variable)[] options)
            => options.Select(static option => new ForeignOptionDefinition(option.Name, option.Value, option.Variable)).ToArray();

        Assert.IsEmpty(ForeignOptions.Differences(Options(("a", "1", null), ("b", "2", null)), Options(("b", "2", null), ("a", "1", null))), "Order means nothing.");
        Assert.IsEmpty(ForeignOptions.Differences(Options(("host", "db", null)), Options(("host", null, "db_host"))), "A variable's value is not compared.");
        Assert.IsEmpty(ForeignOptions.Differences(Options(("password", "*****", null)), Options(("password", "other", null))), "A password is not compared.");
        CollectionAssert.AreEqual(new[] { ("a", "SET"), ("b", "DROP"), ("c", "ADD") }, ForeignOptions.Differences(Options(("a", "1", null), ("b", "2", null)), Options(("a", "9", null), ("c", "3", null))).ToArray());
        CollectionAssert.AreEqual(new[] { "host=:'db_host'", "password=*****" }, ForeignOptions.Describe(Options(("password", "x", null), ("host", null, "db_host"))).ToArray(), "Never shown.");
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
