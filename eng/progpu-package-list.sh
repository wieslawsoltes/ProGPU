#!/usr/bin/env bash

# Keep the portable group buildable on Linux. Mobile target frameworks are packed
# separately on macOS, then both groups are verified together before publishing.
progpu_portable_package_ids=(
  ProGPU.Backend
  ProGPU.Backend.Native
  ProGPU.Backend.Dawn
  ProGPU.Media
  ProGPU.Media.Editing
  ProGPU.Media.Scene
  ProGPU.WinRT
  ProGPU.Windows.Media
  ProGPU.Linux.Media
  ProGPU.Text.Shaping
  ProGPU.Browser
  ProGPU.DirectX
  ProGPU.Transpiler
  ProGPU.Compute
  ProGPU.Vector
  ProGPU.Text
  ProGPU.Fonts.Inter
  ProGPU.Fonts.Noto
  ProGPU.Scene
  ProGPU.Voxel
  ProGPU.Layout
  ProGPU.Virtualization
  ProGPU.WinUI
  ProGPU.Voxel.WinUI
  ProGPU.WinUI.Themes.Fluent
  ProGPU.WinUI.Charts
  ProGPU.WinUI.Designer
  ProGPU.Xaml
  ProGPU.Xaml.Roslyn
  ProGPU.Xaml.SourceGenerator
  ProGPU.Xaml.Workspaces
  ProGPU.Xaml.Cli
  ProGPU.Avalonia
  ProGPU.Uno
  ProGPU.Dxf
  ProGPU.SkiaSharp
  ProGPU.System.Drawing.Common
  LibreWPF.Interop
)

progpu_portable_package_projects=(
  src/ProGPU.Backend/ProGPU.Backend.csproj
  src/ProGPU.Backend.Native/ProGPU.Backend.Native.csproj
  src/ProGPU.Backend.Dawn/ProGPU.Backend.Dawn.csproj
  src/ProGPU.Media/ProGPU.Media.csproj
  src/ProGPU.Media.Editing/ProGPU.Media.Editing.csproj
  src/ProGPU.Media.Scene/ProGPU.Media.Scene.csproj
  src/ProGPU.WinRT/ProGPU.WinRT.csproj
  src/ProGPU.Windows.Media/ProGPU.Windows.Media.csproj
  src/ProGPU.Linux.Media/ProGPU.Linux.Media.csproj
  src/ProGPU.Text.Shaping/ProGPU.Text.Shaping.csproj
  src/ProGPU.Browser/ProGPU.Browser.csproj
  src/ProGPU.DirectX/ProGPU.DirectX.csproj
  src/ProGPU.Transpiler/ProGPU.Transpiler.csproj
  src/ProGPU.Compute/ProGPU.Compute.csproj
  src/ProGPU.Vector/ProGPU.Vector.csproj
  src/ProGPU.Text/ProGPU.Text.csproj
  src/ProGPU.Fonts.Inter/ProGPU.Fonts.Inter.csproj
  src/ProGPU.Fonts.Noto/ProGPU.Fonts.Noto.csproj
  src/ProGPU.Scene/ProGPU.Scene.csproj
  src/ProGPU.Voxel/ProGPU.Voxel.csproj
  src/ProGPU.Layout/ProGPU.Layout.csproj
  src/ProGPU.Virtualization/ProGPU.Virtualization.csproj
  src/ProGPU.WinUI/ProGPU.WinUI.csproj
  src/ProGPU.Voxel.WinUI/ProGPU.Voxel.WinUI.csproj
  src/ProGPU.WinUI.Themes.Fluent/ProGPU.WinUI.Themes.Fluent.csproj
  src/ProGPU.WinUI.Charts/ProGPU.WinUI.Charts.csproj
  src/ProGPU.WinUI.Designer/ProGPU.WinUI.Designer.csproj
  src/ProGPU.Xaml/ProGPU.Xaml.csproj
  src/ProGPU.Xaml.Roslyn/ProGPU.Xaml.Roslyn.csproj
  src/ProGPU.Xaml.SourceGenerator/ProGPU.Xaml.SourceGenerator.csproj
  src/ProGPU.Xaml.Workspaces/ProGPU.Xaml.Workspaces.csproj
  tools/ProGPU.Xaml.Cli/ProGPU.Xaml.Cli.csproj
  src/ProGPU.Avalonia/ProGPU.Avalonia.csproj
  src/ProGPU.Uno/ProGPU.Uno.csproj
  src/ProGPU.Dxf/ProGPU.Dxf.csproj
  src/SkiaSharp/SkiaSharp.csproj
  src/System.Drawing.Common/System.Drawing.Common.csproj
  src/ProGPU.Wpf.Interop/ProGPU.Wpf.Interop.csproj
)

