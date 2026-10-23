namespace PgProj.Parser.Tests;

/// <summary>
/// G-114: an object named without a schema is the one in <c>public</c>, PostgreSQL's default <c>search_path</c>, which is how a live
/// database reads it back. Every schema-scoped name the parser reads takes that default; a name that is not schema-scoped (an extension
/// installed wherever its control file says, a schema, a role) does not.
/// </summary>
[TestClass]
public sealed class DefaultSchemaParsingTests
{
    [TestMethod]
    public void EverySchemaScopedObjectTakesPublic()
    {
        var tree = new PgQueryParser().Parse("""
            CREATE TABLE parent (id int PRIMARY KEY);
            CREATE TABLE child (id int, parent_id int REFERENCES parent (id), CONSTRAINT child_id_check CHECK (id > 0));
            ALTER TABLE child ADD COLUMN note text;
            ALTER TABLE child ENABLE ROW LEVEL SECURITY;
            CREATE VIEW v AS SELECT 1 AS one;
            CREATE FUNCTION f() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE SEQUENCE s;
            CREATE TYPE mood AS ENUM ('ok');
            CREATE DOMAIN d AS int;
            CREATE INDEX child_parent_idx ON child (parent_id);
            CREATE TRIGGER trg BEFORE INSERT ON child FOR EACH ROW EXECUTE FUNCTION tf();
            CREATE POLICY pol ON child USING (true);
            COMMENT ON TABLE child IS 'c';
            """);

        foreach (var node in tree.Nodes.Where(static node => node is not GrantNode))
        {
            Assert.AreEqual("public", node.SchemaName, $"{node.GetType().Name} {node.Name}");
        }

        Assert.IsTrue(tree.Nodes.OfType<TableNode>().SelectMany(static table => table.Columns).All(static column => column.SchemaName == "public"));
        Assert.AreEqual("public", tree.Nodes.OfType<AlterTableAddColumnNode>().Single().Column.SchemaName);
        var foreignKey = tree.Nodes.OfType<ConstraintNode>().Single(static constraint => constraint.ForeignKeyReferencedTableName is not null);
        Assert.AreEqual("public", foreignKey.ForeignKeyReferencedSchemaName);
    }

    [TestMethod]
    public void AQualifiedNameIsKept()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE s.t (id int REFERENCES r.parent (id)); CREATE INDEX i ON s.t (id);");

        Assert.AreEqual("s", tree.Nodes.OfType<TableNode>().Single().SchemaName);
        Assert.AreEqual("s", tree.Nodes.OfType<IndexNode>().Single().SchemaName);
        Assert.AreEqual("r", tree.Nodes.OfType<ConstraintNode>().Single().ForeignKeyReferencedSchemaName);
    }

    [TestMethod]
    public void APartitionOfAnUnqualifiedParentIsInPublic()
    {
        var child = new PgQueryParser().Parse("CREATE TABLE m1 PARTITION OF m FOR VALUES IN (1);").Nodes.OfType<TableNode>().Single();

        Assert.AreEqual("public", child.SchemaName);
        Assert.AreEqual("public", child.PartitionOfSchemaName);
    }

    [TestMethod]
    public void AnOrdinaryTableHasNoParentSchema()
        => Assert.IsNull(new PgQueryParser().Parse("CREATE TABLE t (id int);").Nodes.OfType<TableNode>().Single().PartitionOfSchemaName);

    [TestMethod]
    public void AColumnWithNoForeignKeyHasNoReferencedSchema()
        => Assert.IsNull(new PgQueryParser().Parse("CREATE TABLE t (id int PRIMARY KEY);").Nodes.OfType<ConstraintNode>().Single().ForeignKeyReferencedSchemaName);

    [TestMethod]
    public void AnExtensionWithoutASchemaHasNone()
        => Assert.IsNull(new PgQueryParser().Parse("CREATE EXTENSION citext;").Nodes.OfType<ExtensionNode>().Single().SchemaName);

    [TestMethod]
    [DataRow("GRANT SELECT ON t TO r;")]
    [DataRow("GRANT USAGE ON SEQUENCE s TO r;")]
    [DataRow("GRANT EXECUTE ON FUNCTION f() TO r;")]
    [DataRow("GRANT EXECUTE ON PROCEDURE p() TO r;")]
    [DataRow("GRANT EXECUTE ON ROUTINE f() TO r;")]
    public void AnObjectGrantTargetTakesPublic(string sql)
        => Assert.AreEqual("public", Grant(sql).Targets.Single().SchemaName);

    [TestMethod]
    [DataRow("GRANT USAGE ON SCHEMA app TO r;")]
    [DataRow("GRANT SELECT ON ALL TABLES IN SCHEMA app TO r;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO r;")]
    public void ASchemaLevelGrantIsNotRequalified(string sql)
        => Assert.IsTrue(Grant(sql).Targets.All(static target => target.SchemaName != "public"), sql);

    [TestMethod]
    public void ARoleMembershipIsNotQualified()
        => Assert.IsTrue(Grant("GRANT admin TO alice;").Targets.All(static target => target.SchemaName is null));

    [TestMethod]
    public void AParserCanDefaultToAnotherSchema()
    {
        // A project's searchPath puts its first schema here (G-93).
        var tree = new PgQueryParser("shop").Parse("CREATE TABLE t (id int); CREATE INDEX i ON t (id);");

        Assert.IsTrue(tree.Nodes.Where(static node => node is not ColumnNode).All(static node => node.SchemaName == "shop"));
    }

    [TestMethod]
    public void ADefaultedSchemaIsMarkedAndAWrittenOneIsNot()
    {
        // The mark lets the semantic layer find a reference to an existing object along the search path (G-93).
        var tree = new PgQueryParser().Parse("CREATE TABLE s.t (id int REFERENCES parent (id)); CREATE INDEX i ON t (id); GRANT SELECT ON t, s.u TO r;");

        Assert.IsFalse(tree.Nodes.OfType<TableNode>().Single().IsSchemaDefaulted);
        Assert.IsTrue(tree.Nodes.OfType<IndexNode>().Single().IsSchemaDefaulted);
        Assert.IsTrue(tree.Nodes.OfType<ConstraintNode>().Single(static constraint => constraint.ForeignKeyReferencedTableName is not null).IsForeignKeyReferencedSchemaDefaulted);
        CollectionAssert.AreEqual(new[] { true, false }, Grant("GRANT SELECT ON t, s.u TO r;").Targets.Select(static target => target.IsSchemaDefaulted).ToArray());
    }

    private static GrantNode Grant(string sql) => new PgQueryParser().Parse(sql).Nodes.OfType<GrantNode>().Single();
}
