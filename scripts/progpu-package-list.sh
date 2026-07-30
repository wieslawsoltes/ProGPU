#!/usr/bin/env bash

# shellcheck disable=SC2034 # This file is sourced by the pack and publish scripts.
progpu_avalonia_package_ids=(
  "ProGPU.Avalonia.Rendering"
  "ProGPU.Avalonia.SilkNet"
  "ProGPU.Avalonia.Rendering"
  "ProGPU.Avalonia.SilkNet"
)

progpu_avalonia_package_projects=(
  "src/ProGPU.Avalonia.Rendering/ProGPU.Avalonia.Rendering.csproj"
  "src/ProGPU.Avalonia.SilkNet/ProGPU.Avalonia.SilkNet.csproj"
  "src/ProGPU.Avalonia.Rendering.V11/ProGPU.Avalonia.Rendering.V11.csproj"
  "src/ProGPU.Avalonia.SilkNet.V11/ProGPU.Avalonia.SilkNet.V11.csproj"
)

progpu_avalonia_package_versions=(
  "12.0.5-preview.30"
  "12.0.5-preview.30"
  "11.3.18-preview.30"
  "11.3.18-preview.30"
)

if [[ "${#progpu_avalonia_package_ids[@]}" -ne "${#progpu_avalonia_package_projects[@]}" ||
      "${#progpu_avalonia_package_ids[@]}" -ne "${#progpu_avalonia_package_versions[@]}" ]]; then
  echo "ProGPU Avalonia package list arrays must have the same length." >&2
  exit 1
fi
