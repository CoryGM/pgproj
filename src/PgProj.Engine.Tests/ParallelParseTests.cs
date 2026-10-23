using System.Text.Json;
using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-312: a project's files are parsed in parallel, and what comes out is what a parse of one file after another gives: every tree in the files' order, with
/// the same parse tree and diagnostics, and the same model. The parser's own state is per thread (its parse context, the last statement's columns, the large
/// stack's flag) and libpg_query keeps its memory per thread, so many parses at once must not see each other's.
/// </summary>
[TestClass]
public sealed class ParallelParseTests
{
    [TestMethod]
    public void ManyParsesAtOnceGiveWhatOneAfterAnotherGives()
    {
        // Arrange: the samples' files and a set of files that each exercise the per-thread state (expressions read as written, column clauses,
        // a syntax error with its range, an unmodeled statement), repeated so many parses overlap.
        var documents = SampleDocuments().Concat(SyntheticDocuments(200)).ToArray();
        var parser = new PgQueryParser();
        var sequential = ProjectModelBuilder.ParseAll(documents, document => parser.Parse(document.Content) with { FilePath = document.FilePath }, 1, CancellationToken.None);

        for (var round = 0; round < 4; round++)
        {
            // Act
            var parallel = ProjectModelBuilder.ParseAll(documents, document => parser.Parse(document.Content) with { FilePath = document.FilePath }, 16, CancellationToken.None);

            // Assert
            Assert.HasCount(sequential.Length, parallel);
            for (var i = 0; i < sequential.Length; i++)
            {
                Assert.AreEqual(sequential[i].FilePath, parallel[i].FilePath, "the trees keep the files' order");
                Assert.AreEqual(sequential[i].RawJsonAst, parallel[i].RawJsonAst, sequential[i].FilePath);
                CollectionAssert.AreEqual(sequential[i].Diagnostics.ToArray(), parallel[i].Diagnostics.ToArray(), sequential[i].FilePath);
            }

            Assert.AreEqual(ModelJson(sequential), ModelJson(parallel));
        }
    }

    [TestMethod]
    public void TheSamplesBuildTheSameModelAsBefore()
    {
        // A whole build (which parses in parallel) gives the model of the files parsed one after another.
        var projectFile = Path.Combine(RepositoryRoot(), "samples", "MultiSchema", "pgproj.json");
        var snapshot = ProjectSnapshot.Read(DiskFileSource.Instance, projectFile, null, null, CancellationToken.None);
        var built = ProjectModelBuilder.Build(snapshot, null, null, null, null, CancellationToken.None);

        var parser = snapshot.Project.HasConfiguredSearchPath ? new PgQueryParser(snapshot.Project.DefaultSchema) : new PgQueryParser();
        var oneByOne = snapshot.Workspace.Documents.Select(document => parser.Parse(document.Content) with { FilePath = document.FilePath }).ToArray();

        CollectionAssert.AreEqual(oneByOne.Select(static tree => tree.RawJsonAst).ToArray(), built.SyntaxTrees.Select(static tree => tree.RawJsonAst).ToArray());
        CollectionAssert.AreEqual(oneByOne.Select(static tree => tree.FilePath).ToArray(), built.SyntaxTrees.Select(static tree => tree.FilePath).ToArray());
    }

    [TestMethod]
    public void ADefectInOneParseIsThrownAsItself()
    {
        // A defect is an exception, as it was when the files were parsed one after another: the first file's, not an AggregateException.
        var documents = SyntheticDocuments(40).ToArray();

        var parser = new PgQueryParser();
        var exception = Assert.Throws<InvalidOperationException>(() => ProjectModelBuilder.ParseAll(
            documents,
            document => document.FilePath.EndsWith("file7.sql", StringComparison.Ordinal) || document.FilePath.EndsWith("file30.sql", StringComparison.Ordinal)
                ? throw new InvalidOperationException(document.FilePath)
                : parser.Parse(document.Content),
            8,
            CancellationToken.None));

        StringAssert.EndsWith(exception.Message, "file7.sql");
    }

    [TestMethod]
    public void ACancelledBuildStopsParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ProjectModelBuilder.ParseAll(
            SyntheticDocuments(10).ToArray(), static document => new PgQueryParser().Parse(document.Content), 4, cancellation.Token));
    }

    private static string ModelJson(IReadOnlyList<SqlSyntaxTree> trees)
    {
        var model = new SemanticBuilder().Build(trees);
        return JsonSerializer.Serialize(model) + JsonSerializer.Serialize(model.Diagnostics.Select(static d => d.ToString()));
    }

    private static IEnumerable<WorkspaceDocument> SampleDocuments()
        => Directory.GetFiles(Path.Combine(RepositoryRoot(), "samples"), "*.sql", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(static path => new WorkspaceDocument { FilePath = path, Content = File.ReadAllText(path) });

    private static IEnumerable<WorkspaceDocument> SyntheticDocuments(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var text = (i % 4) switch
            {
                0 => $"CREATE TABLE s{i}.t (id bigint PRIMARY KEY, a text DEFAULT 'x' || 'y' COLLATE \"C\", b numeric(10, 2) CHECK (b > 0) NOT NULL, c int GENERATED ALWAYS AS (id * 2) STORED);\nCREATE INDEX ON s{i}.t (lower(a)) WHERE b > {i};\n",
                1 => $"CREATE VIEW v{i} AS SELECT {i} AS n, now() AS at;\nCREATE FUNCTION f{i}(x int) RETURNS int LANGUAGE sql AS $$ SELECT x + {i} $$;\n",
                2 => $"-- a syntax error at a known place\nCREATE TABLE e{i} (a int,, b int);\n",
                _ => $"CREATE TEXT SEARCH CONFIGURATION c{i} (COPY = english);\nALTER TABLE t{i} ADD COLUMN z int DEFAULT {i};\n",
            };
            yield return new WorkspaceDocument { FilePath = Path.Combine(Path.GetTempPath(), "g312", $"file{i}.sql"), Content = text };
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pgproj.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (pgproj.slnx) was not found above " + AppContext.BaseDirectory);
    }
}
