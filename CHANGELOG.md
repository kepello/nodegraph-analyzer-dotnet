# Changelog

All notable changes to `@kepello/nodegraph-analyzer-dotnet`. Reconstructed from git history; format follows [Keep a Changelog](https://keepachangelog.com/).

## [0.10.0] — 2026-05-14

**Breaking — analyzer-config-consolidation (Fathom work row 0.1.2).** Reads its config slice from stdin (UTF-8 JSON, EOF-terminated) instead of `<repoRoot>/nodegraph-analyzer-<name>.config.json`. Imported helper `loadAnalyzerConfig` (gone from `@kepello/nodegraph-analysis/protocol`) replaced by `readAnalyzerConfigFromStdin()`. Per-analyzer config files at workspace root are no longer read — operators must move `include` / `exclude` / `includeComments` into the workspace `.fathom/fathom.config.json` under `analyzers.<name>` and delete the standalone file.

Subprocess contract: the orchestrator (`@kepello/nodegraph-analysis@^2.0.0`) writes `JSON.stringify(entry minus command)` to this analyzer's stdin and closes it before reading NDJSON from stdout. Standalone invocation must pipe a JSON object on stdin or the analyzer throws at startup with a clear error message.

Peer-dep bump: `@kepello/nodegraph-analysis@^2.0.0` (was `^0.18.1`). No other behavior changes.

## [0.9.1] — 2026-05-10

Bug fix: csproj and sln structural artifacts now set `contentHash` on their element (Fathom work-md row 2.2.25 — partial fix). Patch bump.

### Fixed

- **`Program.ProjectFiles.cs` `BuildCsprojArtifact` and `BuildSlnArtifact`** now set `contentHash` on the emitted project / solution element. Without it, the substrate's `upsert` couldn't compare incoming-vs-existing hashes for these elements — they reported as `superseded` on every re-ingest pass even when the source file was unchanged. Surfaced 2026-05-10 by the `ingestSummary` feature (Fathom 1.11.16): every back-to-back `fathom analyze` against the workspace meta-repo showed the 2 .csproj elements drifting. Fix: hash the artifact's raw text and assign it as the element's `contentHash` (same source as the artifact-level contentHash; one element per file means the hashes coincide). Matches the pattern Roslyn-emitted .cs elements already follow.

### Impact

Back-to-back runs against the Fathom workspace meta-repo: 2 csproj elements drop from the `superseded`-each-run list. No effect on artifact-level identity or per-run metric values.

## [0.9.0] — 2026-05-10

`.csproj` and `.sln` files are now first-class file types — claimed in `--discover` mode and emitted as structural artifacts in normal analyze mode (Fathom work-md row 1.11.15, Phase 1). Strictly additive — no changes to existing `.cs` analysis behavior. Phase 2 (MSBuildWorkspace integration for .cs files when a project is available) is a follow-up that ships as `0.10.0`.

### Added

- **`.csproj` discovery + structural artifact** — emits one artifact per `.csproj` with `language: "csproj"` and a single `project`-kind element. Element metadata: `sdk`, `targetFrameworks` (array; covers `<TargetFramework>` and `<TargetFrameworks>` multi-target form), `outputType`, `packageReferenceCount`, `projectReferenceCount`. Artifact-level edges:
  - `references` (subtype `package`) → `<package>@<version>` pseudo-target for each `<PackageReference>`.
  - `references` (subtype `project`) → resolved absolute path of each `<ProjectReference>` (relative paths resolved against the .csproj's own directory; backslash-to-forward-slash normalized for Windows-style includes).
- **`.sln` discovery + structural artifact** — emits one artifact per `.sln` with `language: "sln"` and a single `solution`-kind element. Element metadata: `formatVersion` (e.g., "12.00"), `projectCount`. Artifact-level edges:
  - `contains` → resolved absolute path of each project file referenced via the `Project(...)` lines. Solution-folder entries (no extension) are filtered out.
- **NuGet dependencies** — `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.3.0 + `Microsoft.Build.Locator` 1.7.8 added (currently unused; pre-positioned for Phase 2 workspace integration).
- **`Program.ProjectFiles.cs`** — new file housing `ProjectFileHelpers` static class with `BuildCsprojArtifact(content, filePath)` and `BuildSlnArtifact(content, filePath)`.

### Changed

- **`DiscoverCsFiles` renamed to `DiscoverFiles`** — now walks any extension in the new `FileExtensionsHolder.Analyzed` set (`.cs`, `.csproj`, `.sln`). Universal-skip extension check added (mirrors `UNIVERSAL_SKIP_EXTENSIONS` from `@kepello/nodegraph-analysis@0.18.2+`: `.dll`, `.pdb`).
- **`RunOutput`** — branches by extension: `.cs` files run through the existing Roslyn pipeline (parallel, unchanged); `.csproj` and `.sln` files run sequentially through the new XML/text parsers.

### Why

`fathom discover` previously reported `.csproj` and `.sln` files in the unclaimed-extensions hint. .NET project files ARE C# project structure — they belong in the dotnet analyzer's claim set. Surfaced 2026-05-10 by operator after `analyzer-skip-extensions` (Fathom 2.2.22) cleared the .dll/.pdb noise and made the .csproj/.sln gap visible.

### Tests

8 new test cases in `tests/ProjectFilesTests.cs`: csproj basic SDK project, PackageReferences as edges, ProjectReferences with relative-path resolution, multi-target framework parsing, malformed XML produces problems-bearing artifact; sln with projects produces contains edges, sln solution-folder entries filtered out, empty sln. 68 tests pass (was 60).

End-to-end smoke against the dotnet analyzer's own dir: 2 .csproj artifacts emit with correct metadata + edges (3 `Microsoft.*` PackageReferences for src; 5 xunit/Microsoft.* PackageReferences for tests).

### Phase 2 preview (`0.10.0`)

The Roslyn pipeline currently compiles each `.cs` file in isolation with only the System runtime as a reference (see `Program.cs` `DecomposeWithRoslyn` + the existing comment at the function header). Phase 2 will use the discovered `.csproj`/`.sln` files to build a real `MSBuildWorkspace`, giving every .cs file proper SemanticModel access with NuGet packages and ProjectReferences resolved. That's a behavior change to what `.cs` analysis emits (better resolution → potentially more/different edges) and warrants a separate ship.

## [0.8.0] — 2026-05-10

`--discover` CLI flag added (Fathom work-md row 1.11.13). Strictly additive — minor bump because it adds new public CLI behavior.

### Added

- **`--discover` flag** — when passed alongside `--path <root>`, the analyzer walks its inputs using its own per-analyzer config (`<root>/nodegraph-analyzer-dotnet.config.json`, same as normal analysis — `DiscoverCsFiles` already did this; the flag short-circuits before the Roslyn parse loop) and prints absolute file paths to stdout, one per line, then exits 0. No NDJSON, no analysis. Used by `fathom discover` (in `@kepello/fathom-cli@2.2.0+`) to render the analyzer-aware preview of what would be analyzed.

### Why

`fathom discover` previously walked the filesystem with universal skip-dirs only and reported the result as "files that would be analyzed." Misleading: each analyzer determines its own inputs via per-analyzer config + per-language extension filtering. The fix moves discovery into the analyzers (the only place that knows its own rules) and has fathom-cli aggregate the per-analyzer claim sets.

## [0.7.1] — 2026-05-10

Defensive backstop (Fathom work-md row 2.2.18). Strictly additive; peer-dep relax only.

### Changed

- **Edge emission now passes through a C# `DedupeEdges` helper** in `Program.cs` — collapses any per-source edge list to one edge per `(type, targetName)`, mirroring `dedupeEdges` in `@kepello/nodegraph-analysis/protocol@0.18.1` (TypeScript) and the equivalent Swift helper in `nodegraph-analyzer-swift@0.7.1`. The substrate's `edges_live_unique_*` UNIQUE invariant excludes `subtype` from the key, so two edges differing only in subtype collide at ingest. The hand-rolled `seen` Set inside `ExtractRelationships` was already correct (keys both `subtype:name` and `type:name`); this helper is the artifact-level backstop covering the `using`-directive imports + `contains` edges that didn't previously have explicit dedupe.
- **Peer-dep on `@kepello/nodegraph-analysis`** bumped to `^0.18.1`.

## [0.7.0] — 2026-05-10

Protocol-breaking refactor coordinated with `@kepello/nodegraph-analysis@0.17.0` (Fathom work-md row 2.7.4, decisions 1–10 in [.agents/plans/analysis-refactor.md](../../.agents/plans/analysis-refactor.md)).

### Removed

- **`--mode` / `--include` / `--exclude` / `--include-comments` CLI flags** — orchestrator no longer passes any. Per-analyzer tuning lives in `<repoRoot>/nodegraph-analyzer-dotnet.config.json` (`{ include?, exclude?, includeComments? }`).
- **mode parameter on `BuildArtifact` / `DecomposeWithRoslyn` / `ExtractRelationships`** — single-path full-depth analysis only. Always-emitted edges that were previously gated on `isFullMode`: instantiates, overrides, delegates, generic constraints, decorators, partial.

### Added

- New `LoadAnalyzerConfig` reading `<repoRoot>/nodegraph-analyzer-dotnet.config.json`. Universal skip-dirs (mirrors the JS-side `UNIVERSAL_SKIP_DIRS`, plus `bin` / `.vs`) baked into `WalkDirectory`.

### Changed

- CLI invocation contract: `nodegraph-analyzer-dotnet --path <repoRoot>`.
- Peer-dep on `@kepello/nodegraph-analysis` bumped to `^0.17.0`.

## [0.6.2] — 2026-05-02

- Peer-bump to engine `^0.10.0` (engine trimmed its main barrel; engine internals moved to the `/engine` subpath). No analyzer-side behaviour change; sync release alongside HTML / CSS / markdown / TS / Swift coordinated publish.

## [0.6.1] — 2026-05-02

- Peer-bump to engine `^0.9.0`. No analyzer-side behaviour change; sync release alongside HTML / CSS / markdown / TS / Swift coordinated publish.

## [0.6.0] — 2026-05-02

- Emit `codeSmells.magicNumberCount` per element. Roslyn `LiteralExpressionSyntax` walk; allowlist `|v| ∈ {0,1,2}`; skips `const` / `readonly` `FieldDeclaration`s, `const` `LocalDeclarationStatement`s, and `EnumMember` initializers.
- Peer-bump to engine `^0.5.0`.

## [0.5.0] — 2026-05-02

- Emit documentation observation per element via Roslyn `DocumentationCommentTrivia`: `hasDocComment` (`///` + `/** */`), `docCommentLineCount`, `commentTagCounts` (TODO/FIXME/HACK/XXX/NOTE word-boundary scan).

## [0.4.0] — 2026-05-01

- Per-method scalars (slice 3, .NET portion): all ten complexity / Halstead inputs. Cognitive complexity is a clean-room Sonar 2017 port.
- Intra-class `accessesField` / `callsMethod` edges (LCOM4 input).

## [0.3.0] — 2026-05-01

- Migrate to the `AnalyzerArtifact` wire shape (slice 1 of the wire-format change). The analyzer no longer emits the BDS-specific format inherited from its `bds-v3` origin.

## [0.2.0] — 2026-05-01

- Peer-bump for the 0.4.0 wire contract.

## [0.1.0] — 2026-05-01

- Initial release: .NET / C# analyzer subprocess relocated from `bds-v3`. Roslyn-based; ships a managed binary plus a bash shim invoking `dotnet`.
