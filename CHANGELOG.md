# Changelog

All notable changes to `@kepello/nodegraph-analyzer-dotnet`. Reconstructed from git history; format follows [Keep a Changelog](https://keepachangelog.com/).

## [0.36.0] — 2026-06-04

`baseTypes` (F7) now emits **fully-qualified** names (Fathom row `dotnet-basetypes-fqn-interfacer-precision` 3.1.1.1.7). Fixes the namespace-discarding omission — direct base `…Split('.').Last()` + transitive `b.Name` — that forced the L1 `interfacer` rule to match ambiguous simple tokens (a domain `Page` collided with `System.Web.UI.Page`; `Report` with `Telerik.Reporting.Report`). Now the transitive base-class chain + implemented interfaces emit via `FullyQualifiedFormat` (global:: omitted, type-args stripped so `ClientBase<T>` → `System.ServiceModel.ClientBase`). In-source global-namespace types stay simple; namespaced/external types emit the FQN — including unresolved error symbols, which preserve the source-written qualifier (`System.Windows.Forms.Form` even when WinForms isn't referenced). **Atomic with the engine catalogue migration** (`@kepello/nodegraph-analysis@3.13.0`) — until the catalogue is FQN, interfacer wouldn't match, so the two ship together. 3 `baseTypes` tests updated to FQN; 144 pass.

## [0.35.0] — 2026-06-04

ASP.NET **Web Site Project** support (Fathom row `dotnet-web-site-project-support` 5.0.73). WSPs have no `.csproj` by design (compiled by `aspnet_compiler`/the runtime), so Roslyn's MSBuildWorkspace can't load them and all their files fell to the references-free `sharedCompilation` (row 5.0.72 made that loud: ~55% of EnvisionWeb). Now the analyzer detects WSPs from the `.sln` and builds each a referenced `Compilation` from its DECLARED references, feeding them through the SAME `BuildArtifact`→`SemanticModel` path as csproj files — unified, no second-class path.

### Added

- **`WebSiteProjectParser`** (`Program.WebSiteProject.cs`) — `.sln` WSP entries (type GUID `{E24C65DC-…}` + `WebsiteProperties`) → name, physical path, `TargetFrameworkMoniker`, `ProjectReferences`.
- **`FrameworkReferenceResolver`** (`Program.FrameworkReferences.cs`) — resolves each WSP's OWN TFM to its `Microsoft.NETFramework.ReferenceAssemblies.<tfm>` NuGet pack (the cross-platform mechanism; `ToolLocationHelper` returns empty on macOS — verified). No pack ⇒ observable `problem`, no borrowed-dir fallback.
- **`WebConfigParser`** — declared 3rd-party assemblies from `web.config <compilation><assemblies>` (skips the `*` wildcard — declared refs only, never a blind bin manifest).
- **`WebSiteProjectLoader`** (`Program.WebSiteProjectLoader.cs`) — assembles the WSP `Compilation`: framework pack (per-TFM) + `ProjectReferences` → the sibling project's already-built `Compilation` (`CompilationReference`, e.g. `CloudCore` — cross-tier symbols now resolve) + `web.config` assemblies located by name in `bin/`. Merged into the orchestrator's `projectMap`.

### Validated

- EnvisionWeb (~15s raw): **references-free 1017 → 20** (−997) — both WSPs (`EnvisionAnywhere.com` v4.7.2, `login.envisiongo.com` v4.8) resolved with their own per-TFM framework refs; no pack-missing problems, no fallback. The residual 20 are `.cs` in a csproj's dir but not its `<Compile>` set (a separate gap; still flagged loud by 5.0.72).
- 17 new unit tests (WSP parse 6 + framework resolver 10 + web.config 1); 144 pass.

## [0.34.0] — 2026-06-04

References-free analysis is now **loud** (Fathom row `dotnet-references-free-analysis-loud` 5.0.72). A `.cs` file not owned by any loaded `.csproj` falls to the System-runtime-only `sharedCompilation` — orphan files, an ASP.NET **Web Site Project** (no project file by design), a `.csproj` that failed to load, or a host without MSBuild — which silently degrades every external-symbol fact (framework base types → `interfacer`, external call resolution, overrides). That degradation was invisible (project-*load* failures emit problems; a per-file fallback emitted nothing). Surfaced chasing the L1 residuals: **~53% of EnvisionWeb (the 983-file `EnvisionAnywhere.com` Web Site Project) analyzes references-free, silently** — so every EnvisionWeb L1 number measured to date is partly computed on degraded data.

### Added

- **`ReferencesFreeReporting`** (`Program.ReferencesFree.cs`): per-file Group-J `Limitation` (`csharp-references-free-compilation`, `significant`) on every element of a references-free file, so downstream can filter/flag rather than trust silently; plus one proportional run-level summary `problem` — **`error`** when the analysis is wholly references-free (MSBuild unavailable / 0 projects loaded), **`warning`** otherwise, naming the largest references-free tiers (e.g. an entire Web Site Project) so the operator sees *which* tier is blind.
- This also makes the analyzer a **diagnostic** for true resolution state: a re-measure now reports, per file, which tier actually resolved (incl. whether old-style .NET Framework csproj resolve their refs on a non-Windows host).

### Tests

- `ReferencesFreeReportingTests` (5): limitation shape; summary null when none; partial → warning naming the tier; 0-projects → error; MSBuild-unavailable → error. End-to-end smoke on the `dotnet-msbuild` fixture: `Orphan.cs` (1 of 4) emits the warning + the per-file limitation while the 2 restored projects resolve. 127 tests pass.

## [0.33.0] — 2026-06-03

L1 `interfacer` transitive boundary-base detection (Fathom row `l1-interfacer-transitive-base` 3.1.1.1.4 / G5). Surfaced by the EnvisionWeb re-measure.

### Changed

- **`baseTypes` now includes TRANSITIVE base-class ancestors, not just the direct base list.** A boundary class is often reached through a project intermediate base (the EnvisionWeb WebForms shape: `ModalWaitingListEdit : AuthenticatedModalBase : … : UserControl`, ×112 such classes). The direct base list named only `AuthenticatedModalBase` — not in the L1 boundary catalogue — so the `interfacer` rule missed it and the class fell to `unclassified`/mis-stereotyped. The facet now walks the type symbol's `BaseType` chain (stopping at `System.Object`, cycle-guarded) and appends each ancestor's simple name, so the framework terminal (`UserControl`/`Page`/`Form`) is present and the unchanged engine `interfacer` rule matches it. Robust across compilation modes: an unresolved external base (references-free fallback) resolves to an error symbol that still carries the simple name, so the terminal is captured either way. Direct-base + interface behaviour preserved (deduped). 2 new tests (in-source transitive chain; framework terminal through an in-source intermediate); 122 pass.

## [0.32.0] — 2026-06-03

L1 unclassified-residual burn-down — L0 portion (Fathom row `l1-unclassified-residual-refinement` 3.1.1.1.3, G1).

### Added

- **`isStatic` facet** — emits whether an element carries the `static` modifier (a static method/member, or a `static class`). Sourced from the already-parsed `flavors["static"]`. Distinct from `dispatchKind == "static"`, which also tags plain instance methods that are merely statically dispatched (no virtual/override) — so `dispatchKind` could not be reused. Auto-flattened to `metadata.isStatic` at ingest. The L1 `classStereotype` rule reads it to detect static-utility containers (a class whose behavioural members are all static = a `helper-module`) that the `*Helper(s)` name-suffix rule misses (`Utils`, `Common`, a `static class Extensions`). 1 integration test (`isStatic` tracks the modifier across a static class + a non-static class's static vs instance methods); 120 tests pass.

## [0.31.0] — 2026-06-03

Fixes a systemic intra-class-edge gap surfaced while auditing the L1 unclassified residual (Fathom row `dotnet-l0-partial-class-field-index`).

### Fixed

- **Partial-class member index now unions ALL declarations of a type.** `IntraClassHelpers.BuildIndex` was built from the single `TypeDeclarationSyntax` in the file being walked, so members declared in a *different* partial file were invisible to the index. The canonical case is WinForms/WebForms: controls live in `Form.Designer.cs`, handlers/logic in `Form.cs`. Consequences were silent and broad — for **every type split across files**:
  - `accessesField` read/write edges to the other-partial fields were never emitted → **LCOM4 cohesion + field-coupling were silently too optimistic**, and field-writing methods misclassified (the L1 `mutator` stereotype collapsed into `command`).
  - the `returnsField` fact (fed by the same index) missed getters returning an other-partial field → the L1 `accessor` stereotype under-fired.
  - same-class `callsMethod` to other-partial methods was missed → `collaborator-internal` under-fired.
  The fix resolves the type symbol (`semanticModel.GetDeclaredSymbol`) and unions the index across all its `DeclaringSyntaxReferences` over the shared multi-file compilation; falls back to the single declaration when the symbol can't be resolved. New `BuildIndex(IEnumerable<TypeDeclarationSyntax>)` overload; the single-decl overload delegates to it. **Measured impact** (clean re-analyze): Utilities `mutator 0→59` (recovered from `command`), `collaborator-internal 1→9`; MyPatientNow `mutator 0→1817`, `command 2823→1027` — ~1900 methods across the two corpora were misclassified because their writes to controls/partial fields were invisible. 2 integration tests (`PartialClassMemberIndexTests`: cross-file `accessesField` + `returnsField`); 119 tests pass.

## [0.30.0] — 2026-06-02

L1 stereotype-precision overhaul — **Stage 5 (class-side), L0 portion** (Fathom row `l1a-stereotype-derivation-precise` 3.1.1.1).

### Added

- **`baseTypes` facet (F7)** — emits the simple names of a type's base list (base class + interfaces) on container elements, **including external/framework bases** that the `extends`/`implements` edges drop because they resolve to no workspace node (WinForms `Form`, WebForms `Page`/`UserControl`, WPF `Window`, …). A base type is a fact (a name), not a relationship to a node we have, so it's a metadata facet, not a dangling edge — consistent with the no-silent-degradation/no-dangling-edge stance. Auto-flattened to `metadata.baseTypes` at ingest. The L1 `classStereotype` rule reads it to tag boundary/`interfacer` classes by base type, which name heuristics miss (functionally-named pages). Verified on Utilities: 30 classes with `baseTypes` — 14 `Form`, 1 `UserControl`, plus `ApplicationSettingsBase`/`EventArgs`/interfaces. No new test fixtures (the facet is exercised by the analysis-side `interfacer` tests + the cross-corpus measurement).

## [0.29.0] — 2026-06-02

L1 stereotype-precision overhaul — **Stage 2 (.NET analyzer)** (Fathom row `l1a-stereotype-derivation-precise` 3.1.1.1). Emits the two new F6 return-shape facts (`accessesField.subtype` was already emitted, 0.25.0). The L1 derivation rules consume these at Stage 4.

### Added

- **`returnKind` facet** (`void` / `boolean` / `primitive` / `reference` / `collection` / `unknown`) — resolved via the `SemanticModel` (categorizes the return `ITypeSymbol`: enums → primitive, arrays / `IEnumerable` implementers → collection, etc.) with a **syntactic fallback** for orphan files whose Compilation can't bind the type. `Task`/`ValueTask` and `Nullable<T>` are unwrapped (an `async Task<bool>` predicate reads as `boolean`). New `Program.ReturnShape.cs`.
- **`returnsField` facet** (boolean) — true iff a body-bearing get-shaped member returns an own-class field/property directly. Reuses the intra-class member-field index and matches the same forms the LCOM4 extractor uses: `this.X` / `base.X` member access **and** the bare `return _backingField;` C# convention; handles expression-bodied getters (`=> _x`). Walks top-level returns only (skips nested lambdas / local functions).
- Both for body-bearing callables only; setters / init / event accessors / destructors → `void` + `returnsField=false`; constructors → `reference` + `false`.

### Validation

- 5 integration tests (spawn the built DLL): every `returnKind` category (incl. `Task<bool>`→boolean unwrap + enum→primitive), the backing-field / `this.X` / expression-bodied getter `returnsField` cases vs computed-value false, setter void, constructor reference. 116 tests pass. Scale check on the Utilities corpus (517 methods): `returnKind` resolves with **0% `unknown`** (void 236, reference 136, boolean 73, primitive 59, collection 13); `returnsField` fires 38× — the getter detection works on real OO code (vs 1× on the functional TS corpus).

## [0.28.0] — 2026-06-02

L1 stereotype-precision group (Fathom `l1-dotnet-baseline` 5.5.0, fix #2 analyzer side).

### Added

- **`event-handler` entryPoint** — `DetectEntryPoint` emits the deferred E-catalogue `event-handler` kind for methods matching the .NET event-callback convention `(object sender, TEventArgs e)` (exactly 2 params; first `object`/`object?`; second a type whose simple name ends in `EventArgs` — WinForms/WebForms/WPF/`EventHandler<T>`; derived EventArgs included). The L1 `methodStereotype` derivation (`@kepello/nodegraph-analysis@^3.5.0`) consumes this to classify the role agnostically. 1 new integration test. 111 tests pass.

## [0.27.0] — 2026-06-01

Two L0-.NET residual fixes (Fathom rows 5.0.68.3 + 5.0.68.2.1).

### Fixed

- **Case-only canonical-name collisions no longer drop elements (5.0.68.3).** C# allows case-distinct siblings (`isAuto` vs `IsAuto`); both lowercase to the same canonical key. The walk previously emitted a hard error and DROPPED the second declaration. Now both are emitted (the second disambiguated with a `-casedup{n}` suffix) and a structured `csharp-canonical-name-collision` limitation is recorded. Edge resolution is case-insensitive by design, so edges to the bare name resolve to the first declaration — a documented residual, not a dropped element. On Utilities: 3 hard errors → 0; +3 elements kept.
- **Contains edges now reuse the authoritative canonical map (5.0.68.3 root cause).** The element walk and the type→member `contains` pass were two independent computations of the member canonical name; the contains pass recomputed from the raw name and missed the disambiguation suffixes (the `$count` overload-signature counter AND the new `-casedup`), so contains edges mis-targeted on signature/case collisions — which mis-feeds L3 cluster membership. Restructured into a **two-pass** design: pass 1 assigns every node its final collision-resolved canonical into one map; pass 2 emits elements + contains edges reading that map. One source of truth.

### Added

- **`library-export-method` extended to public-property accessors (5.0.68.2.1).** Get/set accessors of a public property on a library-export type are externally callable (`new T().Prop` / `.Prop = v`), so they now classify as method-level entry points (TS parity, 5.3.4.3.3). Required fixing `DeriveAccessibility` to inherit the enclosing property's accessibility for accessors without an explicit modifier (they defaulted to `private`, hiding public accessors). On Utilities: `library-export-method` 137 → 221.

### Tests

- 3 new integration tests (case-collision keeps-both + limitation; public-property accessors → library-export-method; private-property accessors → none). 110 tests pass. Resolution unchanged at 100% across the corpora.

## [0.26.1] — 2026-06-01

Indexer accessor-call fix surfaced by the EnvisionWeb corpus validation (Fathom row `dotnet-l0-emission-completeness-probe` 5.0.68.4).

### Fixed

- **Indexer accessor-call target dropped the indexer parameter signature.** An indexer with an explicit `get`/`set` block emits a separate accessor element whose natural key OMITS the indexer's parameter signature (`store:indexer:get`, not `store:indexer-string:get`) — its `GetQualifiedRawName` recurses through the property name without re-adding the indexer's params. `ResolveAccessorTarget` was building the accessor target *with* the sig, so every explicit-accessor-block indexer access dangled. Now: accessor-element target = `<class>/<member>/get|set` (no sig); only the bare property/indexer element (expression-bodied, no accessor block) carries the sig. Surfaced by EnvisionWeb (40 such danglers); covers both generic (`Dict<TKey,TValue>`) and non-generic indexers. New integration test.

### Verified

- **EnvisionWeb (1,864 C# files, ~103k call edges): 100% internal-call resolution** (102,796/102,796, 0 danglers) — the third and largest validation corpus, confirming the L0-.NET resolution work generalizes. 108 analyzer tests.

## [0.26.0] — 2026-06-01

L0-.NET method entry-point inheritance (Gate 3 of the L0-.NET baseline, Fathom row `dotnet-l0-method-entrypoint-inheritance` 5.0.68.2).

### Added

- **`library-export-method` entry-point kind.** A PUBLIC method on a library-export type (public top-level type) is externally reachable via `new T().M()`, so it's a method-level entry point — not `none`. Mirrors the TS analyzer's `library-export-method` (5.0.55 / 5.3.4.3.1). Accessibility-gated: private/protected/internal methods stay `none` (not externally callable — followed the TS reference, which excludes protected, over the gate's looser "public/protected" wording). Constructors excluded (reachable via the type). Closes the Gate-3 finding where 93% of C# elements were `none` because public methods on library types never inherited an entry-point — starving L1 stereotype + L2 capability-unit seeding. Distribution shift: Utilities `none` 93%→89% (+137 `library-export-method`); MyPatientNow +611.

### Tests

- New integration test: public method on a public type → `library-export-method`; private method + public method on an internal type → `none`. 107 tests pass.

### Known follow-on

- Property get/set accessors on public properties are externally callable (TS extends `library-export-method` to them, 5.3.4.3.3) but C# derives an accessor's accessibility as `private` by default rather than inheriting the property's — so accessors aren't yet classified. Tracked as a parity follow-on.

## [0.25.0] — 2026-06-01

L0-.NET emission-completeness (Gate 5 of the L0-.NET baseline, Fathom row `dotnet-l0-emission-completeness-probe` 5.0.68.4). Closes the resolution-rate-invisible emission gaps — edges the analyzer *should* emit but skipped, which a 100% resolution rate cannot detect.

### Added

- **Property / indexer access → `calls` accessor edges** (the C# E2 closer; mirrors TS 5.0.66/5.0.67). `obj.Prop` (read) emits `calls`/property-get → the get-accessor element (`<class>:<prop>:get`); `obj.Prop = x` (write) → property-set; `obj[i]` → the indexer accessor/element. Resolved via the property symbol (`ResolveAccessorTarget`); plain fields and external/BCL accessors emit no edge. Previously these were modeled only as a generic `references` edge, leaving the call graph blind to all property access (+136 call-graph edges on the Utilities corpus alone).

### Fixed

- **Nested-type intra-class qualification.** Intra-class `callsMethod`/`accessesField` targets qualified with the immediate class name only (`MyDataTable/...`), so calls inside nested types (the ubiquitous typed-DataSet `MyDataSet.MyDataTable` pattern) dangled against the full element key (`mydataset:mydatatable:...`). Now qualifies with the full `GetQualifiedRawName` nested path.

### Verified

- **100% internal-call resolution across two independent corpora** (Utilities 827/827, MyPatientNow 6991/6991 — the latter a 22k-element typed-DataSet-heavy codebase that surfaced the nested-class gap). Full C# emission-completeness checklist verified + cataloged in the baseline doc.

### Tests

- 3 new integration tests (property get/set + field-negative, indexer via property test, nested-type intra-class) + the existing suite. 105 tests pass.

## [0.24.1] — 2026-06-01

Internal-call resolution **99.71% → 100%** on PNE/Utilities (closes the 5.0.68.1 residual, Fathom row `dotnet-l0-partial-class-dispose-binding` 5.0.68.1.1).

### Fixed

- **File-path case divergence on cross-file edges.** Element natural keys use the analyzer's directory-walked (on-disk-case) path, but Roslyn's `SyntaxTree.FilePath` for a project document follows the case **as written in the .csproj `<Compile Include>`** — which differs for e.g. legacy WinForms `Foo.designer.cs` (csproj) vs `Foo.Designer.cs` (disk), invisible on a case-insensitive FS. A cross-file targetRef built from the Roslyn path then couldn't string-match the callee's element key → dangled. `ResolveCallTarget` / `ResolveTypeTarget` / `ResolveTargetFile` now normalize the resolved declaration path to the discovered on-disk case (`NamingHelpers.BuildCanonicalPathMap` + `CanonicalizeFilePathCase`). General fix (any csproj-case ≠ disk-case file), not just the 2 `frmFieldValue.Dispose(bool)` danglers it surfaced through.

### Tests

- 2 new `NamingTests` pinning the path-case normalization (csproj-case → disk-case; unknown path unchanged). 104 tests pass.

## [0.24.0] — 2026-06-01

Internal call/constructor/delegate resolution overhaul (Fathom row `dotnet-l0-internal-call-resolution` 5.0.68.1). Closes Gate 1 of the L0-.NET baseline: internal-target `calls`/`callsMethod` resolution **66.31% → 99.71%** on PNE/Utilities. Unifying principle: every emitted call target must bind to the callee's element natural key, or emit no edge / a structured limitation — never a bare unbindable name.

### Fixed

- **Intra-class `callsMethod` now signature-qualified.** Was class-qualified but sig-less (`class/method`), binding only to zero-arg methods (40% resolution). Resolves the called name against the class's overloads by **arity** and emits `class/method(int,string)` → matches the method element key. Same-arity overloads → `csharp-ambiguous-overload` limitation (no guessed edge). The 167-dangler bulk; callsMethod → 100%.
- **`new T()` constructor + `event += handler` delegate resolution.** Replaced loose name-only `allNames` matching + bare cross-file-unaware `Add` with semantic resolution: new `ResolveTypeTarget` resolves the `new` type → cross-file `AddWithTargetRef` to the type's element key; external/BCL types (no declaration) emit no edge — eliminating false-positives (e.g. `new ResourceManager()` matching a local `ResourceManager` property). Delegate handlers resolve via `ResolveCallTarget`, else a `csharp-unresolved-call` limitation.
- **`ResolveCallTarget` derives its target from the resolved declaration's syntax** (`GetQualifiedRawName` + `GetParamSignature`, the element-key functions) instead of the semantic `ContainingType.Name` + `ToDisplayString` signature — fixing nested-class qualification and signature-spelling divergence.
- **`calls` bare-name fallback removed** — semantic-resolution failure now records a `csharp-unresolved-call` limitation instead of a phantom unbindable edge.

### Changed

- `GetParamSignature` moved to shared `NamingHelpers` (single source of truth for the edge target + element key signature).

### Tests

- 3 new `IntraClassTests` (signatured target, arity disambiguation, same-arity-ambiguity limitation) + new `CallResolutionIntegrationTests` (subprocess end-to-end: constructor cross-file + BCL-no-edge, delegate, callsMethod) pinning the `Program.cs` orchestration the unit project doesn't compile. 102 tests pass.

### Residual

- 2/691 csharp danglers = WinForms partial-class `Dispose(bool)` (Fathom follow-on 5.0.68.1.1). 0.29%, well past the 98% gate.

## [0.23.0] — 2026-05-28

Two-hash adoption (Fathom row 1.12.5): emit `sourceHash` (renamed from `contentHash`) on every artifact/element — it always held a source hash. The overlay composes the substrate `contentHash` from it, so an analyzer-rule change that doesn't touch source still re-tips. Peer dep on `@kepello/nodegraph-analysis` retargeted to `^3.0.0`.

## [0.22.0] — 2026-05-24

MSBuildWorkspace integration. Phase 2 of Fathom row `dotnet-csproj-sln-handling` (1.11.15) — closes the row and (by downstream measurement) the csharp side of `analyzer-cross-language-asymmetry` (5.0.50).

### Added

- `Program.MSBuildIntegration.cs` — new helper. `LoadProjects(csprojPaths, problems)` opens each discovered `.csproj` via `MSBuildWorkspace`, lets the workspace auto-resolve transitive `ProjectReference` siblings, then returns a per-document map (`absolute path → (Compilation, SyntaxTree)`). Compilation is shared per-project across documents; SyntaxTree is per-document.
- `MSBuildLocator.RegisterDefaults` fires at process start in `Program.cs` before any MSBuild type JITs. Failures (no .NET SDK on host) degrade to a stderr note + the existing System-runtime-only `sharedCompilation` fallback.
- `Microsoft.CodeAnalysis.CSharp.Workspaces@5.3.0` added to both `src` and `tests` csprojs. `Microsoft.CodeAnalysis.Workspaces.MSBuild` on its own doesn't pull in C# language handlers; `MSBuildWorkspace.OpenProjectAsync` throws `Cannot open project ... because the language 'C#' is not supported` without it.

### Changed

- `BuildArtifact` / `DecomposeWithRoslyn` parameter type relaxed from `CSharpCompilation` → `Compilation` (base type) so the workspace-produced `Compilation` is accepted without a cast. Behavior unchanged; only `Compilation.GetSemanticModel` is used.
- `RunOutput` now branches per-file: if the file path is in the MSBuild project map, the project's `Compilation`+document tree is used (NuGet PackageReferences + ProjectReferences resolved). Otherwise the existing single-`sharedCompilation` path runs — orphan `.cs` files outside any `.csproj` directory still produce artifacts unchanged.
- Per-project load failures emit `{type: "problem", problem: ...}` lines and fall through; one broken `.csproj` does not abort the analyzer.

### Why

Pre-Phase-2 `DecomposeWithRoslyn` built a single Compilation with only `typeof(object).Assembly.Location` as a reference. Every PackageReference + ProjectReference symbol was unresolvable, which collapsed downstream Fathom layers for .NET workspaces: csharp `entry_point_distribution` 91% `none`, csharp L5 scenarios `stepCount` 0 across 100% sampled, 0 csharp entities at L7b (per row 5.0.50). Phase 1 already shipped `.csproj`/`.sln` discovery + structural artifacts and pre-positioned `Microsoft.CodeAnalysis.Workspaces.MSBuild@5.3.0` + `Microsoft.Build.Locator@1.7.8`; Phase 2 wires them through.

### Tests

96/96 pass (91 prior + 5 new in `tests/MSBuildIntegrationTests.cs`). New tests pin every shape in the failure-class catalog per Rule 8: (1) positive load on the restored fixture; (2) malformed `.csproj` emits a problem and the load map stays empty; (3) cross-project resolution — `Worker.Run` → `Helper.DoThing` symbol carries `DeclaringSyntaxReferences` pointing at the absolute path of `Lib/Helper.cs`; (4) cross-package resolution — `JsonConvert.SerializeObject` resolves to a metadata-only symbol with empty `DeclaringSyntaxReferences` (pins the "external → no edge" invariant `Program.cs`'s `ResolveCallTarget` depends on); (5) orphan fallback — `Orphan.cs` outside any `.csproj` is absent from the loaded map. RED-confirmed pre-fix: 5/5 failed.

Fixture: new `fathom/fathom-test-fixtures/dotnet-msbuild/` with `Lib/Lib.csproj` (net9.0 class library, one class `Helper.DoThing()`) + `App/App.csproj` (net9.0 console app, `<PackageReference>` Newtonsoft.Json@13.0.3, `<ProjectReference>` to Lib, `App/Worker.cs` invokes both cross-boundary methods) + `Orphan.cs` (no `.csproj`). `setup.sh` runs `dotnet restore App/App.csproj`; `bin/`/`obj/` gitignored. Restore is required before tests 1/3/4 (tests fail-loudly if `obj/project.assets.json` is absent rather than silently skipping).

End-to-end smoke against the fixture (built `dist/NodegraphAnalyzerDotnet.dll`): `MSBuildWorkspace loaded 9 document(s) from 2 project(s); 0 problem(s)`; `Worker.Run` emits `calls` edge to `helper/dothing` with `targetRef = :Users:...:Lib:Helper.cs#helper:dothing` (full cross-project resolution); `Orphan.cs` artifact emits via sharedCompilation fallback; no edge to external `JsonConvert.SerializeObject` (existing external-classifier behavior preserved).

### Risk envelope

- Hosts without .NET SDK: `MSBuildLocator.RegisterDefaults()` throws → caught → stderr note → all .cs files go through the System-runtime-only `sharedCompilation` (pre-Phase-2 behavior).
- Broken `.csproj`: per-project try/catch logs a problem and continues; sibling projects load.
- `.cs` files outside any loaded project's directory: not in the project map → `sharedCompilation` path. No regression vs Phase 1.
- Performance: MSBuildWorkspace load adds a one-time per-project cost. Smoke run on the fixture took ~1.2s end-to-end (2 projects, 9 documents). Per-large-workspace characterization will surface in the live PNE measurement; if it's >2× the prior path, a follow-on row will be filed (not blocking).

### Closes

- **Row 1.11.15 `dotnet-csproj-sln-handling` Phase 2.** Phase 1 shipped 2026-05-10 (0.9.0); Phase 2 ships here. Row moves Active → Done.
- **Row 5.0.50 `analyzer-cross-language-asymmetry` (csharp portion).** Closed by downstream re-measurement of the integration against a real .NET workspace — see workspace `.agents/planning/changelog.md` for the operator-facing pre/post numbers. Swift portion stays Parked under the Swift park.

## [0.21.0] — 2026-05-24

Cross-platform Node shim replaces bash shim. Closes Fathom row `analyzers-windows-cmd-shim` (0.2.1) on the .NET side.

### Changed

- `bin/nodegraph-analyzer-dotnet.js` replaces `bin/nodegraph-analyzer-dotnet`. The new entry is a Node script (`#!/usr/bin/env node`) that spawns `dotnet <dll>` with the user's args. npm's standard bin-launcher generation produces a Unix symlink and Windows `.cmd` / `.ps1` wrappers automatically — no per-platform shim files needed and no bash dependency.
- `package.json` `bin` field updated to point at the new `.js`.
- README prerequisites updated: `bash` removed; Node is sufficient (along with the .NET 9 runtime).

### Removed

- The bash-only `bin/nodegraph-analyzer-dotnet` shim. No backwards compatibility shim — consumers go through npm's resolved `node_modules/.bin/nodegraph-analyzer-dotnet`, which now points at the JS file.

### Why

Pre-fix Windows users without WSL had no working binary path: npm-generated `.cmd` wrappers around a bash shim require bash on PATH. The architectural fix is to make the bin entry a Node script (the standard npm cross-platform pattern), not to add a sibling `.cmd` file. Same approach the workspace's TS analyzers and CLI tools already use; the .NET analyzer was the outlier.

### Tests

Smoke-tested: `bin/nodegraph-analyzer-dotnet.js --help` invokes the .NET binary and reaches the args validator (exits with the analyzer's own "required argument" error, confirming the spawn chain works). Existing 91 analyzer tests still pass (only the bin entrypoint changed; analyzer logic untouched).

## [0.20.0] — 2026-05-24

Canonical-name underscore preservation. Closes Fathom row `dotnet-canonical-name-underscore-collision` (5.2.1).

### Fixed

- `Canonicalize` (per-segment name canonicalization) now preserves `_` in identifiers. Pre-fix the rule treated underscore as a separator (anything not `[a-z0-9]` got collapsed to a dash; leading dashes were trimmed), so `_field` and `Field` both reduced to `field` and collided on the canonical natural-key path. Real-world impact: PNP/Utilities reported 13 element-name collisions (`api/_environment` vs `api/Environment` and similar); PNE/HealthGorilla had 1.

### Refactor

- Hoisted `Canonicalize` into a new `NamingHelpers` static class in `src/Program.Naming.cs` so the rule is unit-testable from the test project. The top-level `Canonicalize` in `Program.cs` is now a one-line forwarder.

### Tests

91/91 tests pass (79 prior + 12 new `NamingTests.cs`). New tests pin: underscore preservation (`_field` ≠ `Field`, leading / trailing / dunder); baseline behavior (PascalCase lowercasing, dot-separator collapse, paren / comma collapse, digit preservation, trailing-punctuation trim, repeat-punctuation single-dash).

### Breaking

Pre-prod per `feedback_pre_prod_no_migration`: any existing `.fathom/graph.db` with `_*`-named C# elements has different canonical names post-update. Delete + rebuild is the operator's accepted migration. The breakage surface is small in practice (only C# codebases with leading-underscore field naming where the underscore-stripped name collides with another sibling element — typically rare outside the specific naming-convention collisions this row was filed against).

## [0.19.0] — 2026-05-24

Shadow-aware intra-class edge extraction. Closes Fathom row `analyzers-intraclass-shadow` (2.2.4) on the .NET side.

### Fixed

- `IntraClassHelpers.ExtractEdges` now suppresses Cases 2 + 3 (bare-identifier invocation matching a method name; bare identifier matching a field/property name) when the name is shadowed by a locally-introduced binding in the method body — parameters, local variable declarations, foreach iteration variables, catch variables, and pattern-variable designations. Previously, a parameter or local named the same as a class member fired a spurious `accessesField` / `callsMethod` edge into the cohesion graph (LCOM4 over-reported cohesion).
- `this.X` / `base.X` (Case 1) remains unambiguous and continues to fire regardless of local bindings.

### Why

2026-05-24 promotion of trade-off 2.2.4 under the tighten-from-the-bottom principle — L0 wire-protocol precision affects L2/L3 cohesion-driven derivations. C# convention frequently omits `this.` for member access, so Cases 2 + 3 carry the dominant signal, making shadowing the dominant precision risk. The TS analyzer addressed the same risk earlier by silencing its bare-identifier branch entirely (TS `this.` discipline is stronger); .NET retains the bare-identifier branch and gains a scope tracker instead.

### Tests

79/79 tests pass (68 prior + 11 new in `tests/IntraClassTests.cs`). New tests cover every shadowing pattern category per the test-fixture-pattern-catalog standard: parameter, local, foreach iteration variable, catch variable, and pattern designation, each tested against both `accessesField` and `callsMethod` where applicable. Positive baselines (no-shadow `this.X` and bare reference both fire) and a negative (`this.X` still fires even when a parameter shadows) are also pinned. RED-confirmed pre-fix: 8/11 failed.

### Trade-off

Conservative — doesn't model block scopes precisely. A class member whose name collides with an unrelated local in any branch of the method body is dropped entirely from the cohesion graph for that method. Under-counting is the chosen bias for LCOM4 (false-positives previously over-reported cohesion). Scope-precise tracking would tighten further but adds complexity without a felt consumer need today.

## [0.18.0] — 2026-05-23

`overrides` edge emission. P4a of Fathom row `l2-overrides-edge-first-class` (3.1.2.1). Cross-language parity with `@kepello/nodegraph-analyzer-typescript@0.36.0`.

### Added

- .NET analyzer emits `overrides` edges for class methods that override a parent's method. Two emission paths handled via Roslyn's semantic model:
  - **Class override**: `IMethodSymbol.OverriddenMethod` (non-null when the `override` keyword is used on a virtual/abstract base method).
  - **Interface implementation**: `containingType.AllInterfaces` × `FindImplementationForInterfaceMember` — handles both implicit (name-match) and explicit (`void IFoo.Bar()`) interface implementations.
- Edge direction: source = OVERRIDING method (this class's member); target = OVERRIDDEN method (parent class or interface member). Matches the substrate's existing child→parent convention.
- Cross-file `targetRef` resolution via `DeclaringSyntaxReferences[0].SyntaxTree.FilePath`.

### Why

Continuation of the architectural ship started in `nodegraph-analysis@2.29.0` (P1 protocol), `nodegraph-analyzer-typescript@0.36.0` (P2 TS emission), and `nodegraph-capability-units@0.8.0` + `fathom-cli@4.23.0` (P3 L2 closure walker). P4a brings .NET to parity — every C# overlay/repository/service impl now contributes `overrides` edges, closing the L2 visibility gap on .NET workspaces (analogous to the +5.78pp TS Gate 3 movement observed today on Fathom workspace).

### Tests

68/68 .NET tests pass. New emission code does not affect existing test fixtures (they don't exercise heritage). Cross-language behavior verified against the same conformance fixtures the TS analyzer uses; live impact on .NET workspaces (PNE / PNP) measured at next operator-driven analyze run.

## [0.17.0] — 2026-05-16

Additive — Group G canonical documentation extraction. Closes part of Fathom row 4.6.1. .NET analyzer now passes 25/25 conformance fixtures at `full` — the top of the level ladder.

### Added

- `ExtractCanonicalDocumentation(node)` C# helper — pulls the contiguous `///` doc-comment lines from the leading trivia, strips `///` prefixes, wraps in a root element, parses via `XDocument.Parse`, and extracts the G1 canonical tag content.
- `ParseXmlDocFragment(xml)` exported (callable) for direct testability without trivia extraction.
- Tag mapping: `<summary>` → summary; `<param name="X">` → params[X]; `<returns>` → returns; `<exception cref="T:Type">` → throws[] (cref normalized to bare-name); `<example>` → examples[].
- Malformed XML falls back to summary-only with the raw text — defensive against partial doc comments.

### Element emission

`documentation` (when extracted) + `documentationCoverage` (true/false) emitted on every element. Whitespace runs in tag content are collapsed to single spaces — XML-doc tags often span multiple lines and the canonical shape wants flat readable strings.

## [0.16.0] — 2026-05-16

Additive — Group F type-system facets. Closes part of Fathom row 4.5.1. .NET analyzer now passes 24/24 conformance fixtures at `l6-ready`.

### Added

- **F2 `dispatchKind`** on methods + constructors. Full C# dispatch matrix covered: `abstract` (abstract modifier), `static` (static modifier), `virtual` (virtual modifier or interface method), `override` (override modifier without sealed), `final` (sealed override), `static` for plain instance methods (no virtual/override — can't be overridden without a virtual/abstract base), `static` for constructors.
- **F3 `callableRole`** — `constructor` for ConstructorDeclarationSyntax; `none` for plain methods. Static-factory / builder-step / conversion deferred.
- **F5 `isAsync`** — true on methods carrying the `async` modifier; false otherwise. Constructors can't be async in C#.

### Notes

- F1 already shipped via 4.1.2's `references` edge emission with `parameter-type` / `return-type` subtypes; new fixtures reuse B1/B2 coverage.
- F4 (generic instantiations — SHOULD level) deferred to a separate row.

## [0.15.1] — 2026-05-16

Dropped — `Handler` removed from the E3 class-name catalogue. Closes Fathom row 4.4.2.3. Matches the parallel TS analyzer 0.15.1 ship for cross-language consistency. See that package's changelog for triage evidence + decision rationale.

E3 catalogue now: `Service`, `Endpoint`, `Hub`, `Worker`, `Consumer`, `Job`, `Function`.

## [0.15.0] — 2026-05-16

Additive — .NET analyzer emits `entryPoint: "wcf-service"` on top-level public types declared in a `.svc.cs` source file. Closes Fathom row 4.4.2.2.

### Changed

- New detection branch in `DetectEntryPoint` fires for `TypeDeclarationSyntax` where (a) accessibility is `public`, (b) `filePath` ends in `.svc.cs` (case-insensitive), and (c) the type is at namespace level (no enclosing type). Priority: after `http-controller`, BEFORE `library-export`. heuristicNote still rides along.

### Stress-test verification

PNP re-run: 30 classes shifted from `library-export` → `wcf-service` (8250 → 8220). All 10 spot-checked samples are real WCF service implementations (`CalendarService.svc.cs`, `RampService.svc.cs`, `MyPatientNowMessageService.svc.cs`, etc. under `PatientNowLib/`). PNE has 0 wcf-service — no `.svc.cs` files in the legacy codebase. J1 limitations unchanged.

## [0.14.0] — 2026-05-16

Additive — .NET analyzer emits `entryPoint: "http-controller"` on classes whose name ends in `Controller`. Closes Fathom row 4.4.2.1.

### Changed

- New detection branch in `DetectEntryPoint` fires for `ClassDeclarationSyntax` whose identifier ends with `Controller` (excluding the bare name). Priority: after `main` + method-level `http-handler`, BEFORE `library-export`. Returns `("http-controller", null, null, heuristicNote)`.
- Removed `Controller` from `EntryPointHelpers.E3ClassNamePatterns`; the dedicated branch fires first. Catalogue keeps `Service`, `Handler`, `Endpoint`, `Hub`, `Worker`, `Consumer`, `Job`, `Function`.

### Stress-test verification

PNE+PNP re-run after the shift: 684 PNP Controllers + 4 PNE Controllers (= 688 total, matching the 4.4.2 triage prediction) shifted from `library-export` → `http-controller`. heuristicNote signal dropped from 850 → 162 total records — Controller no longer clutters the triage flywheel. J1 limitations unchanged at 7.

## [0.13.0] — 2026-05-16

Additive — emit `entryPointHeuristicNote` whenever a class declaration matches the E3 class-name-suffix catalogue, regardless of which first-class entry-point kind won. Pairs with `@kepello/nodegraph-analysis@2.7.0`'s new optional facet.

### Added

- `SuggestiveNameNote(node)` C# helper factored out of the E3 branch.
- `DetectEntryPoint` tuple grows to `(Kind, Trigger, Limitation, HeuristicNote)`; the call site emits `entryPointHeuristicNote` as an element facet when present.
- .NET analyzer now passes 19/19 conformance fixtures at `l5-ready` (new E3-companion fixture added).

### Stress-test result

PNP corpus (7,085 artifacts / 416K elements): `entryPointHeuristicNote` volume jumped from 5 (E3-only) to 809 (heuristicNote-across-all-kinds). J1 `entry-point-pattern-unmatched` limitation count unchanged at 5 — limitation gating is still `entryPoint === "other"`, the facet just preserves the signal for the cases where a first-class kind also matched. proposedKind catalogue: `controller-class: 684, service-class: 109, handler-class: 10, hub-class: 6`. PNE corpus (1,873 artifacts / 105K elements): 2 → 41.

## [0.12.0] — 2026-05-16

Adds Group E (entry-point) detection. Closes Fathom row 4.4.1 — .NET analyzer now passes 18/18 conformance fixtures at the `l5-ready` level (Groups A–E).

### Added

- `DetectEntryPoint(node, accessibility, filePath)` C# helper that returns the (kind, trigger, limitation) tuple per element. Detection covers:
  - **`main`** — top-level `Main` method (case-insensitive) inside `Program` class, or implicit `<Main>$` for C# 9+ top-level statements.
  - **`http-handler`** — methods decorated with one of `[HttpGet]`, `[HttpPost]`, `[HttpPut]`, `[HttpDelete]`, `[HttpPatch]`, `[HttpHead]`, `[HttpOptions]`, or class-level `[Route(…)]` (ASP.NET Core minimal-API / MVC conventions).
  - **`library-export`** — every `public class` declared at namespace level (Q3 = aggressive seed; consumers can post-filter via the assembly's public-API surface).
  - **`other`** + J1 limitation — class names suffixed `Handler`, `Listener`, `Worker`, `Service`, `Controller`, `EventHandler` that didn't match a first-class kind. Emits structured limitation `{ kind: "entry-point-pattern-unmatched", metadata: { pattern, suggestedKind } }`.
- New `EntryPointHelpers` static class with `HttpAttributeNames` + `E3ClassNamePatterns` constants. Lives after `FileExtensionsHolder` per the C# top-level-statement ordering rule.
- Per-element `entryPoint` (string) + `entryPointTrigger` (object) facets emitted via the existing JSON writer pipeline.

### Trade-offs

- Detection doesn't verify attribute origin (e.g., that `[HttpGet]` resolves to `Microsoft.AspNetCore.Mvc.HttpGetAttribute`). Matches the spec's pragmatic MAY-level stance for v1.
- `library-export` seed is aggressive — every public class qualifies, even when it's only public for assembly-internal reuse. Downstream filters (`InternalsVisibleTo`, public-API analyzer) refine this in row 4.5.x territory.

## [0.11.0] — 2026-05-14

**Breaking — overload disambiguation switches from visit-order `$N` suffixes to parameter-type signatures** (Fathom work row `analyzers-overload-natural-key-retrofit` 2.2.23). Same convention as the Swift port shipped in 0.9.0. Callable declarations (methods / constructors / destructors / operators / indexers / local functions) now suffix their raw name with `(Type1,Type2,...)` extracted from `BaseMethodDeclarationSyntax.ParameterList` / `IndexerDeclarationSyntax.ParameterList`; the suffix gets sanitized to dashes by the existing `Canonicalize` regex.

The previous `${count}` visit-order logic produced unstable identity across runs — deleting one overload re-keyed all surviving ones, causing the substrate to tombstone + re-insert physically-unchanged elements with the wrong content hash + edge associations. With the new convention, sibling reordering / addition / deletion leaves surviving overloads' canonical names unchanged.

The `$N` collision counter stays as a defensive fallback for the unusual case of two callables producing identical signatures (e.g. via generic constraints).

### Added

- `GetParamSignature(SyntaxNode)` static helper producing `"(Type1,Type2,...)"` for callable nodes, empty string for non-callables.

### Changed

- Qualified raw names for callable declarations now include the parameter-type signature.
- Type-declaration contains-edge construction matches the new suffix shape so member edges resolve to the correct canonical name.

### Migration

- Operators with persistent graphs see overload element ids shift from `name`, `name$1`, ... to `name-type1`, `name-type1-type2`, etc. The substrate detects this as tombstone-then-insert; consumers querying by canonical id need to update. Pre-prod stance — no graceful renaming. Delete `<workspace>/.fathom/graph.db` and re-run `fathom analyze`.

### Tests

68/68 xUnit tests pass unchanged.

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
