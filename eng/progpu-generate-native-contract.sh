#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  "${repo_root}/src/ProGPU.Native/include/progpu_native.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeContract.g.cs"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_direct2d.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeDirect2DContract.g.cs"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_text_interaction.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeTextInteractionContract.g.cs"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_text_styles.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeTextStylesContract.g.cs"
