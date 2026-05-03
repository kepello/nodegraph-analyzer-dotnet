# Changelog

All notable changes to `@kepello/nodegraph-analyzer-dotnet`. Reconstructed from git history; format follows [Keep a Changelog](https://keepachangelog.com/).

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
