#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
selector="${repo_root}/eng/progpu-native-package-scenarios.sh"

if (( $# != 0 )); then
  echo "usage: $0" >&2
  exit 2
fi

# This is the original serial JIT/NativeAOT scene list, independent of grouping.
expected_mil="$(printf '%s\n' \
  --mil-drawing-group-only --mil-glyph-run-drawing-only \
  --mil-text-render-options-only --mil-visual-clip-only \
  --mil-visual-opacity-mask-only --mil-visual-effect-only \
  --mil-visual-guideline-only --mil-drawing-image-only --mil-guideline-only | sort)"
# New text processes are independent of, and cannot replace, those nine cases.
expected_text="$(printf '%s\n' \
  --text-device-advances-only --text-hinted-paragraph-render-only \
  --text-hinted-paragraph-dawn-render-only | sort)"
expected_text_group="$(printf '%s\n' --text-device-advances-only --text-hinted-paragraph-render-only | sort)"
expected_dawn_group='--text-hinted-paragraph-dawn-render-only'
expected_all="$(printf '%s\n' "${expected_mil}" "${expected_text}" | sort)"
all="$("${selector}" all | sort)"
grouped_mil="$(for group in drawings visuals guidelines; do "${selector}" "${group}"; done | sort)"
grouped_text="$(for group in text text-dawn; do "${selector}" "${group}"; done | sort)"
if [[ "${all}" != "${expected_all}" || "${grouped_mil}" != "${expected_mil}" ||
      "${grouped_text}" != "${expected_text}" || "$("${selector}" text | sort)" != "${expected_text_group}" ||
      "$("${selector}" text-dawn)" != "${expected_dawn_group}" || -n "$("${selector}" core)" ]]; then
  echo 'Native package groups must cover the original nine MIL and three independent text cases exactly once.' >&2
  exit 1
fi
for group in drawings visuals guidelines text text-dawn; do
  case "${group}" in
    drawings|visuals|guidelines) count=3 ;;
    text) count=2 ;;
    text-dawn) count=1 ;;
  esac
  if [[ "$("${selector}" "${group}" | wc -l | tr -d ' ')" != "${count}" ]]; then
    echo "Native package group ${group} must contain ${count} cases." >&2
    exit 1
  fi
done
if "${selector}" unknown >/dev/null 2>&1 || "${selector}" >/dev/null 2>&1 ||
   "${selector}" core unexpected >/dev/null 2>&1 || "${selector}" all unexpected >/dev/null 2>&1 ||
   "${selector}" text unexpected >/dev/null 2>&1 || "${selector}" text-dawn unexpected >/dev/null 2>&1; then
  echo 'Native package scenario selection must reject invalid arguments.' >&2
  exit 1
fi
echo 'Native package groups preserve all nine MIL and three text JIT/NativeAOT cases exactly once.'
