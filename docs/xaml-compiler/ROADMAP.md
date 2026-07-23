# ProGPU XAML Compiler Roadmap

Status: normative, evidence-gated roadmap

Current product state: pre-MVP, with the WinUI compiler vertical slice proven and the project watch host implemented

Primary MVP framework: ProGPU.WinUI

Long-term scope: framework-neutral compiler platform with WinUI, Avalonia, WPF, MAUI, and user-defined profiles

## How progress is measured

Progress is measured by passed gates, not elapsed time or source-file count. A gate is complete only when its public contracts, positive and negative tests, deterministic artifacts, documentation, clean-room record, and relevant runtime evidence are all present. `FEATURE_MATRIX.md` remains the detailed maturity ledger; this roadmap defines product-level ordering and exit criteria.

The current position is:

| Product gate | State | Meaning |
|---|---|---|
| M0 Compiler architecture | Complete | The framework-neutral Roslyn pipeline, profile boundary, typed construction IR, structured C# emission, source-generator host, Workspaces host, and clean-room rules exist |
| M1 WinUI vertical slice | Complete for MVP breadth; runtime depth remains partial | Representative pages and the unchanged Fluent theme compile, generate Roslyn trees, construct dictionaries/templates, and execute selected binding/state behavior |
| M2 Project preview and watch | Active/advanced | Immutable project preview, semantic delta planning, transactional coordination, CLI preview, and CLI watch exist; full evaluated project-graph input watching and IDE transport remain |
| M3 Coordinated C# metadata updates | Not started as an executable producer | Metadata changes are detected and classified, but Roslyn metadata/IL/PDB deltas are not yet produced and applied through a framework-neutral transport |
| M4 XAML live-patch semantics | Partial | Transactional root replacement and last-good retention exist; namescope/resource/template-aware patching and guaranteed fallback coverage are incomplete |
| M5 Quality and conformance closure | Partial | Strong focused tests and Fluent corpus gates exist; sustained performance, subtree reuse, broader fuzzing, determinism matrices, and visual/runtime conformance remain |
| M6 Productization | Partial | Packages, MSBuild integration, CLI tool packaging, samples, and playground exist; project selection, published-feed install validation, compatibility docs, and release qualification remain |

This means the project is past the compiler-prototype stage but is not yet an MVP. Four blocking product gates remain: executable metadata updates, complete hot-reload fallback semantics, quality closure, and end-user productization. M2 is the current integration gate.

## MVP definition

The MVP is a usable WinUI-first product proving that the same compiler core can host other XAML dialects without redesign. It is not defined as complete parity with every framework or every XAML 2009 feature.

An MVP user must be able to:

1. install packages or the .NET tool without repository project references;
2. build a ProGPU.WinUI project with the incremental source generator and normal MSBuild;
3. compile representative application XAML plus the unchanged external Fluent theme;
4. inspect precise XAML diagnostics and generated Roslyn syntax;
5. run the standalone CLI against a real project;
6. watch XAML and C# inputs, preserve the last good application state after a failed edit, and apply a valid recovery;
7. use the sample playground in real project context;
8. author a framework profile or extension through documented, versioned contracts without changing compiler core.

## MVP gates

### M0 — Architecture and clean-room foundation

State: complete.

Required evidence:

- Roslyn `SourceText`, spans, locations, diagnostics, symbols, compilations, projects, documents, changes, syntax annotations, formatting, and Workspaces are the shared compiler foundation.
- The XAML green/red syntax model is isomorphic to Roslyn where Roslyn does not expose third-party green-node construction.
- Parser, infoset, schema, bound model, construction IR, and hosts have one-way ownership boundaries.
- Framework behavior is supplied through immutable versioned profiles and typed optional capabilities.
- Generated C# is constructed as Roslyn syntax. Generated C# text is output serialization only and is never parsed back as compiler input.
- Clean-room provenance and source audits prevent copied or ported implementation.

### M1 — WinUI compiler and Fluent validation

State: complete for MVP compiler breadth; remaining runtime parity is tracked after MVP unless it blocks ordinary sample use.

Required evidence:

- Class-backed pages and classless dictionaries compile through the same pipeline.
- Names, events, attached members, content, resources, styles, templates, markup extensions, ordinary binding, and compiled binding have representative generated/runtime tests.
- `ProGPU.WinUI.Themes.Fluent` links the external theme input unchanged and compiles it without diagnostic suppression.
- Generated Fluent dictionaries construct Default, Light, and HighContrast partitions.
- Representative Button, CheckBox, and ComboBox templates construct and exercise selected bindings and visual states.
- Every reachable publicly constructible Fluent style target applies, materializes its generated template when present, and completes layout in corpus-wide implicit and declared-target gates.
- Missing object-model surface is added as typed public API rather than hidden through dynamic lookup, reflection, or diagnostic suppression.

### M2 — Project preview, watch, and host protocol

State: active/advanced.

Already implemented:

