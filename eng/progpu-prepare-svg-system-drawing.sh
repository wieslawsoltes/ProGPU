#!/usr/bin/env bash
set -euo pipefail

svg_source_root=${1:?Usage: progpu-prepare-svg-system-drawing.sh SVG_SOURCE_ROOT}
svg_project="$svg_source_root/Source/Svg.csproj"

if [[ ! -f "$svg_project" ]]; then
  echo "SVG.NET project not found: $svg_project" >&2
  exit 1
fi

target_frameworks_before='<TargetFrameworks>net8.0;net9.0;netcoreapp3.1;netstandard2.1;netstandard2.0;net462;net472;net481</TargetFrameworks>'
target_frameworks_after='<TargetFrameworks>net10.0;net8.0;net9.0;netcoreapp3.1;netstandard2.1;netstandard2.0;net462;net472;net481</TargetFrameworks>'
net9_constants='<DefineConstants Condition="$(TargetFramework.StartsWith('\''net9'\''))">$(DefineConstants);NETCORE;NETNEXT</DefineConstants>'
net10_constants='<DefineConstants Condition="$(TargetFramework.StartsWith('\''net10'\''))">$(DefineConstants);NETCORE;NETNEXT</DefineConstants>'
progpu_reference='<ProjectReference Condition="'\''$(TargetFramework)'\'' == '\''net10.0'\'' and '\''$(UseProGpuSystemDrawing)'\'' == '\''true'\''" Include="$(ProGpuSourceRoot)/src/System.Drawing.Common/System.Drawing.Common.csproj" AdditionalProperties="ManagePackageVersionsCentrally=true" />'

if ! grep -Fq "$target_frameworks_after" "$svg_project"; then
  grep -Fq "$target_frameworks_before" "$svg_project"
  sed -i "s|$target_frameworks_before|$target_frameworks_after|" "$svg_project"
fi

if ! grep -Fq "$net10_constants" "$svg_project"; then
  grep -Fq "$net9_constants" "$svg_project"
  sed -i "/StartsWith('net9')/a\\        $net10_constants" "$svg_project"
fi

if ! grep -Fq "$progpu_reference" "$svg_project"; then
  grep -Fq '<PackageReference Include="ExCSS"' "$svg_project"
  sed -i "/<PackageReference Include=\"ExCSS\"/i\\        $progpu_reference" "$svg_project"
fi

[[ $(grep -Fc "$target_frameworks_after" "$svg_project") -eq 1 ]]
[[ $(grep -Fc "$net10_constants" "$svg_project") -eq 1 ]]
[[ $(grep -Fc "$progpu_reference" "$svg_project") -eq 1 ]]
