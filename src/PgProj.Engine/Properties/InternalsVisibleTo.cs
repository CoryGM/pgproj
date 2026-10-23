using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PgProj.Engine.Tests")]
// The CLI is the reference shell; it still uses a few internal texts (ChangeListText, PublishTexts, the documentation's marker, the diagram's script), G-320.
[assembly: InternalsVisibleTo("pgproj")]
