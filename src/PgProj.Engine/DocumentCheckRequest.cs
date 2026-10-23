using PgProj.Core;

namespace PgProj.Engine;

/// <summary>
/// What <see cref="PgProjEngine.CheckDocumentAsync"/> checks (G-313): one file of a project, named by <see cref="DocumentPath"/>, with the project named as
/// <see cref="BuildRequest"/> names it (its <c>pgproj.json</c> in <see cref="Files"/>, or its <see cref="Settings"/> and <see cref="RootDirectory"/>).
/// </summary>
public sealed record DocumentCheckRequest
{
    /// <summary>Where the project's files are read from. The disk by default; an editor's <see cref="OverlayFileSource"/> gives its unsaved buffers.</summary>
    public IProjectFileSource Files { get; init; } = DiskFileSource.Instance;

    /// <summary>The path of the project file in <see cref="Files"/>; relative paths are resolved against the current directory.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>The project's settings, for a caller with no <c>pgproj.json</c>; validated as the file is. Needs <see cref="RootDirectory"/>.</summary>
    public ProjectSettings? Settings { get; init; }

    /// <summary>The directory relative paths in <see cref="Settings"/> are resolved against.</summary>
    public string? RootDirectory { get; init; }

    /// <summary>The file to check; a relative path is resolved against the current directory.</summary>
    public required string DocumentPath { get; init; }

    /// <summary>The text to check, an editor's buffer; null reads the file from <see cref="Files"/>.</summary>
    public string? Text { get; init; }

    /// <summary>The version of <see cref="Text"/>, given back in the result; null takes the version <see cref="Files"/> gives the file.</summary>
    public int? Version { get; init; }

    /// <summary>The cache of the project's builds (G-311): the check's parse is held in it, so the build that follows does not parse the text again.</summary>
    public ParseCache? ParseCache { get; init; }
}
