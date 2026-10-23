using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-121 part 2: the constructs in a project that need a newer server than the one it is published to are found and named, so that
/// <c>publish --execute</c> can refuse before the server fails partway through the deployment.
/// </summary>
[TestClass]
public sealed class ServerFeatureRequirementsTests
{
    [TestMethod]
    public void AProjectOfPreVersion18ConstructsNeedsNoNewerServer()
    {
        // Arrange
        var model = Build("CREATE TABLE public.p (id int PRIMARY KEY); CREATE TABLE public.t (a int CHECK (a > 0), CONSTRAINT f FOREIGN KEY (a) REFERENCES public.p (id) NOT VALID);");

        // Act / Assert
        Assert.IsEmpty(ServerFeatureRequirements.Find(model));
        Assert.IsEmpty(ServerFeatureRequirements.FindNewerThan(model, 16));
    }

    [TestMethod]
    public void ANotEnforcedConstraintNeedsPostgreSql18AndIsNamed()
    {
        // Arrange
        var model = Build("CREATE TABLE public.p (id int PRIMARY KEY); CREATE TABLE public.t (a int, CONSTRAINT z CHECK (a > 0) NOT ENFORCED, CONSTRAINT f FOREIGN KEY (a) REFERENCES public.p (id) NOT ENFORCED, CONSTRAINT ok CHECK (a < 9));");

        // Act
        var features = ServerFeatureRequirements.Find(model);

        // Assert: in key order, and only the two that are not enforced.
        Assert.AreEqual(2, features.Count);
        Assert.IsTrue(features.All(static f => f.MinimumMajorVersion == 18));
        StringAssert.Contains(features[0].Description, "NOT ENFORCED constraint 'public.t.f'");
        StringAssert.Contains(features[1].Description, "NOT ENFORCED constraint 'public.t.z'");
    }

    [TestMethod]
    public void AVirtualGeneratedColumnNeedsPostgreSql18AndIsNamed()
    {
        // Arrange: a stored column, a virtual one, and one that says neither (virtual on 18).
        var model = Build("CREATE TABLE public.t (a int, s int GENERATED ALWAYS AS (a + 1) STORED, v int GENERATED ALWAYS AS (a + 2) VIRTUAL, u int GENERATED ALWAYS AS (a + 3));");

        // Act
        var features = ServerFeatureRequirements.Find(model);

        // Assert
        Assert.AreEqual(2, features.Count);
        Assert.IsTrue(features.All(static f => f.MinimumMajorVersion == 18));
        Assert.IsTrue(features.Any(static f => f.Description == "VIRTUAL generated column 'public.t.v'"));
        Assert.IsTrue(features.Any(static f => f.Description == "VIRTUAL generated column 'public.t.u'"));
    }

    [TestMethod]
    public void ATemporalKeyAndForeignKeyNeedPostgreSql18AndAreNamed()
    {
        // Arrange
        var model = Build(
            "CREATE TABLE public.room (id int, label text, during tsrange, CONSTRAINT room_pk PRIMARY KEY (id, during WITHOUT OVERLAPS), CONSTRAINT room_uq UNIQUE (label, during WITHOUT OVERLAPS)); "
            + "CREATE TABLE public.booking (room_id int, during tsrange, CONSTRAINT booking_fk FOREIGN KEY (room_id, PERIOD during) REFERENCES public.room (id, PERIOD during), CONSTRAINT plain UNIQUE (room_id));");

        // Act
        var features = ServerFeatureRequirements.Find(model).Select(static f => f.Description).ToArray();

        // Assert
        CollectionAssert.AreEqual(
            new[]
            {
                "temporal foreign key (PERIOD) 'public.booking.booking_fk'",
                "temporal key (WITHOUT OVERLAPS) 'public.room.room_pk'",
                "temporal key (WITHOUT OVERLAPS) 'public.room.room_uq'",
            },
            features,
            string.Join(" | ", features));
    }

    [TestMethod]
    public void ANotValidNotNullNeedsPostgreSql18AndIsNamed()
    {
        // Arrange: an ordinary NOT NULL (named or not) is fine everywhere; a NOT VALID one is not.
        var model = Build(
            "CREATE TABLE public.t (a int NOT NULL, b int CONSTRAINT nn NOT NULL, c int, d int); "
            + "ALTER TABLE public.t ADD CONSTRAINT nc NOT NULL c NOT VALID;");

        // Act
        var features = ServerFeatureRequirements.Find(model);

        // Assert
        Assert.AreEqual(1, features.Count);
        Assert.AreEqual(18, features[0].MinimumMajorVersion);
        Assert.AreEqual("NOT VALID NOT NULL on column 'public.t.c'", features[0].Description);
    }