- immutable `Project`/`AdditionalDocument` preview compilation with unsaved text;
- complete sibling XAML and resource-index compilation;
- stable syntax, semantic, dependency, and metadata delta classification;
- prepare/apply/commit coordination with last-good retention;
- debounced latest-wins watch sessions with cancellation and stale-baseline retry;
- no-op acceptance without unnecessary runtime publication;
- standalone `watch` with human and JSON Lines output plus transactional artifact writes;
- process coverage against a real MSBuild-loaded sample.

Remaining exit work:

- derive the watch input set from the evaluated project and referenced-project graph, including imported props, targets, editor configuration, linked files, and resources outside the project directory;
- add a versioned IDE/playground transport that sends immutable snapshots and structured results without owning compiler state;
- publish duration, allocation, canceled-work, cache-hit, and queue-depth telemetry;
- prove rapid edit storms, delete/rename, project reload, and host shutdown behavior on Windows, macOS, and Linux.

### M3 — Roslyn metadata-delta production and transport

State: pending and MVP-blocking.

Required implementation:

- a framework-neutral edit-session contract that owns the last accepted Roslyn `Compilation`, baseline module metadata, active capabilities, and generation;
- Roslyn `EmitDifference` production of metadata, IL, and PDB deltas from accepted C# changes;
- rude-edit and unsupported-runtime diagnostics with original C# or XAML locations where available;
- explicit capability negotiation for metadata-update support, dynamic-code support, and restart-required changes;
- candidate-first ordering: validate the XAML artifact, produce and validate metadata deltas, apply metadata, publish the XAML replacement, then commit both baselines;
- an explicit recovery state when metadata publication succeeds but framework replacement fails; no silent divergence;
- typed adapters for .NET metadata update handlers and framework-specific tree replacement;
- bounded lifetime and disposal of module metadata, collectible contexts, and edit-session caches.

Required evidence:

- method-body, property, type-shape/rude-edit, XAML-only, and combined XAML+C# edits;
- canceled and superseded edits;
- metadata apply failure and framework publication failure;
- recovery from the last jointly committed generation;
- multi-project edits and referenced-project dependency changes;
- no unbounded retention of compilations, symbols, PEs, or collectible contexts.

### M4 — Complete MVP XAML hot-reload behavior

State: partial and MVP-blocking.

Required implementation:

- explicit namescope enter/exit and owner operations in neutral IR;
- stable mapping from syntax/semantic identities to live namescope objects;
- typed property, content, collection, dictionary, and template-root patch operations where safe;
- resource-definition, merged-dictionary, theme-partition, and lookup-generation deltas;
- template factory replacement that releases old ordinary/compiled binding lifetimes;
- state transfer for focus, selection, scroll, input, control state, and framework-owned state through profile contracts;
- a transactional whole-root or owning-subtree replacement fallback for every edit that cannot be patched safely;
- deterministic restart-required diagnostics only when neither patch nor replacement is supported.

Required evidence:

- add/remove/rename named elements and references;
- local, merged, external, and theme resources;
- templates with multiple materializations and independent binding lifetimes;
- collection roots and non-writable content roots;
- failed activation, failed state restoration, and valid recovery;
- repeated reloads without stale subscriptions or collectible-context leaks.

### M5 — Performance, determinism, and conformance closure

State: partial and MVP-blocking.

Required implementation and evidence:

- changed-text syntax subtree reuse and measured incremental invalidation boundaries;
- allocation and throughput budgets for tokenizer, XML parser, markup parser, bind/lower, generator, project preview, and watch;
- percentile latency for cold build, warm no-op, one-file edit, dependent-resource edit, Fluent compilation, and hot reload;
- grammar-aware fuzzing, corpus mutation, malformed-input recovery, cancellation, depth/size limits, and diagnostic bounds;
- deterministic generated source, manifests, diagnostics, and packaged artifacts across checkout roots, path separators, cultures, and repeated processes;
- source-generator tracked-step tests proving unrelated files and resources stay cached;
- framework/runtime, headless interaction, accessibility, and representative Fluent image baselines;
- clean-room audits of the final tree and every branch-reachable commit.

MVP budgets are recorded with the benchmark environment and fail on statistically repeatable regressions. They are not weakened to make a release pass.

### M6 — Packaging, samples, playground, and release qualification

State: partial and MVP-blocking.

Required implementation and evidence:

- source-generator, framework facade, Workspaces, CLI, Fluent theme, runtime, and MSBuild packages install from an isolated feed;
- source-generator assemblies load in Roslyn without leaking dependency DLLs as analyzers;
- a clean consumer restores, builds, runs, persists generated output, watches edits, and invokes the CLI tool;
- the playground selects a real project/target framework/XAML item and uses the same Workspaces/watch services;
- sample pages cover each supported MVP feature and visibly expose generated behavior;
- CLI commands have stable exit codes, schemas, diagnostics, cancellation, and transactional output behavior;
- public profile/extension SDK documentation, versioning policy, migration notes, and a minimal third-party profile sample;
- Windows, macOS, and Linux CI is green for every applicable build/test/package gate;
- the draft pull request contains the clean-room record, evidence summary, known limitations, and release checklist.

