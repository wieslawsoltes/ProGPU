# Native MIL compositor

## Goal

ProGPU will provide a reflection-free C++ composition endpoint that can consume
LibreWPF's canonical DUCE/MIL command batches, retain the WPF resource graph,
lower it to the existing ProGPU semantic scene ABI, and execute that scene on
the same C++ renderer through either wgpu-native or provider-resolved Dawn.
The existing managed `ProGPU.Scene` compositor remains available as an
independent compatibility lane while parity is established.

This is a clean source-integrated replacement, not a binary shim that scrapes
managed WPF objects. Protocol structs are derived from the MIT-licensed WPF MCG
model and are consumed as typed byte records with explicit bounds checks.

## Protocol findings

WPF's client channel writes each command as:

```text
uint32 item_size_including_header
uint32 MILCMD
byte[] packed_command_fields_and_optional_payload
byte[0..3] dword_alignment_padding
```

`item_size` is at least eight, is divisible by four, and must fit entirely in
the submitted batch. The command packet returned by WPF's reader begins at the
`MILCMD` field and has `item_size - 4` bytes. Resource handles are 32-bit and
belong to a channel-local namespace. The current retail protocol contains 141
commands:

| Range | Area | Count |
| --- | --- | ---: |
| `0x01`–`0x3d` | transport, resources, visuals, targets, glyphs | 61 |
| `0x3e`–`0x56` | nested render-data instruction stream | 25 |
| `0x57`–`0x8d` | retained media, 3D, effect, geometry, brush, drawing resources | 55 |

The transport and render-data streams use the same item framing. The outer
`MilCmdRenderData` packet carries the nested stream byte count and then the
nested command bytes. Commands and structures are packed, so the decoder uses
bounded byte copies and never casts an untrusted address to a command struct.

## Architecture

```text
LibreWPF source-built PresentationCore
  DUCE command producer / typed portable producer
             |
             | canonical MIL batch bytes
             v
ProGPU.Native.MIL (C++20)
  framing validator -> channel handle table -> retained resource graph
      -> render-data decoder -> semantic scene lowering -> damage tracking
             |
             | ProGPU semantic scene stream ABI
             v
ProGPU.Native C++ compositor
  wgpu-native adapter       provider-resolved Dawn adapter
                                      |
                           Windows Dawn D3D12 / DXGI path
```

The native MIL layer is backend-neutral. It produces the same semantic scene
stream used by current C++ samples and the managed `ProGPU.Scene.Native`
compiler. Backend selection, device loss, external-image binding, submission
lifetime, hit testing, and render-target ownership remain in the native
compositor.

## Delivery stages and gates

### Stage 0 — protocol foundation (implemented)

- Complete command-ID namespace (`0x01`–`0x8d`).
- Zero-copy, bounds-checked batch reader.
- Transactional channel state: a rejected batch cannot partially mutate the
  live graph.
- Size-versioned C ABI exported by both native renderer modules and a typed
  allocation-free .NET batch submission owner in `ProGPU.Backend.Native`.
- Initial typed resource, visual, generic-target, and opaque render-data state.
- Strict failure for unknown commands, unsupported commands, invalid handles,
  type mismatches, invalid graph operations, and malformed sizes.

### Stage 1 — complete retained 2D resources

The first Stage 1 vertical slice is implemented in the typed C++ API, the
size-versioned C ABI, and `NativeMilChannel.CompileScene(...)`. It
decodes the exact WPF `MILCMD_SOLIDCOLORBRUSH` record and nested
`MILCMD_DRAW_RECTANGLE` record, applies retained visual offsets and opacity,
walks the target's visual tree with cycle/depth validation, and emits the
shared pointer-free ProGPU semantic scene stream. Animated/transform brushes,
rectangle pens, and other nested commands deliberately fail closed until their
typed resources are implemented. The slice is covered by a byte-level fixture
that checks semantic brush, state, primitive, scene identity, generation, and
tree metrics. The C ABI supports an explicit required-size query and writes
into caller-owned storage; the managed owner returns the completed semantic
stream with typed compilation metrics for direct native compositor submission.

