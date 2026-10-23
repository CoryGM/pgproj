using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-213: an extension is named by its name alone, and its schema, version, and comment are compared only when the project states them. An extension a
/// kept one requires (CASCADE installed it) is not removed. The old side here plays the database, which states everything.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExtensionDiffTests
{
    private const string Live = """
        CREATE EXTENSION citext SCHEMA public VERSION '1.6';
        COMMENT ON EXTENSION citext IS 'data type for case-insensitive character strings';
        """;

    [TestMethod]
    [DataRow("CREATE EXTENSION citext;")]
    [DataRow("CREATE EXTENSION IF NOT EXISTS citext;")]
    [DataRow("CREATE EXTENSION citext SCHEMA public;")]
    [DataRow("CREATE EXTENSION citext VERSION '1.6';")]
    [DataRow("CREATE EXTENSION citext CASCADE; COMMENT ON EXTENSION citext IS 'data type for case-insensitive character strings';")]
    public void WhatTheProjectDoesNotStateOrStatesAlikeIsNoChange(string project)
    {
        var changes = new DiffEngine().Diff(Build(Live), Build(project));

        Assert.IsEmpty(changes.Modified, string.Join(", ", changes.Modified.SelectMany(static change => change.Details.Select(static detail => detail.PropertyName))));
        Assert.IsEmpty(changes.Added);
        Assert.IsEmpty(changes.Removed);
    }

    [TestMethod]
    [DataRow("CREATE EXTENSION citext SCHEMA app;", nameof(TypeDefinition.SchemaName), "public", "app")]
    [DataRow("CREATE EXTENSION citext VERSION '1.8';", nameof(TypeDefinition.ExtensionVersion), "1.6", "1.8")]
    [DataRow("CREATE EXTENSION citext; COMMENT ON EXTENSION citext IS 'ours';", nameof(TypeDefinition.Comment), "data type for case-insensitive character strings", "ours")]
    [DataRow("CREATE EXTENSION citext; COMMENT ON EXTENSION citext IS NULL;", nameof(TypeDefinition.Comment), "data type for case-insensitive character strings", null)]
    public void AStatedDifferenceIsTheOnlyChange(string project, string property, string? oldValue, string? newValue)
    {
        var change = new DiffEngine().Diff(Build(Live), Build(project)).Modified.Single();

        Assert.AreEqual("extension:citext", change.NodeId);
        var detail = change.Details.Single();
        Assert.AreEqual(property, detail.PropertyName);
        Assert.AreEqual(oldValue, detail.OldValue);
        Assert.AreEqual(newValue, detail.NewValue);
    }

    /// <summary>A database compared against a project: the project now plays the old side, and what the database states is compared both ways.</summary>
    [TestMethod]
    public void TheOtherDirectionComparesWhatTheDatabaseStates()
    {
        var change = new DiffEngine().Diff(Build("CREATE EXTENSION citext SCHEMA app;"), Build(Live)).Modified.Single();

        CollectionAssert.AreEquivalent(
            new[] { nameof(TypeDefinition.SchemaName), nameof(TypeDefinition.ExtensionVersion), nameof(TypeDefinition.Comment) },
            change.Details.Select(static detail => detail.PropertyName).ToArray());
    }

    [TestMethod]
    public void AnExtensionThatAKeptOneRequiresIsNotRemoved()
    {
        // The database's earthdistance requires cube, which requires seg (a made-up chain, to cover a requirement of a requirement).
        var live = Build("CREATE EXTENSION seg; CREATE EXTENSION cube; CREATE EXTENSION earthdistance; CREATE EXTENSION hstore;");
        live.Types = WithRequires(live, ("cube", ["seg"]), ("earthdistance", ["cube"]));

        var changes = new DiffEngine().Diff(live, Build("CREATE EXTENSION earthdistance CASCADE;"));

        CollectionAssert.AreEqual(new[] { "extension:hstore" }, changes.Removed.Select(static change => change.NodeId).ToArray());
    }

    [TestMethod]
    public void ARequiredExtensionIsRemovedWithTheOneThatRequiresIt()
    {
        var live = Build("CREATE EXTENSION cube; CREATE EXTENSION earthdistance;");
        live.Types = WithRequires(live, ("earthdistance", ["cube"]));

        var changes = new DiffEngine().Diff(live, Build("CREATE TABLE public.t (a integer);"));

        CollectionAssert.AreEquivalent(new[] { "extension:cube", "extension:earthdistance" }, changes.Removed.Where(static change => change.NodeId.StartsWith("extension:", StringComparison.Ordinal)).Select(static change => change.NodeId).ToArray());
    }

    private static IReadOnlyDictionary<string, TypeDefinition> WithRequires(DatabaseModel model, params (string Name, string[] Requires)[] requirements)
    {
        var types = model.Types.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var (name, requires) in requirements)
        {
            var key = ExtensionKeys.Create(name);
            types[key] = types[key] with { ExtensionRequires = [.. requires.Select(ExtensionKeys.Create)] };
        }

        return types;
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
