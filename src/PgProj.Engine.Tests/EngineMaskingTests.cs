namespace PgProj.Engine.Tests;

/// <summary>
/// G-318: the engine masks every message it returns, never the deployment SQL (the user's decision of 2026-10-21 on #229), so a shell needs no masker of its own.
/// </summary>
[TestClass]
public sealed class EngineMaskingTests
{
    private const string Secret = "hunter2";

    [TestMethod]
    public async Task AStatusMessageIsMaskedByEveryCallThatReadsAProject()
    {
        // The loader names the missing pre-deploy script by its path, and this path holds a password.
        var fixture = new ProjectFixture().With("pgproj.json", $$"""{ "name": "fixture", "version": "1", "source": "./sql", "predeploy": "./password={{Secret}}.sql" }""");
        var engine = new PgProjEngine();
        var token = TestContext.CancellationToken;

        var build = await engine.BuildAsync(new BuildRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, token);
        var compare = await engine.CompareAsync(new CompareRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, TargetConnectionString = "Host=127.0.0.1;Port=1" }, token);
        var publish = await engine.PublishAsync(new PublishRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile, TargetConnectionString = "Host=127.0.0.1;Port=1" }, token);
        var document = await engine.DocumentAsync(new DocumentRequest { Files = fixture.InMemory(), ProjectFilePath = fixture.ProjectFile }, token);

        Assert.AreEqual(BuildStatus.ProjectInvalid, build.Status);
        foreach (var message in new[] { build.Message, compare.Message, publish.Message, publish.Compare.Message, document.Message })
        {
            StringAssert.Contains(message, "password=*****");
            Assert.DoesNotContain(Secret, message!);
        }

        Assert.DoesNotContain(Secret, build.ToJson());
        Assert.DoesNotContain(Secret, compare.ToJson());
    }

    [TestMethod]
    public void EveryMessageOfAPublishIsMaskedAndItsScriptIsNot()
    {
        var script = $"CREATE ROLE app LOGIN PASSWORD '{Secret}';\n";
        var item = new PublishItem(PublishNoticeKind.Superuser, $"CREATE ROLE app LOGIN PASSWORD '{Secret}'", Detail: $"password={Secret}");
        var notice = new PublishNotice(PublishNoticeKind.Superuser, $"Needs a superuser: CREATE ROLE app LOGIN PASSWORD '{Secret}'", [item]);
        var result = new PublishResult
        {
            Status = PublishStatus.ExecutionFailed,
            Message = $"failed with password={Secret}",
            Compare = new CompareResult { Message = $"password={Secret}", Diagnostics = [new EngineDiagnostic(EngineSeverity.Warning, $"postgresql://u:{Secret}@h/db")] },
            Parts = [new PublishPart { TransactionalScript = script, Script = script, AfterCommit = [new PublishStep($"step password={Secret}", script)] }],
            Warnings = [notice],
            Refusals = [notice],
            Script = script,
            Execution = new PublishExecution
            {
                Failure = new PublishFailure($"42601: password={Secret}", "42601", $"password={Secret}", null, $"CREATE ROLE app PASSWORD '{Secret}'", false, [$"step password={Secret}"]),
                FailureReport = [$"Publish failed: password={Secret}"],
            },
        };

        var masked = EngineMasking.Publish(result);

        Assert.AreEqual("failed with password=*****", masked.Message);
        Assert.AreEqual("password=*****", masked.Compare.Message);
        Assert.AreEqual("postgresql://u:*****@h/db", masked.Compare.Diagnostics[0].Message);
        foreach (var maskedNotice in masked.Warnings.Concat(masked.Refusals))
        {
            Assert.AreEqual("Needs a superuser: CREATE ROLE app LOGIN PASSWORD '*****'", maskedNotice.Text);
            Assert.AreEqual("CREATE ROLE app LOGIN PASSWORD '*****'", maskedNotice.Items[0].Text);
            Assert.AreEqual("password=*****", maskedNotice.Items[0].Detail);
        }

        var failure = masked.Execution!.Failure!;
        Assert.AreEqual("42601: password=*****", failure.Message);
        Assert.AreEqual("password=*****", failure.Detail);
        Assert.AreEqual("CREATE ROLE app PASSWORD '*****'", failure.Statement);
        Assert.AreEqual("step password=*****", failure.NotDone[0]);
        Assert.AreEqual("Publish failed: password=*****", masked.Execution.FailureReport[0]);

        // What is deployed, printed, and written is the SQL as it is.
        Assert.AreEqual(script, masked.Script);
        Assert.AreSame(result.Parts, masked.Parts);
        Assert.AreEqual(script, masked.Parts[0].Script);
        Assert.AreEqual(script, masked.Parts[0].AfterCommit[0].Sql);
    }

    [TestMethod]
    public void AnEventsMessageIsMaskedAndTheScriptItCarriesIsNot()
    {
        var seen = new List<EngineEvent>();
        var progress = EngineMasking.Progress(new ListProgress(seen))!;
        var script = $"ALTER ROLE app PASSWORD '{Secret}';";

        progress.Report(new EngineEvent(EngineEventKind.SourceConnected, Message: $"Connected with password={Secret}"));
        progress.Report(new EngineEvent(EngineEventKind.ScriptPlanned, Script: script));

        Assert.AreEqual("Connected with password=*****", seen[0].Message);
        Assert.AreEqual(script, seen[1].Script);
        Assert.AreSame(progress, EngineMasking.Progress(progress), "a progress is masked once");
    }

    [TestMethod]
    public void WhatInitAndDocumentSayIsMasked()
    {
        var init = EngineMasking.Init(new InitResult
        {
            Message = $"password={Secret}",
            Messages = [new InitMessage(EngineEventLevel.Warning, $"PASSWORD '{Secret}'")],
            Files = [new PgProj.Core.ScaffoldedFile("Schema/app/app.sql", $"CREATE ROLE app PASSWORD '{Secret}';")],
        });
        var document = EngineMasking.Document(new DocumentResult
        {
            Problems = [new DocProblem("app.t", DocSeverity.Error, "since-date", $"'password={Secret}' is not a date")],
        });

        Assert.AreEqual("password=*****", init.Message);
        Assert.AreEqual("PASSWORD '*****'", init.Messages[0].Text);
        Assert.Contains(Secret, init.Files[0].Content, "a scaffolded file is the database's SQL, as it is");
        Assert.AreEqual("'password=*****' is not a date", document.Problems[0].Message);
    }

    [TestMethod]
    public void AResultWithNothingToMaskIsTheSameObject()
    {
        var build = new BuildResult { Status = BuildStatus.Failed, Diagnostics = [new EngineDiagnostic(EngineSeverity.Error, "missing table 'orders'")] };
        var publish = new PublishResult { Message = "nothing secret", Warnings = [new PublishNotice(PublishNoticeKind.LockRewrite, "rewrites orders", [])] };

        Assert.AreSame(build, EngineMasking.Build(build));
        Assert.AreSame(publish, EngineMasking.Publish(publish));
    }

    private sealed class ListProgress(List<EngineEvent> seen) : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => seen.Add(value);
    }

    public TestContext TestContext { get; set; } = null!;
}
