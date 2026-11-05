#!/usr/bin/env bash
set -euo pipefail
package="${1:?Supply the DX12 runtime NuGet package.}"
entries="$(unzip -Z1 "${package}")"
require_entry() {
  if ! grep -Fxq "$1" <<<"${entries}"; then
    echo "Missing DX12 package entry: $1" >&2
    exit 1
  fi
}
require_entry buildTransitive/ProGPU.Backend.Dx12.targets
for rid in win-x64 win-arm64; do
  for file in wgpu_native.dll dxcompiler.dll dxil.dll \
    wgpu-native-build.json dxc-compiler-package.json progpu-dx12-runtime.json \
    LICENSE.MIT LICENSE.APACHE LICENSE-LLVM.txt LICENSE-MS.txt LICENCE-MIT.txt \
    wgpu-LICENSE.MIT wgpu-LICENSE.APACHE webgpu-headers-LICENSE \
    Microsoft.Direct3D.DXC.package.xml; do
    require_entry "buildTransitive/runtimes/${rid}/native/${file}"
  done
done
if grep -Ei '(^|/)d3d10warp\.dll$|^lib/.*\.dll$' <<<"${entries}"; then
  echo 'DX12 package must contain neither WARP nor a placeholder managed assembly.' >&2
  exit 1
fi
echo 'Verified both DX12 package RIDs, receipts, licenses and native-only contents.'