progpu_portable_package_purposes=(
  "WebGPU device, swapchain, Silk.NET windowing, and platform backend services."
  "Experimental typed .NET host plus validated x64/arm64 desktop runtimes for the ProGPU C++ WebGPU renderer."
  "Exact-ABI Dawn shared texture memory and cross-queue fence extensions."
  "Framework-neutral media playback, diagnostics, audio processing, effects, and provider contracts."
  "Reusable non-destructive composition, project serialization, overlays, effects, and native export coordination."
  "Retained 2D/3D WebGPU media presentation and fused effect integration."
  "Platform-neutral WinRT-shaped foundation, storage, property-set, and media value contracts."
  "Native Windows Media Foundation, D3D11/DXGI, and platform-audio media provider."
  "Dependency-free Linux V4L2, DMA-BUF, Vulkan Video, and PipeWire media provider."
  "AOT-safe OpenType shaping contracts and execution primitives."
  "Batched .NET WebAssembly dispatcher and navigator.gpu browser host services."
  "DirectX-compatible facade and shader-oriented API surface implemented on ProGPU/WebGPU."
  "Shader/source transformation helpers used by generated GPU pipelines."
  "Compute pipeline helpers for GPU-side effects, acceleration, and future hit-test indexes."
  "Vector primitives, paths, geometry, brushes, pens, and rasterization data models."
  "Text layout, glyph metrics, and GPU-ready text rendering helpers."
  "Official Inter font assets and typed accessors for deterministic UI typography."
  "Official Noto fallback assets and typed accessors for CJK and symbol coverage."
  "Scene graph, compositor commands, retained visuals, effects, and presentation primitives."
  "Chunked voxel worlds, greedy meshing, collision, terrain generation, and grid ray casting."
  "Measure/arrange layout substrate shared by higher-level UI adapters."
  "Virtualization helpers for large retained visual and item surfaces."
  "WinUI-shaped controls and app model implemented on ProGPU."
  "Playable WinUI voxel control with first-person input and retained ProGPU rendering."
  "Source-generated unchanged WinUI Fluent theme resources and inspectable XAML content."
  "Chart controls and chart rendering primitives for the WinUI-shaped layer."
  "Designer/editor controls and diagnostics for ProGPU WinUI surfaces."
  "Framework-neutral XAML syntax, schema, diagnostics, and compiler contracts."
  "Roslyn symbol type system and structured C# emitter for the XAML compiler."
  "Incremental XAML source generator plus transitive MSBuild integration."
  "Roslyn Workspace editing, formatting, and bidirectional XAML services."
  "Standalone XAML compiler and Roslyn/MSBuild workspace command-line tool."
  "Avalonia integration and compositor backend adapter."
  "Uno/WinUI integration and compositor backend adapter."
  "DXF import/rendering support for ProGPU vector scenes."
  "ProGPU-backed portable SkiaSharp compatibility shim used by drawing and imaging adapters."
  "ProGPU-backed portable System.Drawing.Common compatibility shim for LibreWinForms and GDI-style callers."
  "LibreWPF portable interop contracts consumed by the ProGPU/Silk.NET SDK lane."
)

