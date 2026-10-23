namespace PgProj.Parser.Tests;

/// <summary>
/// G-310: reading a statement's text must not cost time quadratic in its length. A table's every column asks for its type, its default, its
/// clauses, and a rename annotation, and each used to lex or tokenize the whole statement (a 2,000-column table took 9 seconds). The text is
/// now lexed once per statement and each read finds its tokens by position. These check that the tokens found that way are the tokens a
/// fresh lex of the same text gives, at every position, and that a wide table is read correctly in a handful of passes however many columns
/// it has. They count passes (they cannot time them: the tests are deterministic).
/// </summary>
[TestClass]
public sealed class StatementTextPassTests
{
    private static readonly string[] Statements =
    [
        "CREATE TABLE t (a int DEFAULT 1, b text DEFAULT 'it''s', c numeric(10,2) CHECK (c > 0 AND c <= 1.5e3));",
        "CREATE TABLE \"My T\" (\"größe\" int, d text DEFAULT E'a\\'b' || $x$ ) $x$, -- a comment, with 'quote\n e int /* block ( */ DEFAULT 2);",
        "CREATE FUNCTION f(a int[] DEFAULT ARRAY[1,2], b int DEFAULT 3) RETURNS SETOF \"char\" LANGUAGE sql AS $$ SELECT 'é' $$;",
        "CREATE VIEW v AS SELECT a::text, b <= c, d <> e, f || g FROM t WHERE x = U&'d\\0061t' AND y IS NOT NULL;",
        "SELECT 1;",
        "",
    ];

    [TestMethod]
    public void TheTokensFromAPositionAreThoseOfALexOfTheTextFromThere()
    {
        foreach (var sql in Statements)
        {
            for (var position = 0; position <= sql.Length; position++)
            {
                var expected = (SourceText.Scan(sql[position..]) ?? []).Select(token => (token.Start + position, token.End + position)).ToList();

                CollectionAssert.AreEqual(expected, SourceText.TokensFrom(sql, position).ToList(), $"from {position} of: {sql}");
            }
        }
    }

    [TestMethod]
    public void TheTokensBeforeAPositionAreThoseOfALexOfTheTextUpToIt()
    {
        foreach (var sql in Statements)
        {
            for (var position = 0; position <= sql.Length; position++)
            {
                var before = SourceText.Scan(sql[..position]) ?? [];
                var expected = before.Reverse().Take(8).Select(token => token.Start).ToList();

                CollectionAssert.AreEqual(expected, SourceText.StartsBefore(sql, position).ToList(), $"before {position} of: {sql}");
            }
        }
    }

    [TestMethod]
    public void AWideTableIsReadInAFewPassesWhateverItsWidth()
    {
        const int columns = 300;
        var sql = WideTable(columns);
        var counter = TextPassCounter.Start();

        var tree = new PgQueryParser().Parse(sql);

        Assert.IsEmpty(tree.Diagnostics);
        var table = tree.Nodes.OfType<TableNode>().Single();
        Assert.HasCount(columns, table.Columns);
        for (var index = 0; index < columns; index++)
        {
            var column = table.Columns[index];
            Assert.AreEqual($"c{index}", column.Name);
            switch (index % 5)
            {
                case 0:
                    Assert.AreEqual(index.ToString(System.Globalization.CultureInfo.InvariantCulture), column.RawDefaultExpression);
                    Assert.AreEqual("integer", column.WrittenType);
                    break;
                case 1:
                    Assert.AreEqual($"START WITH {index}", column.IdentityOptionsText);
                    break;
                case 2:
                    Assert.AreEqual($"old{index}", column.RenamedFrom);
                    Assert.AreEqual("'x'", column.RawDefaultExpression);
                    Assert.AreEqual("varchar(20)", column.WrittenType);
                    break;
                case 3:
                    Assert.AreEqual($"c0 + {index}", column.GeneratedExpression);
                    break;
                default:
                    Assert.AreEqual("numeric(10,2)", column.WrittenType);
                    Assert.IsNull(column.RenamedFrom);
                    break;
            }
        }

        var checks = tree.Nodes.OfType<ConstraintNode>().Where(constraint => constraint.RawCheckExpression is not null).ToList();
        Assert.HasCount(columns / 5 + 10, checks);
        Assert.IsTrue(checks.Any(check => check.RawCheckExpression == "c4 > 0 AND c4 < 14"));

        // One lex of the statement, and a few for the clauses read from a position; one tokenization for the older readers. Before G-310 each of
        // these ran once per column (hundreds of times).
        Assert.IsLessThan(10, counter.Scans, $"the lexer ran {counter.Scans} times");
        Assert.IsLessThan(5, counter.Tokenizations, $"the tokenizer ran {counter.Tokenizations} times");
    }

