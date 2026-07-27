#!/usr/bin/env bash
set -euo pipefail

mode="${1:---report}"
if [[ "$mode" != "--report" && "$mode" != "--enforce" ]]; then
    echo "Usage: $0 [--report|--enforce]" >&2
    exit 2
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

readonly -a imported_commits=(
    "44aaf3d0"
    "25ed6588"
    "6979bfdc"
)

readonly -a audited_roots=(
    "src/ProGPU.Avalonia.Rendering"
    "src/ProGPU.Avalonia.Rendering.V11"
    "tests/Avalonia.ProGpu.UnitTests"
    "tests/Avalonia.ProGpu.RenderTests"
    "tests/Avalonia.IntegrationTests.SilkNet"
    "samples/ControlCatalog.Skia"
    "src/ProGPU.Avalonia.SilkNet"
    "src/ProGPU.Avalonia.SilkNet.V11"
    "src/ProGPU.Avalonia.SkiaShim"
    "tests/Avalonia.SkiaShim.RenderTests"
    "integration/ProGpuPackageApp"
    "samples/ControlCatalog"
    "samples/ControlCatalog.Desktop"
    "samples/MiniMvvm"
    "samples/ProGpuSandbox"
    "samples/RenderDemo"
    "samples/SampleControls"
)

remaining_paths=0
reachable_commits=0
notice_files=0

for commit in "${imported_commits[@]}"; do
    if git merge-base --is-ancestor "$commit" HEAD 2>/dev/null; then
        printf 'history: imported commit %s is reachable from HEAD\n' "$commit"
        ((reachable_commits += 1))
    fi

    while IFS= read -r path; do
        [[ -z "$path" ]] && continue
        if [[ -e "$path" ]]; then
            printf 'tree: imported path remains: %s\n' "$path"
            ((remaining_paths += 1))
        fi
    done < <(
        git show --format= --name-only --no-renames "$commit" -- "${audited_roots[@]}" |
            sed '/^$/d'
    )
done

while IFS= read -r path; do
    [[ -z "$path" ]] && continue
    printf 'tree: provenance notice remains: %s\n' "$path"
    ((notice_files += 1))
done < <(
    find . \
        -path './.git' -prune -o \
        -path './.worktrees' -prune -o \
        -path './artifacts' -prune -o \
        -path '*/bin' -prune -o \
        -path '*/obj' -prune -o \
        -type f -name 'PORTING-NOTICE.txt' -print 2>/dev/null |
        LC_ALL=C sort
)

printf '\nAvalonia clean-room audit: %d imported paths, %d notices, %d reachable import commits.\n' \
    "$remaining_paths" "$notice_files" "$reachable_commits"

if [[ "$mode" == "--enforce" ]] &&
    ((remaining_paths != 0 || notice_files != 0 || reachable_commits != 0)); then
    exit 1
fi
