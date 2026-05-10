# Changelog

All notable changes to `@kepello/nodegraph-analyzer-dotnet`. Reconstructed from git history; format follows [Keep a Changelog](https://keepachangelog.com/).

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
