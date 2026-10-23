using System.Diagnostics;
using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

public sealed partial class PgProjEngine
{
    /// <summary>
    /// Checks one document of a project on its own (G-313), for an editor that shows a syntax error as it is typed: only <c>pgproj.json</c> and the deploy
    /// scripts are read, never the other SQL files, so it answers in milliseconds on a project of any size. A schema or template file gives the parser's
    /// diagnostics a build gives for it, parsed as the build parses it (the project's default schema, or the template's stand-in); a deploy script or a file
    /// it includes, the build's check of that file. The semantic checks need the whole project: they come from <see cref="BuildAsync"/>. With
    /// <see cref="DocumentCheckRequest.ParseCache"/> the parse is held, so the build that follows does not parse the text again. A project that is missing
    /// or not valid, or a file it does not read, is a <see cref="DocumentCheckStatus"/>, not an exception. Every message is masked.
    /// </summary>
    public Task<DocumentCheckResult> CheckDocumentAsync(DocumentCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);
        if (request.ProjectFilePath is not null && request.Settings is not null)
        {
            throw new ArgumentException("A request names the project by its file (ProjectFilePath) or by its Settings, not both.", nameof(request));
        }

        if (request.ProjectFilePath is null && request.Settings is null)
        {
            throw new ArgumentException("A request names the project by its file (ProjectFilePath) or by its Settings, and by one of them.", nameof(request));
        }

        if (request.Settings is not null && string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            throw new ArgumentException("A request with Settings needs the RootDirectory its relative paths are resolved against.", nameof(request));
        }

        try
        {
            return Task.FromResult(EngineMasking.DocumentCheck(CheckDocument(request, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<DocumentCheckResult>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<DocumentCheckResult>(exception);
        }
    }

    private static DocumentCheckResult CheckDocument(DocumentCheckRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var documentPath = Path.GetFullPath(request.DocumentPath);

        PgProject project;
        try
        {
            var loader = new PgProjectLoader();
            project = request.Settings is not null
                ? loader.Load(request.Files, request.Settings, request.RootDirectory!)
                : loader.Load(request.Files, request.ProjectFilePath!);
        }
        catch (FileNotFoundException)
        {
            return new DocumentCheckResult { Status = DocumentCheckStatus.ProjectNotFound, DocumentPath = documentPath, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };
        }
        catch (PgProjectValidationException exception)
        {
            return new DocumentCheckResult { Status = DocumentCheckStatus.ProjectInvalid, Message = exception.Message, DocumentPath = documentPath, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };
        }

        cancellationToken.ThrowIfCancellationRequested();

        // A path is the project's file when the file source says it is: exactly on a case-sensitive file system, ignoring case on another (G-212). The
        // project's own spelling of the path is used from here on, so the diagnostics and the cache's key are the build's.
        var comparer = request.Files.IsCaseSensitive(Path.GetDirectoryName(documentPath) ?? documentPath) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        bool IsDocument(string path) => comparer.Equals(Path.GetFullPath(path), documentPath);
        var defaultSchema = project.HasConfiguredSearchPath ? project.DefaultSchema : DefaultSchema.Name;
        var version = request.Version ?? (request.Text is null ? request.Files.Version(documentPath) : null);

        DocumentCheckResult Checked(DocumentCheckKind kind, string path, IEnumerable<SemanticDiagnostic> diagnostics) => new()
        {
            Status = DocumentCheckStatus.Succeeded,
            DocumentPath = path,
            Kind = kind,
            Version = version,
            Diagnostics = [.. diagnostics.Select(EngineDiagnostic.From)],
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };

        if (project.SqlFiles.FirstOrDefault(IsDocument) is { } schemaFile)
        {
            return Checked(DocumentCheckKind.Schema, schemaFile, ParserDiagnostics(schemaFile, Parse(schemaFile, defaultSchema)));
        }

        if (project.Tenants is { } tenants && tenants.TemplateFiles.FirstOrDefault(IsDocument) is { } templateFile)
        {
            return Checked(DocumentCheckKind.TenantTemplate, templateFile, ParserDiagnostics(templateFile, Parse(templateFile, tenants.StandInSchema)));
        }

        if (project.PreDeployScriptFiles.Concat(project.PostDeployScriptFiles).FirstOrDefault(file => IsDocument(file.FilePath)) is { } script)
        {
            var file = script with { Text = request.Text ?? script.Text };
            var tree = Parse(file.FilePath, defaultSchema, file.Text);
            return Checked(DocumentCheckKind.DeployScript, file.FilePath, [.. DeployScriptCheck.SyntaxErrors(file, tree), .. DeployScriptCheck.ProjectVariables(file) is { } problem ? [problem] : Array.Empty<SemanticDiagnostic>()]);
        }

        if (project.Tenants is { } scriptTenants
            && (scriptTenants.PreDeploy?.Files ?? []).Concat(scriptTenants.PostDeploy?.Files ?? []).FirstOrDefault(file => IsDocument(file.FilePath)) is { } tenantScript)
        {
            var file = tenantScript with { Text = request.Text ?? tenantScript.Text };
            return Checked(DocumentCheckKind.DeployScript, file.FilePath, DeployScriptCheck.TenantScript(file, scriptTenants, new PgQueryParser(defaultSchema)));
        }

        return new DocumentCheckResult { Status = DocumentCheckStatus.NotInProject, DocumentPath = documentPath, Version = version, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };

        SqlSyntaxTree Parse(string path, string schema, string? text = null)
        {
            text ??= request.Text ?? request.Files.ReadAllText(path);
            var parser = new PgQueryParser(schema);
            return request.ParseCache?.GetOrParse(path, schema, text, parser.Parse) ?? parser.Parse(text);
        }
    }

    /// <summary>A file's parser diagnostics as a build reports them (the semantic model's <c>DeclarationPass</c> maps them the same way).</summary>
    private static IEnumerable<SemanticDiagnostic> ParserDiagnostics(string path, SqlSyntaxTree tree)
        => tree.Diagnostics.Select(diagnostic => new SemanticDiagnostic(
            diagnostic.Severity switch
            {
                SqlDiagnosticSeverity.Error => SemanticDiagnosticSeverity.Error,
                SqlDiagnosticSeverity.Warning => SemanticDiagnosticSeverity.Warning,
                _ => SemanticDiagnosticSeverity.Info,
            },
            diagnostic.Message,
            SemanticLocation.FromSql(path, diagnostic),
            diagnostic.Source));
}
