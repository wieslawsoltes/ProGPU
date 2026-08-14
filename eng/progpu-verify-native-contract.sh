#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeContract.g.cs"
