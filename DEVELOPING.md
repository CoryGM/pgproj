# Developing pgproj

This guide explains how to set up a development environment, understand the architecture, run tests, and work effectively within the pgproj codebase.

---

## 1. Prerequisites

You will need:

- .NET 10 SDK or later  
- PostgreSQL 14, 15, 16, 17, or 18 (local or remote). These are the versions the Docker suite is run against (G-120, G-269). The parser is
  libpg_query with PostgreSQL 18's grammar (NuGet `Npgquery`, native library only), which parses every version's SQL. The PostgreSQL 18
  schema features (`NOT ENFORCED`, temporal keys, `VIRTUAL` columns, `NOT NULL ... NOT VALID`; G-121) are supported, and a project that
  uses one can only be published to a PostgreSQL 18 server.  
- Visual Studio 2022 or JetBrains Rider  
- Git  
- Optional: Docker for running PostgreSQL locally  

---

## 2. Repository Structure

The repository is organized into clearly separated subsystems:

- PgProj.Parser — SQL parsing via PgQuery  
- PgProj.Semantics — semantic model and binding passes  
- PgProj.Dependencies — dependency graph analysis  
- PgProj.Diff — diff engine comparing semantic models  
- PgProj.Deploy — SQL generation and deployment operations  
- PgProj.Engine — the pipeline as a library (G-309): the project's files read through `IProjectFileSource`, `PgProjEngine.BuildAsync`, `CompareAsync`, `PublishAsync`, `ScaffoldAsync`, `DocumentAsync`; no console, no command-line parsing, no exit codes, no files written  
- PgProj.Cli — command-line interface (init, build, diff, publish, document); the reference shell over the engine (docs/shells.md)  
- src/*.Tests — unit and integration tests  
- docs/notes/ — user documentation, one page per topic (linked from README.md)  
- docs/library.md — using `PgProj.Engine` as a library (its examples are compiled and run by `LibraryExamplesTests`)  
- docs/shells.md — adding a front end (a shell) over the engine; the CLI is the reference one  
- samples/ — example projects: `MultiSchema` (several schemas, pre/post-deploy scripts) and `SchemaPerTenant` (a schema per tenant)  
- tools/ — `test-all-versions` (the Docker suite on every supported PostgreSQL version), `diffqa` (the differential QA harness), `engine-parity` (compares the CLI's output with a baseline build, G-309), `CleanDirectories.ps1` (deletes every `bin` and `obj` folder)  
- packaging/ — the release packaging scripts (section 13)  
- docs/ai/copilot-*.md — the governance rules for AI-assisted work (binding for Copilot, and for Claude Code via CLAUDE.md)
- CLAUDE.md — entry point that makes the governance files binding for Claude Code sessions (it holds no rules of its own)

Each subsystem has strict architectural boundaries.

---

## 3. Building the Solution

Run:

dotnet build

---

## 4. Running the CLI

From the CLI output directory:

./pgproj build --project ./pgproj.json

Adjust paths as needed for your build configuration.

---

## 5. pgproj.json

This file defines:

- SQL input paths  
- PostgreSQL connection strings (the target diff and publish use, the source init scaffolded from)  
- Optional schema filters  
- Optional output paths  

Example:

{
  "name": "mydb",
  "version": "1.0",
  "schemas": ["public"],
  "source": "./schema",
  "targetConnectionString": "Host=localhost;Database=mydb;Username=postgres",
  "predeploy": "./scripts/pre.sql",
  "postdeploy": "./scripts/post.sql"
}

---

## 6. Understanding the Pipeline

pgproj is built as a layered pipeline:

### 6.1 Parser (PgProj.Parser)
- Uses PgQuery to parse SQL into AST nodes.

### 6.2 Semantic Passes (PgProj.Semantics)
- Bind names, types, and references.
- Build a fully bound DatabaseModel.
- Produce semantic diagnostics.

### 6.3 Dependency Graph (PgProj.Dependencies)
- Analyze object dependencies.
- Order operations.
- Detect cycles.

### 6.4 Diff Engine (PgProj.Diff)
- Compare two semantic models.
- Produce added/removed/modified changes.

### 6.5 Deployment (PgProj.Deploy)
- Convert changes into SQL operations.
- Generate scripts via ScriptGenerator.

### 6.6 Engine (PgProj.Engine)
- The pipeline as a library, and the only way the commands run it. `PgProjEngine.BuildAsync` (what `build` does), `CompareAsync` (`diff`: the live read, the server renderings, tenants, source mode in memory), `PublishAsync` (`publish`: the plan, the checks as typed `PublishNotice` warnings and refusals, and with `PublishMode.Execute` the run, in transactions, with `--concurrently`, `--comments-only`, and tenants), `ScaffoldAsync` (`init`), and `DocumentAsync` (`document`).
- A run reads the project's files once, through `IProjectFileSource` (disk, memory, or an editor's unsaved buffers over disk), into a snapshot, and returns a result record: a `Status` for every problem a user can cause, an exception only for a defect or a cancellation. It never prints, writes a file, parses a command line, or exits.
- Hooks let a shell act between steps (`AfterBuild`, `AfterPlan`, `AfterScaffold`); progress is `IProgress<EngineEvent>`; `BuildResult.ToJson` and `CompareResult.ToJson` are the JSON document `build --format json` and `diff --format json` print.
- Its public surface is guarded by `PublicApiTests` (`src/PgProj.Engine.Tests/PublicApi.txt`): change it on purpose, with the owner's agreement and a CHANGELOG entry. How to call it: [`docs/library.md`](docs/library.md) (its examples are run by `LibraryExamplesTests`).

### 6.7 Shells: the CLI (PgProj.Cli)
- A shell turns a person's or a program's request into an engine request, and the result into what its world shows. The CLI is the first shell and the reference: it parses the options of init, build, diff, publish, and document (`--project`, `--source-connection`, `--target-connection`, `--output`, `--execute`, `--dry-run`, ...), reads the connection variables, calls the engine, prints and writes what it returns, and maps statuses to exit codes. Its output is fixed by `tools/engine-parity/parity.sh`.
- A new shell (an editor extension, a language server, a web API) follows [`docs/shells.md`](docs/shells.md) and holds no pipeline logic.

---

## 7. Running Tests

Run:

dotnet test

Tests live under src/*.Tests.

`src/PgProj.Integration.Tests` runs the publish/rollback/re-diff workflow against a disposable PostgreSQL
container (Testcontainers), so **Docker must be running** for `dotnet test` to pass. Without Docker those
tests fail with "Failed to connect to Docker endpoint"; the other test projects are unaffected. Run the
integration tests after any change to `LiveDatabaseModelLoader` or deployment SQL generation.

The container is `postgres:18-alpine`, the newest supported release, unless `PGPROJ_TEST_POSTGRES_IMAGE` names another image. 18 is the
baseline: test counts are measured on it. The supported versions are 14, 15, 16, 17, and 18 (G-269), and a change to the live loader or to
deployment SQL must pass the whole suite on every one of them (G-120): the older ones are where what differs on them is checked (an older server
refusing a newer construct, a catalog that reads differently). When a new major version is supported, it becomes the default and the baseline.
`ServerVersionTests` fails if the server is not the major version the image names.

**Run every version with the script.** It builds, runs the unit tests once, then the integration suite per version in parallel, at most three at a
time (each starts its own container), with a timeout per version, and prints a line per version; logs go to `artifacts/test-runs/<UTC time>/`:

    tools/test-all-versions.sh                        # bash: 14 to 18
    tools/test-all-versions.sh 15 18                  # only those
    MAX_PARALLEL=2 TIMEOUT_MINUTES=45 tools/test-all-versions.sh

    tools/test-all-versions.ps1                       # PowerShell 7
    tools/test-all-versions.ps1 -Versions 15, 18 -MaxParallel 2

The runs share the built test DLLs, so do not rebuild while one is going (`NO_BUILD=1` or `-NoBuild` skips the script's own build). One version
by hand: `PGPROJ_TEST_POSTGRES_IMAGE=postgres:15-alpine dotnet test src/PgProj.Integration.Tests`. CI runs the five as a parallel matrix on every
pull request.

**A test for one version range** says so with `[PostgreSqlVersion(Minimum = 18)]` or `[PostgreSqlVersion(Maximum = 17)]`
(`src/PgProj.Integration.Tests/PostgreSqlVersionAttribute.cs`); on any other version it is reported as skipped with the reason. The version is
read from the image tag. A test that checks different results on different versions branches on `ServerMajorAsync()` in its body instead.

**A test that diffs a live model and a project model itself** (rather than through the CLI) uses `EndToEndSupport.DiffAsPublishedAsync`, which has the
server render the project's views first, as `diff` and `publish` do; comparing raw text depends on how a version prints a view.

**Timed tests** (G-321) are in the category `Performance`: each compares two measurements taken in the same run (a size against eight times it, or one
way against another) with a wide margin, so it does not depend on the machine, and runs alone (`[DoNotParallelize]`). They guard the performance fixes of
G-310, G-311, G-313, G-314, G-315, and G-322. `dotnet test --filter "TestCategory=Performance"` runs only them; `--filter "TestCategory!=Performance"` leaves them out.

All non-trivial changes must include tests.

To find gaps rather than test a change, use the differential QA harness in [`tools/diffqa`](tools/diffqa/README.md). It loads PostgreSQL's own
regression SQL into a database, round-trips it through `init`/`publish` and through its `pg_dump`, and compares the dumps. It writes a triage
report grouping every failure and difference. The real-world sweep, which runs pgproj over the schemas of GitLab, Synapse, MusicBrainz, and other projects (fetched, not stored here), is [`tools/diffqa/realworld`](tools/diffqa/realworld/README.md).

---

## 8. Debugging Tips

- Use pgproj build to validate SQL and inspect diagnostics (`--format json` for the result as the engine returns it).  
- Use pgproj diff to compare project vs live database.  
- Use pgproj publish --dry-run to preview SQL.  
- Use pgproj publish --output ./deploy.sql to save scripts.  
- Use --target-connection (or PGPROJ_TARGET_CONNECTION) to override targetConnectionString in pgproj.json.

---

## 9. Adding New SQL Features

When adding support for new PostgreSQL features:

1. Extend parser mappings.  
2. Extend semantic model types.  
3. Add or update semantic passes.  
4. Update dependency extraction.  
5. Update diff logic.  
6. Update SQL generation, and in `PgProj.Engine` whatever the feature adds to a command's run: a check before `publish` runs is a `PublishNotice` (a new
   `PublishNoticeKind` at the end of the enum) found in the engine, never in a shell, and a statement only a superuser can run goes on `SuperuserStatements`.  
7. Update `LiveDatabaseModelLoader` and its `init` scaffold output so both `diff`/`publish` (reading a live
   database) and `init` (scaffolding a new project from one) cover the feature — see `copilot-instructions.md`
   §1, "Live Database Loader / Scaffold", for the full rule and what counts as done.
8. Add tests.
9. Update documentation.

Follow the Copilot governance files to avoid breaking invariants.

---

## 10. Development Rules

To keep the system stable:

- Do not break architectural invariants.  
- Do not change public API shapes without approval; the engine's (`PublicApi.txt`) and its JSON document are guarded by tests.  
- Do not put pipeline logic in a shell (the CLI or any other): it goes in the engine, so every shell has it.  
- Do not change diff semantics.  
- Do not change SQL generation semantics.  
- Do not change CLI command or option semantics.  
- Do not change pgproj.json field names or meanings.

---

## 11. Using GitHub Copilot

Copilot may:

- Fix bugs  
- Improve readability  
- Improve documentation  
- Add tests  
- Suggest safe refactorings  

Copilot must not:

- Change architecture  
- Change semantic meaning  
- Change CLI behavior  
- Change pgproj.json semantics  

See the copilot-*.md files in `docs/ai/` for full rules. Claude Code sessions follow the same rules via `CLAUDE.md`.

---

## 12. Asking Questions

If you are unsure about:

- whether a change is safe  
- how a subsystem behaves  
- how to extend the pipeline  

please:

- Open an issue  
- Start a discussion  
- Consult the Copilot governance files  

---

## 13. Releasing (G-81)

**Platforms.** `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, and `osx-arm64`. There is no `osx-x64`: the `Npgquery` package
that carries libpg_query has no Intel Mac library. The projects are not tied to a platform: `dotnet build` and `dotnet test` work on
Linux, Windows, and macOS, and the build output carries libpg_query for every platform under `runtimes/<rid>/native` (`PgProj.Parser`
copies them; `PgQueryInterop` loads the one for the running platform). A `dotnet publish -r <rid>` keeps only that platform's library.

**Versions.** The version is the git tag `vX.Y.Z` (MinVer, `src/Identity.props`, which the CLI and the engine's package share, so one tag versions both). An untagged commit builds as a pre-release of the next
version, for example `0.1.0-preview.0.181`; `pgproj --version` prints it (System.CommandLine's default option on the root command;
`PgProj.Cli.Tests/VersionOptionTests.cs` runs it, G-167). Do not set a version by hand.

**Cutting a release.**

1. Move the `## [Unreleased]` notes of `CHANGELOG.md` under a `## [X.Y.Z] - YYYY-MM-DD` heading (they become the release notes) and
   commit.
2. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`. A tag with a suffix (`v0.2.0-rc.1`) is published as a pre-release; when `CHANGELOG.md`
   has no section for it, its notes are the `[Unreleased]` section (`packaging/release-notes.sh`), so step 1 waits for the final version.
3. The `Release` workflow (`.github/workflows/release.yml`) builds Linux, then Windows, then macOS, installs and runs every package,
   and creates the GitHub Release with the archives, `.deb`, `.rpm` (signed), `.msi`, `SHA256SUMS.txt` and its signature, the Linux signing key, and a
   CycloneDX SBOM. Then it rebuilds the signed apt and dnf repositories from the releases and publishes them to GitHub Pages (job `repository`, below,
   Signing). It also packs the engine's NuGet
   package, `PgProj.Engine` (G-319), verifies it on Linux, Windows, and macOS with a program that uses it (`packaging/nuget/verify-package.sh`), attaches the
   `.nupkg` and `.snupkg` to the release, and pushes them to nuget.org last (job `nuget-publish`), only when the repository variable `NUGET_PUBLISH` is `true`
   (set once the Trusted Publishing policy exists; without it the release is made and nuget.org is skipped). The CLI as a .NET tool, the package
   `pgproj` (G-323), goes the same way beside it: packed, installed with `dotnet tool install` and smoke-tested on Linux, Windows, and macOS
   (`packaging/nuget/verify-tool.sh`), attached, and pushed. Running the workflow by hand (Actions, Run workflow) is a dry
   run: it builds and verifies everything, the package included, and keeps the files as workflow artifacts; nothing is published.

**The NuGet package.** One package, `PgProj.Engine`: the engine and the six assemblies it is built on (its project references are `PrivateAssets="all"` and
packed into `lib/net10.0` by the `PackInnerAssemblies` target; their `.pdb` files go to the `.snupkg`), `libpg_query` for the five runtimes under
`runtimes/<rid>/native`, a dependency on Npgsql, and `buildTransitive/PgProj.Engine.targets`, which fails a consumer's build for any other runtime
(`PGPROJ001`). `PackageRuntimesTests` checks that the parser's libraries, the package's, the build check, and the package readme name the same runtimes. It is
pushed with nuget.org's **Trusted Publishing**: the policy on the `CoryGM` nuget.org account trusts `release.yml` of `CoryGM/pgproj`, and the job exchanges
GitHub's OIDC token for a one-hour key (`NuGet/login`), so no API key is stored. The package is not author-signed; nuget.org signs every package it publishes.
A project that references the engine's project (the tests, the CLI) references the inner projects it uses itself.

**The .NET tool.** `pgproj` (`dotnet tool install --global pgproj`) is `src/PgProj.Cli` packed with `PackAsTool` (its readme is `src/PgProj.Cli/README.md`):
framework-dependent, so it needs the .NET 10 runtime, with every assembly it uses and `libpg_query` for the five runtimes under
`tools/net10.0/any/runtimes/<rid>/native` (a build without a runtime identifier keeps them all). It is pushed by the same Trusted Publishing policy, whose
scope must allow the package `pgproj` as well as `PgProj.*`. On a platform without a library (an Intel Mac, a musl Linux) it installs, and every file
reports `Unable to load libpg_query` (`docs/troubleshooting.md`). Every public member of the engine has an XML comment, which the package carries for IntelliSense: a public member without one fails the build (CS1591 is an error in `PgProj.Engine.csproj`).

`CI` (`.github/workflows/ci.yml`) builds and runs the unit tests on Linux, Windows, and macOS, and the Docker integration suite on
PostgreSQL 14, 15, 16, 17, and 18, for every push to `main` and every pull request.

`Prune workflow runs` (`.github/workflows/prune-runs.yml`) runs every Monday, and by hand, and keeps the 10 most recent completed runs of each workflow, deleting
the older ones with their logs and artifacts (including the files a `Release` dry run keeps). A run that is still going is not touched.

**Building the packages locally.** These are the commands the workflow runs (version without the `v`):

```sh
packaging/linux/build-packages.sh linux-x64 0.1.0 dist      # .tar.gz, .deb, .rpm (nfpm, or Docker for its image)
packaging/linux/verify-packages.sh dist 0.1.0 linux-x64     # installs each in a clean container, runs it, and connects it to a PostgreSQL it starts (Docker)
packaging/linux/signing-key.sh signing                      # the key from LINUX_SIGNING_KEY, or else a throwaway one (Docker)
packaging/linux/sign-rpms.sh dist signing                   # signs and checks the .rpm files
packaging/linux/build-repository.sh site signing stable/ testing/   # the apt and dnf repositories from two folders of packages
packaging/linux/verify-repository.sh site stable 0.1.0 linux-x64   # installs from them with apt and dnf in clean containers
packaging/publish-unix.sh osx-arm64 0.1.0 dist              # run on a Mac: it signs ad hoc with codesign
packaging/smoke-test.sh path/to/pgproj 0.1.0                # starts pgproj and builds packaging/smoke
dotnet pack src/PgProj.Engine/PgProj.Engine.csproj -c Release -o nupkg
packaging/nuget/verify-package.sh nupkg 0.1.0               # the package's contents; a program using it built, published, and run
dotnet pack src/PgProj.Cli/PgProj.Cli.csproj -c Release -o nupkg
packaging/nuget/verify-tool.sh nupkg 0.1.0                  # the tool's contents; installed with dotnet tool install, and smoke-tested
```

```powershell
dotnet tool install --global wix --version 5.0.2
packaging/windows/build-packages.ps1 -Rid win-x64 -Version 0.1.0 -Output dist   # .zip and .msi
```

Build the Linux packages on Linux (a Windows file system loses the executable bit that the `.tar.gz` needs).

**Signing.**

- *Windows* (G-333): the workflow signs `pgproj.exe` and `PgProj.*.dll` before they are zipped and packed into the MSI, then the MSI, with **Azure
  Artifact Signing** (account `pgprojsigning` in East US, certificate profile `pgprojrelease`, subject `CN=Cory Maass`), timestamped by
  `http://timestamp.acs.microsoft.com` (the service's certificates last about three days), and checks each with `signtool verify /pa`. Nothing secret is
  stored: the `windows` job runs in the environment `release` (Settings, Environments; it allows tags `v*` and the branch `main`, so a dry run signs
  too), whose secrets `AZURE_CLIENT_ID` and `AZURE_TENANT_ID` name an app registration with a federated credential for
  `repo:CoryGM@1860202/pgproj@1431014955:environment:release` and the Artifact Signing Certificate Profile Signer role on the profile. (The repository
  uses GitHub's immutable OIDC subject, which carries the owner's and the repository's IDs, so a recreated repository gets a new ID and the credential
  must be changed to it.) signtool's plugin
  (`Microsoft.ArtifactSigning.Client`, its version and SHA256 pinned in `release.yml`) logs in as that app with GitHub's OIDC token; Azure accepts one for
  about five minutes, so `build-packages.ps1` fetches a new one before each signing. No `az login` and no Azure subscription is involved. Without the
  secrets (a fork) the files are built unsigned. A local `build-packages.ps1` builds unsigned files.
- *macOS:* the files are ad-hoc signed only (Apple silicon will not run an unsigned executable). They are not signed with a Developer
  ID and not notarized (no Apple Developer account), so a browser download is quarantined (`README.md`, Installing).
- *Linux* (G-331): one OpenPGP key, `packaging/linux/pgproj-signing-key.asc` (the public half; the fingerprint is checked against the secret), signs the
  `.rpm` files, `SHA256SUMS.txt` (`SHA256SUMS.txt.asc`, which covers every file of a release), and the apt and dnf repositories' metadata. The `.deb` files
  are not signed themselves: apt checks the repository they come from. The job `linux-signing` signs the `.rpm` files and, with the release key, replaces
  the unsigned ones in the build's artifacts; it also builds the repositories from that build and installs from them with apt (Debian 12, Ubuntu 24.04)
  and dnf (Fedora) on amd64 and arm64. Without the secrets `LINUX_SIGNING_KEY` and `LINUX_SIGNING_PASSPHRASE` it does all of that with a throwaway key,
  and nothing is released signed or published.
  - *The repositories* (job `repository`, on a tag): GitHub Pages serves `https://corygm.github.io/pgproj` (Settings, Pages, Source: GitHub Actions; the
    `github-pages` environment allows tags `v*`). Each tag rebuilds the whole site from the GitHub Releases (`packaging/linux/collect-releases.sh`):
    `stable` holds the three newest releases, and `testing` the newest pre-release when it is newer than the newest release, or else the newest release.
    Pages serves at most 1 GB, and a version's four packages are about 160 MB. An older `.rpm` that was released unsigned is signed as it goes in.
  - *Making the key* (once, on a trusted machine; Git for Windows includes `gpg`):

    ```sh
    gpg --quick-generate-key "pgproj release signing <1860202+CoryGM@users.noreply.github.com>" rsa4096 sign 2y
    gpg --list-secret-keys --keyid-format long                                   # the fingerprint
    gpg --armor --export <fingerprint> > packaging/linux/pgproj-signing-key.asc  # commit this
    gpg --armor --export-secret-keys <fingerprint> | gh secret set LINUX_SIGNING_KEY -R CoryGM/pgproj
    gh secret set LINUX_SIGNING_PASSPHRASE -R CoryGM/pgproj                      # asks for the passphrase
    ```

    Keep a backup of the secret key offline. Before it expires, extend it (`gpg --quick-set-expire <fingerprint> 2y`), export the public key again, and
    update both the file and the secret: users import the key again from `pgproj.asc`.

**Provenance.** The workflow attests build provenance (`gh attestation verify`) when the repository is public; the step is skipped while
it is private, because attestations for private repositories need a plan this project does not have.

**Tools.** MinVer (versions), nfpm (`.deb`, `.rpm`; container image `ghcr.io/goreleaser/nfpm`), WiX 5.0.2 (MSI), CycloneDX (SBOM), `NuGet/login` (Trusted
Publishing), GnuPG, `rpmsign` and `createrepo_c` (in a Fedora container) and `apt-ftparchive` (in a Debian one) for the Linux signing and repositories, and
`actions/upload-pages-artifact` and `actions/deploy-pages` (GitHub Pages). WiX 6
and later make the person installing it accept a fee-related EULA (`wix eula accept`), so 5.0.2 is pinned deliberately.

---

Happy developing!