- Generate packed protocol declarations and size metadata from a checked-in
  neutral manifest produced from WPF MCG inputs.
- Implement scalar animation resources, transforms, geometry, brushes, pens,
  drawings, images, glyph runs, caches, guidelines, effects, and complete
  render-data decoding.
- Lower every supported update to stable semantic resource identities and
  generation numbers; unchanged resources must not be rebuilt.
- Add fixture capture/replay comparison against WPF's `CMilDataStreamReader`
  behavior and the existing managed LibreWPF renderer.

### Stage 2 — targets, scheduling, and parity services

- Add connection/partition/channel objects, handle duplication, out-of-band
  target updates, sync-flush replies, tier/capability replies, present/vblank
  scheduling, dirty regions, and device-loss recovery.
- Implement HWND and generic targets without leaking host-specific ownership
  into the retained scene layer.
- Add native hit-test result mapping and compositor diagnostics.

### Stage 3 — effects, 3D, media, and interop

- Complete shader effects, opacity masks, bitmap caches, 3D cameras/models,
  D3DImage/external texture binding, media frames, and color/text parity.
- Keep unsupported shader bytecode and external-handle forms fail-closed until
  a typed conversion or native backend implementation exists.

### Stage 4 — DirectX and DXGI facade parity

- Move the existing managed `ProGPU.DirectX` device/resource/view/pipeline
  compatibility model onto a native handle ABI shared with the MIL endpoint.
- Implement the measured D3D11/D3D12/DXGI/D3DCompiler export surface required
  by real package consumers. Do not attempt an unbounded system-DLL clone.
- On Windows, use Dawn's D3D12 backend for the compositor and explicit shared
  texture/fence paths for D3D11/D3D12 interop. Validate adapter LUID, format,
  alpha mode, row pitch, synchronization, resize, occlusion, and device loss.
- Preserve WebGPU behavior and semantic output across wgpu-native and Dawn;
  backend-specific differences require golden-image and metrics evidence.

### Stage 5 — LibreWPF selection and release gate

- Add an explicit runtime selector: managed portable, native MIL WebGPU, or
  native MIL Dawn/DirectX. The managed portable lane stays buildable and
  testable throughout migration.
- Package native runtimes for Windows x64/arm64 and the existing supported
  non-Windows RIDs. Verify exact ABI and protocol versions at startup.
- Run package-mode Toolkit/AvalonDock, Xceed when licensed, SciChart, input,
  clipping, hit testing, multi-window, DPI, and shutdown tests.

## Windows validation matrix

The primary integration guest is the discovered Parallels Windows 11 ARM64 VM.
The lane records guest OS build, .NET SDK, CMake/MSVC/Clang versions, adapter and
driver identity, Dawn backend, feature/limit set, and whether rendering uses
hardware, WARP, or another fallback. Required comparisons are:

1. Microsoft WPF MIL output on Windows versus LibreWPF managed portable output.
2. LibreWPF managed portable versus native MIL semantic streams.
3. Native wgpu/Dawn output versus Dawn D3D12 output.
4. DirectX compatibility API behavior versus native DirectX where the API is
   intentionally supported.

Tests use deterministic scenes, pixel tolerances, semantic stream hashes,
resource-generation/damage metrics, GPU validation output, and process lifetime
checks. A screenshot alone is not a parity result.

## Invariants

- No reflection or private managed field scanning in the product bridge.
- No pointer-shaped WPF objects in public package contracts.
- All protocol reads are bounds checked; unknown required data fails closed.
- Channel batches are transactional at the ProGPU boundary.
- Resource identity and generation are stable across unchanged frames.
- Native renderer APIs remain reusable by WPF, WinUI, and Avalonia.
- DirectX is a backend/interop surface over the shared renderer, not a second
  scene implementation.
