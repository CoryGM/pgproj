using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PgProj.Parser.Tests")]
// The CLI's log writer masks secrets with the parser's redactor (G-143).
[assembly: InternalsVisibleTo("pgproj")]
[assembly: InternalsVisibleTo("PgProj.Engine")]