## MVP exit criteria

MVP is declared only when all of the following are true:

- M0 through M6 are complete.
- The feature matrix contains no `Planned` or undocumented behavior for the declared WinUI MVP subset.
- The unchanged pinned Fluent input compiles and the documented representative runtime scenarios pass.
- Source generator, MSBuild, CLI compile/preview/watch, and project-context playground produce equivalent accepted semantics.
- XAML-only and combined XAML+C# hot reload pass last-good, failure, recovery, and leak tests.
- Package installation and use succeed from a clean machine-equivalent environment.
- Performance and deterministic-output gates are published and passing.
- Clean-room and license audits are complete.

## Final-product roadmap

The final product is reached through the following evidence gates. Work may overlap, but a framework package cannot claim conformance before the shared gates it depends on are complete.

### F1 — WinUI production parity

- Complete the WinUI object model needed by current stable WinUI XAML and Fluent resources.
- Complete resource precedence, system resources, native contrast/theme providers, animations, transitions, nested target paths, accessibility, localization, custom controls, visual states, and designer metadata.
- Expand binding grammar, validation, collection views, async behavior, converters, and debugging/source maps.
- Provide full runtime, interaction, accessibility, image, performance, and hot-reload conformance suites.

### F2 — XAML language and XAML 2009 completion

- Complete intrinsic types/directives, generics and constraints, factories/arguments, references/fixups, XData, conditional namespaces, deferred loading, ambient services, serialization, and XML Schema regex behavior.
- Complete lossless visitors/rewriters, structural factories, formatter rules, incremental parsing, and writer round trips.
- Publish a clause-indexed standards conformance matrix with positive, negative, recovery, formatting, and round-trip evidence.

### F3 — Avalonia profile and runtime

- Ship independent profile, analyzer facade, MSBuild assets, runtime adapters, resource/style/template/property-system semantics, compiled binding, hot reload, samples, and corpus tests.
- Keep Avalonia selector and markup-extension behavior in profile-owned syntax/semantic extensions rather than compiler-core forks.

### F4 — WPF profile and runtime

- Ship independent profile, analyzer facade, MSBuild assets, dependency-property/routed-event/resource/style/template/binding semantics, pack URI handling, serialization/localization metadata, hot reload, samples, and corpus tests.
- Preserve typed source-integrated WPF seams and keep reflection out of compilation and normal runtime paths.

### F5 — MAUI profile and runtime

- Ship independent profile, analyzer facade, MSBuild assets, markup-extension service semantics, compiled binding, resources/styles/templates, multi-targeting, trimming/AOT behavior, hot reload, samples, and corpus tests.

### F6 — Bidirectional editing and language services

- Complete structural XAML factories, rewriters, formatting rule chains, simplification, classifications, navigation, rename, completion, quick info, diagnostics, and code actions.
- Expand source annotations and semantic inverses so safe XAML↔generated-C# changes round-trip transactionally.
- Provide an LSP/IDE-neutral service over Roslyn Workspaces; editor adapters remain thin transports.
- Reject ambiguous or stale inverse edits rather than guessing.

### F7 — AOT, binary artifacts, and deployment

- Define an optional versioned binary XAML/construction artifact derived from canonical bound/IR data, never from generated C# text.
- Support trimming annotations, NativeAOT, deterministic linking, startup profiles, lazy resource loading, and precompiled lookup tables.
- Keep source generation as a supported first-class path and prove semantic equivalence between source and binary artifacts.

### F8 — Public extension SDK and compatibility kit

- Stabilize versioned contracts for schema rules, symbol shapes, directives, markup grammars, validators, transforms, emitters, resources, hot reload, formatting, and inverse editing.
- Publish analyzers and compatibility tests that reject contract/version/capability conflicts deterministically.
- Supply clean-room profile templates, samples, package layout, and CI conformance tooling.

### F9 — Long-term quality and ecosystem

- Maintain standards/framework version matrices and an intentional deprecation policy.
- Continuously test large public corpora that can be used legally and clean-room.
- Track cold/warm build, IDE latency, hot reload, memory, startup, and runtime frame effects as release gates.
- Maintain fuzzing, differential behavior tests based on specifications or independently observed behavior, security review, SBOM, signing, and reproducible packages.

## Scope-control rules

- A framework-specific shortcut never counts as progress if it bypasses the neutral syntax, schema, bound, IR, profile, or workspace contract.
- A feature is not complete because a corpus parses; semantic binding, structured emission, generated compilation, runtime behavior, diagnostics, editing, and hot reload must be claimed separately.
- Root replacement is an accepted fallback, not proof of fine-grained hot reload.
- Reflection confined to explicitly documented tooling activation does not authorize reflection in compiler, generated code, binding, resources, or normal runtime.
- No roadmap gate permits copying or porting implementation or test source from another project.
- Every completed gate updates `REQUIREMENTS.md`, `FEATURE_MATRIX.md`, `IMPLEMENTATION_PLAN.md`, the clean-room record when research was performed, and the relevant package/sample documentation.
