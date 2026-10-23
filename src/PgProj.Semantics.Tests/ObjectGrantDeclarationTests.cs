using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-65: only three grant forms used to be declared, and the rest produced no grant and no diagnostic. Every form the register
/// lists is now a grant, and a form that is recognized but not modeled is reported.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ObjectGrantDeclarationTests
{
    [TestMethod]
    [DataRow("GRANT SELECT ON TABLE public.t TO r;", "table:public.t->r = SELECT")]
    [DataRow("GRANT SELECT ON public.t TO r;", "table:public.t->r = SELECT")]
    [DataRow("GRANT USAGE ON SEQUENCE public.s TO r;", "sequence:public.s->r = USAGE")]
    // EXECUTE is every privilege a routine has, so it is ALL PRIVILEGES, as for ALL FUNCTIONS IN SCHEMA (G-61).
    [DataRow("GRANT EXECUTE ON FUNCTION public.f(integer) TO r;", "function:public.f(int4)->r = ALL PRIVILEGES")]
    [DataRow("GRANT EXECUTE ON FUNCTION public.f() TO r;", "function:public.f()->r = ALL PRIVILEGES")]
    [DataRow("GRANT EXECUTE ON PROCEDURE public.p(text, integer) TO r;", "procedure:public.p(text,int4)->r = ALL PRIVILEGES")]
    [DataRow("GRANT SELECT ON t TO r;", "table:public.t->r = SELECT")] // an unqualified table is the one in public (G-114)
    public void EachObjectFormIsDeclared(string sql, string expected)
    {
        Assert.AreEqual(expected, Describe(sql).Single());
    }

    [TestMethod]
    public void SeveralGranteesAreSeveralGrants()
    {
        CollectionAssert.AreEqual(
            new[] { "schema:s->PUBLIC = USAGE", "schema:s->a = USAGE", "schema:s->b c = USAGE" },
            Describe("GRANT USAGE ON SCHEMA s TO a, \"b c\", PUBLIC;"));
    }

    [TestMethod]
    public void SeveralObjectsAreSeveralGrants()
    {
        CollectionAssert.AreEqual(
            new[] { "table:public.a->r = SELECT", "table:public.b->r = SELECT" },
            Describe("GRANT SELECT ON public.a, public.b TO r;"));
    }

    [TestMethod]
    public void SeveralObjectsAndGranteesMakeOneGrantEach()
    {
        Assert.AreEqual(4, Build("GRANT SELECT ON public.a, public.b TO r1, r2;").Grants.Count);
    }

    [TestMethod]
    public void AllInSchemaToSeveralGranteesIsSeveralGrants()
    {
        CollectionAssert.AreEqual(
            new[] { "all tables in schema:s->a = SELECT, INSERT", "all tables in schema:s->b = SELECT, INSERT" }, // written order (G-112)
            Describe("GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA s TO a, b;"));
    }

    [TestMethod]
    public void TheFullPrivilegeSetOfATableIsAllPrivileges()
    {
        Assert.AreEqual(
            "table:public.t->r = ALL PRIVILEGES",
            Describe("GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON public.t TO r;").Single());
    }

    /// <summary>A schema or table that is called "tables" must not be taken for the tables of a schema.</summary>
    [TestMethod]
    public void AnObjectNamedTablesIsNotMistakenForAllTables()
    {
        Assert.AreEqual(
            "sequence:tables.q->r = USAGE",
            Describe("GRANT USAGE ON SEQUENCE tables.q TO r;").Single());
    }

    // ---- options and columns are part of a grant's identity ----

    [TestMethod]
    public void GrantOptionMakesAGrantWithADifferentIdentity()
    {
        var plain = Build("GRANT SELECT ON public.t TO r;").Grants.Values.Single();
        var withOption = Build("GRANT SELECT ON public.t TO r WITH GRANT OPTION;").Grants.Values.Single();

        Assert.IsFalse(plain.WithGrantOption);
        Assert.IsTrue(withOption.WithGrantOption);
        Assert.AreNotEqual(plain.Key, withOption.Key);
    }

    [TestMethod]
    public void ColumnPrivilegesKeepTheirColumnsInWrittenOrderAndApartFromWholeObjectPrivileges()
    {
        var grants = Build("GRANT SELECT (name, id), UPDATE (name) ON public.t TO r; GRANT INSERT ON public.t TO r;").Grants.Values
            .OrderBy(static grant => grant.Key, StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(3, grants.Length);
        var select = grants.Single(static grant => grant.Privileges.SequenceEqual(["SELECT"]));
        CollectionAssert.AreEqual(new[] { "name", "id" }, select.Columns.ToArray(), "Written order (G-112).");
        var update = grants.Single(static grant => grant.Privileges.SequenceEqual(["UPDATE"]));
        CollectionAssert.AreEqual(new[] { "name" }, update.Columns.ToArray());
        Assert.IsEmpty(grants.Single(static grant => grant.Privileges.SequenceEqual(["INSERT"])).Columns);
    }

    [TestMethod]
    public void ColumnOrderDoesNotChangeTheGrant()
    {
        var one = Build("GRANT SELECT (a, b) ON public.t TO r;").Grants.Values.Single();
        var other = Build("GRANT SELECT (b, a) ON public.t TO r;").Grants.Values.Single();

        Assert.AreEqual(one.Key, other.Key);
    }

    [TestMethod]
    public void DifferentColumnsAreDifferentGrants()
    {
        var one = Build("GRANT SELECT (a) ON public.t TO r;").Grants.Values.Single();
        var other = Build("GRANT SELECT (b) ON public.t TO r;").Grants.Values.Single();

        Assert.AreNotEqual(one.Key, other.Key);
    }

    // ---- default privileges ----

    [TestMethod]
    public void DefaultPrivilegesForDifferentRolesAreDifferentGrants()
    {
        var grants = Build(
            "ALTER DEFAULT PRIVILEGES FOR ROLE a IN SCHEMA s GRANT SELECT ON TABLES TO r;"
            + "ALTER DEFAULT PRIVILEGES FOR ROLE b IN SCHEMA s GRANT SELECT ON TABLES TO r;"
            + "ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT SELECT ON TABLES TO r;").Grants.Values.ToArray();

        Assert.HasCount(3, grants);
        CollectionAssert.AreEquivalent(new[] { "a", "b", null }, grants.Select(static grant => grant.ForRole).ToArray());
        Assert.AreEqual(1, grants.Select(static grant => grant.ObjectKey).Distinct().Count());
    }

    [TestMethod]
    public void DefaultPrivilegesOnSeveralSchemasAreOneGrantEach()
    {
        Assert.AreEqual(2, Build("ALTER DEFAULT PRIVILEGES IN SCHEMA a, b GRANT SELECT ON TABLES TO r;").Grants.Count);
    }

    [TestMethod]
    // USAGE is every privilege a type has (ALL PRIVILEGES); a schema has two, so USAGE alone is USAGE.
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO r;", "default types->r = ALL PRIVILEGES")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT USAGE ON TYPES TO r;", "default types in schema:s->r = ALL PRIVILEGES")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT USAGE ON SCHEMAS TO r;", "default schemas->r = USAGE")]
    public void DefaultPrivilegesOnTypesAndSchemasAreDeclared(string sql, string expected)
    {
        Assert.AreEqual(expected, Describe(sql).Single());
    }

    // ---- role membership ----

    [TestMethod]
    public void RoleMembershipIsAGrantOfTheRoleToTheMember()
    {
        var grant = Build("GRANT reader TO alice WITH ADMIN OPTION;").Grants.Values.Single();

        Assert.AreEqual("role:reader", grant.ObjectKey);
        Assert.AreEqual("alice", grant.Grantee);
        Assert.IsTrue(grant.WithGrantOption);
    }

    [TestMethod]
    public void SeveralRolesToSeveralMembersAreOneGrantEach()
    {
        Assert.AreEqual(4, Build("GRANT r1, r2 TO a, b;").Grants.Count);
    }

    // ---- what is not modeled is reported, not ignored ----

    [TestMethod]
    [DataRow("GRANT CONNECT ON DATABASE d TO r;", "DATABASE")]
    [DataRow("GRANT USAGE ON TYPE public.ty TO r;", "TYPE")]
    [DataRow("GRANT USAGE ON LANGUAGE plpgsql TO r;", "LANGUAGE")]
    [DataRow("GRANT USAGE ON FOREIGN DATA WRAPPER w TO r;", "FDW")]
    public void GrantOnAnUnmodeledKindIsReported(string sql, string kind)
    {
        var model = Build(sql);

        Assert.IsEmpty(model.Grants);
        var warning = model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning);
        StringAssert.Contains(warning.Message, kind, StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(warning.Message, "not modeled", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void RevokeIsReportedNotSilentlyIgnored()
    {
        var model = Build("REVOKE SELECT ON public.t FROM r;");

        Assert.IsEmpty(model.Grants);
        StringAssert.Contains(model.Diagnostics.Single(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning).Message, "REVOKE");
    }

    [TestMethod]
    public void GranteeThatNamesTheRunningUserIsReportedAndTheOtherGranteesAreKept()
    {
        var model = Build("GRANT SELECT ON public.t TO CURRENT_USER, r;");

        Assert.AreEqual("table:public.t->r = SELECT", model.Grants.Values.Select(static grant => $"{grant.ObjectKey}->{grant.Grantee} = {string.Join(", ", grant.Privileges)}").Single());
        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void AllProceduresInSchemaIsReported()
    {
        var model = Build("GRANT EXECUTE ON ALL PROCEDURES IN SCHEMA s TO r;");

        Assert.IsEmpty(model.Grants);
        Assert.IsTrue(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void EveryModeledFormProducesNoDiagnostic()
    {
        var model = Build(
            "GRANT SELECT ON public.t TO r1, r2 WITH GRANT OPTION;"
            + "GRANT SELECT (a) ON public.t TO r;"
            + "GRANT USAGE ON SEQUENCE public.s TO r;"
            + "GRANT EXECUTE ON FUNCTION public.f(integer) TO r;"
            + "GRANT r1 TO r2;"
            + "ALTER DEFAULT PRIVILEGES FOR ROLE o IN SCHEMA s GRANT USAGE ON TYPES TO r;");

        Assert.IsEmpty(model.Diagnostics.Where(static diagnostic => diagnostic.Severity != SemanticDiagnosticSeverity.Info).ToArray());
    }

    /// <summary>Saying a grant twice is legal SQL and means the same thing: it is one grant and not an error.</summary>
    [TestMethod]
    public void RepeatedIdenticalGrantIsOneGrantWithoutAnError()
    {
        var model = Build("GRANT SELECT ON public.t TO r; GRANT SELECT ON public.t TO r, r;");

        Assert.AreEqual(1, model.Grants.Count);
        Assert.IsFalse(model.HasErrors);
    }

    // ---- expansion into atoms ----

    [TestMethod]
    public void AnAtomIsOnePrivilegeOnOneObjectForOneGrantee()
    {
        var model = Build("create table public.t (id integer); GRANT SELECT, INSERT ON public.t TO a, b;");

        var atoms = model.Grants.Values.SelectMany(grant => GrantExpansion.Expand(grant, model)).ToArray();

        Assert.AreEqual(4, atoms.Length);
        CollectionAssert.AreEquivalent(
            new[] { "a:INSERT", "a:SELECT", "b:INSERT", "b:SELECT" },
            atoms.Select(static atom => $"{atom.Grantee}:{atom.Privilege}").ToArray());
        Assert.IsTrue(atoms.All(static atom => atom.Target == "table:public.t"));
    }

    [TestMethod]
    public void AllTablesInSchemaExpandsToEveryTableAndViewOfTheSchemaOnly()
    {
        var model = Build(
            "create schema s; create table s.a (id integer); create table s.b (id integer); create view s.v as select 1 as n;"
            + "create table other.c (id integer);"
            + "GRANT SELECT ON ALL TABLES IN SCHEMA s TO r;");

        var targets = GrantExpansion.Expand(model.Grants.Values.Single(), model).Select(static atom => atom.Target).Order(StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(new[] { "table:s.a", "table:s.b", "table:s.v" }, targets);
    }

    [TestMethod]
    public void AllPrivilegesExpandsToTheFullSetForTheKindOfObject()
    {
        var model = Build("create table public.t (id integer); GRANT ALL ON public.t TO r; GRANT ALL ON SCHEMA public TO r;");

        var byTarget = model.Grants.Values
            .SelectMany(grant => GrantExpansion.Expand(grant, model))
            .GroupBy(static atom => atom.Target)
            .ToDictionary(static group => group.Key, static group => group.Select(static atom => atom.Privilege).Order(StringComparer.Ordinal).ToArray());

        CollectionAssert.AreEqual(new[] { "DELETE", "INSERT", "REFERENCES", "SELECT", "TRIGGER", "TRUNCATE", "UPDATE" }, byTarget["table:public.t"]);
        CollectionAssert.AreEqual(new[] { "CREATE", "USAGE" }, byTarget["schema:public"]);
    }

    [TestMethod]
    public void MaintainIsNotAnAtomSoServersBeforeAndAfterVersion17Agree()
    {
        var model = Build("create table public.t (id integer); GRANT SELECT, MAINTAIN ON public.t TO r;");

        CollectionAssert.AreEqual(new[] { "SELECT" }, GrantExpansion.Expand(model.Grants.Values.Single(), model).Select(static atom => atom.Privilege).ToArray());
    }

    [TestMethod]
    public void ColumnGrantExpandsToOneAtomPerColumnThatBelongsToItsTable()
    {
        var model = Build("create table public.t (a integer, b integer); GRANT SELECT (a, b) ON public.t TO r;");

        var atoms = GrantExpansion.Expand(model.Grants.Values.Single(), model);

        CollectionAssert.AreEquivalent(new[] { "column:public.t.a", "column:public.t.b" }, atoms.Select(static atom => atom.Target).ToArray());
    }

    /// <summary>A function is told from its overloads by its argument types, however they are spelled.</summary>
    [TestMethod]
    [DataRow("create function public.f(a integer) returns integer language sql as $$ select a $$;", "GRANT EXECUTE ON FUNCTION public.f(int4) TO r;")]
    [DataRow("create function public.f(a int4) returns integer language sql as $$ select a $$;", "GRANT EXECUTE ON FUNCTION public.f(integer) TO r;")]
    [DataRow("create function public.f(a varchar(10), b text[]) returns integer language sql as $$ select 1 $$;", "GRANT EXECUTE ON FUNCTION public.f(character varying, text[]) TO r;")]
    public void FunctionGrantNamesTheSameTargetAsTheFunctionItGrantsOn(string function, string grant)
    {
        var model = Build(function + grant);

        var target = GrantExpansion.Expand(model.Grants.Values.Single(), model).Single().Target;
        var declared = model.Functions.Values.Single();

        Assert.AreEqual($"function:public.{GrantExpansion.RoutineObjectName(declared).ToLowerInvariant()}", target);
    }

    [TestMethod]
    public void OverloadsHaveDifferentTargets()
    {
        var model = Build(
            "create function public.f(a integer) returns integer language sql as $$ select a $$;"
            + "create function public.f(a text) returns integer language sql as $$ select 1 $$;"
            + "GRANT EXECUTE ON FUNCTION public.f(integer) TO r;");

        var target = GrantExpansion.Expand(model.Grants.Values.Single(), model).Single().Target;

        Assert.AreEqual("function:public.f(int4)", target);
    }

    [TestMethod]
    public void PublicExecuteOnARoutineIsImplicit()
    {
        var model = Build("create function public.f() returns integer language sql as $$ select 1 $$; GRANT EXECUTE ON FUNCTION public.f() TO PUBLIC;");

        Assert.IsTrue(GrantExpansion.Expand(model.Grants.Values.Single(), model).Single().IsImplicit);
    }

    [TestMethod]
    public void DefaultPrivilegesAtomsKeepTheirRole()
    {
        var model = Build("ALTER DEFAULT PRIVILEGES FOR ROLE o IN SCHEMA s GRANT SELECT ON TABLES TO r;");

        Assert.AreEqual("default tables in schema:s for role:o", GrantExpansion.Expand(model.Grants.Values.Single(), model).Single().Target);
    }

    private static string[] Describe(string sql)
        => Build(sql).Grants.Values
            .Select(static grant => $"{grant.ObjectKey}->{grant.Grantee} = {string.Join(", ", grant.Privileges)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
