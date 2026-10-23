using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class ConstraintOperationTests
{
    [TestMethod]
    public void CreateOperationEmitsPrimaryKeyConstraint()
    {
        var constraint = CreateConstraint("orders_pkey", "PRIMARY KEY") with
        {
            PrimaryKeyColumns = ["id", "customer_id"],
        };

        var sql = GenerateSql(constraint);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"orders_pkey\"\nPRIMARY KEY (\"id\", \"customer_id\");",
            sql);
    }

    [TestMethod]
    public void CreateOperationEmitsUniqueConstraint()
    {
        var constraint = CreateConstraint("orders_number_key", "UNIQUE") with
        {
            UniqueColumns = ["order_number"],
        };

        var sql = GenerateSql(constraint);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"orders_number_key\"\nUNIQUE (\"order_number\");",
            sql);
    }

    [TestMethod]
    public void CreateOperationEmitsForeignKeyConstraintWithoutActions()
    {
        var constraint = CreateConstraint("orders_customer_id_fkey", "FOREIGN KEY") with
        {
            ForeignKeyColumns = ["customer_id"],
            ForeignKeyReferencedSchemaName = "public",
            ForeignKeyReferencedTable = "customers",
            ForeignKeyReferencedColumns = ["id"],
        };

        var sql = GenerateSql(constraint);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"orders_customer_id_fkey\"\n" +
            "FOREIGN KEY (\"customer_id\") REFERENCES \"public\".\"customers\" (\"id\");",
            sql);
    }

    [TestMethod]
    public void CreateOperationEmitsForeignKeyConstraintWithActionsAndMatchType()
    {
        var constraint = CreateConstraint("orders_customer_id_fkey", "FOREIGN KEY") with
        {
            ForeignKeyColumns = ["customer_id"],
            ForeignKeyReferencedSchemaName = "public",
            ForeignKeyReferencedTable = "customers",
            ForeignKeyReferencedColumns = ["id"],
            ForeignKeyUpdateAction = "n",
            ForeignKeyDeleteAction = "c",
            ForeignKeyMatchType = "f",
        };

        var sql = GenerateSql(constraint);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"orders_customer_id_fkey\"\n" +
            "FOREIGN KEY (\"customer_id\") REFERENCES \"public\".\"customers\" (\"id\") MATCH FULL ON UPDATE SET NULL ON DELETE CASCADE;",
            sql);
    }

    [TestMethod]
    public void CreateOperationWritesTheColumnListOfSetNullAndSetDefault()
    {
        var constraint = CreateConstraint("ch_fk", "FOREIGN KEY") with
        {
            ForeignKeyColumns = ["a", "b"],
            ForeignKeyReferencedSchemaName = "public",
            ForeignKeyReferencedTable = "par",
            ForeignKeyReferencedColumns = ["a", "b"],
            ForeignKeyMatchType = "f",
            ForeignKeyUpdateAction = "c",
            ForeignKeyDeleteAction = "n",
            ForeignKeyDeleteSetColumns = ["b"],
        };

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"ch_fk\"\n" +
            "FOREIGN KEY (\"a\", \"b\") REFERENCES \"public\".\"par\" (\"a\", \"b\") MATCH FULL ON UPDATE CASCADE ON DELETE SET NULL (\"b\");",
            GenerateSql(constraint));
        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"ch_fk\"\n" +
            "FOREIGN KEY (\"a\", \"b\") REFERENCES \"public\".\"par\" (\"a\", \"b\") MATCH FULL ON UPDATE CASCADE ON DELETE SET DEFAULT (\"a\", \"b\");",
            GenerateSql(constraint with { ForeignKeyDeleteAction = "d", ForeignKeyDeleteSetColumns = ["a", "b"] }));
    }

    [TestMethod]
    public void CreateOperationOmitsClausesForDefaultForeignKeyActionsAndMatchType()
    {
        var constraint = CreateConstraint("orders_customer_id_fkey", "FOREIGN KEY") with
        {
            ForeignKeyColumns = ["customer_id"],
            ForeignKeyReferencedSchemaName = "public",
            ForeignKeyReferencedTable = "customers",
            ForeignKeyReferencedColumns = ["id"],
            ForeignKeyUpdateAction = "a",
            ForeignKeyDeleteAction = "a",
            ForeignKeyMatchType = "s",
        };

        var sql = GenerateSql(constraint);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"orders_customer_id_fkey\"\n" +
            "FOREIGN KEY (\"customer_id\") REFERENCES \"public\".\"customers\" (\"id\");",
            sql);
    }

    [TestMethod]
    public void CreateOperationEmitsCheckConstraint()
    {
        var constraint = CreateConstraint("orders_quantity_check", "CHECK") with
        {
            RawCheckExpression = "quantity > 0",
        };

        var sql = GenerateSql(constraint);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\"\nADD CONSTRAINT \"orders_quantity_check\"\nCHECK (quantity > 0);",
            sql);
    }

    [TestMethod]
    public void CreateOperationReportsMissingDataRatherThanEmittingBrokenSql()
    {
        var constraint = CreateConstraint("orders_pkey", "PRIMARY KEY");

        var sql = GenerateSql(constraint);

        StringAssert.Contains(sql, "TODO");
        Assert.IsFalse(sql.Contains("()", StringComparison.Ordinal));
    }

    private static string GenerateSql(ConstraintDefinition constraint)
        => new CreateOperation
        {
            NodeId = constraint.Key,
            NodeKind = ChangeNodeKind.Constraint,
            Node = constraint,
        }.GenerateSql();

    private static ConstraintDefinition CreateConstraint(string name, string constraintType)
        => new(
            $"public.{name}",
            name,
            "public",
            "orders",
            constraintType,
            [],
            [],
            null,
            [],
            null,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);
}
