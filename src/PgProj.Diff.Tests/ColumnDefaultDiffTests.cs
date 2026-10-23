using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-56/G-55: a column DEFAULT was never captured, so the diff was blind to a changed default, and every defaulted
/// column re-diffed after publish because of derived reference lists. Both directions matter: two spellings of the same
/// default must not be reported (phantom diff), and a real change must not be missed (silent under-migration).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnDefaultDiffTests
{
    [TestMethod]
    public void SelfDiffIsEmpty()
    {
        var model = BuildModel("integer default 0, d2 text default 'x', d3 timestamptz default now()");

        var result = new DiffEngine().Execute(model, model);

        Assert.IsEmpty(result.ModifiedColumns, Describe(result));
    }

    /// <summary>The right-hand form is how PostgreSQL (pg_get_expr) or a different author would write the same default.</summary>
    [TestMethod]
    [DataRow("integer default 0", "integer   DEFAULT   0")]
    [DataRow("integer default 0", "integer default (0)")]
    [DataRow("integer default 0", "integer default ((0))")]
    [DataRow("text default 'x'", "text default 'x'::text")]
    [DataRow("varchar(20) default 'x'", "varchar(20) default 'x'::character varying")]
    [DataRow("integer default -1", "integer default '-1'::integer")]
    [DataRow("integer default -1", "integer default - 1")]
    [DataRow("bigint default 9000000000", "bigint default '9000000000'::bigint")]
    [DataRow("numeric(10,2) default 1.50", "numeric(10,2) default 1.50")]
    [DataRow("timestamptz default now()", "timestamptz default NOW ( )")]
    [DataRow("timestamptz default CURRENT_TIMESTAMP", "timestamptz default current_timestamp")]
    [DataRow("timestamptz default date_trunc('day', now())", "timestamptz default date_trunc('day'::text, now())")]
    [DataRow("text default coalesce(current_setting('a.b', true), 'n')", "text default COALESCE(current_setting('a.b'::text, true), 'n'::text)")]
    [DataRow("bigint default nextval('public.s')", "bigint default nextval('public.s'::regclass)")]
    [DataRow("text[] default '{}'", "text[] default '{}'::text[]")]
    [DataRow("date default '2020-01-01'", "date default '2020-01-01'::date")]
    [DataRow("interval default '1 day'", "interval default '1 day'::interval")]
    [DataRow("integer default 1 + 2", "integer default (1 + 2)")]
    [DataRow("text default 'a' || 'b'", "text default ('a'::text || 'b'::text)")]
    [DataRow("integer[] default ARRAY[1,2]", "integer[] default ARRAY[1, 2]")]
    [DataRow("boolean default TRUE", "boolean default true")]
    [DataRow("text default 'x' -- a note\n", "text default 'x'")]
    [DataRow("text default 'x' /* a note */", "text default 'x'")]
    [DataRow("text default null", "text")]
    [DataRow("text default null::text", "text")]
    // G-63: PostgreSQL prints a name without its schema when the schema is on the search path (public, pg_catalog).
    [DataRow("timestamp default public.utc_now()", "timestamp default utc_now()")]
    [DataRow("uuid default public.uuid_generate_v7()", "uuid default uuid_generate_v7()")]
    [DataRow("timestamptz default pg_catalog.now()", "timestamptz default now()")]
    [DataRow("timestamptz default \"public\".utc_now()", "timestamptz default utc_now()")]
    [DataRow("integer default coalesce(public.f(1), public.g(2))", "integer default coalesce(f(1), g(2))")]
    [DataRow("integer default PUBLIC.F(1)", "integer default f(1)")]
    [DataRow("text default public.f('a')::text", "text default f('a'::text)::text")]
    public void CosmeticDifferencesProduceNoColumnChange(string oldDefinition, string newDefinition)
    {
        var result = new DiffEngine().Execute(BuildModel(oldDefinition), BuildModel(newDefinition));

        Assert.IsEmpty(result.ModifiedColumns, $"{oldDefinition} vs {newDefinition}: " + Describe(result));
    }

    /// <summary>The differences the canonical form must never fold away, or publish would under-migrate silently.</summary>
    [TestMethod]
    [DataRow("integer default 0", "integer default 5")]
    [DataRow("integer default 0", "integer default 0 + 1")]
    [DataRow("integer default 1", "integer default -1")]
    [DataRow("text default 'a'", "text default 'b'")]
    [DataRow("text default 'a  b'", "text default 'a b'")]
    [DataRow("text default 'A'", "text default 'a'")]
    [DataRow("text default 'a.b'", "text default 'a_b'")]
    [DataRow("text default lower('A')", "text default upper('A')")]
    [DataRow("timestamptz default now()", "timestamptz default CURRENT_TIMESTAMP")]
    [DataRow("timestamptz default now()", "timestamptz default clock_timestamp()")]
    [DataRow("bigint default nextval('public.a')", "bigint default nextval('public.b')")]
    [DataRow("bigint default '1'::bigint", "bigint default 2")]
    [DataRow("text[] default '{}'", "text[] default '{a}'")]
    [DataRow("integer", "integer default 0")]
    [DataRow("integer default 0", "integer")]
    [DataRow("integer default 0", "integer default null")]
    [DataRow("integer default (1 + 2) * 3", "integer default 1 + 2 * 3")]
    // G-63: only public and pg_catalog are dropped; any other schema is a real difference.
    [DataRow("integer default app.f()", "integer default f()")]
    [DataRow("integer default app.f()", "integer default other.f()")]
    [DataRow("integer default public.f()", "integer default public.g()")]
    [DataRow("integer default public.f()", "integer default app.f()")]
    [DataRow("text default 'public.f'", "text default 'f'")]
    public void GenuineDefaultChangesAreReported(string oldDefinition, string newDefinition)
    {
        var result = new DiffEngine().Execute(BuildModel(oldDefinition), BuildModel(newDefinition));

        Assert.HasCount(1, result.ModifiedColumns, $"{oldDefinition} vs {newDefinition}: " + Describe(result));
        Assert.IsTrue(
            result.ModifiedColumns[0].Details.Any(static detail => detail.PropertyName == nameof(ColumnDefinition.DefaultExpressionNode)),
            $"{oldDefinition} vs {newDefinition}: " + Describe(result));
    }

    [TestMethod]
    public void OnlyTheColumnWhoseDefaultChangedIsReported()
    {
        var oldModel = Build("create table public.t (a integer default 1, b text default 'x', c boolean);");
        var newModel = Build("create table public.t (a integer default 1, b text default 'y', c boolean);");

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedColumns, Describe(result));
        Assert.AreEqual("public.t.b", result.ModifiedColumns[0].NodeId);
    }

    /// <summary>
    /// G-55: the live model does not derive a column's table/column/function references from the statement text
    /// (its statement quotes every name), so those derived lists differ for every defaulted column. They must not be
    /// what the diff reports.
    /// </summary>
    [TestMethod]
    public void DerivedReferenceListsOfADefaultedColumnAreNotCompared()
    {
        var projectModel = BuildModel("integer default 0, d2 timestamptz default now()");
        var liveLikeModel = projectModel with
        {
            Tables = projectModel.Tables.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value with
                {
                    Columns = pair.Value.Columns
                        .Select(static column => column with
                        {
                            ReferencedTables = [],
                            ReferencedColumns = [],
                            ReferencedTypes = [],
                            ReferencedFunctions = [],
                        })
                        .ToArray(),
                },
                StringComparer.OrdinalIgnoreCase),
        };

        var result = new DiffEngine().Diff(liveLikeModel, projectModel);

        Assert.AreEqual(0, result.Added.Count + result.Removed.Count + result.Modified.Count, string.Join("; ", result.Modified.Select(static change => change.NodeId)));
    }

    [TestMethod]
    public void QuotedLiveStyleStatementAndUnquotedProjectStatementDiffEmpty()
    {
        var project = Build("create table s.items (id integer primary key, qty integer not null default 0, created_at timestamptz not null default now());");
        var live = Build("CREATE TABLE \"s\".\"items\" (\n\"id\" integer NOT NULL,\n\"qty\" integer DEFAULT 0 NOT NULL,\n\"created_at\" timestamp with time zone DEFAULT now() NOT NULL\n);\nALTER TABLE \"s\".\"items\" ADD CONSTRAINT items_pkey PRIMARY KEY (\"id\");");

        var result = new DiffEngine().Execute(live, project);

        Assert.IsEmpty(result.ModifiedColumns, Describe(result));
    }

    private static string Describe(DiffResult result)
        => string.Join("; ", result.ModifiedColumns.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => $"{detail.PropertyName}: {detail.OldValue} -> {detail.NewValue}")) + "]"));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static DatabaseModel BuildModel(string columnDefinitions)
        => Build($"create table public.t (c {columnDefinitions}\n);");

    /// <summary>G-58: PostgreSQL omits <c>public.</c> from a sequence name because it is on the search path.</summary>
    [TestMethod]
    [DataRow("bigint default nextval('public.s')", "bigint default nextval('s'::regclass)")]
    [DataRow("bigint default nextval('public.s'::regclass)", "bigint default nextval('s'::regclass)")]
    [DataRow("bigint default nextval('\"public\".s')", "bigint default nextval('s')")]
    public void PublicSchemaOnASequenceNameIsNotADifference(string oldDefinition, string newDefinition)
    {
        var result = new DiffEngine().Execute(BuildModel(oldDefinition), BuildModel(newDefinition));

        Assert.IsEmpty(result.ModifiedColumns, Describe(result));
    }

    [TestMethod]
    [DataRow("bigint default nextval('app.s')", "bigint default nextval('s')")]
    [DataRow("bigint default nextval('public.a')", "bigint default nextval('public.b')")]
    [DataRow("text default 'public.s'", "text default 's'")]
    public void OtherSchemasAndNonSequenceTextAreStillDifferences(string oldDefinition, string newDefinition)
    {
        var result = new DiffEngine().Execute(BuildModel(oldDefinition), BuildModel(newDefinition));

        Assert.HasCount(1, result.ModifiedColumns, Describe(result));
    }

    /// <summary>
    /// The project side of <c>id serial</c> against the exact text a live server produces for it (an <c>int4</c> column, a
    /// schema-qualified <c>nextval</c>, and the owned sequence): a published serial column must re-diff empty (G-58).
    /// </summary>
    [TestMethod]
    [DataRow("public", "nextval('t_id_seq'::regclass)")]
    [DataRow("app", "nextval('app.t_id_seq'::regclass)")]
    public void SerialColumnAgainstItsLiveRenderingDiffsEmpty(string schema, string liveDefault)
    {
        var project = Build($"create table {schema}.t (id serial primary key, name text not null default 'x');");
        var live = Build(
            $"CREATE TABLE \"{schema}\".\"t\" (\n\"id\" integer DEFAULT {liveDefault} NOT NULL,\n\"name\" text DEFAULT 'x'::text NOT NULL\n);\n"
            + $"ALTER TABLE \"{schema}\".\"t\" ADD CONSTRAINT t_pkey PRIMARY KEY (\"id\");\n"
            + $"CREATE SEQUENCE \"{schema}\".\"t_id_seq\" AS integer INCREMENT BY 1 MINVALUE 1 MAXVALUE 2147483647 START WITH 1 CACHE 1 OWNED BY \"{schema}\".\"t\".\"id\";");

        var result = new DiffEngine().Diff(live, project);

        Assert.AreEqual(0, result.Added.Count + result.Removed.Count + result.Modified.Count, string.Join("; ", result.Modified.Select(static change => change.NodeId + " [" + string.Join(", ", change.Details.Select(static detail => $"{detail.PropertyName}: {detail.OldValue} -> {detail.NewValue}")) + "]")));
    }

    [TestMethod]
    [DataRow("integer", "int")]
    [DataRow("bigint", "int8")]
    [DataRow("smallint", "pg_catalog.int2")]
    public void IntegerTypesCompareBySameTypeNotSpelling(string left, string right)
    {
        Assert.AreEqual(ColumnTypeNormalizer.NormalizeBaseType(left), ColumnTypeNormalizer.NormalizeBaseType(right));
        Assert.AreNotEqual(ColumnTypeNormalizer.NormalizeBaseType("integer"), ColumnTypeNormalizer.NormalizeBaseType("bigint"));
    }
}
