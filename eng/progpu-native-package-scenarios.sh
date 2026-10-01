#!/usr/bin/env bash
set -euo pipefail

# Independent consumer processes retain identical assertions in each group.
scenarios=(
  --mil-drawing-group-only
  --mil-glyph-run-drawing-only
  --mil-text-render-options-only
  --mil-visual-clip-only
  --mil-visual-opacity-mask-only
  --mil-visual-effect-only
  --mil-visual-guideline-only
  --mil-drawing-image-only
  --mil-guideline-only
  --text-device-advances-only
  --text-hinted-paragraph-render-only
  --text-hinted-paragraph-dawn-render-only
)

if (( $# != 1 )); then
  echo "usage: $0 <all|core|drawings|visuals|guidelines|text|text-dawn>" >&2
  exit 2
fi
case "$1" in
  all) offset=0; count=12 ;;
  core) exit 0 ;;
  drawings) offset=0; count=3 ;;
  visuals) offset=3; count=3 ;;
  guidelines) offset=6; count=3 ;;
  text) offset=9; count=2 ;;
  text-dawn) offset=11; count=1 ;;
  *) echo "Unknown native package scenario group: $1" >&2; exit 2 ;;
esac
printf '%s\n' "${scenarios[@]:offset:count}"