# Exact runtime dependency closure of the Avalonia renderer and Silk.NET host.
# Keep this list topologically ordered so the replacement-stack pack lane stays
# fast while still producing every ProGPU package that an isolated consumer can
# restore without falling back to a previously published runtime binary.
progpu_avalonia_runtime_package_ids=(
  ProGPU.Backend
  ProGPU.Backend.Dawn
  ProGPU.Text.Shaping
  ProGPU.Transpiler
  ProGPU.WinRT
  ProGPU.Vector
  ProGPU.Text
  ProGPU.Compute
  ProGPU.Scene
  ProGPU.SkiaSharp
)

progpu_avalonia_runtime_package_projects=(
  src/ProGPU.Backend/ProGPU.Backend.csproj
  src/ProGPU.Backend.Dawn/ProGPU.Backend.Dawn.csproj
  src/ProGPU.Text.Shaping/ProGPU.Text.Shaping.csproj
  src/ProGPU.Transpiler/ProGPU.Transpiler.csproj
  src/ProGPU.WinRT/ProGPU.WinRT.csproj
  src/ProGPU.Vector/ProGPU.Vector.csproj
  src/ProGPU.Text/ProGPU.Text.csproj
  src/ProGPU.Compute/ProGPU.Compute.csproj
  src/ProGPU.Scene/ProGPU.Scene.csproj
  src/SkiaSharp/SkiaSharp.csproj
)

progpu_avalonia_runtime_package_purposes=(
  "Avalonia runtime closure: WebGPU device and platform backend."
  "Avalonia runtime closure: typed Dawn native presentation and shared-resource interop."
  "Avalonia runtime closure: AOT-safe OpenType shaping."
  "Avalonia runtime closure: shader/source transformation."
  "Avalonia runtime closure: platform-neutral WinRT value contracts used by vector primitives."
  "Avalonia runtime closure: retained vector primitives."
  "Avalonia runtime closure: text layout and rendering."
  "Avalonia runtime closure: compute pipelines."
  "Avalonia runtime closure: retained compositor scene."
  "Avalonia runtime closure: SkiaSharp compatibility surface."
)

progpu_mobile_package_ids=(
  ProGPU.Android
  ProGPU.iOS
  ProGPU.Android.Media
  ProGPU.Apple.Media
)

progpu_mobile_package_projects=(
  src/ProGPU.Android/ProGPU.Android.csproj
  src/ProGPU.iOS/ProGPU.iOS.csproj
  src/ProGPU.Android.Media/ProGPU.Android.Media.csproj
  src/ProGPU.Apple.Media/ProGPU.Apple.Media.csproj
)

progpu_mobile_package_purposes=(
  "Native Android SurfaceView host, input, storage, and WebGPU/Vulkan integration."
  "Native UIKit and CAMetalLayer host, input, storage, and WebGPU/Metal integration."
  "Native Android MediaPlayer, ImageReader, AHardwareBuffer, and platform-audio media provider."
  "Native AVFoundation, IOSurface, and platform-audio provider for iOS and macOS."
)

progpu_package_ids=("${progpu_portable_package_ids[@]}" "${progpu_mobile_package_ids[@]}")
progpu_package_projects=("${progpu_portable_package_projects[@]}" "${progpu_mobile_package_projects[@]}")
progpu_package_purposes=("${progpu_portable_package_purposes[@]}" "${progpu_mobile_package_purposes[@]}")

# Every owned project under src must be classified as shipping or intentionally
# non-shipping. The verifier fails when a newly added project is omitted.
progpu_nonshipping_projects=(
  src/PresentationCore/PresentationCore.csproj
  src/ProGPU.Avalonia.SkiaSourceCompatibility/ProGPU.Avalonia.SkiaSourceCompatibility.csproj
  src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj
  src/ProGPU.Native.ManagedSample/ProGPU.Native.ManagedSample.csproj
  src/ProGPU.Samples.ActivityMonitor/ProGPU.Samples.ActivityMonitor.csproj
  src/ProGPU.Samples.Android/ProGPU.Samples.Android.csproj
  src/ProGPU.Samples.Avalonia/ProGPU.Samples.Avalonia.csproj
  src/ProGPU.Samples.Browser/ProGPU.Samples.Browser.csproj
  src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj
  src/ProGPU.Samples.Uno/ProGPU.Samples.Uno/ProGPU.Samples.Uno.csproj
  src/ProGPU.Samples.iOS/ProGPU.Samples.iOS.csproj
  src/ProGPU.Samples/ProGPU.Samples.csproj
  src/ProGPU.Tests.Headless/ProGPU.Tests.Headless.csproj
  src/ProGPU.Tests/ProGPU.Tests.csproj
  src/ProGPU.Voxel.Tests/ProGPU.Voxel.Tests.csproj
  src/ProGPU.Xaml.Tests/ProGPU.Xaml.Tests.csproj
  src/WindowsBase/WindowsBase.csproj
)

