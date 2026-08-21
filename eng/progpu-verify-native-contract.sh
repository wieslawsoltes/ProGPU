#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeContract.g.cs"

"${repo_root}/eng/generate-native-unicode-tables.py" --verify

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeUnicodeCategoryGenerator/ProGPU.NativeUnicodeCategoryGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/src/Text/Unicode/progpu_native_unicode_categories.generated.hpp"
