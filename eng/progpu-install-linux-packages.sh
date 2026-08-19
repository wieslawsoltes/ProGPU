#!/usr/bin/env bash
set -euo pipefail

if (( $# == 0 )); then
  echo "No Linux packages were requested." >&2
  exit 2
fi

missing_packages=()
for package in "$@"; do
  status="$(dpkg-query -W -f='${Status}' "${package}" 2>/dev/null || true)"
  if [[ "${status}" != "install ok installed" ]]; then
    missing_packages+=("${package}")
  fi
done

if (( ${#missing_packages[@]} == 0 )); then
  echo "All requested Linux packages are already installed; skipping apt."
  exit 0
fi

echo "Installing missing Linux packages: ${missing_packages[*]}"
if sudo env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a apt-get install -y \
  -o Acquire::Retries=3 \
  -o Dpkg::Use-Pty=0 \
  "${missing_packages[@]}"; then
  exit 0
fi

echo "The cached package indexes could not satisfy the request; refreshing once."
sudo env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a apt-get update \
  -o Acquire::Retries=3 \
  -o Dpkg::Use-Pty=0
sudo env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a apt-get install -y \
  -o Acquire::Retries=3 \
  -o Dpkg::Use-Pty=0 \
  "${missing_packages[@]}"
