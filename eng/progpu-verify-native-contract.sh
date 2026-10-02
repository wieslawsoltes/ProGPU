#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python3 "${repo_root}/eng/progpu-generate-mil-protocol.py" --check
python3 "${repo_root}/eng/progpu-generate-mil-coverage.py" --check
python3 "${repo_root}/eng/progpu-verify-native-memory-inventory.py"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeContract.g.cs"

python3 "${repo_root}/eng/tests/test_native_contract_inline_arrays.py"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_direct2d.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeDirect2DContract.g.cs"

"${repo_root}/eng/generate-native-unicode-tables.py" --verify

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_text_interaction.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeTextInteractionContract.g.cs"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_text_styles.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeTextStylesContract.g.cs"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_text_flow.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeTextFlowContract.g.cs"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_document_flow.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeDocumentFlowContract.g.cs"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeUnicodeCategoryGenerator/ProGPU.NativeUnicodeCategoryGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/src/Text/Unicode/progpu_native_unicode_categories.generated.hpp"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_text_hinting.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeTextHintingContract.g.cs"

dotnet run --project \
  "${repo_root}/eng/ProGPU.NativeContractGenerator/ProGPU.NativeContractGenerator.csproj" \
  --configuration Release -- \
  --verify \
  "${repo_root}/src/ProGPU.Native/include/progpu_native_text_source.h" \
  "${repo_root}/src/ProGPU.Backend.Native/Generated/NativeTextSourceContract.g.cs"