    [TestMethod]
    [DataRow(16, 2)]
    [DataRow(17, 2)]
    [DataRow(18, 0)]
    [DataRow(19, 0)]
    public void OnlyWhatTheServerLacksIsReported(int serverMajorVersion, int expected)
    {
        // Arrange
        var model = Build("CREATE TABLE public.t (a int, CONSTRAINT z CHECK (a > 0) NOT ENFORCED, CONSTRAINT y CHECK (a < 9) NOT ENFORCED);");

        // Act / Assert
        Assert.AreEqual(expected, ServerFeatureRequirements.FindNewerThan(model, serverMajorVersion).Count);
    }

    [TestMethod]
    public void ATableNotNullInAPartitionOfListNeedsPostgreSql18()
    {
        // Arrange: G-266 deploys the list as written; the column form of a named NOT NULL is accepted by every supported server, the table form only by 18.
        var model = Build("""
            CREATE TABLE public.p (partid int NOT NULL, a int, b int) PARTITION BY RANGE (partid);
            CREATE TABLE public.c PARTITION OF public.p (a CONSTRAINT c_an NOT NULL, CONSTRAINT c_bn NOT NULL b) FOR VALUES FROM (1) TO (10);
            """);

        // Act
        var features = ServerFeatureRequirements.Find(model);

        // Assert
        Assert.AreEqual("table constraint NOT NULL in the PARTITION OF list of 'public.c' (column b)", features.Single().Description);
        Assert.AreEqual(18, features.Single().MinimumMajorVersion);
    }

    [TestMethod]
    [DataRow("CREATE TABLE public.t (a int, CONSTRAINT u UNIQUE NULLS NOT DISTINCT (a));", 15, "NULLS NOT DISTINCT constraint")]
    [DataRow("CREATE TABLE public.t (a int); CREATE UNIQUE INDEX i ON public.t (a) NULLS NOT DISTINCT;", 15, "NULLS NOT DISTINCT index")]
    [DataRow("CREATE TABLE public.p (a int PRIMARY KEY, b int); CREATE TABLE public.c (a int, b int, FOREIGN KEY (a, b) REFERENCES public.p ON DELETE SET NULL (b));", 15, "ON DELETE SET NULL|DEFAULT with a column list")]
    [DataRow("CREATE TABLE public.t (a int); CREATE VIEW public.v WITH (security_invoker = true) AS SELECT a FROM public.t;", 15, "security_invoker view")]
    [DataRow("CREATE UNLOGGED SEQUENCE public.s;", 15, "UNLOGGED sequence")]
    [DataRow("CREATE TABLE public.t (a int, b int); CREATE PUBLICATION p FOR TABLE public.t (a);", 15, "with a column list or a row filter")]
    [DataRow("CREATE TABLE public.t (a int); CREATE PUBLICATION p FOR TABLE public.t WHERE (a > 0);", 15, "with a column list or a row filter")]
    [DataRow("CREATE SCHEMA s; CREATE PUBLICATION p FOR TABLES IN SCHEMA s;", 15, "FOR TABLES IN SCHEMA")]
    [DataRow("CREATE TABLE public.t (a text STORAGE main);", 16, "STORAGE in the definition of column")]
    [DataRow("CREATE COLLATION public.c (provider = builtin, locale = 'C.UTF-8');", 17, "builtin collation provider")]
    public void AConstructANewerVersionAddedIsNamedWithItsVersion(string sql, int version, string description)
    {
        // Arrange (G-269: 14 and 15 are supported, so what 15, 16, and 17 added is checked too)
        var model = Build(sql);

        // Act
        var feature = ServerFeatureRequirements.Find(model).Single();

        // Assert
        Assert.AreEqual(version, feature.MinimumMajorVersion);
        StringAssert.Contains(feature.Description, description);
    }

    [TestMethod]
    public void StorageSetWithAlterTableWorksOnEveryVersion()
    {
        // Arrange (G-269)
        var model = Build("CREATE TABLE public.t (a text); ALTER TABLE public.t ALTER COLUMN a SET STORAGE main;");

        // Act / Assert
        Assert.IsEmpty(ServerFeatureRequirements.Find(model));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
