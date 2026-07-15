# Changelog

All notable changes to `@kepello/nodegraph-analyzer-dotnet`. Reconstructed from git history; format follows [Keep a Changelog](https://keepachangelog.com/).

## [0.62.0] — 2026-07-15

**Fathom row `bodyless-setter-misclassified-accessor` (3.1.1.24, crit 3) — a WRITER misclassified as a READER.** `ScalarHelpers.HasExtractableBody` gated the WHOLE F6 return-shape pass, so a body-less non-get accessor — an auto-property `set;`/`init;`, or an abstract property setter — emitted no `returnKind` at all. The engine's `methodStereotype` rule (row 3.1.1.23) classifies a set-accessor as `mutator` only when `returnKind === "void"`; with the fact absent, a body-less setter fell through to `accessor` — a set-accessor is void BY GRAMMAR, body or not, and the body-gate never asked the grammar. (`ReturnShapeHelpers.ExtractReturnKind` itself already special-cased non-get accessors as `void` unconditionally — the defect was entirely in the OUTER gate that never called it for a body-less accessor.)

### Fixed

- **`src/Program.cs`** — a new gate ahead of (and independent from) the existing `HasExtractableBody` block: when `node` is a non-get `AccessorDeclarationSyntax` (`set`/`init` — event `add`/`remove` accessors are syntactically impossible without a body in C#, confirmed structurally, see Tests below) with no extractable body, `returnKindFacet`/`returnsFieldFacet` are set to `"void"`/`false` directly. The getter path is UNCHANGED — a body-less getter still correctly emits neither facet.

### Tests

- `tests/ReturnShapeTests.cs` — 4 new fixtures: bodyless auto-property `set;`, bodyless auto-property `init;`, an abstract property setter, and a bodyless auto-property `get;` (regression guard — getter path untouched). **Witnessed RED first**: `dotnet test tests --filter ReturnShapeTests` failed 3/9 pre-fix (`BodylessAutoPropertySetter_IsVoid`, `BodylessInitAccessor_IsVoid`, `AbstractPropertySetter_IsVoid` — all `Expected: "void", Actual: null`); the getter guard passed pre-fix (confirming the getter path was never broken). GREEN after the fix, 9/9.
- Verified structurally (not asserted from memory): explicit C# event accessor syntax (`event EventHandler Foo { add { } remove { } }`) requires bodies on `add`/`remove` — the compiler rejects a bodiless form outside interfaces, and interface/abstract events use the field-like `event EventHandler Foo;` declaration (`EventFieldDeclarationSyntax`), which never produces per-accessor `AccessorDeclarationSyntax` nodes at all (confirmed by probing the analyzer's own element emission against an interface + abstract-class event declaration — only a single `event`-kind element emits, no `add`/`remove` children). The event add/remove case named in this row's design does not arise in this analyzer's model — not fixed because there is nothing to fix.
- Suite: **285/285 pass** (was 281, +4 from this row's fixtures, 0 changed elsewhere). `dotnet build` clean; `npm run build` (the `dotnet publish` wrapper) clean.

### Measured — Fathom's own home store (engine API query, not raw SQL, not a re-analyze)

Queried the live `analysis`-domain `accessor`-kind elements via `GraphLayer.queryNodes` (the same wiring `nodegraph-inspect-cli`'s `openGraph` uses) for setter-shaped elements (naturalKey ending `:set`/`:init`) with `returnKind` absent — the exact fact-signature this bug leaves. **13 flip candidates** on Fathom's own store: 3 real (non-fixture) auto-property setters in this package's own `Program.cs` (`AnalyzerArgs`/`AnalyzerConfig`, dogfooded) + 10 in `nodegraph-analyzer-conformance`'s C# fixture corpus (these short-circuit to the `test-fixture` stereotype bucket regardless of `returnKind`, so the `methodStereotype` LABEL won't visibly move for them — but the underlying `returnKind` fact still corrects, which is this row's actual scope). **0 TypeScript candidates** in the same store (see the companion TS analyzer's CHANGELOG). This is a SMALL sample — Fathom's own home store has no large third-party C# corpus; the large auto-property-setter population this row targets is C#-DTO-heavy code (EF Core / TypeORM-shaped domains), whose corpus confirmation rides on row 5.0.118 (those scratch stores are currently polluted) — said plainly, not asserted as measured here.
- Incidental observation (not fixed here, flagging for the orchestrator): the 3 real `Program.cs` candidates carry a CACHED `methodStereotype` of literal `null`, not `"accessor"` — this looks like a STALE memoized-derivation cache predating row 3.1.1.23's fix (which would compute `"accessor"` for this exact input today), not a live recompute. Worth a follow-up check on whether this store's memoized derivations need a forced refresh independent of this row's fix.

### Pre-prod migration note

A body-less non-get accessor's `returnKind` newly populates (absent → `"void"`) on re-analyze, which flips its `methodStereotype` from `accessor` to `mutator` (row 3.1.1.23's rule already reads this fact — it was just never emitted). Delete `.fathom/graph.db` and re-analyze — no migration path is provided (pre-prod convention).

## [0.61.1] — 2026-07-14

**Docs — the README's capability list described an analyzer that no longer exists.** No code change; the README ships in the package, hence the patch bump.

### Fixed

- **Element kinds** — the list claimed `namespace` and `type-parameter` elements. Neither is emitted: `GetElementType()` (`src/Program.cs:1647-1676`) has no `NamespaceDeclarationSyntax` case (namespaces are *containers*, surviving on the `qualifiedName` facet and `metadata.fullyQualifiedName`), and the kind is `typeParameter`. The list also omitted `destructor`, `event`, `indexer`, `operator`, `accessor`, `enumMember`, `annotation`, and the `project` / `solution` build-file kinds. Rewritten against the switch, including the `record` → `class` / `record struct` → `struct` mapping.
- **Edges** — the list claimed an `inherits` edge "with the I-prefix interface heuristic". No such edge is emitted and no such heuristic exists: inheritance resolves from **Roslyn symbols** into `extends` / `implements` / `overrides` (`src/Program.cs:2128-2132`, `:2367-2371`). Replaced with the emitted set.
- **"Class-shape facets pending in a follow-up"** — restated as a deliberate contract position: the analyzer does not emit `classShape`, and is not obliged to, because the engine derives class shape from `contains` children for every language.
- **Wire-format compatibility** — claimed `^0.5.0`; `package.json` has pinned `@kepello/nodegraph-analysis ^3.45.0` for some time.

### Known non-conformance (filed, not fixed here)

- `partial` is emitted as an edge **`type`** (`src/Program.cs:2526`), but the declared vocabulary makes it a **subtype of `contains`** (`EDGE_SUBTYPE_VOCABULARY.contains`). Filed as Fathom row `dotnet-partial-edge-type-vs-subtype` (3.1.1.9) — an instance of the unenforced-vocabulary class (3.1.1.10), not fixed in a docs release.

## [0.61.0] — 2026-07-13

**BREAKING (edge shape) — `overrides` edges are now sourced at the overriding METHOD, not the CLASS** (Fathom row `overrides-edge-source-kind-diverges` 3.1.1.6, crit 4).

### The bug

The emission block carried this comment, directly above the code:

> *"Direction: source = OVERRIDING method (this class's member); target = OVERRIDDEN method (parent class or interface member). Same convention as TS analyzer."*

**The contract was documented and then violated.** `ExtractRelationships` is called **once per element node**, and everything it returns is sourced **at that node** — but the block was gated on `node is TypeDeclarationSyntax` and looped over the type's members. So every `overrides` edge came out sourced at the **CLASS**:

| analyzer | `overrides` source |
| --- | --- |
| TypeScript | `method` ✅ |
| **.NET** | **`class`** ❌ |
| Swift | *not emitted at all* |

### Why it survived

The .NET tests asserted only on the edge's **target** key (they were written for the 5.0.112 `targetRef` param-qualification bug). **The source was never pinned** — and one test had actually written the bug down as intended behaviour (*"the `overrides` edge is attached to the CLASS element"*), locking it in. **The bug had a test defending it.**

### Blast radius

**Scenario entries are METHODS.** A class-sourced edge can never match one, so **every `overrides`-keyed feature was a silent no-op on .NET while passing every TypeScript test** — including L7a's alternate-flow grouping and its interface-rooted merge bound. Measured on a 91k-element .NET corpus: **132 `overrides` edges emitted, ZERO usable polymorphic families.** This is what made the whole shape divergence visible; it was found three misdiagnoses deep.

### Fixed

- `src/Program.cs` — the block is now gated on `node is MethodDeclarationSyntax`, so the edge is sourced at the overriding member. Both polymorphism paths are preserved: the explicit `override` keyword (class extension / abstract override) **and** implicit/explicit interface implementation.

### Tests

- **`tests/OverridesEdgeTests.cs`** — new `OverridesEdge_IsSourcedAtTheOVERRIDING_METHOD_notTheClass`, RED-witnessed before the fix (`ElementName = loginmodal` — the class). It pins the SOURCE, which nothing did before.
- The two pre-existing assertions that pinned the CLASS-sourced shape are corrected to the method-sourced one, with the history recorded in-place so the mistake is not repeated.
- **281/281 pass.**

### Cross-analyzer guard

`@kepello/nodegraph-analyzer-conformance@0.19.0` adds an **edge-shape contract ratchet** that runs BOTH analyzers over a shared polymorphism fixture and fails if they disagree on any edge type's source kind. **Pre-fix it flags exactly this**: `overrides  typescript=[method]  csharp=[class]`.


## [0.60.0] — 2026-07-08

**The event-handler `+=` sweep treated ANY compound assignment whose RHS name collided with an in-file identifier as an event subscription attempt, manufacturing spurious `unresolved-call` limitations on ordinary numeric accumulation** (Fathom row `dotnet-compound-assignment-event-sweep-false-positive` 4.7.2.t2, found by a limitations-triage pass, confirmed live on this repo's own self-analysis: `Program.Cognitive.cs:194`'s `state.NestingDepthSum += state.Depth` and `Program.Scalars.cs:73-74`'s `sonarBranchCount += cog.SonarBranchCount` / `+= cog.SonarNestingDepthSum` — three of the meta-repo's 19 unresolved-call records on this repo's own `src/`, roughly a third of the corpus-wide count).

### Fixed

- **The `AddAssignmentExpression` branch (`Program.cs`, the `+=` delegate/event sweep)** — before treating a `+=` whose RHS name matches an in-file identifier as a subscription attempt, the LHS is now resolved via the semantic model. Eligible LHS shapes: an actual `IEventSymbol`, or any property/field/local/parameter whose TYPE is a delegate (`TypeKind.Delegate` — covers named delegate types, `Action`/`Func`, and custom delegates alike). A LHS that resolves to a provably non-delegate type (`int`, `string`, ...) is arithmetic, not a subscription — skipped outright, no edge and no limitation. When semantic resolution of the LHS fails outright (degraded/references-free compilation — orphan file, WSP, failed csproj restore; same fallback posture established at 5.0.68.1/5.0.72), the rule falls back to the RHS: only an in-file METHOD declaration keeps the current emit-edge-or-limitation behavior; a non-method RHS (property/field/local) is treated as arithmetic and skipped. Real event subscriptions and delegate-field method-group wiring are unaffected — the branch's legitimate job (resolving handlers, emitting `unresolved-call` when a real subscription's handler can't bind) is unchanged.
- Expected downstream effect: corpora with numeric/counter compound-assignment patterns see their `unresolved-call` count drop — this repo's own `src/` self-analysis went from 19 to 16 records, with exactly the three arithmetic false positives removed and all others (genuinely unresolved in-file calls in `Program.WebFormsMarkup.cs`/`Program.cs`) unchanged. Noise removal, not a behavior regression.

### Tests

- New fixtures in `tests/CallResolutionIntegrationTests.cs`: `UnresolvedCall_PropertyAccumulationCompoundAssignment_NoLimitation` (mirrors the `Program.Cognitive.cs` shape — two `int` members accumulated via `+=`, no edge/limitation expected); `UnresolvedCall_LocalCounterAccumulationCompoundAssignment_NoLimitation` (mirrors the `Program.Scalars.cs` shape — local `int` counter `+=` a member-access RHS); `DelegatesEdge_DelegateFieldMethodGroupSubscription_EmitsDelegatesEdge` (a delegate-TYPED field, not an `event`-keyword member, subscribed to an in-file method via `+=` — confirms the discrimination rule doesn't over-correct and start dropping legitimate delegate-field wiring; already green pre-fix, pinned to guard the fix doesn't regress it). The pre-existing `UnresolvedCall_EventHandlerPlusEqualsSubscription_EmitsUnresolvedCallLimitation` (0.58.0) stays green unmodified — a real `event`-keyword LHS still routes through the branch's legitimate unresolved-handler path. RED confirmed first: both new arithmetic fixtures failed against the pre-fix build with `unresolved-call` present in the limitation-kind collection; green after.
- Suite: **280 pass** (was 277; +3).

## [0.59.0] — 2026-07-08

**`MakeNaturalKey` was the seventh, divergent copy of the natural-key codec that the `naturalkey-codec-consolidation` arc's mirror wave missed** (Fathom row `5.0.128`, WP-8, finding F1 — a LIVE bug). Every other emitter across the workspace (`nodegraph-analysis`, `nodegraph-work-tracking`, `fathom-mcp`, `nodegraph-analyzer-typescript`, `nodegraph-inspect-cli`, `nodegraph-analyzer-conformance`) picked up the escape-then-substitute codec at row `5.0.120.r3`; this analyzer's C# mirror was never updated and kept the old bare `'/' -> ':'` substitution, with no `\` doubling and no `:`/`#` escaping. Any C# `artifactId`/element name containing a literal `:`, `\`, or `#` emitted a `naturalKey`/`targetRef` that silently dangled on the ingest side, and a name containing a literal `:` could collide with a different element's `/`-substituted key and mis-bind. Empirically confirmed by `nodegraph-analyzer-conformance`'s staged e2e fixture (`Weird#Name.cs`, WP-4, 0.15.0) against this repo's 0.58.0.

This is the ONLY work package in the arc where PERSISTED KEY BYTES CHANGE: emitted `naturalKey`/`crossFileRef`/`targetRef` values for any C# artifact or element name containing `:`, `\`, or `#` are now escaped and differ byte-for-byte from 0.58.0's output. Pre-prod — delete `.fathom/graph.db` and re-analyze; there is no migration path.

### Fixed

- **`MakeNaturalKey`** (`src/Program.cs`, all 4 call sites unchanged, routed through the fix) — extracted into a new `NaturalKeyCodec` static class (`src/Program.NaturalKey.cs`) mirroring `escapeNaturalKeyComponent`/`elementNaturalKey` in `nodegraph-core/src/natural-key.ts` byte-for-byte: doubles pre-existing `\`, escapes pre-existing `:` and `#` with the now-unambiguous `\` marker, THEN substitutes `/` → `:` — order is load-bearing (see the spec's doc comment for the injectivity argument). Empty-name composition (artifact-only key, no trailing `#`) is unchanged and matches `elementNaturalKey`'s semantics exactly.

### Tests

- New `tests/NaturalKeyCodecTests.cs` — loads the shared vector file `nodegraph-analyzer-conformance/fixtures/natural-key-codec/vectors.json` (workspace-relative path, precedent `CrossLangFixturesTests.cs:25`; skips gracefully with a loud test-output notice for a standalone checkout with no `nodegraph-analyzer-conformance` sibling) and asserts `NaturalKeyCodec.EscapeNaturalKeyComponent`/`MakeNaturalKey` reproduce every `componentVectors` and `elementKeyVectors` entry byte-exactly, and every `injectivityPairs` entry actually differs. RED confirmed first: with the pre-fix bare-substitution restored, 2 of the 4 new tests failed for the right reason (23 component-vector mismatches, 4 element-key-vector mismatches — every vector containing `:`/`\`/`#`); restored, green.
- No pre-existing test pinned the old bare-substitution output — full suite stayed green with zero updates needed to prior fixtures.
- Suite: **277 pass** (was 273; +4).

## [0.58.0] — 2026-07-07

**Regression pins for the renamed `ambiguous-overload`/`unresolved-call` limitation-kind strings** (Fathom row `dotnet-renamed-limitation-kind-pins` 5.0.98.2). The rename shipped at 0.46.0 (5.0.98.1, 2026-06-11) with no dedicated spawn-based test pins for either new kind string — coverage came only from incidental runtime behavior. A 2026-07-06 backlog-audit re-confirmed the gap and found the emission sites had drifted (7 intervening commits) to 3 total sites, not 2 as originally scoped: `ambiguous-overload` (`Program.cs` ~line 882), and `unresolved-call` from two DISTINCT branches — the in-file resolution-failure branch (~line 2214) and the event-handler `+=` subscription branch (~line 2395).

### Tests

- Three new spawn-based fixtures in `tests/CallResolutionIntegrationTests.cs`: `AmbiguousOverload_SameArityCall_EmitsAmbiguousOverloadLimitation` (same-class call to two same-arity overloads); `UnresolvedCall_DelegateFieldInvocation_EmitsUnresolvedCallLimitation` (invoking an in-file `Action` field directly, non-`+=` — pins the in-file resolution-failure branch specifically); `UnresolvedCall_EventHandlerPlusEqualsSubscription_EmitsUnresolvedCallLimitation` (`Tick += HandlerField` where `HandlerField` is a field, not a method — pins the `+=` branch specifically, a distinct code path from the previous fixture despite sharing the same kind string). Each fixture was probed in isolation to confirm it fires ONLY its own target limitation, not the others. Sensitivity witnessed: temporarily reverting the `ambiguous-overload` emission to the pre-rename `"csharp-ambiguous-overload"` string made the new test fail for the right reason (`Assert.Contains()` — collection held `"csharp-ambiguous-overload"`, not `"ambiguous-overload"`); restored, green.
- Suite: **273 pass** (was 270; +3). No production code changed — the renamed behavior was already correct, only unpinned.

## [0.57.0] — 2026-07-07

**WSP control-field synthesis's `asp:`/registered-prefix type probes were exact-case-sensitive, dropping every lowercase-tag control as an honest-but-needless residual** (Fathom row `wsp-control-synthesis-case-sensitivity-gap` 5.0.87.t.1, root-caused via a full-fidelity probe replaying the real WSP reference-resolution policy against both EnvisionWeb WSPs — 21/21 residuals close with a case-insensitive resolver, 0 new failures). `MapControlType` built its candidate FQN by concatenating the MARKUP-CASED tag name (`<asp:label>` → probed `System.Web.UI.WebControls.label`, which doesn't exist — the real type is `Label`), so `<asp:label>`/`<asp:hiddenfield>` and several lowercase Telerik-prefixed tags on EnvisionAnywhere.com fell to the Variant-B/unresolved path even though the real type was one namespace-walk away. This closes the EnvisionWeb residual accepted as Trade-off `wsp-control-synthesis-honest-residual` (backlog.md 5.0.87.t) down to 0 — that row's own text (which still cites the residual) needs a correction, filed for the orchestrator.

### Fixed

- **`WebSiteProjectLoader.ResolveTypeName`** (new) — replaces the `typeExists: fqn => compilation.GetTypeByMetadataName(fqn) != null` boolean probe at the WSP compilation seam. An exact `GetTypeByMetadataName` hit still wins outright (bit-for-bit identical to every already-resolving control — deterministic tie-break when an exact match coexists with a case-variant); on a miss, a lazily-built, ONCE-PER-COMPILATION (`ConditionalWeakTable`-cached, not rebuilt per-control) case-insensitive index over the compilation's top-level, arity-0 types (markup tags can't name generic/nested types) returns the CANONICAL metadata name. A case collision found while building the index (two distinct types differing only by case) stores a null sentinel: genuinely ambiguous, never a nondeterministic pick — falls through to the same honest problem paths as a real miss. Not filtered by accessibility (matches `GetTypeByMetadataName`'s own behavior).
- **`WebFormsCompanion.MapControlType`/`BuildCompanions`** (`Program.WebFormsMarkup.cs`) — the `typeExists: Func<string, bool>` parameter is now `resolveType: Func<string, string?>`, returning the canonical name on a hit. This is the load-bearing part of the fix: a naive case-insensitive BOOLEAN probe (confirmed empirically via the corpus, not just reasoned about) suppresses the "unresolved" problem while the synthesized companion field STILL declares the markup-cased (non-existent-cased) type name — silently non-binding, since C# is case-sensitive. Every probe call site (html, `asp:` cascade, registered-namespace) now uses the RETURNED canonical name for the synthesized field's type. Failure paths are unchanged: unregistered prefix still yields `(null, "no Register directive…")`; registered-but-assembly-absent still yields Variant B (markup-cased FQN + loud problem) — neither category is swallowed by the new resolver.

### Tests

- New `tests/WebFormsCaseInsensitiveResolutionTests.cs` — 7 fixtures covering all 5 failure-shape categories: (a) lowercase `asp:` tags (`<asp:label>`, `<asp:hiddenfield>`) resolve to the canonical PascalCase type with no problem — pinned at both the `MapControlType` level and the `BuildCompanions` companion-source level (the field itself declares the canonical type, not just "no problem"); (b) a lowercase registered-prefix tag against a PascalCase registered type resolves clean; (c) a genuinely-unregistered prefix still drops honestly with a problem — unaffected by the resolver; (d) a registered type whose assembly is truly absent from the compilation still yields Variant B (markup-cased FQN + problem), not faked as existing; (e) a compilation declaring both `Ns.Widget` and `Ns.widget` — a tag matching neither exactly is genuinely ambiguous (null, no nondeterministic pick), a tag matching one exactly resolves via the exact-match-first path to exactly that one; plus a cache-reuse pin (repeated resolutions against the same compilation stay correct, proving the once-per-compilation index, not a one-shot side effect). RED confirmed first: with the pre-fix source (`git stash` of the two touched files), the new test file fails to compile against the old `Func<string, bool>` signature and the not-yet-existing `ResolveTypeName` (`CS1503`/`CS0117`) — the signature change itself is the fix, so the red state is the old API rejecting the new call shape; green after.
- Migrated `tests/WebFormsMarkupTests.cs`'s ~10 pre-existing `Func<string, bool>`-shaped call sites (e.g. `set.Contains(fqn)` → `fqn => set.Contains(fqn) ? fqn : null`) to the new `resolveType` shape — all existing assertions pass unchanged in substance.
- Real-corpus re-verification (diagnostician's reusable probe harness, `Compile Include`s the shipped source verbatim): pre-fix exact-only probe reproduces the original 21 unresolved controls on EnvisionAnywhere.com (0 on login.envisiongo.com — unaffected); the REJECTED naive case-insensitive-bool variant drops the unresolved count to 0 but still leaves all 21 companion fields non-binding (the exact trap this design avoids, now empirically reproduced); the shipped fix (`ResolveTypeName`, verbatim) drives BOTH the unresolved-control count AND the non-binding-field count to 0 on both WSPs — confirming the fields are correctly typed, not just the problem suppressed.
- Suite: **270 pass** (was 263; +7).

## [0.56.0] — 2026-07-07

**`record`/`record class` extending a base record/class silently mis-tagged its base-type edge `implements` instead of `extends`** (Fathom row `dotnet-record-basetypes-facet-gap` 5.0.124.2b.1, follow-on residual from 0.55.0 — filed there, independently re-reproduced here before fixing).

### Fixed

- **`ExtractRelationships`'s `isClass` gate (`Program.cs`) tested `node is ClassDeclarationSyntax` only** — `RecordDeclarationSyntax` never satisfies that check, so a `record`/`record class` with a base-list entry always fell to the `else` branch and emitted `implements` regardless of whether the base was actually a base CLASS. Interface implementations happened to tag correctly by coincidence (that branch doesn't depend on the class/interface distinction). Fix: `isClass = node is ClassDeclarationSyntax || (node is RecordDeclarationSyntax rd && rd.Kind() != SyntaxKind.RecordStructDeclaration)` — record structs are excluded from the extends-eligible set on purpose, since a `record struct` can't have a base class, only interfaces.

### Tests

- New fixtures in `tests/RecordDeclarationTests.cs`: a `record` extending a base `record` (`record Dog(...) : Animal(Name)`) now emits `extends`, not `implements`; a plain `class` extending the same base (control) still emits `extends`; a `record struct` implementing an interface still emits `implements` (confirms record structs aren't wrongly promoted to extends-eligible); a `record` implementing an interface still emits `implements` (confirms the pre-existing coincidentally-correct case is unaffected). RED confirmed first: the record-extends-record case failed against the pre-fix build with the exact mis-tag (`Assert.Contains()` — actual edge `("implements", "explicit", "animal")`, not `extends`); the other three fixtures already passed pre-fix (as diagnosed — their correctness was coincidental, not gated by `isClass`), confirmed still green post-fix.
- Suite: **263 pass** (was 259; +4).

## [0.55.0] — 2026-07-06

**C# 9+ `record` / `record struct` declarations were invisible to the graph entirely** (Fathom row `dotnet-record-elements-not-emitted` 5.0.124.2b) — not mislabeled, never emitted at all. A coverage gap on a type-declaration form that's the norm in any modern .NET domain model (DTOs, domain events, CQRS commands/queries).

### Fixed

- **Three dispatch sites switched on Roslyn syntax-node TYPE with no `RecordDeclarationSyntax` case** — `GetElementType` (`Program.cs`) is the actual gate: `GetElementType(node) == null` skips a node before an element is ever created in the canonical-naming pass, and records fell to `_ => null`. `GetDeclarationName` (`Program.cs`) had the same gap (would have returned `null` too, a second trap left for whoever eventually widened the first gate). `MapToDeclarable` (`Program.Analysis.cs`) — the role/flavors/capabilities metadata shared with the TS analyzer's declarable vocabulary — also had no case, so `role` stayed null and no declarable metadata attached even once the element itself existed. All three now carry a `RecordDeclarationSyntax` case, disambiguated by `.Kind()` (confirmed empirically, not assumed): `SyntaxKind.RecordDeclaration` (`record` / `record class`, a CLR class) maps to `"class"`; `SyntaxKind.RecordStructDeclaration` (`record struct`, a CLR struct) maps to `"struct"`. `GetDeclarationName` uses `.Identifier.Text`, same as the existing class/struct cases. The walker itself (`root.DescendantNodes()`) needed no changes — it already visits every descendant generically and was only ever filtered by these two gates.

### Added

- **`flavors["isRecord"] = true`** (`Program.Analysis.cs`, `MapToDeclarable`) — a record now stays distinguishable from a plain class/struct once visible, rather than trading one coverage gap for a smaller kind-hiding one.

### Tests

- New `tests/RecordDeclarationTests.cs` — 4 spawn-based fixtures: a positional record class (`record Person(string Name, int Age)`) now emits a `"class"` element where pre-fix NONE existed; an explicit-body record class (`record class Point { int X { get; init; } }`) emits the type element plus its explicit member (`X`, `"property"`); a positional record struct (`record struct Coordinates(double Lat, double Lng)`) maps to `"struct"`, confirmed via both `kind` and the `metadata.flavors.typeKind` facet; a factual pin (not a fix) confirming positional record parameters (`Name`) surface as `"parameter"`-kind child elements via the pre-existing generic `ParameterSyntax` case — Roslyn represents primary-constructor params as `ParameterSyntax` in the syntax tree, not synthesized `PropertyDeclarationSyntax` (those only exist in the semantic model), so re-classifying them as properties needs semantic-model work and is out of scope for this coverage fix. RED confirmed first: 3 of 4 tests failed against the pre-fix build for the right reason (`Assert.True()` — "no element emitted" — zero elements, not a different error); green after.
- Suite: **259 pass** (was 255; +4).

## [0.54.1] — 2026-07-06

**Regression pins for the dash-segment-boundary check in `SemanticCatalog.IsKnownExternalNamespace`** (5.0.113 reviewer F1). The reviewer confirmed the boundary is currently correct — `Systematic.*` and `MicrosoftFoo.*` are rejected, not false-positively tagged external — but no fixture pinned it: a future "simplification" to a bare `StartsWith(root)` (dropping the `+ "-"` segment terminator) would pass every existing test while silently over-tagging any app-own namespace whose name happens to start with `system`/`microsoft`/etc.

### Tests

- Two new negative pins in `tests/ImportsExternalTaggingTests.cs`: `using Systematic.Foo;` and `using MicrosoftFoo.Bar;` → neither `imports` edge carries `metadata.external`/`resolutionProvenance`. Sensitivity witnessed: temporarily weakening the catalog's match to bare `StartsWith(root)` (dropping the segment terminator) made both new tests FAIL for the right reason (`Assert.Null()` — actual `True`, i.e. the edge got unexpectedly tagged external); restored, both green.
- Suite: **255 pass** (was 253; +2).

## [0.54.0] — 2026-07-06

**`using` directives now tag known-external namespaces — the 5.0.113 imports-resolution wave, C# leg** (Fathom row `imports-resolution-near-total-dangling` 5.0.113). The artifact-level `imports` edge carries no `targetRef` — a namespace is not a single declaring file, so it structurally cannot — which made EVERY `using` read as a plain dangling edge downstream (coupling metrics, callee surfaces), even though the overwhelming majority target the BCL/framework (`System.*`, `Microsoft.*`) and are honestly, permanently external. Corpus-measured 2026-07-06: ~11.8k EnvisionWeb `imports` edges, 0% resolved, dominated by `System.*`/`Microsoft.*`.

### Added

- **`SemanticCatalog.IsKnownExternalNamespace`** (`Program.SemanticCatalog.cs`) — the analyzer-owned known-external-namespace-root catalog (the 3.4-wave analyzer-classifies-against-its-own-vocabulary principle; the engine stays vocabulary-free). Root set: `System` / `Microsoft` / `Windows` (the three BCL/platform roots the catalog didn't yet carry as bare namespace strings) plus every distinct framework-namespace root the catalog's OTHER tables already encode — reused, not re-invented: `Xamarin` / `UIKit` / `Android` / `AndroidX` / `Telerik` (from `BoundaryBaseTypes`) and `Dapper` (from `StoreNamespaces`). Matched against the same kebab-lowercase canonical form the `imports` edge already emits, on a full dash-segment boundary (never a bare substring).
- **`imports` edges targeting a known-external namespace now carry `metadata.external: true` + `resolutionProvenance: "external-library"`** (`Program.cs`, the `using`-directive emission loop) — mirroring the `calls`/`references` external-edge shape already established (Fathom 5.0.80 / H2, `ProvenanceHelpers.ExternalLibrary`). `using Foo = System.Collections.Generic.Dictionary<...>` (alias) and `using static System.Math` (static) classify by their underlying namespace for free — `usingDirective.Name` is already the alias/static TARGET, not the directive's own surface form, so no special-casing was needed for either shape. Unmatched (app-own) namespace usings are UNCHANGED — they stay plainly dangling (honest; resolving them against in-corpus declaring files is parked row `csharp-using-own-namespace-resolution` 5.0.113.r2).

### Tests

- New `tests/ImportsExternalTaggingTests.cs` — 5 spawn-based fixtures: `using System;` / `using System.Collections.Generic;` → tagged external (one test, two assertions); `using Microsoft.AspNetCore.Something;` → tagged external; an app-own namespace using (`Envision.Services`) → NO external metadata (negative pin); an aliased using targeting `System.*` → tagged external by its underlying target; `using static System.Math;` → tagged external. RED confirmed first: 4 of 5 failed against the pre-fix `Program.cs` (`Assert.True(system.External, ...)` etc. — `metadata` absent entirely); the negative pin passed both before and after (unaffected by the fix), as expected.
- Suite: **253 pass** (was 248; +5).

## [0.53.0] — 2026-07-06

**`overrides` edge targetRef param-qualification mismatch — 73% of overrides edges dangled on EnvisionWeb** (Fathom row `dotnet-overrides-targetref-param-qualification` 5.0.112, probe-traced 2026-07-06).

### Fixed

- **`overrides` edge targetRef built from a different param-signature construction than the target method element's own natural key** (`Program.cs`, the `TypeDeclarationSyntax overrideContainer` block). The emitter rebuilt the overridden method's parameter signature BY HAND from the Roslyn symbol via `IParameterSymbol.Type.ToDisplayString()` — which fully-qualifies BCL/namespaced parameter types (`System.EventArgs`) — while the target method element's natural key is built by `NamingHelpers.GetParamSignature` from the DECLARATION SYNTAX, using the short, source-written type name (`EventArgs`). The two constructions diverged on any BCL/namespaced parameter type, so the emitted `targetRef` never matched a live node's key. Corpus-verified on EnvisionWeb: 121 of 166 (73%) `overrides` edges dangled, 116 of them at INTERNAL targets — the `OnInit(EventArgs)` override family alone (58 edges) dangled on `system-eventargs` vs the live node's `eventargs`. Fix: the overrides emitter now resolves the overridden method's declaring syntax (the same declaring reference used for the cross-file targetRef path) and calls `NamingHelpers.GetParamSignature` on it directly — the single source of truth already shared by element natural-key construction and intra-class `callsMethod` resolution (Fathom 5.0.68.1) — instead of duplicating the normalization from the symbol side. Methods with no declaring syntax (external/metadata parents) keep the symbol-based fallback for the informational `targetName`; those never carry a `targetRef` so they weren't part of the dangling population.
- No other param-typed key construction shares the broken path — audited every `ToDisplayString()` call site in `Program.cs`; the other two (`ResolveExternalCallName`, `ResolveExternalPropertyName`) build labels for confirmed-EXTERNAL members that intentionally carry no `targetRef`, not internal resolution targets.

### Corpus note

This ships the fixture-level regression proof only. Re-measuring the EnvisionWeb corpus's dangling-edge count needs a destructive re-analyze of that external corpus — out of scope here (external corpora don't get re-analyzed as part of an analyzer fix); the corpus re-measure rides the next EnvisionWeb rebuild. No consumer migration needed: previously-dangling edges simply re-resolve on next analyze (delete `.fathom/graph.db` + re-analyze, pre-prod convention) — the emitted key SHAPE (`artifactId#name`) is unchanged, only previously-wrong VALUES for BCL/namespaced-param overrides now match.

### Tests

- New `tests/OverridesEdgeTests.cs` — two spawn-based fixtures pinning the resolvability contract (`overrides` edge `targetRef` byte-identical to the target element's own `naturalKey`): a BCL-typed parameter (`EventArgs`, the exact EnvisionWeb `AuthenticatedModalBase`/`OnInit` shape) and an INTERNAL namespaced parameter type (`MyApp.Events.CustomEventArgs` vs the source-written `CustomEventArgs`), pinning both qualification directions. Both RED confirmed first against the pre-fix `Program.cs` (`Assert.Equal()` failures reproducing the exact mismatch: `oninit-system-eventargs` vs `oninit-eventargs`; `render-myapp-events-customeventargs` vs `render-customeventargs`); green after.
- Suite: **248 pass** (was 246; +2).

## [0.52.0] — 2026-07-04

**Boundary-drift wave fix round — two-gate findings on the analyzer-side semantic-catalog port (3.4.1).** Four fixes: a real corpus-confirmed regression, a reviewer-accepted sanctioned delta (documented + pinned, not parity-restored), a silent-pass test-observability gap, and a JS-vs-.NET regex parity fix.

### Fixed

- **`generatedSignals` missing on designer FILE elements** (`Program.cs`, `BuildArtifact`) — the deleted engine's `.designer.cs` DESIGNER_SUFFIX check had no kind gate; it ran over EVERY element whose artifact path matched, including the file element itself (`is-generated.ts`: "the file node, the class, and each member ... are all generated"). The port originally called `SemanticCatalog.ClassifyGeneratedSignals` only per declaration, silently dropping the file element's signal — EnvisionWeb `isGenerated` regressed 10,301 → 10,057 (−244 = the designer file elements). Now also stamped on the artifact object itself (`artifact["generatedSignals"]`), computed the same way with an empty raw-annotation list so only the filename-suffix check applies — the exact `"designer-filename"` signal string the catalog already uses for declarations.
- **`CONTROL_KIND_RULES` `\w` Unicode-vs-ASCII parity** (`Program.SemanticCatalog.cs`) — the deleted engine's tables were JS `RegExp` literals, where `\w` is ASCII-only (`[A-Za-z0-9_]`); .NET's `\w` defaults to Unicode word characters. Added `RegexOptions.ECMAScript` to every `ControlKindRules` pattern (legal alongside the existing `IgnoreCase`) so a non-ASCII type-name segment classifies identically to the deleted JS table (byte-for-byte parity), not as a false-positive control-kind match.

### Changed

- **`ClassifyApiCategory` read/write rationale comment corrected + sanctioned delta #4 documented** (`Program.SemanticCatalog.cs`) — the doc comment previously MIS-QUOTED the deleted engine's formula, dropping the leading `read && write ? "mixed"` term. Rewritten to quote the real formula and explicitly record the accepted, NOT parity-restored, delta: the deleted engine tracked `read`/`write` as independent booleans OR'd across an element's edges, so a lone `ExecuteNonQuery` edge (`executenonquery` — a WriteOps entry — contains the substring `query`, a ReadOps entry) set both booleans from itself and classified "mixed" (a substring-collision artifact; same family: `DbCommandBuilder.Get*Command`). This analyzer's per-edge, write-wins classification resolves the same shape to "write" — sanctioned delta #4 (reviewer finding 2026-07-04); corpus-sized: 135 EnvisionWeb elements flip mixed→write (1,954→2,089 write / 346→211 mixed).
- **`ApiCategory_AdoNet_ReadAndWrite_OnExternalCallsEdges` skip is now observable** (`tests/SemanticCatalogEmissionTests.cs`) — the test's two bare `return`s made it pass-green asserting nothing in offline environments. The `dotnet restore`-failure path now writes a distinct message to test output via `ITestOutputHelper` (still skips — genuinely not exercisable offline). The zero-edges path (restore succeeded, MSBuildWorkspace resolved no `calls` edges) now `Assert.True`-fails instead of silently returning — a successful restore means the environment IS exercisable, so zero edges is a real coverage gap, not a skip condition.

### Tests

- New `tests/SemanticCatalogTests.cs` (linked `Program.SemanticCatalog.cs` directly into the test project, mirroring `NamingTests`'s pattern) — 5 direct unit tests: `ClassifyApiCategory` × ExecuteNonQuery-writes / read / non-persistence-null, `MapControlKind` × non-ASCII-no-match / ASCII-still-matches.
- `GeneratedSignals_AttributeAndDesignerFilename` (`tests/SemanticCatalogEmissionTests.cs`) extended with a new `AnalyzeArtifact` harness helper (returns the raw artifact JSON, not just its `elements`) and two new assertions: the designer file's own artifact carries `generatedSignals: ["designer-filename"]`; the non-designer file's artifact carries no `generatedSignals` at all (honest absence, not `[]`). RED confirmed first: `Assert.NotNull()` failure (`designerArtifact` had no `generatedSignals` property) against the pre-fix `Program.cs`; green after.
- `MapControlKind_NonAsciiTypeSegment_DoesNotMatchAsciiOnlyRule` — RED confirmed first against the pre-fix `Program.SemanticCatalog.cs` (`Assert.Equal() Failure: Expected: "other" Actual: "button"` for `"MyNamespace.PörButton"`); green after the `RegexOptions.ECMAScript` fix.
- Suite: **246 pass** (was 241; +5).

## [0.51.0] — 2026-07-04

**Chunk 7 of the boundary-drift correction wave (Fathom row `conformance-enum-language-leak-reconcile`, folded into 3.4.1) — entry-point kind rename.**

### Changed

- **`DetectEntryPoint` E1 WCF heuristic** (`Program.cs`) — emits `"rpc-service"` instead of the language-specific `"wcf-service"` (renamed core value in `@kepello/nodegraph-analysis@3.41.0`'s `EntryPointKind`). The `.svc.cs`-file detection heuristic is unchanged; the framework detail moves to the `trigger` sub-facet (`entryPointTrigger: { framework: "wcf" }`) instead of living in the core kind — the honest slot the tuple shape already carried, not a new wire field.

### Tests

Suite: **241 pass** (was 240). RED confirmed first: the new `EntryPoint_WcfServiceFile_IsRpcServiceWithWcfTrigger` regression test (`tests/CallResolutionIntegrationTests.cs`) failed against the pre-rename build (`Assert.Equal() Failure: Expected: "rpc-service" Actual: "wcf-service"`) before the `Program.cs` edit; green after.

## [0.50.0] — 2026-07-04

**Boundary-drift correction, analyzer-side half** — the analyzer now emits the .NET framework vocabulary as facets instead of leaving it to the shared L1 engine's hard-coded tables (Fathom row `boundary-drift-correction` 3.4.1, chunk 3). Ported VERBATIM from six `nodegraph-analysis` derivation modules (`stereotypes.ts`, `integration-surface.ts`, `dataaccess-surface.ts`, `interaction-surface.ts`, `serialization-surface.ts`, `is-generated.ts`) — same base-type sets, attribute maps, and match precedence, not "improved" (better symbol-based matching stays a filed residual). This is emission-only: the engine still derives these facts from its own tables today; it consumes the new analyzer-emitted facets in a coordinated `nodegraph-analysis` release. Delete `.fathom/graph.db` (or bump the epoch) and re-analyze to pick up the new facets once that release lands — no migration path, pre-prod.

### Added

- `src/Program.SemanticCatalog.cs` — new partial hosting the ported tables + classification helpers: `BoundaryBaseTypes`/`CollectionBaseTypes`/`RootErrorBaseTypes` (stereotypes.ts), `EndpointAttrs`/`HostAttrs`/`ContractAttrs`/`HttpVerbAttrs` (integration-surface.ts), `StoreNamespaces`/`WriteOps`/`ReadOps` (dataaccess-surface.ts), `UiBases`/`LifecycleByName`/`ControlKindRules` (interaction-surface.ts), `FormatByAttr` (serialization-surface.ts), `GeneratedSignalByAttr`/`DesignerSuffix` (is-generated.ts).
- Element facet `baseTypeRoles` — `{role: "boundary"|"collection"|"error", source}[]` classified over the existing `baseTypes` facet. Contract: any element carrying `baseTypes` also carries `baseTypeRoles` (possibly `[]`).
- Element facet `integrationRole` — `{kind, protocol, framework, operation?, verb?}`, endpoint > HTTP-verb > contract > host precedence over the element's own annotations.
- Element facet `interactionRole` — `{entryKind, framework}` class-level skeleton from `UI_BASES` needle-matching over `baseTypes`.
- Element facets `uiLifecycle` / `uiTriggers` — pure name-match (WebForms lifecycle names + the `btnSave_Click` auto-wired convention), deliberately ungated (the engine keeps the UI-parent-class structural gate on its own read).
- Element facet `serializationFormats` — deduped + sorted wire formats from the element's annotations, gated to type-like/member-like element kinds.
- Element facet `generatedSignals` — attribute-derived signals plus the `.designer.cs` filename convention.
- Edge metadata `apiCategory` (`{domain: "persistence", store, operation?}`) on external `calls` edges whose canonical target matches a persistence namespace prefix (`AddExternal`, `Program.cs`).
- Edge metadata `controlKind` alongside the existing `controlType` on generated-companion `accessesField` edges (companion control-field binding, `Program.cs`).

### Tests

New `tests/SemanticCatalogEmissionTests.cs` — 14 spawn-based fixtures, one per family, each pinned to the engine table row it exercises (`baseTypeRoles` × boundary/collection/error/no-match, `integrationRole` × asmx/wcf/webapi-verb, `interactionRole` + `uiLifecycle` on a WebForms `Page_Load`, `uiTriggers` on an auto-wired `btnSave_Click`, `controlKind` on a synthesized WebForms markup-companion binding (host-conditional on the net472 reference-assembly pack), `serializationFormats` × json/data-contract/runtime-serializable + a non-participating-kind control, `generatedSignals` × attribute + `.designer.cs` filename, `apiCategory` × ado-net read/write via a real MSBuild-project `System.Data.DataTable` fixture (bare-directory mode only references corelib, which doesn't carry `System.Data` — SqlClient/EF NuGet packages need a network restore not assumed available, so `DataTable`/`DataRowCollection` stand in as genuine members of the SAME `system-data` namespace prefix)). All 13 non-control-fixture tests witnessed RED against the pre-fix analyzer before the fix; GREEN after. Full analyzer suite 240 pass (was 226; +14 new tests).

---

## [0.49.0] — 2026-06-22

F1 .NET-sibling — type/container elements no longer emit duplicate `calls` edges (constructor / delegates / property-access) mirroring their members' bodies; method → local-function invocation duplication also fixed. Calls are now attributed to the innermost enclosing element node (own-body ownership). Mirrors analyzer-ts 0.46.0; found via the SCIP differential-oracle spike. Fathom row 3.1.1.1.9.1b-F1.

### Fixed

- `src/Program.cs` — `ExtractRelationships`: added `OwnsCallSite(SyntaxNode site)` local helper — walks from `site.Parent` toward the root and returns `true` only when the first enclosing element node IS the current `node` (reference identity; Roslyn red nodes are stable per tree). The `elementNodes` dictionary (the `canonicalByNode` map keyed by element syntax nodes) is passed in as a new required parameter so the helper can test membership without re-deriving the set.
- `src/Program.cs` — `ExtractRelationships` signature: added `Dictionary<SyntaxNode, (string Name, string QualifiedRaw)> elementNodes` parameter (required, not nullable — no silent-degradation). Call site at `Program.cs:591` updated.
- `src/Program.cs` — object-creation sweep (`ObjectCreationExpressionSyntax`): added `if (!OwnsCallSite(creation)) continue;` at the top of the loop body. Previously UNGUARDED — emitted `calls`/`constructor` from any container element whose `DescendantNodes()` reached into member bodies.
- `src/Program.cs` — delegate-assignment sweep (`AssignmentExpressionSyntax` / `+=`): added `if (!OwnsCallSite(assign)) continue;`. Previously UNGUARDED — emitted `calls`/`delegates` from container elements.
- `src/Program.cs` — property/element-access sweep (`MemberAccessExpressionSyntax` / `ElementAccessExpressionSyntax`): added `if (!OwnsCallSite(access)) continue;`. Previously UNGUARDED — emitted `calls`/`property-get`|`property-set` from container elements.
- `src/Program.cs` — invocation sweep (inside the `MethodDeclarationSyntax || LocalFunctionStatementSyntax` guard): added `if (!OwnsCallSite(invocation)) continue;`. This guard already prevented class-level duplication, but a method containing a local function (which is its own element) would double-count the local function's invocations onto the enclosing method. Now correctly attributed to the local function only.

### No migration

Pre-prod — delete `.fathom/graph.db` and re-analyze to refresh the graph. Previously emitted `calls` edges sourced from class/type elements that belong only to the member methods are now absent; any stored edges from those sources are invalid and will not regenerate.

### Tests

One new RED-witnessed regression fixture in `tests/CallResolutionIntegrationTests.cs`:
- `OwnBodyOwnership_ClassDoesNotDuplicateMemberBodyEdges` — witnessed RED (assertion `DoesNotContain` failed: `svc` element had `calls/constructor:widget`, `calls/delegates`, `calls/property-get:widget/value/get`); GREEN after fix. Covers all three failure categories (constructor duplication, delegate duplication, property-get duplication) plus the lambda-attribution preservation assertion (A3). Helper `AnalyzeEdgesPerElement` added to collect edges keyed by element name (enabling per-element attribution assertions).
- Analyzer 226 pass (was 225; +1 new test).

---

## [0.48.0] — 2026-06-12

Stateful per-line classifier — verbatim-string and block-comment interior lines now classified correctly. Closes Fathom row 3.1.1.1.9.1c `l1-loc-classifier-prefix-only-string-block`.

### Fixed

- **F1 — verbatim-string interior lines no longer miscounted as comments:** The stateless classifier checked trimmed-line prefix only, so a line inside a multi-line C# verbatim string (`@"…"`) whose content started with `//` was counted as a comment line → LOC undercounted, `commentDensity` inflated. Fix: `inVerbatimString` state (entered on `@"`, exited when `HasVerbatimStringClose` finds an unescaped `"` on a subsequent line) suppresses the comment check while inside a verbatim string.
- **F2 — non-star block-comment interior lines no longer miscounted as code:** Same as the TS fix — a block comment whose interior line lacks the `*` prefix was classified as code. Fix: `inBlockComment` state classifies all interior lines as comment regardless of prefix.
- **`HasVerbatimStringClose` helper added** for detecting the closing `"` of a verbatim string (handles `""` escaped pairs; C# 11 raw strings are out of scope).

### Test coverage

Two new RED-witnessed regression fixtures in `SizeObservationTests.cs`:
- `F1_VerbatimStringInterior_CommentTokenIsCode` — `//` line inside `@"…"` classified as code.
- `F2_NonStarBlockCommentInterior_IsComment` — non-star block interior classified as comment.

---

## [0.47.0] — 2026-06-11

**Span-consistent per-line LOC classification — out-of-span XML-doc no longer subtracted from LOC** (Fathom row `l0-dotnet-linesofcode-outofspan` 3.1.1.1.9.1b). `ExtractObservation` / `CountCommentLines` had the same out-of-span defect as the TS analyzer: `GetLocation().GetLineSpan().Span` (the physical span, EXCLUDES leading XML-doc trivia) was used for `physicalLinesOfCode`, but `CountCommentLines` called `DescendantTrivia()` which INCLUDES the first token's leading XML-doc trivia (FullSpan). Subtracting out-of-span doc lines from the in-span count floored LOC to 0 for documented elements and silently understated LOC corpus-wide. A second defect: `trivia.ToFullString().Split('\n').Length` overcounted a 10-line doc block as 11 (trailing-newline +1). Both defects are resolved. Policy matches the Swift analyzer reference and the TS fix in `analyzer-typescript@0.44.0` — both analyzers now agree on the unified span-consistent policy.

### Fixed

- `src/Program.Analysis.cs` — `ExtractObservation`: replaced the `CountCommentLines(node)` trivia-based subtraction with a per-line classification loop strictly within `[startLine, endLine]`. Each physical line is classified as exactly one of blank / comment-only / code; the element's leading XML-doc block (out-of-span) is NOT entered into the LOC formula — its signal lives in `documentation.hasDocComment` / `documentation.docCommentLineCount` (unchanged). A code line with a trailing `// note` starts with code text → classified as CODE (mixed-line policy). `linesOfCode` = physical − blanks − in-span-comment-only lines; `commentDensity` = in-span-comment-only / physical, bounded `[0,1]` by construction.
- `src/Program.Analysis.cs` — `ExtractDocumentation`: `docCommentLineCount` now counts newline characters across all contiguous leading doc-comment trivia items rather than using `ToFullString().Split('\n').Length` on the first trivia item. Each `///` line ends in exactly one `\n`, so the count is exact (no trailing-newline +1 overcount).
- `src/Program.Analysis.cs` — removed `Math.Max(0, …)` floor on `linesOfCode`; replaced with an `InvalidOperationException` throw — a negative after span-consistent classification is structurally impossible and indicates a bug (no-silent-degradation, Fathom row 3.1.1.1.9.1b).
- `src/Program.Analysis.cs` — `CountCommentLines` method removed (replaced by the per-line scan in `ExtractObservation`); a comment records the rationale (mirrors the TS `// countCommentLines removed` comment in `ts-metrics.ts`).

### No migration

Pre-prod — delete `.fathom/graph.db` and re-analyze to refresh the graph. `linesOfCode` values will increase for documented elements (previously floored to 0 or understated); `commentDensity` values will decrease (previously counted out-of-span doc lines). Any cached or stored metrics from prior analyzer versions are invalid and must be regenerated.

### Tests

- `tests/SizeObservationTests.cs` — new file: 8 tests, 5 regression categories (all witnessed RED on the pre-fix code): cat-1 `Cat1_DocumentedMethod_DocLinesGtCodeLines_LocNotZero` (linesOfCode expected 0, actual 3 post-fix; `commentLineCount` expected 0 was actually 11); cat-2 `Cat2_DocumentedLargeMethod_LoCUnderstated` (linesOfCode expected 9 was 5; `commentLineCount` expected 0 was 4); cat-3 `Cat3_TrailingSameLineComment_CountsAsCode` (`commentLineCount` expected 0 was 2); cat-4 `Cat4_CommentDensityBoundedLeOnePointZero` (`commentDensity` expected ≤1.0 was 1.6666); cat-5 `Cat5_DocCommentLineCount_TenLineDocBlock_IsExactlyTen` (`docCommentLineCount` expected 10 was 11). Plus 3 existing-semantics regression guards confirming documentation fields survive the fix.
- `tests/NodegraphAnalyzerDotnet.Tests.csproj` — added `<Compile Include="../src/Program.Analysis.cs" …/>` so in-process tests can call `AnalysisHelpers.ExtractObservation` directly (without spawning the DLL).
- Release DLL rebuilt via `npm run build` (`dotnet publish -c Release -o dist/`).
- Analyzer 223 pass (was 215; +8 new tests).

## [0.46.0] — 2026-06-11

**Emit taxonomy-canonical limitation kinds — fold `csharp-*` namespace** (Fathom row `limitation-kind-taxonomy-bypass` 5.0.98.1). The four `csharp-*` limitation kind strings emitted by this analyzer are renamed to their language-agnostic canonical taxonomy names, now that the taxonomy accepts them as first-class kinds (per `@kepello/nodegraph-limitations@0.6.0`).

### Changed

- `Program.ReferencesFree.cs`: `ReferencesFreeReporting.LimitationKind` constant renamed from `"csharp-references-free-compilation"` → `"references-free-compilation"`.
- `Program.cs` line 548: canonical-name-collision kind string renamed `"csharp-canonical-name-collision"` → `"canonical-name-collision"`.
- `Program.cs` line 829: ambiguous-overload kind string renamed `"csharp-ambiguous-overload"` → `"ambiguous-overload"`.
- `Program.cs` lines 2052 + 2206: both unresolved-call emit sites renamed `"csharp-unresolved-call"` → `"unresolved-call"` (fold into the existing taxonomy kind — it was always this kind).

### No migration

Pre-prod — delete `.fathom/graph.db` and re-analyze to refresh the graph. Existing records carrying the old `csharp-*` strings are invalid under the taxonomy-bypass forcing function added in `@kepello/nodegraph-limitations@0.6.0`; they are cleared on re-analysis.

### Tests

- `tests/ReferencesFreeReportingTests.cs`: assertion updated from `"csharp-references-free-compilation"` → `"references-free-compilation"`. RED witnessed: `Expected: references-free-compilation / Actual: csharp-references-free-compilation`.
- `tests/CallResolutionIntegrationTests.cs`: assertion updated from `"csharp-canonical-name-collision"` → `"canonical-name-collision"`. RED witnessed: `Item not found in collection ["csharp-references-free-compilation", "csharp-canonical-name-collision"]`.
- Release DLL rebuilt via `npm run build` (`dotnet publish -c Release`).
- Analyzer 215 pass (unchanged count; 2 tests now assert the new kind strings).

## [0.45.0] — 2026-06-10

**Reference qualification — bare `references/identifier` and `references/generic-constraint` edges eliminated** (Fathom row `dotnet-l0-ref-qualification` 5.0.93). The A2 census found C# had a ~45.8% ambiguous-tail rate on `references` edges — tails resolving to bare member names (`run`, `process`, `value`) that the substrate tail-matcher could mis-bind, since .NET natural keys are TYPE-QUALIFIED (`runner/run-parsedargs`). Same-file members don't bind bare even within their own artifact. Two emission sites replaced with semantic-model resolution:

### Fixed

- **Identifier references** (`references/identifier`, ~83-93 bare edges per corpus file): the identifier scan now calls `ResolveIdentifierTarget` — a new resolver modelled after `ResolveCallTarget`/`ResolveTypeTarget` — for each `IdentifierNameSyntax`. Workspace-resolved symbols (types, methods, properties, fields, events) emit with a qualified targetRef (`MakeNaturalKey(file, qualifiedRawName)`) including same-file symbols. External/BCL symbols (no `DeclaringSyntaxReferences`) emit no edge — no phantom bare target the substrate can't resolve. The unused `Add` helper (previously the only caller for generic-constraint) is removed.
- **Generic-constraint references** (`references/generic-constraint`): replaced `allNames.Contains` guard + bare `Add(...)` with `ResolveTargetFile(constraint.Type)` + `AddWithTargetRef` — the same two-step pattern the `return-type`/`parameter-type` paths use. Cross-file workspace constraints now get a targetRef they were previously missing entirely; external constraints (e.g. `IDisposable`) emit no edge.

### No migration

Pre-prod — delete `.fathom/graph.db` and re-analyze to refresh the graph. Bare `references` edges in existing graphs are not back-filled; the new qualified edges appear on next analysis.

### Tests

- 6 new spawn-based regression tests (`RefQualificationTests`) — one per fixture category: C1 (cross-file identifier → no bare edge), C2 (same-file method identifier → qualified targetRef, no bare), C3 (external BCL symbol → no edge), C4 (generic constraint on cross-file workspace type → qualified targetRef), C5 (generic constraint on external type → no edge), F6 (two files with same-named class + same-method → calls edge carries file-qualified targetRef). TDD: C2 and C4 were red on the pre-fix DLL; all 6 green post-fix. Analyzer 215 pass (was 209).

## [0.44.0] — 2026-06-09

**`controlType` on generated-companion field edges** (Fathom row `interaction-surface-facet` 5.0.82 — the H4 enrichment's analyzer half). The synthesized companion partial is the only place a markup control field's TYPE is knowable; the `accessesField` edge now carries `metadata.controlType` (the field's declared FQN, `global::` stripped) so the engine's `interactionSurface` can derive `controlKind` (button / label / grid / …) without a source or markup read.

## [0.43.0] — 2026-06-09

**Web Site Project control-field SYNTHESIS** (Fathom row `dotnet-system-web-framework-ref-resolution` 5.0.87 — the id is a misnomer kept per the no-rename rule; the root is NOT System.Web references). WSPs have no `.designer.cs` — control fields (`lbl`, `cb`, `ReportTitle`) are declared ONLY in the `.ascx/.aspx/.master` markup (ASP.NET generates the partial-class fields at runtime), so to the analyzer the codebehind identifier was UNDECLARED and every control binding (`lbl.Text=`, `cb.Items.Add`, `ReportTitle.ReportTitle=`) dropped — the EnvisionWeb 107 method-`unclassified` residual. The analyzer now does what the ASP.NET page compiler does, statically.

### Added

- `Program.WebFormsMarkup.cs` — markup parsing + companion synthesis:
  - `WebFormsMarkupParser.Parse` — directives (`Control`/`Page`/`Master` → `Inherits`+`CodeFile`; `Register` → tag-prefix mappings, **multi-line directives supported** — the real corpus wraps them) + `runat="server"` control discovery (id + tag, case-insensitive). Controls nested inside `*Template` elements get NO field (ASP.NET scopes them to naming containers). Server comments (`<%-- --%>`) hide markup; html comments do NOT (matching runtime behavior).
  - `WebFormsMarkupParser.ParsePagesControls` — web.config `<pages><controls><add tagPrefix=…/>` site-wide registrations (on EnvisionWeb this — not `<assemblies>` — is what declares `telerik:` → Telerik.Web.UI).
  - Tag→type mapping: `asp:` probes `System.Web.UI.WebControls` → `System.Web.UI` → `WebControls.WebParts` (ScriptManager/UpdatePanel live outside WebControls); html server controls map per the `HtmlControls` table (input by `type=`, unlisted → `HtmlGenericControl`); custom prefixes via per-file `Register Namespace/Assembly` + web.config registrations; **`Register Src=` user controls resolve to the target markup's `Inherits` class — IN-SOURCE**, so the dominant EnvisionWeb SetLabels shape (`ReportTitle.ReportTitle=`) emits real cross-file `targetRef` edges, not external ones.
  - `WebFormsCompanion.BuildCompanions` — ONE merged `// <auto-generated>` partial per codebehind class (`protected global::<type> <id>;`), deduped across markup files (no CS0102 error-symbol poisoning), skipping ids the codebehind already declares (ASP.NET's own rule).
- `WebSiteProjectLoader` injects the companions into the WSP `Compilation` ONLY — never the per-file artifact map — so no phantom artifacts exist and the markup stays definitionally non-ingested. Edges to synthesized fields emit in the external shape tagged **`resolutionProvenance: "generated-companion"`** (the H2 value scoped out in 0.42.0, now live).
- WSP references extended to assemblies declared via `<pages><controls>` + `<%@ Register Assembly= %>` (located in `bin/`, same declared-not-globbed policy as `<assemblies>`).

### Dangling-edge guards (the 5.0.77 coexistence hazard)

- The partial-class member index (0.31.0) unions the companion's fields automatically — that's the mechanism resolving the accesses — but the 5.0.77 cross-file `targetRef` logic would have pointed edges at the companion's natural key (a non-ingested file → strict-edge-check abort). Companion-declared members instead emit the established external edge shape (`metadata.external` + `generated-companion`); `ResolveTargetFile`/`ResolveCallTarget`/`ResolveTypeTarget`/`ResolveAccessorTarget` resolve through `FirstNonCompanionDeclRef` (a WSP codebehind class is partial across its real file AND the companion — `DeclaringSyntaxReferences[0]` could be either).

### No silent degradation

- A registered type whose assembly can't be located still gets its field synthesized with the known FQN — the field access resolves, member edges drop honestly (regression Variant B) — plus ONE proportional per-WSP `problem` naming every unresolved control (capped detail). An unregistered prefix (no type name derivable) skips the field, also named in the problem. Neither path reads as success.

### Tests

- 23 unit tests (`WebFormsMarkupTests`) — one per pattern category: directive shapes (multi-line, lowercase attrs, `Reference` ignored), control discovery (runat/id case, html `type=`, template-skip), web.config registrations, asp:/custom/Src/html type mapping, unresolved-prefix problems, companion generation (merge/dedupe/skip-declared/namespace-wrap), Src path resolution (`~/` + relative), companion-path predicate. 2 spawn integration tests (`WebFormsCompanionIntegrationTests`) over a real WSP fixture (.sln + markup + codebehind): every pattern category's edge shape asserted (generated-companion accessesField, external-library member write, IN-SOURCE user-control targetRef, Variant-B honest drop, loud problem, codebehind-declared id not duplicated) + the umbrella invariant that NOTHING carries a targetRef into a companion path. Analyzer 209 pass.

## [0.42.0] — 2026-06-07

**Edge `resolutionProvenance`** (Fathom row `edge-resolution-provenance` 5.0.80; H2 of the 2026-06-07 context-sufficiency audit). An external edge previously carried only `metadata.external` (boolean) — the five distinct non-in-source cases were indistinguishable, so "honest library boundary vs analyzer bug" stayed a source-read (the recurring 5.0.75-vs-5.0.76 "emits only references" ambiguity). External edges now carry `metadata.resolutionProvenance`; the engine persists it via the same edge-metadata passthrough as `external` (no substrate change).

### Added

- `metadata.resolutionProvenance` on external `calls`/property-set edges. Values emitted: **`external-library`** (resolves to a referenced-assembly symbol — BCL/NuGet, e.g. `this.Rows.Add(x)` → System.Data) and **`dynamic`** (a string/value-keyed indexer with no static member target — `Session[k]=v`, `ViewState[k]`, dictionary indexers — the irreducible reflective tail). In-source resolved edges stay UNTAGGED (absence == `in-source`).
- `ProvenanceHelpers` (`Program.Provenance.cs`) — pure classifier from the resolved Roslyn symbol.

### Scoped out (documented, tracked follow-ons)

- `generated-companion` — non-ingested generated/markup companions (WebForms `.ascx` control fields) don't resolve today; they land with H4 (interactionSurface). Targets in INGESTED generated files (typed-DataSet `.Designer.cs`) are in-source; their generated-ness is answerable from H1's `[GeneratedCode]` annotation.
- `framework-injected` — needs base-chain analysis; external framework members default to `external-library`.
- `resolver-gap` — already surfaced file-level by the references-free Limitation (5.0.72); a per-edge tag is redundant pending need.

### Tests

- 3 in-process classifier unit tests + 3 spawn wire tests (external-library call, dynamic indexer vs named external property, in-source-stays-untagged invariant) — TDD red confirmed by running the wire tests against the pre-wiring DLL. 2 engine round-trip tests (`edge-provenance-ingest.test.ts`, both backends). Analyzer 182 + engine 556 pass.

## [0.41.0] — 2026-06-07

**Attributes/annotations (conformance group A8) are now EMITTED** (Fathom row `dotnet-l0-attribute-emission` 5.0.79; H1 of the 2026-06-07 context-sufficiency audit). The `annotations` wire facet was DEFINED in the protocol but emitted by no analyzer (deferred to a "Stage 2" that never landed). In .NET, attributes carry the semantic signal for nearly every non-UI boundary — auth (`[Authorize]`), ORM mapping (`[Table]`/`[Column]`/`[Key]`), service contracts (`[WebMethod]`/`[ServiceContract]`/`[OperationContract]`), generated-code provenance (`[GeneratedCode]`/`[DebuggerNonUserCode]`), test membership (`[Fact]`/`[TestMethod]`), serialization (`[DataContract]`/`[Serializable]`) — so their absence forced a source-read corpus-wide. The engine persists the facet unchanged via the existing deny-list passthrough (`collectConformanceFacets`); no substrate change required.

### Added

- `AnnotationHelpers.Extract` (`Program.Annotations.cs`) emits `annotations` on every declaration carrying attributes (types/methods/properties/fields/events/ctors/operators/enum-members/accessors/parameters). Each annotation has `name` (short, as-written), `qualifiedName` (resolved attribute type FQN, omitted when unresolvable — never guessed), positional `args`, and `namedArgs`.
- A8 Level-2 typed arg primitives: `string` / `number` (incl. negative literals) / `boolean` / `identifier` (dotted member-access permitted). Anything else takes the `expression` escape hatch.

### No silent degradation

- An `expression`-fallback arg emits a J1 limitation (`kind: "fallback-annotation-arg"`) so the precision loss is observable, never silent.

### Scoped out (tracked follow-on)

- The `type-ref` arg kind and its companion `references` edge (`subtype: "annotation-arg"`) for `typeof(T)` args: currently take the honest `expression` fallback (+ limitation), not a silent drop.

### Tests

- 16 in-process unit tests over `AnnotationHelpers` — one fixture per attribute CATEGORY (auth/ORM/service-contract/generated/test/serialization) + the full arg-kind matrix + named-args + bare-attribute + expression-fallback/limitation + qualifiedName resolution (TDD red confirmed: 16 fail on the stub, pass on the impl). 1 end-to-end NDJSON wire test (facet + limitation flow through the built analyzer). 2 engine round-trip tests (`annotations-ingest.test.ts`, both backends). Analyzer 176 + engine 554 pass.

## [0.40.0] — 2026-06-07

Cross-file partial-class `callsMethod` edges now carry a **cross-file `targetRef`** (Fathom row `dotnet-msbuildworkspace-documents-completeness` 5.0.77 — the partial-`callsMethod`-targetRef root). The intra-class member-index fix (`@0.31.0`) made a partial type's cross-file calls EMIT — a report constructor in `Foo.cs` calling `InitializeComponent` declared in `Foo.Designer.cs` — but the edge's bare `class/member` targetName was resolved by the overlay WITHIN the source artifact (`{Foo.cs}#…`), so it couldn't bind to the member's element in the OTHER partial file (`{Foo.Designer.cs}#…`) → the edge dangled. Latent (the edges emitted+dangled since 0.31.0) until `fathom analyze`'s strict-edge-check (enabled for C# `@fathom-cli 4.36.0`, 2026-06-01) surfaced it: **529 unresolvable internal edges aborted analysis of EnvisionWeb** (every report partial's `ctor → InitializeComponent`). The MCP `ensureWarm` measure path doesn't run the strict-check, so the 326→224 / →107 measures succeeded throughout.

### Fix

- In the intra-class edge emission, build a member→declaring-file map across the type's partial declarations (same source as the member index). When a `callsMethod`/`accessesField` target is declared in a DIFFERENT partial file, emit an explicit `targetRef = MakeNaturalKey(canonicalizeFilePath(memberFile), qualifiedTarget)` — byte-identical to the member element's own natural key (case-canonicalized file). Same-file members keep the bare targetName (the overlay resolves intra-artifact). Ambiguous names (same name in 2+ partials) fall back to the bare name.

### Diagnosis correction

The 5.0.77 row first read as a `project.Documents`-completeness/path issue (case/space-named files). Verify-first disproved it: **4 of the 5 dangling sample files have exact-matching csproj/disk paths** — `InitializeComponent` IS in the compilation; the dangling is purely the missing cross-file targetRef. The Documents-completeness item (general references-free degradation for case/space-named files) remains a smaller, separate sibling on the row.

### Validated (raw EnvisionWeb)

- The previously-dangling `clientphoneemailduplicates/constructor` and `webformsubmissionbyclient/constructor` → `initializecomponent` edges now carry a `targetRef` that **resolves to the emitted element** (`.Designer.cs` key). Full `fathom analyze` + re-measure: pending.

### Tests

- 1 spawn integration (partial type across two files: cross-file `callsMethod` carries a targetRef; same-file stays bare; TDD red confirmed by neutering the cross-file branch). 159 pass.

## [0.39.0] — 2026-06-06

Build-excluded (dead/uncompiled) `.cs` files are now **omitted from the corpus**, not analyzed references-free (Fathom row `dotnet-csproj-compile-set-coverage` 5.0.74). The analyzer discovers `.cs` by walking the source tree; a file inside a SUCCESSFULLY-LOADED project's directory but NOT in its compiled document set (not in the csproj's `<Compile>`) is code the build doesn't compile on ANY platform — dead generated output (an orphaned typed-DataSet `.Designer.cs` whose `.xsd` was dropped), excluded subfolders, leftovers. Analyzing them references-free polluted the corpus with non-built code and produced unresolved-symbol noise (this was the determination's *largest* "method-unclassified" bucket — the `EnvisionOnlineDataSet1.Designer.cs` `add*Row`/`remove*Row`/`initVars`, which the build excludes).

These are DISTINCT from a true orphan (a `.cs` under no loaded project at all — still references-free, the 5.0.72 path). Generated-at-build files whose source IS in the csproj (an `.xsd`/`.tt` with a generator) remain in the project's Documents and stay analyzed.

### Added

- **`CompileSetCoverage`** (`Program.CompileSetCoverage.cs`) — `IsUnderAnyProjectDir` (build-excluded predicate), `ParseCompiledFilePaths` (authoritative `<Compile Include>` set from the csproj XML), + `BuildSummaryProblem` (one proportional `warning` naming the largest roots).
- `MSBuildIntegration.LoadProjects` now surfaces the successfully-loaded project directories (from the loaded solution, so a project that FAILED to load doesn't mark its files build-excluded).
- The run **enumerates every build-excluded file** to stderr — so a re-analysis captures the full dead-code AUDIT, not just the summary count.

### `project.Documents` is unreliable as the compiled-set signal — backstopped by csproj `<Compile>`

A first cut keyed "build-excluded" off MSBuildWorkspace's `project.Documents`, which **over-excluded**: `Documents` silently drops `<Compile>` files whose declared path differs from disk by **case** (`Foo.designer.cs` vs `Foo.Designer.cs`), by **separator/subfolder** (`code\App_Start\Bundle.cs`), or by special-folder quirks — even though the build (case-insensitive fs) compiles them. Excluding such a file orphaned its members (a partial's `InitializeComponent`) → unresolvable internal edges (402 on EnvisionWeb, caught by the post-ingest invariant). Fix: a file is build-excluded only when it's in **NEITHER** `Documents` (case-insensitive on macOS/Windows; Ordinal on Linux) **NOR** any csproj's literal `<Compile>` set (parsed from the XML — the authoritative source). A compiled-but-`Documents`-dropped file is therefore NOT excluded; it falls to references-free (benign, members preserved). Only genuinely-uncompiled files (absent under any casing/path) are excluded — on EnvisionWeb, **10** (the orphaned typed-DataSet `EnvisionOnlineDataSet*.Designer.cs`, dead duplicate copies in `App_code/` vs `code/`, `Resources.Designer.cs`, etc.); pre-flight invariant replay confirms **0 unresolvable call edges** post-exclusion.

### Behavior

- A build-excluded `.cs` is omitted from analysis (no artifact emitted) + counted in the warning. NSD: observable, never a silent drop.

### Validated (raw EnvisionWeb)

- **20 of 1,864 files EXCLUDED** (ReportLib 15, EnvisionReportSiteHTML5 3, CloudCore 2) — the orphaned typed-DataSet among them. References-free dropped to **0** (those 20 were the entire references-free residual; they're dead, not under-resolved).

### Tests

- 1 spawn integration (old-style csproj: in-`<Compile>` analyzed, NOT-in-`<Compile>` omitted + warning, **case-mismatched `<Compile>` NOT excluded**; TDD red confirmed by bypassing the branch) + 6 unit (`IsUnderAnyProjectDir` under/sibling-collision/empty; `ParseCompiledFilePaths` backslash/relative/wildcard-skip; `BuildSummaryProblem` null/warning-roots). 158 pass.

## [0.38.0] — 2026-06-06

Old-style **csproj bare-GAC framework references** now resolve cross-platform (Fathom row `dotnet-l0-external-symbol-resolution-residual` 5.0.76.a). An old-style (non-SDK) .NET Framework csproj references framework assemblies as bare GAC refs (`<Reference Include="System.Data" />`, no `HintPath`). On macOS/Linux there is no GAC, and old-style projects don't auto-import the `Microsoft.NETFramework.ReferenceAssemblies` pack, so MSBuild's RAR couldn't resolve them — `System.Data` et al. became error symbols and every member access through them (DataTable manipulation, the whole report-query data layer) silently dropped its edge. This is the EXACT mechanism the WSP support (5.0.73) handles, but for csprojs.

### How MS resolves this (the mechanism we now use)

.NET Framework's GAC/Reference-Assemblies are Windows-only. The cross-platform path is the `Microsoft.NETFramework.ReferenceAssemblies.netXX` NuGet pack, which sets **`TargetFrameworkRootPath`** → its `build/` dir; RAR then resolves `<root>/.NETFramework/vX.X/<assembly>.dll`. SDK-style projects auto-import the pack; old-style csprojs don't. So we feed MSBuildWorkspace the same property.

### Added

- **`FrameworkReferenceResolver.DiscoverReferenceAssemblyDirs`** — finds every restored ReferenceAssemblies pack (newest version per TFM) → its `(versionDir, path)`.
- **`FrameworkReferenceResolver.BuildCombinedReferenceAssemblyRoot`** — assembles ONE root (symlinking each pack's `vX.X` dir) so a single workspace-global `TargetFrameworkRootPath` serves projects of any TFM (RAR selects by each project's `TargetFrameworkVersion`). Non-Windows only (Windows resolves bare refs from the GAC); null when no packs restored (analysis stays references-free, flagged by the 5.0.72 Limitation — NSD).
- `MSBuildIntegration.LoadProjects` passes `TargetFrameworkRootPath` to `MSBuildWorkspace.Create` when the combined root is available.

### Validated (raw EnvisionWeb, production DLL)

- **ReportLib `system.data` external edges: 0 → 1,095** (+3,656 total external edges); MSBuild load **3 problems → 0**. ReportLib's compiled data layer, previously invisible, now resolves.
- **Scope note:** the determination's *largest* method bucket — the typed-DataSet `EnvisionOnlineDataSet1.Designer.cs` (`add*Row`/`remove*Row`/`initVars`) — is NOT fixed by this and is NOT a resolution gap: that file is **dead generated code absent from the csproj's `<Compile>` set** (and unreferenced by any compiled code workspace-wide), so the analyzer's references-free directory-walk fallback was analyzing code the build excludes on *every* platform. That's sub-cause (c) → row `dotnet-csproj-compile-set-coverage` (5.0.74), re-scoped to EXCLUDE build-excluded `.cs` (with a warning), not resolve it.

### Tests

- 1 integration (old-style net48 csproj + bare `System.Data` ref → `this._t.Rows.Add(r)` resolves to `DataRowCollection.Add`; host-conditional + non-Windows) + 4 unit (discovery newest-version/ignore-non-packs/empty; combined-root symlinks/null-when-empty). 151 pass.

## [0.37.0] — 2026-06-05

External-member **behavioral edges** are no longer dropped (Fathom row `dotnet-l0-external-member-call-edges` 5.0.75 — the **root cause** of the 326 EnvisionWeb method-`unclassified`, per determination 3.1.1.1.8). The strict-emit budget silently dropped EVERY edge to an external (no-declaration) symbol, so a method whose body is all external collaboration — `this.Rows.Add(row)` (chained/qualified member call), `Session[k]=v` / `ctrl.Text=x` (external property/indexer write), `_sb.Append(...)` — emitted ONLY a generic `references` identifier edge. With `calls = callsMethod = accessesField = 0`, the (correct) L1 method-stereotype rules saw a no-op → `unclassified`. The miss was broader than the 326: every external-call-heavy method under-counted its external collaboration (a `collaborator-external`/`command` accuracy gap too).

### Added

- **External method invocations** (`ResolveExternalCallName`) — a call whose callee resolves to an external `IMethodSymbol` (no `DeclaringSyntaxReferences`) now emits a `calls`/`external` edge tagged `metadata.external = true`, targetName `{ContainingType}.{method}` (e.g. `system-collections-generic-list-int-add`).
- **External property/indexer WRITES** (`ResolveExternalPropertyName`) — `Session[k]=v`, `ctrl.Text=x` now emit a `calls`/`property-set` edge tagged `metadata.external`. (Reads of external properties are far higher-volume and out of scope here — a noted follow-on.)
- **Strict-emit honored via the tag, not omission**: external edges carry NO `targetRef` (the target is observably unbindable — not a resolvable phantom). The `metadata.external` flag is the no-silent-degradation marker: the edge feeds the behavioral counts (and efferent-coupling/CBO — external collaboration was genuinely under-counted) while staying distinguishable for any consumer that wants to exclude framework coupling.

### Tests

- 2 new `CallResolutionIntegrationTests` (chained/qualified external call → external-tagged `calls`, no targetRef; external property + indexer write → external-tagged `property-set`). 148 pass.

**Pairs with** `@kepello/nodegraph-analysis@3.14.0` (L1 class-rule gaps 3.1.1.1.6). The version bump drives the analyzer epoch → stale memoizations self-invalidate on re-measure.

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
