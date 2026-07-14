# @kepello/nodegraph-analyzer-dotnet

.NET / C# analyzer subprocess for [`@kepello/nodegraph-analysis`](https://github.com/kepello/nodegraph-analysis). Walks C# source via Roslyn and emits NDJSON per the analyzer protocol.

## Prerequisites

- The **.NET 9 SDK or runtime** must be installed and on your `PATH`. The package ships a managed binary (a `.dll`) plus a Node shim that invokes `dotnet`.
- Node.js (any version npm itself supports). The shim is a `node` script, so npm's standard bin-launcher generation gives cross-platform invocation — Unix symlink, Windows `.cmd` / `.ps1` wrappers — without depending on bash.

## Install

```sh
npm install @kepello/nodegraph-analyzer-dotnet
```

GitHub Packages auth required:

```ini
//npm.pkg.github.com/:_authToken=<your-github-PAT-with-read:packages>
@kepello:registry=https://npm.pkg.github.com/
```

After install, the binary is discoverable at `node_modules/.bin/nodegraph-analyzer-dotnet`. Reference it from your orchestrator's analyzer config:

```json
{
  "analyzers": [
    {
      "name": "dotnet",
      "command": "node_modules/.bin/nodegraph-analyzer-dotnet",
      "filter": { "include": ["**/*.cs"] }
    }
  ]
}
```

## Build from source

```sh
git clone https://github.com/kepello/nodegraph-analyzer-dotnet.git
cd nodegraph-analyzer-dotnet
npm run build   # runs `dotnet publish` against src/
```

This produces `dist/NodegraphAnalyzerDotnet.dll` plus its dependencies. The bin shim resolves them at runtime.

## Testing

```sh
dotnet test tests/NodegraphAnalyzerDotnet.Tests.csproj
```

55 xUnit tests covering cyclomatic complexity, cognitive complexity, and Halstead metrics extraction. Tests pin documented behavioral gaps from trade-offs 2.2.3 (recursion detection), 2.2.6 (magic number -2), and 2.2.8 (comment line over-count).

## Status

Emits the `AnalyzerArtifact` wire format defined by [`@kepello/nodegraph-analysis`](https://github.com/kepello/nodegraph-analysis) — see the [analyzer protocol reference](https://github.com/kepello/nodegraph-analysis/blob/main/docs/analyzer-protocol.md). Current capabilities:

- File discovery + parallel Roslyn parse per `.cs` file, plus `.csproj` / `.sln` build-file elements
- **Element kinds** (source of truth: `GetElementType()` in [src/Program.cs](src/Program.cs)) — `file`, `class`, `interface`, `struct`, `enum`, `method`, `property`, `constructor`, `destructor`, `field`, `event`, `indexer`, `operator`, `accessor`, `enumMember`, `parameter`, `typeParameter`, `annotation`, plus `project` / `solution` from build files.
  - `record` and `record class` emit as `class`; `record struct` emits as `struct` (both parse to the same Roslyn syntax type, disambiguated only by `.Kind()`).
  - Local functions emit as `method`.
  - **Namespaces are containers, not elements** — a type declared under `namespace Foo` is treated as top-level, and the namespace survives on the `qualifiedName` facet and `metadata.fullyQualifiedName`.
- **Edges** — `contains`, `imports` (subtype `using`), `extends`, `implements`, `overrides`, `calls`, `references`, `partial`, plus intra-class `accessesField` / `callsMethod` (LCOM4 input). Inheritance edges are resolved from **Roslyn symbols**, not name heuristics.
- Size observations: `linesOfCode`, `physicalLinesOfCode`, `blankLineCount`, `commentLineCount`, `commentDensity`
- Documentation observations: `hasDocComment`, `docCommentLineCount`, `commentTagCounts` (TODO/FIXME/HACK/XXX/NOTE word-boundary scan)
- Code-smell observations: `magicNumberCount` (numeric literals outside `{0, 1, -1, 2}` allowlist; skips `const` / `readonly` field declarations)
- Per-method scalars: `branchCount`, `sonarBranchCount`, `sonarNestingDepthSum`, `maxNestingDepth`, `parameterCount`, `returnStatementCount`, plus all four Halstead inputs (Sonar 2017 cognitive complexity is a clean-room port)
- Framework vocabulary: `integrationRole`, `interactionRole`, `uiLifecycle`, `serializationFormats`, `generatedSignals`, `baseTypeRoles`
- Does **not** emit `classShape` observations — the engine derives class shape from `contains` children.

Wire-format compatibility: ships against `@kepello/nodegraph-analysis ^3.45.0` (see `package.json`).

## License

MIT — see [LICENSE](LICENSE).
