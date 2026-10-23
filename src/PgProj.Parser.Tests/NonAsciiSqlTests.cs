namespace PgProj.Parser.Tests;

/// <summary>
/// G-125: SQL is given to libpg_query as UTF-8, and the positions it returns (bytes) are turned into string indices. It used to be
/// given in the Windows ANSI code page, so an emoji failed the run ("Cannot marshal") and an accented letter was read back as
/// U+FFFD, and every position after either was wrong.
/// </summary>
[TestClass]
public sealed class NonAsciiSqlTests
{
    private const string Emoji = "\U0001F600";

    [TestMethod]
    public void AnAccentedEnumLabelIsReadAsWritten()
    {
        // Arrange
        const string sql = "CREATE TYPE public.mood AS ENUM ('café', 'naïve', '日本語');";

        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert
        Assert.IsFalse(tree.Diagnostics.Any(static d => d.Severity == SqlDiagnosticSeverity.Error));
        var type = tree.Nodes.OfType<TypeNode>().Single();
        CollectionAssert.AreEqual(new[] { "café", "naïve", "日本語" }, type.EnumLabels.ToArray());
    }

    [TestMethod]
    public void AnEmojiInACommentOrALabelDoesNotFailTheRun()
    {
        // Arrange
        var sql = $"-- {Emoji} a comment\nCREATE TYPE public.mood AS ENUM ('ok{Emoji}');";

        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert
        Assert.IsFalse(tree.Diagnostics.Any(static d => d.Severity == SqlDiagnosticSeverity.Error), string.Join("; ", tree.Diagnostics.Select(static d => d.Message)));
        Assert.AreEqual($"ok{Emoji}", tree.Nodes.OfType<TypeNode>().Single().EnumLabels.Single());
    }

    [TestMethod]
    public void ACheckExpressionAfterNonAsciiTextIsReadWhole()
    {
        // Arrange: the CHECK is read from the text by position, after an accent, a CJK letter, and an emoji.
        var sql = $"-- é 日 {Emoji}\nCREATE TABLE public.t (\n  n text CONSTRAINT c CHECK (n <> 'é{Emoji}')\n);";

        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert
        Assert.IsFalse(tree.Diagnostics.Any(static d => d.Severity == SqlDiagnosticSeverity.Error));
        var constraint = tree.Nodes.OfType<ConstraintNode>().Single(static c => c.Name == "c");
        Assert.AreEqual($"n <> 'é{Emoji}'", constraint.RawCheckExpression?.Trim());
    }

    [TestMethod]
    public void AStatementAfterNonAsciiTextIsStillItsOwn()
    {
        // Arrange: statement offsets after the emoji must still cut each statement at its own boundaries.
        var sql = $"CREATE TYPE public.a AS ENUM ('é{Emoji}');\nCREATE TYPE public.b AS ENUM ('x');\nCREATE TYPE public.c AS ENUM ('ü');\n";

        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert
        var types = tree.Nodes.OfType<TypeNode>().ToArray();
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, types.Select(static t => t.Name).ToArray());
        Assert.IsTrue(types[0].RawStatementSql.StartsWith("CREATE TYPE public.a", StringComparison.Ordinal));
        Assert.IsTrue(types[1].RawStatementSql.StartsWith("CREATE TYPE public.b", StringComparison.Ordinal));
        Assert.IsTrue(types[2].RawStatementSql.EndsWith("('ü')", StringComparison.Ordinal), types[2].RawStatementSql);
    }

    [TestMethod]
    public void ASyntaxErrorAfterNonAsciiTextIsPlacedOnItsColumn()
    {
        // Arrange: the error is on the second `,` of line 2, after an accent and an emoji (two UTF-16 units, four UTF-8 bytes).
        var sql = $"-- é {Emoji}\nCREATE TABLE public.b (y int,, z int);\n";

        // Act
        var error = new PgQueryParser().Parse(sql).Diagnostics.Single(static d => d.Severity == SqlDiagnosticSeverity.Error);

        // Assert
        Assert.AreEqual(2, error.LineNumber);
        Assert.AreEqual(30, error.Column);
    }

    [TestMethod]
    public void AnErrorOnALineWithNonAsciiTextBeforeItIsPlacedOnItsColumn()
    {
        // Arrange
        var sql = $"CREATE TABLE public.b (\"é{Emoji}\" int,, z int);\n";

        // Act
        var error = new PgQueryParser().Parse(sql).Diagnostics.Single(static d => d.Severity == SqlDiagnosticSeverity.Error);

        // Assert
        Assert.AreEqual(1, error.LineNumber);
        // A column counts characters as an editor shows them, so the emoji is one column, not two.
        var precedingCharacters = sql[..(sql.IndexOf(",,", StringComparison.Ordinal) + 1)].EnumerateRunes().Count();
        Assert.AreEqual(precedingCharacters + 1, error.Column);
        Assert.AreEqual(33, error.Column);
    }

    [TestMethod]
    public void AnUnmodeledStatementAfterNonAsciiTextIsPlacedOnItsLineAndColumn()
    {
        // Arrange
        var sql = $"-- é {Emoji}\nCREATE TYPE public.a AS ENUM ('é');\n\n  INSERT INTO public.d VALUES ('{Emoji}');\n";

        // Act
        var warning = new PgQueryParser().Parse(sql).Diagnostics.Single();

        // Assert
        Assert.AreEqual(4, warning.LineNumber);
        Assert.AreEqual(3, warning.Column);
        StringAssert.Contains(warning.Message, "INSERT INTO public.d VALUES");
    }

    [TestMethod]
    public void AnAsciiTreeIsReturnedUnchanged()
    {
        // Arrange
        const string sql = "CREATE TABLE public.t (a int);";
        const string json = "{\"stmts\":[{\"stmt\":{},\"stmt_len\":30}]}";

        // Act / Assert
        Assert.IsNull(Utf8Offsets.ByteToCharacterMap(sql));
        Assert.AreSame(json, Utf8Offsets.ToCharacterOffsets(sql, json));
    }

    [TestMethod]
    public void ByteOffsetsAreConvertedToCharacterIndices()
    {
        // Arrange: "é" is 2 bytes, the emoji 4 bytes and 2 UTF-16 units, "日" 3 bytes.
        var sql = $"é{Emoji}日x";
        var json = "{\"stmts\":[{\"stmt\":{\"A\":{\"location\":6}},\"stmt_location\":0,\"stmt_len\":10},{\"stmt\":{\"B\":{\"location\":9}},\"stmt_location\":9}]}";

        // Act
        var converted = Utf8Offsets.ToCharacterOffsets(sql, json);

        // Assert: "日" is at byte 6 and character 3; "x" at byte 9 and character 4; the first statement (10 bytes) is 5 characters long.
        Assert.AreEqual("{\"stmts\":[{\"stmt\":{\"A\":{\"location\":3}},\"stmt_location\":0,\"stmt_len\":5},{\"stmt\":{\"B\":{\"location\":4}},\"stmt_location\":4}]}", converted);
    }
}
