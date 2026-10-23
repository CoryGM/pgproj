namespace PgProj.Parser.Tests;

/// <summary>
/// G-146: the owner a project states for an object is read, instead of being warned about and ignored (G-106, G-47): <c>ALTER TABLE|VIEW|MATERIALIZED
/// VIEW|SEQUENCE ... OWNER TO</c>, <c>ALTER SCHEMA|FUNCTION|PROCEDURE|AGGREGATE|OPERATOR|TYPE|DOMAIN|EVENT TRIGGER|PUBLICATION ... OWNER TO</c>, and
/// <c>CREATE SCHEMA ... AUTHORIZATION</c>. An owner that names no role (<c>CURRENT_USER</c>) or an object of a kind that is not modeled is still reported.
/// </summary>
[TestClass]
public sealed class OwnerParsingTests
{
    [TestMethod]
    [DataRow("ALTER TABLE app.t OWNER TO lc_owner;", "Table")]
    [DataRow("ALTER VIEW app.t OWNER TO lc_owner;", "View")]
    [DataRow("ALTER MATERIALIZED VIEW app.t OWNER TO lc_owner;", "MaterializedView")]
    [DataRow("ALTER SEQUENCE app.t OWNER TO lc_owner;", "Sequence")]
    [DataRow("ALTER TYPE app.t OWNER TO lc_owner;", "Type")]
    [DataRow("ALTER DOMAIN app.t OWNER TO lc_owner;", "Domain")]
    public void ASchemaScopedObjectsOwnerIsRead(string sql, string kind)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);
        var owner = tree.Owners.Single();

        // Assert: nothing is reported, and it is no longer an action the model ignores.
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.IsEmpty(tree.Nodes.OfType<UnsupportedAlterTableNode>().ToArray());
        Assert.AreEqual(kind, owner.ObjectKind);
        Assert.AreEqual("app", owner.SchemaName);
        Assert.AreEqual("t", owner.ObjectName);
        Assert.AreEqual("lc_owner", owner.OwnerRole);
    }

    [TestMethod]
    [DataRow("ALTER SCHEMA app OWNER TO lc_owner;", "Schema", "app")]
    [DataRow("ALTER EVENT TRIGGER audit OWNER TO lc_owner;", "EventTrigger", "audit")]
    [DataRow("ALTER PUBLICATION pub OWNER TO lc_owner;", "Publication", "pub")]
    public void AnObjectWithNoSchemaHasNoneInItsOwner(string sql, string kind, string name)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);
        var owner = tree.Owners.Single();

        // Assert: a schema, an event trigger, and a publication are not given the default schema.
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual(kind, owner.ObjectKind);
        Assert.IsNull(owner.SchemaName);
        Assert.AreEqual(name, owner.ObjectName);
        Assert.AreEqual("lc_owner", owner.OwnerRole);
    }

    [TestMethod]
    public void AnUnqualifiedNameIsInThePublicSchema()
    {
        // Act
        var owner = new PgQueryParser().Parse("ALTER TABLE t OWNER TO lc_owner; ALTER FUNCTION f() OWNER TO lc_owner;").Owners.ToArray();

        // Assert
        Assert.IsTrue(owner.All(static o => o.SchemaName == "public"), string.Join(", ", owner.Select(static o => o.SchemaName)));
    }

    [TestMethod]
    public void ARoutinesArgumentTypesAreReadAsWritten()
    {
        // Act
        var owners = new PgQueryParser().Parse("""
            ALTER FUNCTION app.f(int, text[], app.mood) OWNER TO a;
            ALTER FUNCTION app.g() OWNER TO a;
            ALTER FUNCTION app.h OWNER TO a;
            ALTER PROCEDURE app.p(bigint) OWNER TO a;
            """).Owners.ToDictionary(static owner => owner.ObjectName!);

        // Assert: pg_query spells int as pg_catalog.int4; () is an empty list; no parentheses is no list at all.
        CollectionAssert.AreEqual(new[] { "pg_catalog.int4", "text[]", "app.mood" }, owners["f"].ArgumentTypes!.ToArray());
        Assert.IsEmpty(owners["g"].ArgumentTypes!);
        Assert.IsNull(owners["h"].ArgumentTypes);
        Assert.AreEqual("Procedure", owners["p"].ObjectKind);
        CollectionAssert.AreEqual(new[] { "pg_catalog.int8" }, owners["p"].ArgumentTypes!.ToArray());
    }

    [TestMethod]
    public void AnAggregatesArgumentsAreReadIncludingStar()
    {
        // Act
        var owners = new PgQueryParser().Parse("ALTER AGGREGATE app.a(int) OWNER TO x; ALTER AGGREGATE app.b(*) OWNER TO x;").Owners.ToDictionary(static owner => owner.ObjectName!);

        // Assert
        Assert.AreEqual("Aggregate", owners["a"].ObjectKind);
        CollectionAssert.AreEqual(new[] { "pg_catalog.int4" }, owners["a"].ArgumentTypes!.ToArray());
        Assert.IsEmpty(owners["b"].ArgumentTypes!);
    }

    [TestMethod]
    public void AnOperatorsOperandsAreRead()
    {
        // Act
        var owners = new PgQueryParser().Parse("ALTER OPERATOR app.#+# (int, text) OWNER TO x; ALTER OPERATOR app.@@ (NONE, int) OWNER TO x;").Owners.ToDictionary(static owner => owner.ObjectName!);

        // Assert
        Assert.AreEqual("Operator", owners["#+#"].ObjectKind);
        Assert.AreEqual("pg_catalog.int4", owners["#+#"].LeftType);
        Assert.AreEqual("text", owners["#+#"].RightType);
        Assert.IsNull(owners["@@"].LeftType);
        Assert.AreEqual("pg_catalog.int4", owners["@@"].RightType);
    }

    [TestMethod]
    [DataRow("CREATE SCHEMA app AUTHORIZATION lc_owner;", "app")]
    [DataRow("CREATE SCHEMA AUTHORIZATION lc_owner;", "lc_owner")]
    public void ASchemaCreatedWithAnAuthorizationStatesItsOwner(string sql, string schema)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);
        var owner = tree.Owners.Single();

        // Assert: without a name, the schema is named after the role. Nothing is reported.
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("Schema", owner.ObjectKind);
        Assert.AreEqual(schema, owner.ObjectName);
        Assert.AreEqual("lc_owner", owner.OwnerRole);
    }

    [TestMethod]
    public void TheElementsOfASchemaCreatedWithAnAuthorizationAreStillRead()
    {
        // Act
        var tree = new PgQueryParser().Parse("CREATE SCHEMA app AUTHORIZATION lc_owner CREATE TABLE t (a int);");

        // Assert
        Assert.AreEqual("lc_owner", tree.Owners.Single().OwnerRole);
        Assert.AreEqual("t", tree.Tables.Single().Name);
    }

    [TestMethod]
    public void ARoleNameKeepsItsCaseWhenQuoted()
    {
        // Act
        var owner = new PgQueryParser().Parse("ALTER TABLE app.t OWNER TO \"Mixed Case\";").Owners.Single();

        // Assert
        Assert.AreEqual("Mixed Case", owner.OwnerRole);
    }

    [TestMethod]
    [DataRow("ALTER TABLE app.t OWNER TO CURRENT_USER;")]
    [DataRow("ALTER TABLE app.t OWNER TO SESSION_USER;")]
    [DataRow("ALTER VIEW app.t OWNER TO CURRENT_USER;")]
    [DataRow("ALTER SCHEMA app OWNER TO CURRENT_USER;")]
    [DataRow("ALTER FUNCTION app.f() OWNER TO CURRENT_USER;")]
    [DataRow("CREATE SCHEMA app AUTHORIZATION CURRENT_USER;")]
    public void AnOwnerThatNamesNoRoleIsReportedAndNotRead(string sql)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert: CURRENT_USER is whoever runs the script, which cannot be compared. A table's action is reported by the semantic layer from an
        // unsupported-action node (G-47); every other statement is a parser warning (G-106).
        Assert.IsEmpty(tree.Owners);
        var reported = tree.Diagnostics.Any(static d => d.Severity == SqlDiagnosticSeverity.Warning && d.Message.Contains("not modeled", StringComparison.Ordinal))
            || tree.Nodes.OfType<UnsupportedAlterTableNode>().Any(static node => node.Action == "OWNER TO");
        Assert.IsTrue(reported, string.Join("; ", tree.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    [DataRow("ALTER LANGUAGE plpgsql OWNER TO x;")]
    [DataRow("ALTER INDEX app.i OWNER TO x;")]
    [DataRow("ALTER FOREIGN DATA WRAPPER w OWNER TO x;")]
    [DataRow("ALTER SERVER s OWNER TO x;")]
    public void AnOwnerOfAKindThatIsNotModeledIsStillReported(string sql)
    {
        // Act
        var tree = new PgQueryParser().Parse(sql);

        // Assert
        Assert.IsEmpty(tree.Owners);
        Assert.AreEqual(1, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void AViewsOwnerAloneIsReadButOtherActionsOnAViewAreStillReported()
    {
        // Act
        var tree = new PgQueryParser().Parse("ALTER VIEW app.v OWNER TO x, SET (security_barrier = true);");

        // Assert: the statement does more than set the owner, so it is reported as a whole, as ALTER VIEW always was.
        Assert.IsEmpty(tree.Owners);
        Assert.AreEqual(1, tree.Diagnostics.Count(static d => d.Severity == SqlDiagnosticSeverity.Warning));
    }
}
