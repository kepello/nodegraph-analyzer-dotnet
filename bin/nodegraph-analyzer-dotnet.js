#!/usr/bin/env node
// nodegraph-analyzer-dotnet — cross-platform shim for the .NET analyzer DLL.
//
// Replaces the bash-only shim that previously sat alongside this file
// (Fathom row analyzers-windows-cmd-shim 0.2.1). On any platform npm
// supports, this script is the bin entrypoint — npm generates the
// platform-appropriate launcher (a `.cmd` on Windows, a symlink on
// Unix) that invokes Node on this file. Node then spawns `dotnet`
// with the published DLL.
//
// Requires the .NET 9 runtime on PATH.

"use strict";

const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const dll = path.join(__dirname, "..", "dist", "NodegraphAnalyzerDotnet.dll");

if (!fs.existsSync(dll)) {
  process.stderr.write(
    `nodegraph-analyzer-dotnet: ${dll} not found.\n` +
      `Run 'npm run build' inside the package, or reinstall after a clean publish.\n`,
  );
  process.exit(1);
}

const child = spawn("dotnet", [dll, ...process.argv.slice(2)], {
  stdio: "inherit",
});

child.on("error", (err) => {
  process.stderr.write(
    `nodegraph-analyzer-dotnet: failed to spawn 'dotnet' — ${err.message}\n` +
      `Is the .NET 9 runtime installed and on PATH?\n`,
  );
  process.exit(1);
});

child.on("exit", (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
    return;
  }
  process.exit(code ?? 0);
});