progpu_nonshipping_reasons=(
  "Framework implementation shim; shipped through consuming compatibility packages."
  "Non-shipping source dependency used to validate the ProGPU SkiaSharp contract against Avalonia's ordinary Skia backend."
  "Native C++ renderer differential and performance benchmark."
  "Native C++ renderer managed-host sample."
  "Activity Monitor sample application."
  "Android sample application."
  "Avalonia sample application."
  "Browser sample application."
  "Desktop sample application."
  "Uno sample application."
  "iOS sample application."
  "Shared sample gallery."
  "Headless test project."
  "Test project."
  "Voxel engine test project."
  "XAML compiler and source-generator test project."
  "Framework implementation shim; shipped through consuming compatibility packages."
)

# These packable projects are intentionally owned by scripts/progpu-*.sh rather
# than the runtime package lane above. The v11 and v12 projects share package
# IDs but publish distinct, exact Avalonia-compatible versions.
progpu_integration_lane_projects=(
  src/ProGPU.Avalonia.Rendering/ProGPU.Avalonia.Rendering.csproj
  src/ProGPU.Avalonia.Rendering.V11/ProGPU.Avalonia.Rendering.V11.csproj
  src/ProGPU.Avalonia.SilkNet/ProGPU.Avalonia.SilkNet.csproj
  src/ProGPU.Avalonia.SilkNet.V11/ProGPU.Avalonia.SilkNet.V11.csproj
)

progpu_integration_lane_reasons=(
  "Avalonia 12 renderer package."
  "Avalonia 11 shared-source renderer package."
  "Avalonia 12 Silk.NET package."
  "Avalonia 11 shared-source Silk.NET package."
)

validate_parallel_arrays() {
  local group="$1"
  local ids_count="$2"
  local projects_count="$3"
  local purposes_count="$4"
  # shellcheck disable=SC2055 # Either mismatched pair must fail validation.
  if [[ "${ids_count}" -ne "${projects_count}" || "${ids_count}" -ne "${purposes_count}" ]]; then
    echo "ProGPU ${group} package list arrays must have the same length." >&2
    exit 1
  fi
}

validate_parallel_arrays portable "${#progpu_portable_package_ids[@]}" "${#progpu_portable_package_projects[@]}" "${#progpu_portable_package_purposes[@]}"
validate_parallel_arrays avalonia-runtime "${#progpu_avalonia_runtime_package_ids[@]}" "${#progpu_avalonia_runtime_package_projects[@]}" "${#progpu_avalonia_runtime_package_purposes[@]}"
validate_parallel_arrays mobile "${#progpu_mobile_package_ids[@]}" "${#progpu_mobile_package_projects[@]}" "${#progpu_mobile_package_purposes[@]}"
validate_parallel_arrays complete "${#progpu_package_ids[@]}" "${#progpu_package_projects[@]}" "${#progpu_package_purposes[@]}"

if [[ "${#progpu_nonshipping_projects[@]}" -ne "${#progpu_nonshipping_reasons[@]}" ]]; then
  echo "ProGPU non-shipping project arrays must have the same length." >&2
  exit 1
fi

if [[ "${#progpu_integration_lane_projects[@]}" -ne "${#progpu_integration_lane_reasons[@]}" ]]; then
  echo "ProGPU integration-lane project arrays must have the same length." >&2
  exit 1
fi
