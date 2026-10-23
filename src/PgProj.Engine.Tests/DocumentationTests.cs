using System.Text.Json;
using PgProj.Engine.Documentation;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-159: <c>pgproj document</c>. A comment's text is read as a description and <c>@tag: value</c> lines (every tag optional, custom tags kept, bad values reported without losing what was written),
/// the data dictionary is built from the model (project or live), and it is written as HTML, Markdown, and JSON.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DocumentationTests
{
    private static DatabaseModel Model(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static DatabaseDocumentation Document(string sql, ErdFilter? filter = null)
        => DocumentationBuilder.Build(Model(sql), new DocSource("project", "shop"), filter ?? ErdFilter.None, new DateTime(2026, 10, 2, 14, 3, 11, DateTimeKind.Utc));

    /// <summary>G-216: a view's column and a composite type's field show their comment.</summary>
    [TestMethod]
    public void AViewColumnAndATypeFieldShowTheirComments()
    {
        var model = Model("""
            CREATE TABLE app.t (a int);
            CREATE VIEW app.v AS SELECT a FROM app.t;
            COMMENT ON COLUMN app.v.a IS 'The a of t.';
            CREATE TYPE app.ct AS (x int);
            COMMENT ON COLUMN app.ct.x IS 'The x.';
            """);

        // The view's output columns come from binding, which SemanticBuilder runs for the CLI; this pipeline does not, so they are given here.
        model.Views = model.Views.ToDictionary(static pair => pair.Key, static pair => pair.Value with { OutputColumnNames = ["a"], OutputColumnTypes = ["integer"] });
        var document = DocumentationBuilder.Build(model, new DocSource("project", "shop"), ErdFilter.None, new DateTime(2026, 10, 2, 14, 3, 11, DateTimeKind.Utc));

        var app = document.Schemas.Single(static schema => schema.Name == "app");
        Assert.AreEqual("The a of t.", app.Views.Single().Columns.Single().Description);
        Assert.AreEqual("The x.", app.Types.Single().Fields!.Single().Description);
        StringAssert.Contains(DocumentationFiles.Render(document, DocFormat.Markdown), "| `a` | integer | The a of t. |");
        StringAssert.Contains(DocumentationFiles.Render(document, DocFormat.Html), "<td>The x.</td>");
    }

    // ---- tags

    [TestMethod]
    public void APlainCommentIsTheDescriptionAndNothingElse()
    {
        var parsed = DocTags.Parse("Customers who have bought something.");

        Assert.AreEqual("Customers who have bought something.", parsed.Description);
        Assert.IsFalse(parsed.Issues.Any());
        Assert.IsNull(parsed.BusinessName);
        Assert.AreSame(DocAnnotations.None, DocTags.Parse(null));
        Assert.AreSame(DocAnnotations.None, DocTags.Parse("  \n "));
    }

    [TestMethod]
    public void EveryTagOfTheStarterSetIsRead()
    {
        var parsed = DocTags.Parse("""
            What it is.

            @business-name: Customer Email
            @owner: Sales Ops
            @classification: Confidential
            @pii: yes
            @deprecated: use contact_email
            @since-version: 2.4.0
            @since-date: 2026-01-15
            @retention-months: 84
            @example: a@example.com
            @example: b@example.com
            @see-also: app.customer
            @see-also: https://example.com
            """);

        Assert.AreEqual("What it is.", parsed.Description);
        Assert.AreEqual("Customer Email", parsed.BusinessName);
        Assert.AreEqual("Sales Ops", parsed.Owner);
        Assert.AreEqual("Confidential", parsed.Classification);
        Assert.IsTrue(parsed.Pii);
        Assert.AreEqual(new DocDeprecation(true, "use contact_email"), parsed.Deprecated);
        Assert.AreEqual("2.4.0", parsed.SinceVersion);
        Assert.AreEqual("2026-01-15", parsed.SinceDate);
        Assert.AreEqual(84, parsed.RetentionMonths);
        CollectionAssert.AreEqual(new[] { "a@example.com", "b@example.com" }, parsed.Examples.ToArray());
        CollectionAssert.AreEqual(new[] { "app.customer", "https://example.com" }, parsed.SeeAlso.ToArray());
        Assert.IsFalse(parsed.Issues.Any());
    }

    [TestMethod]
    public void NamesAreNotCaseSensitiveAValueRunsOnOverIndentedLinesAndOtherTagsAreCustom()
    {
        var parsed = DocTags.Parse("@Business-Name: Order\n@Team: growth\n@team: platform\n@note: first line\n    second line\n@flag");

        Assert.AreEqual("Order", parsed.BusinessName);
        CollectionAssert.AreEqual(new[] { "growth", "platform" }, parsed.Custom["team"].ToArray());
        Assert.AreEqual("first line\nsecond line", parsed.Custom["note"].Single());
        Assert.AreEqual(string.Empty, parsed.Custom["flag"].Single());
        Assert.IsNull(parsed.Description);
    }

    [TestMethod]
    public void AnAtSignInTheMiddleOfALineIsText()
    {
        var parsed = DocTags.Parse("Write to help@example.com or see @owner: nobody");

        Assert.AreEqual("Write to help@example.com or see @owner: nobody", parsed.Description);
        Assert.IsNull(parsed.Owner);
    }

    [TestMethod]
    public void ADescriptionTagIsJoinedWithTheLeadingTextAndTextAfterTheTagsIsKept()
    {
        var parsed = DocTags.Parse("Leading.\n@description: Tagged.\n@owner: x\nAfter the tags.");

        Assert.AreEqual("Leading.\n\nTagged.\n\nAfter the tags.", parsed.Description);
        Assert.AreEqual("x", parsed.Owner);
    }

    [TestMethod]
    public void BareDeprecatedIsAFlagAndFalseIsNotDeprecated()
    {
        Assert.AreEqual(new DocDeprecation(true, null), DocTags.Parse("@deprecated").Deprecated);
        Assert.AreEqual(new DocDeprecation(true, null), DocTags.Parse("@deprecated: true").Deprecated);
        Assert.IsNull(DocTags.Parse("@deprecated: no").Deprecated);
    }

    [TestMethod]
    public void DatesAcceptADateOrADateTimeAndRetentionDatesMustBeUtc()
    {
        Assert.AreEqual("2026-10-02", DocTags.Parse("@since-date: 2026-10-02").SinceDate);
        Assert.AreEqual("2026-10-02T14:03:00Z", DocTags.Parse("@since-date: 2026-10-02T14:03:00Z").SinceDate);
        Assert.AreEqual("2026-10-02T09:03:00-05:00", DocTags.Parse("@since-date: 2026-10-02T09:03:00-05:00").SinceDate, "A since date may carry any offset.");
        Assert.AreEqual("2030-01-01", DocTags.Parse("@retention-date: 2030-01-01").RetentionDate);
        Assert.AreEqual("2030-01-01T00:00:00Z", DocTags.Parse("@retention-date: 2030-01-01T00:00:00Z").RetentionDate);
        Assert.AreEqual("2030-01-01T00:00:00+00:00", DocTags.Parse("@retention-date: 2030-01-01T00:00:00+00:00").RetentionDate);

        var offset = DocTags.Parse("@retention-date: 2030-01-01T00:00:00+02:00");
        Assert.IsNull(offset.RetentionDate);
        Assert.AreEqual("2030-01-01T00:00:00+02:00", offset.Invalid["retention-date"]);
        StringAssert.Contains(offset.Issues.Single().Message, "must be in UTC");

        var bad = DocTags.Parse("@since-date: 15/01/2026");
        Assert.IsNull(bad.SinceDate);
        Assert.AreEqual("15/01/2026", bad.Invalid["since-date"]);
        Assert.AreEqual(DocSeverity.Error, bad.Issues.Single().Severity);
        Assert.IsNull(DocTags.Parse("@since-date: 2026-13-45").SinceDate);
    }

    [TestMethod]
    public void RetentionMonthsMustBeAPositiveWholeNumber()
    {
        Assert.AreEqual(12, DocTags.Parse("@retention-months: 12").RetentionMonths);
        foreach (var text in new[] { "0", "-3", "1.5", "a year", string.Empty })
        {
            var parsed = DocTags.Parse($"@retention-months: {text}");
            Assert.IsNull(parsed.RetentionMonths, text);
            Assert.AreEqual(1, parsed.Issues.Count, text);
            Assert.AreEqual(DocSeverity.Error, parsed.Issues[0].Severity);
        }
    }

    [TestMethod]
    public void BothRetentionTagsIsAnErrorThatKeepsWhatWasWrittenAndParsesNeither()
    {
        var parsed = DocTags.Parse("@retention-months: 12\n@retention-date: 2030-01-01");

        Assert.IsNull(parsed.RetentionMonths);
        Assert.IsNull(parsed.RetentionDate);
        Assert.AreEqual("12", parsed.Invalid["retention-months"]);
        Assert.AreEqual("2030-01-01", parsed.Invalid["retention-date"]);
        var issue = parsed.Issues.Single();
        Assert.AreEqual(DocSeverity.Error, issue.Severity);
        StringAssert.Contains(issue.Message, "cannot both be given");
    }

    [TestMethod]
    public void AnUnreadablePiiValueIsAnErrorAndARepeatedSingleTagIsAWarning()
    {
        var pii = DocTags.Parse("@pii: maybe");
        Assert.IsNull(pii.Pii);
        Assert.AreEqual("maybe", pii.Invalid["pii"]);
        Assert.AreEqual(DocSeverity.Error, pii.Issues.Single().Severity);

        var repeated = DocTags.Parse("@owner: a\n@owner: b");
        Assert.AreEqual("b", repeated.Owner);
        Assert.AreEqual(DocSeverity.Warning, repeated.Issues.Single().Severity);
        Assert.IsFalse(DocTags.Parse("@pii: false").Pii!.Value);
        Assert.IsTrue(DocTags.Parse("@PII: YES").Pii!.Value);
    }

    [TestMethod]
    public void ATableListsItsTriggersWithTheirDescriptionsAndTags()
    {
        var document = Document("""
            CREATE TABLE app.orders (id int PRIMARY KEY, total int);
            CREATE FUNCTION app.audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
            CREATE TRIGGER audit_orders AFTER INSERT OR UPDATE ON app.orders FOR EACH ROW WHEN (NEW.total > 0) EXECUTE FUNCTION app.audit('x');
            CREATE TRIGGER audit_stmt BEFORE DELETE ON app.orders FOR EACH STATEMENT EXECUTE FUNCTION app.audit();
            COMMENT ON TRIGGER audit_orders ON app.orders IS $$Writes <b>audit</b> rows.

            @owner: Platform
            @pii: true$$;
            """);

        var triggers = document.Schemas[0].Tables[0].Triggers;
        Assert.AreEqual(2, document.Counts.Triggers);
        var audit = triggers.Single(static trigger => trigger.Name == "audit_orders");
        Assert.AreEqual("app.orders.trigger:audit_orders", audit.QualifiedName);
        Assert.AreEqual("AFTER", audit.Timing);
        CollectionAssert.AreEquivalent(new[] { "INSERT", "UPDATE" }, audit.Events.ToArray());
        Assert.AreEqual("row", audit.Level);
        StringAssert.Contains(audit.Function, "audit(");
        StringAssert.Contains(audit.When, "total");
        Assert.AreEqual("Writes <b>audit</b> rows.", audit.Description);
        Assert.AreEqual("Platform", audit.Owner);
        Assert.AreEqual("statement", triggers.Single(static trigger => trigger.Name == "audit_stmt").Level);
        var warning = document.Problems.Single();
        Assert.AreEqual("app.orders.trigger:audit_orders", warning.Object);
        StringAssert.Contains(warning.Message, "@pii does not apply to a trigger");

        var markdown = DocumentationFiles.Render(document, DocFormat.Markdown);
        var html = DocumentationFiles.Render(document, DocFormat.Html);
        StringAssert.Contains(markdown, "**Triggers**");
        StringAssert.Contains(markdown, "| `audit_orders` | AFTER INSERT OR UPDATE WHEN (");
        StringAssert.Contains(html, "Writes &lt;b&gt;audit&lt;/b&gt; rows.");
        Assert.IsFalse(html.Contains("<b>audit", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ATableListsItsPoliciesWithTheirConditionsDescriptionsAndTags()
    {
        var document = Document("""
            CREATE TABLE app.orders (id int PRIMARY KEY, owner_name text);
            ALTER TABLE app.orders ENABLE ROW LEVEL SECURITY;
            CREATE POLICY own_rows ON app.orders FOR SELECT TO PUBLIC USING (owner_name = current_user);
            CREATE POLICY write_rows ON app.orders AS RESTRICTIVE FOR INSERT TO PUBLIC WITH CHECK (owner_name = current_user);
            COMMENT ON POLICY own_rows ON app.orders IS $$Users see <b>their</b> rows.

            @owner: Security
            @pii: true$$;
            """);

        var table = document.Schemas[0].Tables[0];
        Assert.AreEqual(2, document.Counts.Policies);
        Assert.IsTrue(table.RowSecurity);
        var own = table.Policies.Single(static policy => policy.Name == "own_rows");
        Assert.AreEqual("app.orders.policy:own_rows", own.QualifiedName);
        Assert.AreEqual("SELECT", own.Command);
        Assert.IsTrue(own.Permissive);
        StringAssert.Contains(own.Using, "owner_name");
        Assert.IsNull(own.WithCheck);
        Assert.AreEqual("Users see <b>their</b> rows.", own.Description);
        Assert.AreEqual("Security", own.Owner);
        var write = table.Policies.Single(static policy => policy.Name == "write_rows");
        Assert.IsFalse(write.Permissive);
        Assert.AreEqual("INSERT", write.Command);
        StringAssert.Contains(write.WithCheck, "owner_name");
        var warning = document.Problems.Single();
        Assert.AreEqual("app.orders.policy:own_rows", warning.Object);
        StringAssert.Contains(warning.Message, "@pii does not apply to a policy");

        var markdown = DocumentationFiles.Render(document, DocFormat.Markdown);
        var html = DocumentationFiles.Render(document, DocFormat.Html);
        StringAssert.Contains(markdown, "**Row-level security policies**");
        StringAssert.Contains(markdown, "| `write_rows` | restrictive INSERT |");
        StringAssert.Contains(html, "Users see &lt;b&gt;their&lt;/b&gt; rows.");
        Assert.IsFalse(html.Contains("<b>their", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ATableWithPoliciesButNoRowSecurityIsSaidToBeUnenforced()
    {
        var document = Document("CREATE TABLE app.t (id int PRIMARY KEY); CREATE POLICY p ON app.t USING (true);");

        Assert.IsFalse(document.Schemas[0].Tables[0].RowSecurity);
        StringAssert.Contains(DocumentationFiles.Render(document, DocFormat.Markdown), "(row-level security is not enabled on the table)");
    }

    // ---- which tags apply to which kind of object

    [TestMethod]
    [DataRow("pii", "constraint")]
    [DataRow("pii", "schema")]
    [DataRow("pii", "function")]
    [DataRow("classification", "index")]
    [DataRow("classification", "sequence")]
    [DataRow("retention-months", "routine")]
    [DataRow("retention-date", "type")]
    [DataRow("example", "schema")]
    [DataRow("example", "table")]
    [DataRow("example", "index")]
    public void ATagThatDoesNotApplyToTheKindIsAWarningAndIsStillKept(string tag, string kind)
    {
        var value = tag switch { "pii" => "true", "retention-months" => "12", "retention-date" => "2030-01-01", _ => "x" };
        var parsed = DocTags.Parse($"@{tag}: {value}", kind == "function" ? "routine" : kind);

        var issue = parsed.Issues.Single();
        Assert.AreEqual(DocSeverity.Warning, issue.Severity);
        Assert.AreEqual(tag, issue.Tag);
        StringAssert.Contains(issue.Message, $"@{tag} does not apply to a {(kind == "function" ? "routine" : kind)}");
        StringAssert.Contains(issue.Message, "shown as written");
        Assert.IsTrue(parsed.HasContent, "The tag is still kept.");
    }

    [TestMethod]
    [DataRow("pii", "table")]
    [DataRow("pii", "view")]
    [DataRow("pii", "column")]
    [DataRow("classification", "schema")]
    [DataRow("classification", "table")]
    [DataRow("classification", "column")]
    [DataRow("retention-months", "schema")]
    [DataRow("retention-date", "view")]
    [DataRow("example", "column")]
    [DataRow("example", "constraint")]
    [DataRow("example", "routine")]
    [DataRow("example", "type")]
    public void ATagThatAppliesToTheKindIsNotWarnedAbout(string tag, string kind)
    {
        var value = tag switch { "pii" => "true", "retention-months" => "12", "retention-date" => "2030-01-01", _ => "x" };

        Assert.AreEqual(0, DocTags.Parse($"@{tag}: {value}", kind).Issues.Count);
    }

    [TestMethod]
    public void TheDescriptiveTagsApplyToEveryKindAndCustomTagsAreNeverChecked()
    {
        const string comment = "@description: d\n@business-name: b\n@owner: o\n@see-also: x\n@deprecated\n@since-version: 1\n@since-date: 2026-01-01\n@my-own: y";

        foreach (var kind in DocTags.Kinds)
        {
            Assert.AreEqual(0, DocTags.Parse(comment, kind).Issues.Count, kind);
        }
    }

    [TestMethod]
    public void WithoutAKindNothingIsCheckedAndTheMatrixIsTheOneDocumented()
    {
        Assert.AreEqual(0, DocTags.Parse("@pii: true\n@example: x", null).Issues.Count);
        CollectionAssert.AreEquivalent(new[] { "classification", "pii", "retention-months", "retention-date", "example" }, DocTags.AppliesTo.Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "table", "view", "column" }, DocTags.AppliesTo["pii"].ToArray());
        CollectionAssert.AreEquivalent(new[] { "column", "constraint", "routine", "type" }, DocTags.AppliesTo["example"].ToArray());
        Assert.IsTrue(DocTags.AppliesTo.Keys.All(static tag => DocTags.StarterSet.Contains(tag)));
    }

    [TestMethod]
    public void TheBuilderChecksEachObjectAgainstItsOwnKind()
    {
        var document = Document("""
            CREATE TABLE app.t (id int PRIMARY KEY, email text, CONSTRAINT t_ck CHECK (id > 0));
            CREATE FUNCTION app.f() RETURNS int LANGUAGE sql AS 'SELECT 1';
            COMMENT ON SCHEMA app IS '@pii: true';
            COMMENT ON TABLE app.t IS '@pii: true';
            COMMENT ON COLUMN app.t.email IS '@pii: true';
            COMMENT ON CONSTRAINT t_ck ON app.t IS '@pii: true';
            COMMENT ON FUNCTION app.f() IS '@retention-months: 6';
            """);

        CollectionAssert.AreEquivalent(
            new[] { "app", "app.t.constraint:t_ck", "app.f()" },
            document.Problems.Select(static problem => problem.Object).ToArray());
        Assert.IsTrue(document.Problems.All(static problem => problem.Severity == DocSeverity.Warning));
        Assert.AreEqual(0, document.Counts.Errors);
        Assert.AreEqual(3, document.Counts.Warnings);
    }

    // ---- the dictionary

    private const string Shop = """
        CREATE TABLE app.customer (id bigint PRIMARY KEY, email text NOT NULL UNIQUE, born date, note varchar(40), flag boolean DEFAULT false);
        CREATE TABLE app.orders (id int PRIMARY KEY, customer_id bigint NOT NULL REFERENCES app.customer (id) ON DELETE CASCADE, total numeric(10,2) CHECK (total >= 0));
        CREATE INDEX orders_customer ON app.orders (customer_id);
        CREATE VIEW app.big_orders AS SELECT * FROM app.orders WHERE total > 100;
        CREATE TYPE app.mood AS ENUM ('sad', 'ok');
        CREATE SEQUENCE app.ticket;
        CREATE FUNCTION app.add(a int, b int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT a + b';
        CREATE TABLE audit.entry (id int PRIMARY KEY);
        COMMENT ON TABLE app.customer IS $$A buyer.

        @business-name: Customer
        @see-also: app.orders
        @see-also: https://example.com
        @team: growth$$;
        COMMENT ON COLUMN app.customer.email IS $$Where receipts go.
        @pii: true$$;
        """;

    [TestMethod]
    public void TheDictionaryHasTablesColumnsKeysConstraintsIndexesViewsRoutinesTypesAndSequences()
    {
        var document = Document(Shop);

        CollectionAssert.AreEqual(new[] { "app", "audit" }, document.Schemas.Select(static schema => schema.Name).ToArray());
        var app = document.Schemas[0];
        var customer = app.Tables.Single(static table => table.Name == "customer");
        Assert.AreEqual("app.customer", customer.QualifiedName);
        var id = customer.Columns[0];
        Assert.IsTrue(id.PrimaryKey);
        Assert.IsFalse(id.Nullable, "A primary key column is never null.");
        Assert.AreEqual("bigint", id.Type);
        Assert.IsTrue(customer.Columns.Single(static column => column.Name == "email").Unique);
        Assert.AreEqual("varchar(40)", customer.Columns.Single(static column => column.Name == "note").Type);
        Assert.AreEqual("boolean", customer.Columns.Single(static column => column.Name == "flag").Type, "An internal type name is written as people write it.");

        var orders = app.Tables.Single(static table => table.Name == "orders");
        var foreignKey = orders.Columns.Single(static column => column.Name == "customer_id");
        Assert.IsTrue(foreignKey.ForeignKey);
        CollectionAssert.AreEqual(new[] { "app.customer(id)" }, foreignKey.References!.ToArray());
        CollectionAssert.AreEqual(new[] { "PRIMARY KEY", "FOREIGN KEY", "CHECK" }, orders.Constraints.Select(static constraint => constraint.Type).ToArray(), "The primary key comes first.");
        Assert.AreEqual("CASCADE", orders.Constraints.Single(static constraint => constraint.Type == "FOREIGN KEY").OnDelete);
        Assert.IsNull(orders.Constraints.Single(static constraint => constraint.Type == "FOREIGN KEY").OnUpdate, "NO ACTION is the default and is left out.");
        Assert.AreEqual("btree", orders.Indexes.Single().Method);

        Assert.AreEqual("app.big_orders", app.Views.Single().QualifiedName);
        var routine = app.Routines.Single();
        Assert.AreEqual("function", routine.Kind);
        Assert.AreEqual("add(a integer, b integer)", routine.Signature);
        Assert.AreEqual("app.add(integer, integer)", routine.QualifiedName);
        Assert.AreEqual("integer", routine.Returns);
        Assert.AreEqual("enum", app.Types.Single().Kind);
        CollectionAssert.AreEqual(new[] { "sad", "ok" }, app.Types.Single().Labels!.ToArray());
        Assert.AreEqual("app.ticket", app.Sequences.Single().QualifiedName);
        Assert.AreEqual(2, document.Counts.Schemas);
        Assert.AreEqual(3, document.Counts.Tables);
    }

    [TestMethod]
    public void CommentsBecomeDescriptionsAndTagsAndSeeAlsoLinksToDocumentedObjects()
    {
        var customer = Document(Shop).Schemas[0].Tables.Single(static table => table.Name == "customer");

        Assert.AreEqual("A buyer.", customer.Description);
        Assert.AreEqual("Customer", customer.BusinessName);
        Assert.AreEqual("growth", customer.Custom!["team"].Single());
        Assert.AreEqual(new DocLink("app.orders", "app.orders"), customer.SeeAlso![0]);
        Assert.AreEqual(new DocLink("https://example.com", null), customer.SeeAlso[1]);
        var email = customer.Columns.Single(static column => column.Name == "email");
        Assert.AreEqual("Where receipts go.", email.Description);
        Assert.IsTrue(email.Pii);
        Assert.IsNull(customer.Columns[0].Description);
    }

    [TestMethod]
    public void TagProblemsAreCollectedWithTheirObjectAndCounted()
    {
        var document = Document("""
            CREATE TABLE app.t (id int PRIMARY KEY, a int, b int);
            COMMENT ON COLUMN app.t.a IS '@pii: maybe';
            COMMENT ON COLUMN app.t.b IS $$@owner: x
            @owner: y$$;
            """);

        Assert.AreEqual(1, document.Counts.Errors);
        Assert.AreEqual(1, document.Counts.Warnings);
        Assert.AreEqual("app.t.a", document.Problems.Single(static problem => problem.Severity == DocSeverity.Error).Object);
        Assert.AreEqual("maybe", document.Schemas[0].Tables[0].Columns[1].InvalidTags!["pii"]);
    }

    [TestMethod]
    public void TheSchemaFilterLimitsTheDocumentAndTheDiagram()
    {
        var only = Document(Shop, new ErdFilter(["audit"], Exclude: false));
        var except = Document(Shop, new ErdFilter(["audit"], Exclude: true));

        Assert.AreEqual("audit", only.Schemas.Single().Name);
        Assert.AreEqual("app", except.Schemas.Single().Name);
        StringAssert.Contains(only.Erd, "entry");
        Assert.IsFalse(except.Erd.Contains("entry", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "audit" }, only.SchemasOnly.ToArray());
        CollectionAssert.AreEqual(new[] { "audit" }, except.SchemasExcluded.ToArray());
        CollectionAssert.AreEqual(new[] { "app", "audit" }, DocumentationBuilder.SchemaNames(Model(Shop)).ToArray());
    }

    [TestMethod]
    public void ASchemasCommentIsItsDescriptionAndTags()
    {
        var document = Document("""
            CREATE TABLE billing.invoice (id int PRIMARY KEY);
            CREATE TABLE audit.entry (id int PRIMARY KEY);
            COMMENT ON SCHEMA billing IS $$Everything about money owed.

            @business-name: Billing
            @owner: Finance
            @see-also: billing.invoice
            @retention-months: soon$$;
            """);

        var billing = document.Schemas.Single(static schema => schema.Name == "billing");
        Assert.AreEqual("Everything about money owed.", billing.Description);
        Assert.AreEqual("Billing", billing.BusinessName);
        Assert.AreEqual("Finance", billing.Owner);
        Assert.AreEqual(new DocLink("billing.invoice", "billing.invoice"), billing.SeeAlso![0]);
        Assert.IsNull(document.Schemas.Single(static schema => schema.Name == "audit").Description);
        var problem = document.Problems.Single();
        Assert.AreEqual("billing", problem.Object);
        Assert.AreEqual("retention-months", problem.Tag);
    }

    [TestMethod]
    public void ACommentedSchemaWithNoObjectsIsDocumentedAndCanBeNamedInTheFilter()
    {
        var model = Model("CREATE TABLE app.t (id int PRIMARY KEY); COMMENT ON SCHEMA reports IS 'Nothing here yet.';");

        var document = DocumentationBuilder.Build(model, new DocSource("project", "shop"), ErdFilter.None, DateTime.UtcNow);

        CollectionAssert.AreEqual(new[] { "app", "reports" }, document.Schemas.Select(static schema => schema.Name).ToArray());
        Assert.AreEqual("Nothing here yet.", document.Schemas[1].Description);
        CollectionAssert.AreEqual(new[] { "app", "reports" }, DocumentationBuilder.SchemaNames(model).ToArray());
        Assert.AreEqual("reports", DocumentationBuilder.Build(model, new DocSource("project", "shop"), new ErdFilter(["reports"], Exclude: false), DateTime.UtcNow).Schemas.Single().Name);
    }

    [TestMethod]
    public void TheSchemaDescriptionIsShownInEveryFormat()
    {
        var document = Document("CREATE TABLE app.t (id int PRIMARY KEY); COMMENT ON SCHEMA app IS $$Where <b>things</b> live.\n\n@owner: Platform$$;");

        var markdown = DocumentationFiles.Render(document, DocFormat.Markdown);
        var html = DocumentationFiles.Render(document, DocFormat.Html);
        var json = DocumentationFiles.Render(document, DocFormat.Json);

        StringAssert.Contains(markdown, "## Schema app\n\nWhere <b>things</b> live.\n\n- **Owner:** Platform");
        StringAssert.Contains(html, "Where &lt;b&gt;things&lt;/b&gt; live.");
        Assert.IsFalse(html.Contains("<b>things", StringComparison.Ordinal));
        StringAssert.Contains(html, "<strong>Owner:</strong> Platform");
        using var parsed = JsonDocument.Parse(json);
        Assert.AreEqual("Where <b>things</b> live.", parsed.RootElement.GetProperty("schemas")[0].GetProperty("description").GetString());
        Assert.AreEqual("Platform", parsed.RootElement.GetProperty("schemas")[0].GetProperty("owner").GetString());
    }

    [TestMethod]
    public void AConstraintsCommentIsItsDescriptionAndTagsInEveryFormat()
    {
        var document = Document("""
            CREATE TABLE app.orders (id int, total int, CONSTRAINT orders_pkey PRIMARY KEY (id), CONSTRAINT orders_total_ck CHECK (total >= 0));
            COMMENT ON CONSTRAINT orders_total_ck ON app.orders IS $$Never <b>negative</b>.

            @owner: Finance
            @since-date: soon$$;
            """);

        var table = document.Schemas[0].Tables[0];
        var check = table.Constraints.Single(static constraint => constraint.Name == "orders_total_ck");
        Assert.AreEqual("Never <b>negative</b>.", check.Description);
        Assert.AreEqual("Finance", check.Owner);
        Assert.AreEqual("app.orders.constraint:orders_total_ck", check.QualifiedName);
        Assert.IsNull(table.Constraints.Single(static constraint => constraint.Name == "orders_pkey").Description);
        Assert.AreEqual("app.orders.constraint:orders_total_ck", document.Problems.Single().Object);

        var markdown = DocumentationFiles.Render(document, DocFormat.Markdown);
        var html = DocumentationFiles.Render(document, DocFormat.Html);
        StringAssert.Contains(markdown, "| Name | Type | Columns | Definition | Description |");
        StringAssert.Contains(markdown, "Never <b>negative</b>.<br>Owner: Finance");
        StringAssert.Contains(html, "Never &lt;b&gt;negative&lt;/b&gt;.");
        Assert.IsFalse(html.Contains("<b>negative", StringComparison.Ordinal));
        StringAssert.Contains(html, "<th>Description</th>");
    }

    // ---- the files

    [TestMethod]
    public void JsonCarriesTheMarkerTheVersionAndCamelCasePropertiesWithTheNameFirst()
    {
        var json = DocumentationFiles.Render(Document(Shop), DocFormat.Json);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(DocumentationFiles.Marker, root.GetProperty("generator").GetString());
        Assert.IsTrue(FilePlacementSeesMarker(json));
        var customer = root.GetProperty("schemas")[0].GetProperty("tables").EnumerateArray().Single(static table => table.GetProperty("name").GetString() == "customer");
        Assert.AreEqual("name", customer.EnumerateObject().First().Name, "The name leads the object.");
        Assert.AreEqual("Customer", customer.GetProperty("businessName").GetString());
        Assert.AreEqual("app.orders", customer.GetProperty("seeAlso")[0].GetProperty("target").GetString());
        Assert.IsFalse(customer.TryGetProperty("owner", out _), "Tags that were not written are left out.");
        StringAssert.StartsWith(root.GetProperty("erd").GetString(), "erDiagram");
    }

    private static bool FilePlacementSeesMarker(string text)
        => text.Split('\n').Take(5).Any(static line => line.Contains("Generated by pgproj ", StringComparison.Ordinal));

    [TestMethod]
    public void MarkdownHasAContentsSummaryDiagramAndADictionaryWithLinks()
    {
        var markdown = DocumentationFiles.Render(Document(Shop), DocFormat.Markdown);

        StringAssert.StartsWith(markdown, "<!-- " + DocumentationFiles.Marker);
        StringAssert.Contains(markdown, "# Database documentation: shop");
        StringAssert.Contains(markdown, "## Contents");
        StringAssert.Contains(markdown, "## Summary");
        StringAssert.Contains(markdown, "```mermaid");
        StringAssert.Contains(markdown, "## Schema app");
        StringAssert.Contains(markdown, "<a id=\"app.customer\"></a>");
        StringAssert.Contains(markdown, "[app.orders](#app.orders)");
        StringAssert.Contains(markdown, "| 1 | `id` | bigint | no |  | PK |");
        StringAssert.Contains(markdown, "- **team:** growth");
        StringAssert.Contains(markdown, "ON DELETE CASCADE");
    }

    [TestMethod]
    public void HtmlEncodesEverythingAPersonWroteSoACommentCannotInjectMarkup()
    {
        var html = DocumentationFiles.Render(Document("""
            CREATE TABLE app.t (id int PRIMARY KEY);
            COMMENT ON TABLE app.t IS $$<script>alert(1)</script> & more

            @owner: <b>me</b>
            @note: "quoted"$$;
            """), DocFormat.Html);

        StringAssert.Contains(html, "&lt;script&gt;alert(1)&lt;/script&gt; &amp; more");
        StringAssert.Contains(html, "&lt;b&gt;me&lt;/b&gt;");
        Assert.IsFalse(html.Contains("<script>alert", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<b>me", StringComparison.Ordinal));
        StringAssert.Contains(html, "<!doctype html>");
        StringAssert.Contains(html, ErdDiagram.MermaidScript);
        StringAssert.Contains(html, "<pre class=\"mermaid\">");
        StringAssert.Contains(html, DocumentationFiles.Marker);
    }
}
