# GitHub Copilot Documentation Instructions for pgproj

These instructions tell GitHub Copilot how to safely generate documentation for
this repository without breaking architectural invariants or changing behavior.

---

## 1. Documentation Sources

Document only behavior that exists in code. Read the relevant source first:

- `InitCommand.cs`, `BuildCommand.cs`, `DiffCommand.cs`, `PublishCommand.cs`, `DocumentCommand.cs` — CLI command behavior (options, help, what is printed and written)
- `src/PgProj.Engine/PgProjEngine*.cs` and the request and result records beside them — what each command does, its statuses, warnings, and refusals; `docs/library.md` describes them, and `LibraryExamplesTests` runs its examples, so the page is changed with them
- `Program.cs` — root command behavior
- `PgProjectLoader.cs` — `pgproj.json` behavior
- `ScriptGenerator.cs` — SQL generation behavior
- `DiffEngine.cs` — diff behavior

Copilot must not invent undocumented fields or behavior, or speculate about future features.

---

## 2. General Principles

Copilot may generate Markdown documentation (README sections, CLI docs, `pgproj.json` docs,
semantic model/diff engine/SQL generation overviews, developer onboarding guides), using clear
language, examples, usage instructions, and error conditions.

Copilot must not change code behavior while generating documentation, introduce new CLI commands
or options, change `pgproj.json` field names or meanings, or document a removed/deprecated
feature or a feature that does not exist.

---

## 3. Documentation Is Part of Done (binding)

Updating the documentation is not a follow-up: a change that adds or alters anything a user can see is not finished until the places that describe it are current. For a feature, a command, an option, an output, a message, or a supported or unsupported object kind, update in the same change:

- the CLI help in code: the command's description, every option's description, and its examples (and the root command's help when a command is added);
- `docs/commands.md`, the command reference: the command's section, its option list and examples (moved there from `README.md` at the user's request of
  2026-10-22); and `README.md` when the change is one a new user meets: installing, the first project, the three workflows, connections, the project file table,
  the command list;
- the notes in `docs/notes/`: the page for the object kind or behavior (a new topic gets a new page, listed in `docs/notes/README.md`), and `supported-objects.md` or `unmodeled-statements.md` when what is supported or not modeled changes;
- the documents that describe the feature (`documentation.md`, `pgproj.json.md`, the option list in `copilot-cli.md`), and `CHANGELOG.md` under `[Unreleased]`, written for a user.

Also correct any text the change makes false (a limit that was lifted, a count of commands, a statement that something is not modeled): search the Markdown and the help text for the old wording instead
of trusting memory. Extend the help tests for a new option or text. State in the gap issue's closing comment which documents were updated, or why one did not need it.

---

# End of Copilot Documentation Instructions
