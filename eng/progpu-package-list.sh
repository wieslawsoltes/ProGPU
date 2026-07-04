#!/usr/bin/env bash

progpu_package_ids=(
  ProGPU.Backend
  ProGPU.DirectX
  ProGPU.Transpiler
  ProGPU.Compute
  ProGPU.Vector
  ProGPU.Text
  ProGPU.Scene
  ProGPU.Layout
  ProGPU.Virtualization
  ProGPU.WinUI
  ProGPU.WinUI.Charts
  ProGPU.WinUI.Designer
  ProGPU.Avalonia
  ProGPU.Uno
  ProGPU.Dxf
  LibreWPF.Interop
)

progpu_package_projects=(
  src/ProGPU.Backend/ProGPU.Backend.csproj
  src/ProGPU.DirectX/ProGPU.DirectX.csproj
  src/ProGPU.Transpiler/ProGPU.Transpiler.csproj
  src/ProGPU.Compute/ProGPU.Compute.csproj
  src/ProGPU.Vector/ProGPU.Vector.csproj
  src/ProGPU.Text/ProGPU.Text.csproj
  src/ProGPU.Scene/ProGPU.Scene.csproj
  src/ProGPU.Layout/ProGPU.Layout.csproj
  src/ProGPU.Virtualization/ProGPU.Virtualization.csproj
  src/ProGPU.WinUI/ProGPU.WinUI.csproj
  src/ProGPU.WinUI.Charts/ProGPU.WinUI.Charts.csproj
  src/ProGPU.WinUI.Designer/ProGPU.WinUI.Designer.csproj
  src/ProGPU.Avalonia/ProGPU.Avalonia.csproj
  src/ProGPU.Uno/ProGPU.Uno.csproj
  src/ProGPU.Dxf/ProGPU.Dxf.csproj
  src/ProGPU.Wpf.Interop/ProGPU.Wpf.Interop.csproj
)

progpu_package_purposes=(
  "WebGPU device, swapchain, Silk.NET windowing, and platform backend services."
  "DirectX-compatible facade and shader-oriented API surface implemented on ProGPU/WebGPU."
  "Shader/source transformation helpers used by generated GPU pipelines."
  "Compute pipeline helpers for GPU-side effects, acceleration, and future hit-test indexes."
  "Vector primitives, paths, geometry, brushes, pens, and rasterization data models."
  "Text layout, glyph metrics, and GPU-ready text rendering helpers."
  "Scene graph, compositor commands, retained visuals, effects, and presentation primitives."
  "Measure/arrange layout substrate shared by higher-level UI adapters."
  "Virtualization helpers for large retained visual and item surfaces."
  "WinUI-shaped controls and app model implemented on ProGPU."
  "Chart controls and chart rendering primitives for the WinUI-shaped layer."
  "Designer/editor controls and diagnostics for ProGPU WinUI surfaces."
  "Avalonia integration and compositor backend adapter."
  "Uno/WinUI integration and compositor backend adapter."
  "DXF import/rendering support for ProGPU vector scenes."
  "LibreWPF portable interop contracts consumed by the ProGPU/Silk.NET SDK lane."
)

if [[ "${#progpu_package_ids[@]}" -ne "${#progpu_package_projects[@]}" ||
      "${#progpu_package_ids[@]}" -ne "${#progpu_package_purposes[@]}" ]]; then
  echo "ProGPU package list arrays must have the same length." >&2
  exit 1
fi
