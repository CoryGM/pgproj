namespace PgProj.Engine;

/// <summary>How a check of one document ended (G-313).</summary>
public enum DocumentCheckStatus
{
    /// <summary>The document was checked; <see cref="DocumentCheckResult.Diagnostics"/> holds what was found, errors included.</summary>
    Succeeded,

    /// <summary>There is no project file at the request's <see cref="DocumentCheckRequest.ProjectFilePath"/>.</summary>
    ProjectNotFound,

    /// <summary>The project file or settings are not valid, or a file they name is missing: <see cref="DocumentCheckResult.Message"/> says why.</summary>
    ProjectInvalid,

    /// <summary>The project does not read the document (not one of its schema, template, or deploy script files): nothing was checked.</summary>
    NotInProject,
}

/// <summary>What a document is to its project (G-313), which decides how it is checked.</summary>
public enum DocumentCheckKind
{
    /// <summary>Not one of the project's files.</summary>
    None,

    /// <summary>A schema file under the project's <c>source</c>.</summary>
    Schema,

    /// <summary>A file of the tenant template (G-290), read in the stand-in schema as a build reads it.</summary>
    TenantTemplate,

    /// <summary>A pre- or post-deployment script, or a file one includes with <c>:r</c>, the project's or a tenant's.</summary>
    DeployScript,
}

/// <summary>
/// The result of a check of one document (G-313): the diagnostics a build gives for that file without the semantic checks, which need the whole project.
/// For a schema or template file, the parser's (syntax errors with their ranges, statements and clauses pgproj does not model, a statement too deep, a
/// near-miss <c>@renamed-from</c>); for a deploy script, the build's check of it (syntax errors, and its use of the tenant variables). A missing table or a
/// cycle comes only from <see cref="PgProjEngine.BuildAsync"/>, so a shell keeps the two sets apart and replaces each when its own kind arrives. Every message
/// is masked.
/// </summary>
public sealed record DocumentCheckResult
{
    /// <summary>How the check ended: <see cref="DocumentCheckStatus.Succeeded"/> when the document was checked, whatever it found.</summary>
    public DocumentCheckStatus Status { get; init; }

    /// <summary>Why the project could not be read, for <see cref="DocumentCheckStatus.ProjectInvalid"/>.</summary>
    public string? Message { get; init; }

    /// <summary>The document's full path.</summary>
    public string? DocumentPath { get; init; }

    /// <summary>What the document is to its project, which decides how it was checked.</summary>
    public DocumentCheckKind Kind { get; init; }

    /// <summary>The version of the text checked: the request's, or the one the file source gave the file.</summary>
    public int? Version { get; init; }

    /// <summary>What was found, in the order a build reports it for this file.</summary>
    public IReadOnlyList<EngineDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>How long the check took.</summary>
    public long ElapsedMilliseconds { get; init; }
}