    [TestMethod]
    public void AFunctionWithManySettingsIsReadInAFewPasses()
    {
        const int settings = 200;
        var clauses = Enumerable.Range(0, settings)
            .Select(index => (index % 4) switch
            {
                0 => $"SET custom.s{index} = 'v{index}'",
                1 => $"SET custom.s{index} TO -{index}",
                2 => $"SET custom.s{index} FROM CURRENT",
                _ => $"SET custom.s{index} = a, b",
            })
            .ToArray();
        var counter = TextPassCounter.Start();

        var tree = new PgQueryParser().Parse($"CREATE FUNCTION f() RETURNS int LANGUAGE sql {string.Join(' ', clauses)} AS $$ SELECT 1 $$;");

        Assert.IsEmpty(tree.Diagnostics);
        var read = tree.Functions.Single().Signature!.Settings;
        Assert.HasCount(settings, read);
        for (var index = 0; index < settings; index++)
        {
            var written = clauses[index];
            Assert.AreEqual(written, read.Single(setting => setting.Name == $"custom.s{index}").WrittenText);
        }

        Assert.IsLessThan(10, counter.Scans, $"the lexer ran {counter.Scans} times");
    }

    [TestMethod]
    public void ATableWithManyLikeClausesIsReadInAFewPasses()
    {
        const int clauses = 200;
        var written = Enumerable.Range(0, clauses).Select(index => (index % 2) == 0 ? $"LIKE s{index} INCLUDING ALL" : $"like  s{index}").ToArray();
        var counter = TextPassCounter.Start();

        var tree = new PgQueryParser().Parse($"CREATE TABLE t (a int, {string.Join(", ", written)}, z int);");

        Assert.IsEmpty(tree.Diagnostics);
        var read = tree.Nodes.OfType<TableNode>().Single().LikeClauses;
        Assert.HasCount(clauses, read);
        for (var index = 0; index < clauses; index++)
        {
            Assert.AreEqual(written[index].Replace("  ", " ", StringComparison.Ordinal), read[index].RawText);
        }

        Assert.IsLessThan(10, counter.Scans, $"the lexer ran {counter.Scans} times");
    }

    [TestMethod]
    public void AColumnIsFoundByItsNameAsPostgreSqlReadsIt()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (Plain int DEFAULT 1, \"Mixed\" int DEFAULT 2, \"a\"\"b\" int DEFAULT 3, \"lower\" int DEFAULT 4, \"UPPER\" int DEFAULT 5);");

        var defaults = tree.Nodes.OfType<TableNode>().Single().Columns.ToDictionary(column => column.Name!, column => column.RawDefaultExpression);

        Assert.AreEqual("1", defaults["plain"]);
        Assert.AreEqual("2", defaults["Mixed"]);
        Assert.AreEqual("3", defaults["a\"b"]);
        Assert.AreEqual("4", defaults["lower"]);
        Assert.AreEqual("5", defaults["UPPER"]);
    }

    private static string WideTable(int columns)
    {
        var builder = new System.Text.StringBuilder("CREATE TABLE public.wide (\n");
        for (var index = 0; index < columns; index++)
        {
            builder.Append("  ").Append((index % 5) switch
            {
                0 => $"c{index} integer DEFAULT {index} NOT NULL",
                1 => $"c{index} bigint GENERATED BY DEFAULT AS IDENTITY (START WITH {index})",
                2 => $"-- @renamed-from: old{index}\n  c{index} varchar(20) DEFAULT 'x'",
                3 => $"c{index} integer GENERATED ALWAYS AS (c0 + {index}) STORED",
                _ => $"c{index} numeric(10,2)",
            }).Append(",\n");
        }

        for (var index = 4; index < columns; index += 5)
        {
            builder.Append($"  CONSTRAINT chk_{index} CHECK (c{index} > 0 AND c{index} < {index + 10}),\n");
        }

        for (var index = 0; index < 10; index++)
        {
            builder.Append($"  CHECK (c0 > {index}),\n");
        }

        builder.Length -= 2;
        return builder.Append("\n);\n").ToString();
    }
}
