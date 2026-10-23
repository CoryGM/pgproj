using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-65: a grant on a named object (or on columns of one table) applies to that object as it is when the grant runs, so it
/// depends on the object: it is emitted after the object is created and revoked before it is dropped. Only the object it names,
/// and for a routine only the overload it names.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ObjectGrantDependencyTests
{
    private const string Objects =
        "create schema s; create table s.t (id integer, name text); create table s.other (id integer); create view s.v as select id from s.t; "
        + "create sequence s.q; create function s.f(x integer) returns integer language sql as $$ select x $$; "
        + "create function s.f(x text) returns integer language sql as $$ select 1 $$; "
        + "create procedure s.p(x integer) language sql as $$ select 1 $$; ";

    [TestMethod]
    [DataRow("GRANT SELECT ON s.t TO r;", "s.t")]
    [DataRow("GRANT SELECT (name) ON s.t TO r;", "s.t")]
    [DataRow("GRANT SELECT ON s.v TO r;", "s.v")]
    [DataRow("GRANT USAGE ON SEQUENCE s.q TO r;", "s.q")]
    [DataRow("GRANT EXECUTE ON FUNCTION s.f(integer) TO r;", "s.f(integer)")]
    [DataRow("GRANT EXECUTE ON FUNCTION s.f(text) TO r;", "s.f(text)")]
    [DataRow("GRANT EXECUTE ON PROCEDURE s.p(integer) TO r;", "s.p(integer)")]
    public void GrantDependsOnlyOnTheObjectItNames(string grant, string expectedDependency)
    {
        var model = Build(Objects + grant);

        var dependencies = DependenciesOf(model, model.Grants.Values.Single());

        CollectionAssert.AreEqual(new[] { expectedDependency }, dependencies);
    }

    [TestMethod]
    public void GrantToSeveralGranteesOnOneTableHasTheSameDependencyEach()
    {
        var model = Build(Objects + "GRANT SELECT ON s.t TO a, b;");

        foreach (var grant in model.Grants.Values)
        {
            CollectionAssert.AreEqual(new[] { "s.t" }, DependenciesOf(model, grant));
        }
    }

    [TestMethod]
    [DataRow("GRANT USAGE ON SCHEMA s TO r;")]
    [DataRow("GRANT reader TO alice;")]
    [DataRow("ALTER DEFAULT PRIVILEGES FOR ROLE o IN SCHEMA s GRANT SELECT ON TABLES TO r;")]
    [DataRow("GRANT SELECT ON s.missing TO r;")]
    public void GrantThatNamesNoObjectOfTheModelDependsOnNoObject(string grant)
    {
        var model = Build(Objects + grant);

        Assert.IsEmpty(DependenciesOf(model, model.Grants.Values.Single()));
    }

    private static string[] DependenciesOf(DatabaseModel model, GrantDefinition grant)
        => new DependencyAnalyzer().Analyze(model)
            .GetDependencies(grant.Key)
            .Select(node => model.Functions.TryGetValue(node.NodeId, out var function) ? Overload(function) : model.Procedures.TryGetValue(node.NodeId, out var procedure) ? Overload(procedure) : node.NodeId)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string Overload(FunctionDefinition function)
        => $"{function.SchemaName}.{function.Name}({string.Join(",", function.Signature!.Parameters.Select(static parameter => ShortType(parameter.DataType)))})";

    private static string Overload(ProcedureDefinition procedure)
        => $"{procedure.SchemaName}.{procedure.Name}({string.Join(",", procedure.Signature!.Parameters.Select(static parameter => ShortType(parameter.DataType)))})";

    private static string ShortType(string? dataType)
        => ColumnTypeNormalizer.NormalizeBaseType(dataType) switch
        {
            "int4" => "integer",
            var other => other,
        };

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
