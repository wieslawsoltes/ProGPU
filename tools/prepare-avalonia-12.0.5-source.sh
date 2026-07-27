#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_repo="${PROGPU_AVALONIA_REPOSITORY:-${repo_root}/external/Avalonia}"
avalonia_root="${PROGPU_AVALONIA_ROOT:-${repo_root}/.worktrees/avalonia-12.0.5}"
expected_revision="fee9c561ce036e8a3e8cee2397c75ca599b4790d"
official_repository="https://github.com/AvaloniaUI/Avalonia.git"
patches=(
  "${repo_root}/eng/avalonia/12.0.5/progpu-compositor.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-text-tests.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-controlcatalog.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-package.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-native-dawn.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-source-samples.patch"
  "${repo_root}/eng/avalonia/12.0.5/progpu-renderdemo.patch"
)

patch_arguments()
{
  local patch="$1"
  local patch_name
  patch_name="$(basename "${patch}")"
  if [[ "${patch_name}" == "progpu-native-dawn.patch" ]]; then
    printf '%s\n' \
      "--exclude=src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionTarget.cs"
    return
  fi
  if [[ "${patch_name}" != "progpu-compositor.patch" ]]; then
    return
  fi

  # The compositor patch is the retained-scene foundation. Later focused
  # patches own the final versions of these files and must be applied from the
  # pristine pinned revision, not on top of the older foundation hunks.
  printf '%s\n' \
    "--exclude=native/Avalonia.Native/src/OSX/metal.mm" \
    "--exclude=packages/Avalonia/Avalonia.csproj" \
    "--exclude=samples/ControlCatalog/**" \
    "--exclude=src/Avalonia.X11/Avalonia.X11.csproj" \
    "--exclude=src/Avalonia.X11/X11Window.cs" \
    "--exclude=tests/Avalonia.Skia.UnitTests/**"
}

apply_or_validate_patch()
{
  local patch="$1"
  local -a arguments=()
  while IFS= read -r argument; do
    arguments+=("${argument}")
  done < <(patch_arguments "${patch}")

  if git -C "${avalonia_root}" apply \
      --check \
      --reverse \
      ${arguments[@]+"${arguments[@]}"} \
      "${patch}" \
      2>/dev/null; then
    return
  fi

  if ! git -C "${avalonia_root}" apply \
      --check \
      ${arguments[@]+"${arguments[@]}"} \
      "${patch}"; then
    echo "Avalonia source patch cannot be applied cleanly: ${patch}" >&2
    exit 5
  fi

  git -C "${avalonia_root}" apply \
    ${arguments[@]+"${arguments[@]}"} \
    "${patch}"
}

validate_applied_patch()
{
  local patch="$1"
  local -a arguments=()
  while IFS= read -r argument; do
    arguments+=("${argument}")
  done < <(patch_arguments "${patch}")

  git -C "${avalonia_root}" apply \
    --check \
    --reverse \
    ${arguments[@]+"${arguments[@]}"} \
    "${patch}"
}

if [[ ! -d "${source_repo}/.git" && ! -f "${source_repo}/.git" ]]; then
  echo "Pinned Avalonia repository was not found at ${source_repo}." >&2
  exit 2
fi

if ! git -C "${source_repo}" cat-file -e "${expected_revision}^{commit}"; then
  echo "Fetching pinned official Avalonia 12.0.5 commit ${expected_revision}..."
  if ! git -C "${source_repo}" fetch \
      --no-tags \
      --depth=1 \
      "${official_repository}" \
      "${expected_revision}"; then
    echo "Unable to fetch official Avalonia 12.0.5 commit ${expected_revision} from ${official_repository}." >&2
    exit 3
  fi
fi

if ! git -C "${source_repo}" cat-file -e "${expected_revision}^{commit}"; then
  echo "Official Avalonia 12.0.5 commit ${expected_revision} remains unavailable in ${source_repo} after the pinned fetch." >&2
  exit 3
fi

if [[ ! -e "${avalonia_root}/.git" ]]; then
  mkdir -p "$(dirname "${avalonia_root}")"
  # Git for Windows commonly enables core.autocrlf globally. Keep the owned
  # patched worktree byte-stable so the reviewed LF patches apply identically
  # on Windows, Linux, and macOS.
  git \
    -c core.autocrlf=false \
    -c core.eol=lf \
    -C "${source_repo}" \
    worktree add --detach "${avalonia_root}" "${expected_revision}"
fi

if git -C "${avalonia_root}" diff --quiet &&
    git -C "${avalonia_root}" diff --cached --quiet; then
  # Force a byte-stable checkout after worktree creation as well. Git for
  # Windows does not consistently propagate command-scoped checkout settings
  # through worktree-add's internal reset, and an older script may also have
  # left a pristine CRLF worktree behind after its first patch check failed.
  git \
    -c core.autocrlf=false \
    -c core.eol=lf \
    -C "${avalonia_root}" \
    reset --hard "${expected_revision}"
  git \
    -c core.autocrlf=false \
    -c core.eol=lf \
    -C "${avalonia_root}" \
    checkout-index --all --force
fi

actual_revision="$(git -C "${avalonia_root}" rev-parse HEAD)"
if [[ "${actual_revision}" != "${expected_revision}" ]]; then
  echo "Pinned Avalonia revision mismatch: expected ${expected_revision}, found ${actual_revision}." >&2
  exit 4
fi

git -C "${avalonia_root}" submodule update --init --recursive

for patch in "${patches[@]}"; do
  apply_or_validate_patch "${patch}"
done

for patch in "${patches[@]}"; do
  validate_applied_patch "${patch}"
done

echo "Prepared official Avalonia 12.0.5 source at ${avalonia_root}."
