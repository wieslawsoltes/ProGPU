#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_repo="${PROGPU_AVALONIA_REPOSITORY:-${repo_root}/external/Avalonia}"
avalonia_root="${PROGPU_AVALONIA_ROOT:-${repo_root}/.worktrees/avalonia-12.0.5}"
expected_revision="fee9c561ce036e8a3e8cee2397c75ca599b4790d"
patches=(
  "${repo_root}/eng/avalonia/12.0.5/progpu-compositor.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-text-tests.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-controlcatalog.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-package.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-native-dawn.patch"
)

if [[ ! -d "${source_repo}/.git" && ! -f "${source_repo}/.git" ]]; then
  echo "Pinned Avalonia repository was not found at ${source_repo}." >&2
  exit 2
fi

if ! git -C "${source_repo}" cat-file -e "${expected_revision}^{commit}"; then
  echo "Official Avalonia 12.0.5 commit ${expected_revision} is unavailable in ${source_repo}." >&2
  exit 3
fi

if [[ ! -e "${avalonia_root}/.git" ]]; then
  mkdir -p "$(dirname "${avalonia_root}")"
  git -C "${source_repo}" worktree add --detach "${avalonia_root}" "${expected_revision}"
fi

actual_revision="$(git -C "${avalonia_root}" rev-parse HEAD)"
if [[ "${actual_revision}" != "${expected_revision}" ]]; then
  echo "Pinned Avalonia revision mismatch: expected ${expected_revision}, found ${actual_revision}." >&2
  exit 4
fi

git -C "${avalonia_root}" submodule update --init --recursive

for patch in "${patches[@]}"; do
  if git -C "${avalonia_root}" apply --check --reverse "${patch}"; then
    continue
  fi

  if ! git -C "${avalonia_root}" apply --check "${patch}"; then
    echo "Avalonia source patch cannot be applied cleanly: ${patch}" >&2
    exit 5
  fi

  git -C "${avalonia_root}" apply "${patch}"
done

for patch in "${patches[@]}"; do
  git -C "${avalonia_root}" apply --check --reverse "${patch}"
done

echo "Prepared official Avalonia 12.0.5 source at ${avalonia_root}."
