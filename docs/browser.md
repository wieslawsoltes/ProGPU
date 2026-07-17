# ProGPU browser backend

`ProGPU.Browser` is the standalone browser lane; it does not depend on Uno. Managed renderer code targets the typed `IWebGpuApi` seam. Desktop contexts install `SilkWebGpuApi`, which forwards every operation directly to Silk.NET. Browser work is encoded into a versioned, little-endian command stream and decoded against `navigator.gpu`.

## Current status

The package implements the complete renderer-facing `IWebGpuApi` surface used by ProGPU: resource and pipeline descriptors, render and compute passes, uploads, copies, submission, surface acquisition, mapped-buffer read/write, and lifetime operations. The ordinary retained `ProGPU.Samples` gallery renders through this facade; the browser path does not use a separate smoke renderer or translated shaders.

DOM pointer, wheel, keyboard, text/IME, focus, cursor, clipboard, resize, and file-open events are adapted through browser platform seams. High-frequency input is drained as one fixed-width batch per animation frame. The browser host uses the separately packable `ProGPU.Fonts.Inter` assembly, which embeds all 36 unmodified Inter 4.1 and Inter Display static faces from the official `rsms/inter` release, so startup does not depend on operating-system font files and weight/slant matching uses real outlines. Faces are registered lazily and only requested styles are parsed. Inter Regular is the primary UI face. Official Noto Sans CJK JP and Noto Sans Symbols 2 faces are registered as lazy fallbacks through the shared font manager; each is parsed once only after Inter actually misses a glyph in its repertoire. Explicit control fonts and the gallery's system-font selectors remain independent.

`MainThread`, `Worker`, and `IsolatedWorker` execution modes are implemented. `Auto` selects an isolated worker when cross-origin isolation and shared memory are available, an ordinary OffscreenCanvas worker otherwise, and the main thread when canvas transfer is unavailable. All GPU packets produced during one managed frame are coalesced into one worker task so the current surface texture remains valid across multiple internal queue submissions. Device loss reports diagnostics and reloads the retained application to reconstruct device-owned resources.

## Samples

- `ProGPU.Samples` is the shared gallery library and has no executable entry point.
- `ProGPU.Samples.Desktop` owns the Silk window entry point and Windows manifest.
- `ProGPU.Samples.Browser` owns the WebAssembly entry point, canvas, JavaScript decoder, capability diagnostics, and browser assets.

Run the browser gallery with:

```bash
dotnet run --project src/ProGPU.Samples.Browser/ProGPU.Samples.Browser.csproj
```

### Publish with managed WebAssembly AOT

Install the .NET WebAssembly build tools once, then publish the Release host:

```bash
dotnet workload install wasm-tools
dotnet publish src/ProGPU.Samples.Browser/ProGPU.Samples.Browser.csproj \
  -c Release \
  -o artifacts/browser-aot
```

Release enables `RunAOTCompilation`, native relinking, and trimming by default. The deployable site is `artifacts/browser-aot/wwwroot`; serve that directory from an HTTP(S) origin rather than opening `index.html` from the filesystem. For example:

```bash
python3 -m http.server 8080 --directory artifacts/browser-aot/wwwroot
```

All ProGPU and sample assemblies are compiled to WebAssembly AOT modules. `netDxf.netstandard` 2.4.0 is intentionally kept in .NET's supported mixed interpreter/AOT mode because the .NET 10 Mono AOT compiler currently asserts while compiling that upstream assembly. The DXF gallery still runs through the same browser host and WebGPU renderer; only that third-party parser assembly is interpreted.

To troubleshoot AOT-specific behavior, disable AOT explicitly without changing the project:

```bash
dotnet publish src/ProGPU.Samples.Browser/ProGPU.Samples.Browser.csproj \
  -c Release \
  -p:RunAOTCompilation=false \
  -o artifacts/browser-interpreter
```

### GitHub Pages

`.github/workflows/browser-pages.yml` publishes the browser sample in Release AOT mode on every push to `main`, uploads `artifacts/browser-aot/wwwroot` as the Pages artifact, and deploys it through the `github-pages` environment. The workflow also supports manual dispatch from the Actions tab. The repository is configured for GitHub Actions Pages publishing at:

<https://wieslawsoltes.github.io/ProGPU/>

All page and runtime URLs are relative, including the framework module imported by `main.js`, so the application works below the `/ProGPU/` repository prefix. Keep `wwwroot/.nojekyll` in the browser project: it prevents Pages from treating the generated `_framework` directory as Jekyll-private content.

The status overlay reports the selected transport, adapter/profile, managed frame count, command dispatches, and command bytes. It is hidden by default; the browser-only **Show Browser WebGPU Diagnostics** toggle on the shared gallery Settings page controls it and persists the preference in browser storage. Startup and WebGPU errors reveal it automatically. The canvas beneath it is the same shared gallery used by `ProGPU.Samples.Desktop`.

## Command protocol

Packets start with a 16-byte header containing the `PGPU` magic, protocol version, flags, total byte length, and command count. Each command has an eight-byte header and is padded to eight-byte alignment. Resource handles combine a 20-bit index with a 12-bit generation so stale JavaScript registry references are rejected.

The managed command arena uses reusable unmanaged memory. JavaScript reads it through the runtime's current linear-memory view, without JSON or per-draw interop. Resource setup may use additional coarse dispatches; steady-state rendering uses one GPU command handoff per managed frame. Worker transports transfer all packets for the frame as one batch. Upload bytes stay inline with the coarse packet and are passed directly to `queue.writeBuffer` or `queue.writeTexture`.

## Capabilities and hosting

The portable profile uses core WebGPU. The full profile requests optional features used by existing shaders, including `bgra8unorm-storage`. When an optional feature is missing, initialization reports a profile downgrade and identifies the dependent functionality instead of rewriting WGSL.

The standard artifact runs wherever `navigator.gpu` is available. Cross-origin-isolated deployments should send these headers before enabling shared-memory worker modes:

```text
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
```

Without those headers, an explicit `IsolatedWorker` request downgrades to `Worker` and records the reason in `BrowserGpuCapabilities.Diagnostics`. Mapped-buffer completion remains asynchronous, matching WebGPU's `mapAsync` contract; mapped-write data is copied back into the browser mapping before `unmap`.

The validation matrix covers:

- main-thread `navigator.gpu` rendering;
- OffscreenCanvas worker rendering;
- cross-origin-isolated worker rendering;
- pointer/focus/text input against shared controls;
- exact embedded WGSL creation in Chrome;
- typed protocol coverage and desktop solution tests.

Browser selection must remain feature-based. Do not branch on user-agent names.
