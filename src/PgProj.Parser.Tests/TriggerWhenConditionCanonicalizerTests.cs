using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-40: the project's <c>WHEN</c> condition is what the author wrote and the live one is what <c>pg_get_triggerdef</c> deparses, so
/// they must compare equal whenever PostgreSQL treats them as the same condition, and unequal whenever they are not. The "rendered"
/// spellings below were captured from PostgreSQL 16 (each is what the server printed for the "written" one), so the equivalences are
/// what the server does and not what was assumed. Both directions are tested (<c>copilot-diff.md</c>): a fold that equated two
/// different conditions would hide a real change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TriggerWhenConditionCanonicalizerTests
{
    private static readonly Dictionary<string, string> ColumnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = "int4",
        ["b"] = "int4",
        ["n"] = "text",
        ["v"] = "varchar",
        ["c"] = "citext",
    };

    [TestMethod]
    [DataRow("old.a is distinct from new.a", "(old.a IS DISTINCT FROM new.a)")]
    [DataRow("new.a > 0", "(new.a > 0)")]
    [DataRow("(new.a > 0)", "(new.a > 0)")]
    [DataRow("new.a != 0", "(new.a <> 0)")]
    [DataRow("NEW.A>0 AND new.b<10", "((new.a > 0) AND (new.b < 10))")]
    [DataRow("new.a > 0 or new.b is null", "((new.a > 0) OR (new.b IS NULL))")]
    [DataRow("not (new.a = 1)", "(NOT (new.a = 1))")]
    [DataRow("new.a between 1 and 5", "((new.a >= 1) AND (new.a <= 5))")]
    [DataRow("new.a not between 1 and 5", "((new.a < 1) OR (new.a > 5))")]
    [DataRow("new.a in (1, 2, 3)", "(new.a = ANY (ARRAY[1, 2, 3]))")]
    [DataRow("new.a not in (1, 2, 3)", "(new.a <> ALL (ARRAY[1, 2, 3]))")]
    [DataRow("coalesce(new.a, 0) > 0", "(COALESCE(new.a, 0) > 0)")]
    [DataRow("new.b = any(array[1,2])", "(new.b = ANY (ARRAY[1, 2]))")]
    [DataRow("old.* is distinct from new.*", "(old.* IS DISTINCT FROM new.*)")]
    [DataRow("lower(new.n) = 'x'", "(lower(new.n) = 'x'::text)")]
    [DataRow("pg_catalog.lower(new.n) = 'x'", "(lower(new.n) = 'x'::text)")]
    [DataRow("new.n like 'a%'", "(new.n ~~ 'a%'::text)")]
    [DataRow("new.n not like 'a%'", "(new.n !~~ 'a%'::text)")]
    [DataRow("new.n ilike 'a%'", "(new.n ~~* 'a%'::text)")]
    [DataRow("new.a + 1 > 2 * new.b", "(((new.a + 1) > (2 * new.b)))")]
    [DataRow("new.n = 'it''s'", "(new.n = 'it''s'::text)")]
    [DataRow("new.a > 0 and (new.b > 0 or new.b is null)", "(((new.a > 0) AND ((new.b > 0) OR (new.b IS NULL))))")]
    [DataRow("new.a > 0 and new.b > 0 and new.a < 9", "(((new.a > 0) AND (new.b > 0) AND (new.a < 9)))")]
    [DataRow("(new.a > 0 and new.b > 0) and new.a < 9", "(((new.a > 0) AND (new.b > 0) AND (new.a < 9)))")]
    [DataRow("new.v = 'x'", "((new.v)::text = 'x'::text)")]
    [DataRow("new.v::text = 'x'", "((new.v)::text = 'x'::text)")]
    [DataRow("new.v = 'x'::text", "((new.v)::text = 'x'::text)")]
    public void ASpellingAndWhatPostgreSqlPrintsForItAreTheSameCondition(string written, string rendered)
    {

        Assert.AreEqual(Canonical(written), Canonical(rendered), $"'{written}' and '{rendered}' must compare equal.");
    }

    [TestMethod]
    [DataRow("new.d = '2020-01-01'", "(new.d = '2020-01-01'::date)")]
    [DataRow("new.ts > '2020-01-01'", "(new.ts > '2020-01-01 00:00:00'::timestamp without time zone)")]
    [DataRow("new.c = 'X'", "(new.c = 'X'::name)")]
    [DataRow("new.n similar to 'a%'", "(new.n ~ similar_to_escape('a%'::text))")]
    public void ALiteralWhoseTypePostgreSqlInfersFromTheOtherOperandIsNotFolded(string written, string rendered)
    {
        // Folding these would need the type of the other operand, or PostgreSQL's own literal parsing. Known limit (G-40): the
        // condition compares as changed, which recreates the trigger, and is never reported as unchanged when it differs.
        Assert.AreNotEqual(Canonical(written), Canonical(rendered));
    }

    [TestMethod]
    [DataRow("new.a > -1", "(new.a > '-1'::integer)")]
    [DataRow("new.x > 0", "(new.x > (0)::numeric)")]
    [DataRow("new.x > 0::numeric", "(new.x > (0)::numeric)")]
    [DataRow("new.x > -1", "(new.x > ('-1'::integer)::numeric)")]
    [DataRow("new.v in ('a', 'b')", "((new.v)::text = ANY ((ARRAY['a'::character varying, 'b'::character varying])::text[]))")]
    [DataRow("new.v not in ('a', 'b')", "((new.v)::text <> ALL ((ARRAY['a'::character varying, 'b'::character varying])::text[]))")]
    [DataRow("new.n = 'x'::varchar", "(new.n = ('x'::character varying)::text)")]
    public void ConstantsPostgreSqlPrintsWithACastAreTheSameAsTheBareConstant(string written, string rendered)
    {
        Assert.AreEqual(Canonical(written), Canonical(rendered), $"'{written}' and '{rendered}' must compare equal.");
    }

    [TestMethod]
    [DataRow("new.a > -1", "new.a > 1")]
    [DataRow("new.a > '-1'::integer", "new.a > '-2'::integer")]
    [DataRow("new.a > '-1'::integer", "new.a > '-1'::text")]
    [DataRow("new.x > 0", "new.x > 1")]
    [DataRow("new.x > 1::numeric(5,2)", "new.x > 1")]
    [DataRow("new.x > '1'::numeric", "new.x > 1")]
    [DataRow("new.a = 1::text", "new.a = 1")]
    [DataRow("new.n = 'x'::bpchar", "new.n = 'x'")]
    [DataRow("new.n = 'x'::name", "new.n = 'x'")]
    [DataRow("new.a = any (array[1, 2]::text[])", "new.a = any (array[1, 2])")]
    [DataRow("new.v in ('a', 'b')", "new.v in ('a', 'c')")]
    [DataRow("new.v in ('a', 'b')", "new.v not in ('a', 'b')")]
    public void ACastThatChangesWhatIsComparedIsKept(string one, string other)
    {
        Assert.AreNotEqual(Canonical(one), Canonical(other), $"'{one}' and '{other}' are different conditions.");
    }

    [TestMethod]
    public void WhitespaceCaseAndParenthesesDoNotMatter()
    {
        Assert.AreEqual(
            Canonical("new.a>0   AND\n\tnew.b  <  10"),
            Canonical("((NEW.a > 0) and (new.b < 10))"));
    }

    [TestMethod]
    [DataRow("new.a > 0 and new.b > 0", "new.a > 0 or new.b > 0")]
    [DataRow("(new.a > 0 and new.b > 0) or new.a < 9", "new.a > 0 and (new.b > 0 or new.a < 9)")]
    [DataRow("new.a > 0", "new.a >= 0")]
    [DataRow("new.a > 0", "new.a > 1")]
    [DataRow("new.a > 0", "old.a > 0")]
    [DataRow("new.a > 0", "new.b > 0")]
    [DataRow("new.n = 'x'", "new.n = 'X'")]
    [DataRow("new.n = 'x'", "new.n <> 'x'")]
    [DataRow("new.a = 1", "not (new.a = 1)")]
    [DataRow("new.a in (1, 2)", "new.a in (1, 3)")]
    [DataRow("new.a in (1, 2)", "new.a not in (1, 2)")]
    [DataRow("new.a in (1, 2)", "new.a in (2, 1)")]
    [DataRow("new.a between 1 and 5", "new.a between 1 and 6")]
    [DataRow("new.a between 1 and 5", "new.a not between 1 and 5")]
    [DataRow("new.n like 'a%'", "new.n like 'b%'")]
    [DataRow("new.n like 'a%'", "new.n not like 'a%'")]
    [DataRow("new.n like 'a%'", "new.n ilike 'a%'")]
    [DataRow("lower(new.n) = 'x'", "upper(new.n) = 'x'")]
    [DataRow("new.a is null", "new.a is not null")]
    [DataRow("old.a is distinct from new.a", "old.a is not distinct from new.a")]
    [DataRow("new.a > 0", "new.a > 0 and new.b > 0")]
    public void ConditionsThatReallyDifferAreNeverEqual(string one, string other)
    {
        Assert.AreNotEqual(Canonical(one), Canonical(other), $"'{one}' and '{other}' are different conditions.");
    }

    [TestMethod]
    public void ACastToTextIsKeptOnAColumnThatIsNotVarchar()
    {
        // Dropping it would equate (new.a)::text = '1' with new.a = '1', which are different conditions on an integer column,
        // and on a citext column a text comparison is case-sensitive where the column's own is not.
        Assert.AreNotEqual(Canonical("new.a::text = '1'"), Canonical("new.a = '1'"));
        Assert.AreNotEqual(Canonical("new.c::text = 'X'"), Canonical("new.c = 'X'"));
        Assert.AreNotEqual(Canonical("new.n::text = 'x'"), Canonical("new.n = 'x'"), "Only a varchar column is folded, and only where the type is known.");
    }

    [TestMethod]
    public void ACastToTextIsKeptOnAColumnWhoseTypeIsUnknown()
    {
        Assert.AreNotEqual(Canonical("new.v::text = 'x'", knowsColumns: false), Canonical("new.v = 'x'", knowsColumns: false));
    }

    [TestMethod]
    public void OnlyTheCastToTextOfAStringConstantIsDropped()
    {
        Assert.AreNotEqual(Canonical("new.a = 1::text"), Canonical("new.a = 1"));
        Assert.AreNotEqual(Canonical("new.n = 'x'::char"), Canonical("new.n = 'x'"));
        Assert.AreNotEqual(Canonical("new.n = lower('x')::text"), Canonical("new.n = lower('x')"));
    }

    [TestMethod]
    public void OnlyThePublicAndCatalogSchemasAreDroppedFromAFunctionName()
    {
        Assert.AreEqual(Canonical("public.f(new.a) > 0"), Canonical("f(new.a) > 0"));
        Assert.AreNotEqual(Canonical("app.f(new.a) > 0"), Canonical("f(new.a) > 0"));
        Assert.AreNotEqual(Canonical("app.f(new.a) > 0"), Canonical("other.f(new.a) > 0"));
    }

    [TestMethod]
    public void AQuotedMixedCaseColumnIsNotTheLowerCaseOne()
    {
        Assert.AreNotEqual(Canonical("new.\"Mixed\" > 0"), Canonical("new.mixed > 0"));
    }

    [TestMethod]
    public void TheResultIsDeterministic()
    {
        Assert.AreEqual(Canonical("new.a > 0 and new.b < 1"), Canonical("new.a > 0 and new.b < 1"));
    }

    [TestMethod]
    public void NothingAndUnreadableJsonYieldNull()
    {
        Assert.IsNull(TriggerWhenConditionCanonicalizer.Canonicalize(null));
        Assert.IsNull(TriggerWhenConditionCanonicalizer.Canonicalize("  "));
        Assert.IsNull(TriggerWhenConditionCanonicalizer.Canonicalize("{ not json"));
    }

    private static string? Canonical(string condition, bool knowsColumns = true)
    {
        var trigger = new PgQueryParser().Parse($"CREATE TRIGGER t AFTER UPDATE ON public.x FOR EACH ROW WHEN ({condition}) EXECUTE FUNCTION public.f();")
            .Nodes.OfType<TriggerNode>().Single();

        return TriggerWhenConditionCanonicalizer.Canonicalize(trigger.WhenConditionAst, knowsColumns ? column => ColumnTypes.GetValueOrDefault(column) : null);
    }
}
