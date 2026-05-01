# @kepello/nodegraph-analyzer-dotnet

.NET / C# analyzer subprocess for [`@kepello/nodegraph-analysis`](https://github.com/kepello/nodegraph-analysis). Walks C# source via Roslyn and emits NDJSON per the analyzer protocol.

## Prerequisites

- The **.NET 9 SDK or runtime** must be installed and on your `PATH`. The package ships a managed binary (a `.dll`) plus a small bash shim that invokes `dotnet`.
- Bash (or compatible shell) on the host. Windows users running outside WSL will need a `.cmd` wrapper, currently TODO.

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

## Status

**Slice 0a (pre-slice-1).** Package shape only. The analyzer currently emits the BDS-specific wire format inherited from its previous home in `bds-v3`. Slice 1 will switch it to the `nodegraph-analysis` wire format uniformly with the other analyzer subpackages.

See [`@kepello/nodegraph-analysis/docs/migration-dotnet.md`](https://github.com/kepello/nodegraph-analysis/blob/main/docs/migration-dotnet.md) for the full migration plan.

## License

MIT — see [LICENSE](LICENSE).
