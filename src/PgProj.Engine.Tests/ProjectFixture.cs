using PgProj.Core;

namespace PgProj.Engine.Tests;

/// <summary>
/// A small project (G-309a) that can be put on disk or held in memory under the same paths, so a test can build both and compare. It has a
/// pre-deploy script that includes another file with <c>:r</c>, a schema file with an unmodeled statement (a warning), and, optionally, a
/// file with a syntax error and a tenant CSV file.
/// </summary>
internal sealed class ProjectFixture
{
    public ProjectFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "pgproj-engine-" + Guid.NewGuid().ToString("N"));
        Files[Path.Combine(Root, "pgproj.json")] = """
            {
              "name": "fixture",
              "version": "1",
              "source": "./sql",
              "predeploy": "./scripts/pre.sql",
              "excludeGrants": true
            }
            """;
        Files[Path.Combine(Root, "sql", "Schema", "app.sql")] = "CREATE SCHEMA app;\nCREATE TABLE app.customer (id bigint PRIMARY KEY, email text NOT NULL);\nINSERT INTO app.customer VALUES (1, 'a');\n";
        Files[Path.Combine(Root, "sql", "Schema", "orders.sql")] = "CREATE TABLE app.orders (id bigint PRIMARY KEY, customer_id bigint REFERENCES app.customer (id));\n";
        Files[Path.Combine(Root, "sql", "Scripts", "seed.sql")] = "-- not schema: the Scripts folder is never read as schema\nCREATE TABLE ignored (a int);\n";
        Files[Path.Combine(Root, "scripts", "pre.sql")] = "-- before\n:r ./extra.sql\nSELECT 1;\n";
        Files[Path.Combine(Root, "scripts", "extra.sql")] = "SELECT 2;\n";
    }

    public string Root { get; }

    public string ProjectFile => Path.Combine(Root, "pgproj.json");

    /// <summary>Every file of the project: full path to text.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public ProjectFixture With(string relativePath, string text)
    {
        Files[Path.Combine(Root, relativePath)] = text;
        return this;
    }

    /// <summary>The project held in memory.</summary>
    public InMemoryFileSource InMemory(bool caseSensitive = true)
    {
        var source = new InMemoryFileSource(caseSensitive);
        foreach (var (path, text) in Files)
        {
            source.Set(path, text);
        }

        return source;
    }

    /// <summary>The project written to disk under <see cref="Root"/>; call <see cref="Delete"/> when done.</summary>
    public void WriteToDisk()
    {
        foreach (var (path, text) in Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    public void Delete()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
