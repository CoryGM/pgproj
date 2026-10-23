using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-121: a live snapshot the parser cannot read stops the read with an error naming each object to blame, instead of giving an empty
/// model (the parser returns no nodes at all for text with a syntax error). The SQL here is what PostgreSQL 18 reports for a
/// <c>NOT ENFORCED</c> constraint and a temporal key, which pgproj's parser reads but reports as unsupported (`PostgreSql18Syntax`) until they are modeled (G-121 part 2).
/// </summary>
[TestClass]
public sealed class LiveSnapshotUnparseableTests
{
    private static readonly LiveTableSnapshot Other = new("s", "other", [new LiveColumnSnapshot("x", "integer", true, null, false, null, null)]);

    private static readonly LiveTableSnapshot Enforced = new("s", "t", [new LiveColumnSnapshot("a", "integer", true, null, false, null, null)]);

    [TestMethod]
    public void AnUnreadableConstraintStopsTheReadAndIsNamed()
    {
        // Arrange
        LiveConstraintSnapshot[] constraints = [new("s", "t", "a_pos", "CHECK ((a > 0)) BOGUS")];

        // Act
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Load([Other, Enforced], constraints));

        // Assert
        StringAssert.StartsWith(exception.Message, "The database could not be read: pgproj cannot parse the SQL PostgreSQL gives for this object");
        StringAssert.Contains(exception.Message, "Schema/s/Table/t.sql: syntax error at or near \"BOGUS\": ");
        StringAssert.Contains(exception.Message, "CHECK ((a > 0)) BOGUS;");
        Assert.DoesNotContain("other.sql", exception.Message, StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "Nothing was compared or written");
    }

    [TestMethod]
    public void ANotEnforcedConstraintIsReadAsNotEnforced()
    {
        // Arrange (G-121 part 2): what pg_get_constraintdef prints on PostgreSQL 18.
        var parent = new LiveTableSnapshot("s", "p", [new LiveColumnSnapshot("id", "integer", false, null, false, null, null)]);
        var child = new LiveTableSnapshot("s", "c", [new LiveColumnSnapshot("pid", "integer", true, null, false, null, null)]);
        LiveConstraintSnapshot[] constraints =
        [
            new("s", "p", "p_pkey", "PRIMARY KEY (id)"),
            new("s", "c", "c_pid_fkey", "FOREIGN KEY (pid) REFERENCES s.p(id) NOT ENFORCED"),
            new("s", "t", "a_pos", "CHECK ((a > 0)) NOT ENFORCED"),
            new("s", "t", "a_max", "CHECK ((a < 9))"),
        ];

        // Act
        var model = Load([Other, Enforced, parent, child], constraints);

        // Assert
        Assert.IsFalse(model.Constraints["s.t.a_pos"].IsEnforced);
        Assert.IsFalse(model.Constraints["s.c.c_pid_fkey"].IsEnforced);
        Assert.IsTrue(model.Constraints["s.t.a_max"].IsEnforced);
        Assert.IsTrue(model.Constraints["s.p.p_pkey"].IsEnforced);
    }

    [TestMethod]
    public void ANotEnforcedConstraintIsScaffoldedNotEnforcedInItsTablesFile()
    {
        // Arrange
        LiveConstraintSnapshot[] constraints = [new("s", "t", "a_pos", "CHECK ((a > 0)) NOT ENFORCED")];

        // Act
        var file = Scaffold([Other, Enforced], constraints).Single(static f => f.RelativePath.EndsWith("t.sql", StringComparison.Ordinal) && f.RelativePath.Contains("Table", StringComparison.Ordinal));

        // Assert
        StringAssert.Contains(file.Content, "CHECK ((a > 0)) NOT ENFORCED;");
        Assert.IsEmpty(CreateLoader().FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void ATemporalKeyAndForeignKeyAreReadAsTemporal()
    {
        // Arrange (G-121 part 2b): what pg_get_constraintdef prints on PostgreSQL 18.
        var room = new LiveTableSnapshot("s", "room", [
            new LiveColumnSnapshot("id", "integer", false, null, false, null, null),
            new LiveColumnSnapshot("during", "tstzrange", false, null, false, null, null),
        ]);
        var booking = new LiveTableSnapshot("s", "booking", [
            new LiveColumnSnapshot("room_id", "integer", true, null, false, null, null),
            new LiveColumnSnapshot("during", "tstzrange", true, null, false, null, null),
        ]);
        LiveConstraintSnapshot[] constraints =
        [
            new("s", "room", "room_pkey", "PRIMARY KEY (id, during WITHOUT OVERLAPS)"),
            new("s", "room", "room_uq", "UNIQUE (during, id WITHOUT OVERLAPS)"),
            new("s", "booking", "booking_room_fkey", "FOREIGN KEY (room_id, PERIOD during) REFERENCES s.room(id, PERIOD during)"),
        ];

        // Act
        var model = Load([Other, room, booking], constraints);

        // Assert
        var key = model.Constraints["s.room.room_pkey"];
        Assert.IsTrue(key.WithoutOverlaps);
        CollectionAssert.AreEqual(new[] { "id", "during" }, key.PrimaryKeyColumns.ToArray());
        Assert.IsTrue(model.Constraints["s.room.room_uq"].WithoutOverlaps);
        CollectionAssert.AreEqual(new[] { "during", "id" }, model.Constraints["s.room.room_uq"].UniqueColumns.ToArray());
        var foreignKey = model.Constraints["s.booking.booking_room_fkey"];
        Assert.IsTrue(foreignKey.ForeignKeyUsesPeriod);
        CollectionAssert.AreEqual(new[] { "room_id", "during" }, foreignKey.ForeignKeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "id", "during" }, foreignKey.ForeignKeyReferencedColumns.ToArray());
    }

    [TestMethod]
    public void ATemporalKeyIsScaffoldedInItsTablesFile()
    {
        // Arrange
        var room = new LiveTableSnapshot("s", "room", [
            new LiveColumnSnapshot("id", "integer", false, null, false, null, null),
            new LiveColumnSnapshot("during", "tstzrange", false, null, false, null, null),
        ]);

        // Act
        var file = Scaffold([room], [new("s", "room", "room_pkey", "PRIMARY KEY (id, during WITHOUT OVERLAPS)")])
            .Single(static f => f.RelativePath.Contains("Table", StringComparison.Ordinal) && f.RelativePath.EndsWith("room.sql", StringComparison.Ordinal));

        // Assert
        StringAssert.Contains(file.Content, "PRIMARY KEY (id, during WITHOUT OVERLAPS);");
        Assert.IsEmpty(CreateLoader().FindUnparseableFiles([file]));
    }

    [TestMethod]
    public void AVirtualGeneratedColumnIsReadAsVirtual()
    {
        // Arrange (G-122, G-121 part 2c): PostgreSQL 18's attgenerated 'v'.
        var generated = new LiveTableSnapshot("s", "g", [
            new LiveColumnSnapshot("a", "integer", true, null, false, null, null),
            new LiveColumnSnapshot("b", "integer", true, null, true, "(a * 2)", null, IsVirtual: true),
            new LiveColumnSnapshot("c", "integer", true, null, true, "(a * 3)", null),
        ]);

        // Act
        var model = Load([Other, generated], []);

        // Assert
        var columns = model.Tables["s.g"].Columns.ToDictionary(static c => c.Name);
        Assert.IsTrue(columns["b"].IsGenerated);
        Assert.IsTrue(columns["b"].IsVirtual);
        Assert.IsTrue(columns["c"].IsGenerated);
        Assert.IsFalse(columns["c"].IsVirtual);
    }

    [TestMethod]
    public void AVirtualGeneratedColumnIsScaffoldedVirtualInItsTablesFile()
    {
        // Arrange
        var generated = new LiveTableSnapshot("s", "g", [
            new LiveColumnSnapshot("a", "integer", true, null, false, null, null),
            new LiveColumnSnapshot("b", "integer", true, null, true, "(a * 2)", null, IsVirtual: true),
        ]);

        // Act
        var file = Scaffold([generated], []).Single(static f => f.RelativePath.Contains("Table", StringComparison.Ordinal) && f.RelativePath.EndsWith("g.sql", StringComparison.Ordinal));

        // Assert
        StringAssert.Contains(file.Content, "GENERATED ALWAYS AS ((a * 2)) VIRTUAL");
        Assert.IsEmpty(CreateLoader().FindUnparseableFiles([file]));
    }
    [TestMethod]
    public void AStoredGeneratedColumnIsStillRead()
    {
        // Arrange
        var generated = new LiveTableSnapshot("s", "g", [
            new LiveColumnSnapshot("a", "integer", true, null, false, null, null),
            new LiveColumnSnapshot("b", "integer", true, null, true, "(a * 2)", null),
        ]);

        // Act
        var model = Load([Other, generated], []);

        // Assert
        Assert.IsTrue(model.Tables["s.g"].Columns.Single(static c => c.Name == "b").IsGenerated);
    }

    [TestMethod]
    public void EveryUnreadableObjectIsListed()
    {
        // Arrange
        var parent = new LiveTableSnapshot("s", "p", [new LiveColumnSnapshot("id", "integer", false, null, false, null, null)]);
        var child = new LiveTableSnapshot("s", "c", [new LiveColumnSnapshot("pid", "integer", true, null, false, null, null)]);
        LiveConstraintSnapshot[] constraints =
        [
            new("s", "p", "p_pkey", "PRIMARY KEY (id)"),
            new("s", "c", "c_pid_fkey", "FOREIGN KEY (pid) REFERENCES s.p(id) BOGUS"),
            new("s", "t", "a_pos", "CHECK ((a > 0)) BOGUS"),
        ];

        // Act
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Load([Other, Enforced, parent, child], constraints));

        // Assert
        StringAssert.Contains(exception.Message, "these 2 objects");
        StringAssert.Contains(exception.Message, "Schema/s/Table/c.sql: ");
        StringAssert.Contains(exception.Message, "Schema/s/Table/t.sql: ");
        Assert.DoesNotContain("Table/p.sql", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void WithoutObjectFilesTheParsersOwnErrorIsGiven()
    {
        // Arrange
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("s")], [Enforced], [new LiveConstraintSnapshot("s", "t", "a_pos", "CHECK ((a > 0)) BOGUS")], []);

        // Act
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => CreateLoader().BuildModelFromSnapshotSql(snapshotSql));

        // Assert
        StringAssert.StartsWith(exception.Message, "The database could not be read: pgproj cannot parse the SQL PostgreSQL gives for it (syntax error at or near \"BOGUS\"). ");
    }

    [TestMethod]
    public void AReadableSnapshotStillLoads()
    {
        // Arrange
        LiveConstraintSnapshot[] constraints = [new("s", "t", "a_pos", "CHECK ((a > 0))")];

        // Act
        var model = Load([Other, Enforced], constraints);

        // Assert
        Assert.IsTrue(model.Tables.ContainsKey("s.other"));
        Assert.IsTrue(model.Tables.ContainsKey("s.t"));
        Assert.IsTrue(model.Constraints.ContainsKey("s.t.a_pos"));
    }

    [TestMethod]
    public void AScaffoldOfReadableObjectsHasNothingUnparseable()
    {
        // Arrange
        var files = Scaffold([Other, Enforced], [new("s", "t", "a_pos", "CHECK ((a > 0))")]);

        // Act
        var unparseable = CreateLoader().FindUnparseableFiles(files);

        // Assert
        Assert.IsEmpty(unparseable);
    }

    [TestMethod]
    public void ALongListIsCutAfterTenObjects()
    {
        // Arrange
        var unparseable = Enumerable.Range(1, 12)
            .Select(static number => new UnparseableFile($"Schema/s/Table/t{number:00}.sql", "syntax error at or near \"ENFORCED\"", null))
            .ToArray();

        // Act
        var exception = LiveDatabaseModelLoader.CreateUnparseableException("scaffolded", unparseable, []);

        // Assert
        StringAssert.StartsWith(exception.Message, "The database could not be scaffolded: pgproj cannot parse the SQL PostgreSQL gives for these 12 objects");
        StringAssert.Contains(exception.Message, "t10.sql");
        Assert.DoesNotContain("t11.sql", exception.Message, StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "... and 2 more");
    }

    private static DatabaseModel Load(IReadOnlyList<LiveTableSnapshot> tables, IReadOnlyList<LiveConstraintSnapshot> constraints)
    {
        LiveSchemaSnapshot[] schemas = [new("s")];
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(schemas, tables, constraints, []);
        return CreateLoader().BuildModelFromSnapshotSql(snapshotSql, objectFiles: () => Scaffold(tables, constraints));
    }

    private static IReadOnlyList<ScaffoldedFile> Scaffold(IReadOnlyList<LiveTableSnapshot> tables, IReadOnlyList<LiveConstraintSnapshot> constraints)
        => LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("s")], tables, constraints, [], [], [], [], [], [], [], []);

    private static LiveDatabaseModelLoader CreateLoader() => new(new PgQueryParser(), new SemanticBuilder());
}
