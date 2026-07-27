#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:-$repo_root/artifacts/avalonia-retained-pixel-contract}"
profile="$repo_root/tools/profile-avalonia-controlcatalog.sh"
page_filter='^(Buttons|Composition|Acrylic|BitmapCache|Canvas|AdornerLayer|Clipboard|HeaderedContentControl|Notifications)$'
warmup_frames="${PROGPU_AVALONIA_PIXEL_WARMUP_FRAMES:-10}"
measure_frames="${PROGPU_AVALONIA_PIXEL_MEASURE_FRAMES:-10}"

run_profile() {
  local output="$1"
  shift
  env \
    PROGPU_AVALONIA_PAGE_FILTER="$page_filter" \
    PROGPU_AVALONIA_WARMUP_FRAMES="$warmup_frames" \
    PROGPU_AVALONIA_MEASURE_FRAMES="$measure_frames" \
    PROGPU_AVALONIA_BACKENDS=source-progpu \
    PROGPU_AVALONIA_SCREENSHOTS=1 \
    "$@" \
    "$profile" "$output"
}

read_fallback_nodes() {
  sed -n \
    's/^[[:space:]]*"RetainedCompositionFallbackNodes":[[:space:]]*\([0-9][0-9]*\),\{0,1\}[[:space:]]*$/\1/p' \
    "$1"
}

mkdir -p "$output_root"

run_profile "$output_root/retained"
run_profile \
  "$output_root/flattened" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_RETAINED_SCENE=0

for slug in \
  buttons \
  composition \
  acrylic \
  bitmapcache \
  canvas \
  adornerlayer \
  clipboard \
  headeredcontentcontrol \
  notifications; do
  retained="$output_root/retained/source-progpu/$slug.png"
  flattened="$output_root/flattened/source-progpu/$slug.png"
  if ! cmp -s "$retained" "$flattened"; then
    echo "Retained and flattened pixels differ for $slug." >&2
    exit 10
  fi
  fallback_nodes="$(
    read_fallback_nodes \
      "$output_root/retained/source-progpu/$slug.json"
  )"
  if [[ "$fallback_nodes" != "0" ]]; then
    echo "$slug used $fallback_nodes retained fallback nodes; expected zero." >&2
    exit 16
  fi
done

page_filter='^Buttons$'
run_profile \
  "$output_root/geometry-clip-retained" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_BENCHMARK_ROOT_GEOMETRY_CLIP=1
run_profile \
  "$output_root/geometry-clip-flattened" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_RETAINED_SCENE=0 \
  PROGPU_AVALONIA_BENCHMARK_ROOT_GEOMETRY_CLIP=1

geometry_retained="$output_root/geometry-clip-retained/source-progpu/buttons.png"
geometry_flattened="$output_root/geometry-clip-flattened/source-progpu/buttons.png"
if ! cmp -s "$geometry_retained" "$geometry_flattened"; then
  echo "Retained and flattened geometry-clip pixels differ." >&2
  exit 11
fi

geometry_result="$output_root/geometry-clip-retained/source-progpu/buttons.json"
fallback_nodes="$(
  read_fallback_nodes "$geometry_result"
)"
if [[ "$fallback_nodes" != "0" ]]; then
  echo "Geometry clip used $fallback_nodes retained fallback nodes; expected zero." >&2
  exit 12
fi

run_profile \
  "$output_root/aliased-text-retained" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_BENCHMARK_ROOT_ALIASED_TEXT=1
run_profile \
  "$output_root/aliased-text-flattened" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_RETAINED_SCENE=0 \
  PROGPU_AVALONIA_BENCHMARK_ROOT_ALIASED_TEXT=1

aliased_retained="$output_root/aliased-text-retained/source-progpu/buttons.png"
aliased_flattened="$output_root/aliased-text-flattened/source-progpu/buttons.png"
if ! cmp -s "$aliased_retained" "$aliased_flattened"; then
  echo "Retained and flattened inherited text-option pixels differ." >&2
  exit 13
fi

if cmp -s \
  "$output_root/retained/source-progpu/buttons.png" \
  "$aliased_retained"; then
  echo "Inherited aliased-text fixture did not alter the Buttons pixels." >&2
  exit 14
fi

aliased_result="$output_root/aliased-text-retained/source-progpu/buttons.json"
aliased_fallback_nodes="$(
  read_fallback_nodes "$aliased_result"
)"
if [[ "$aliased_fallback_nodes" != "0" ]]; then
  echo "Inherited text options used $aliased_fallback_nodes retained fallback nodes; expected zero." >&2
  exit 15
fi

run_profile \
  "$output_root/inherited-drawing-options-channel-retained" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_INHERITED_DRAWING_OPTIONS_FIXTURE=1
run_profile \
  "$output_root/inherited-drawing-options-channel-flattened" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_RETAINED_SCENE=0 \
  PROGPU_AVALONIA_INHERITED_DRAWING_OPTIONS_FIXTURE=1

inherited_channel_retained="$output_root/inherited-drawing-options-channel-retained/source-progpu/buttons.png"
inherited_channel_flattened="$output_root/inherited-drawing-options-channel-flattened/source-progpu/buttons.png"
if ! cmp -s \
  "$inherited_channel_retained" \
  "$inherited_channel_flattened"; then
  echo "Retained and flattened inherited drawing-options channel pixels differ." >&2
  exit 28
fi
if ! grep -q \
    "inherited drawing-options channel fixture attached" \
    "$output_root/inherited-drawing-options-channel-retained/source-progpu/buttons.log"; then
  echo "Inherited drawing-options channel fixture was not applied." >&2
  exit 29
fi
inherited_channel_fallback_nodes="$(
  read_fallback_nodes \
    "$output_root/inherited-drawing-options-channel-retained/source-progpu/buttons.json"
)"
if [[ "$inherited_channel_fallback_nodes" != "0" ]]; then
  echo "Inherited drawing-options channel used $inherited_channel_fallback_nodes fallback nodes; expected zero." >&2
  exit 30
fi

run_profile \
  "$output_root/topology-channel-retained" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_TOPOLOGY_FIXTURE=1
run_profile \
  "$output_root/topology-channel-flattened" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_RETAINED_SCENE=0 \
  PROGPU_AVALONIA_TOPOLOGY_FIXTURE=1

topology_channel_retained="$output_root/topology-channel-retained/source-progpu/buttons.png"
topology_channel_flattened="$output_root/topology-channel-flattened/source-progpu/buttons.png"
if ! cmp -s \
  "$topology_channel_retained" \
  "$topology_channel_flattened"; then
  echo "Retained and flattened topology channel pixels differ." >&2
  exit 31
fi
if ! grep -q \
    "typed retained topology channel fixture" \
    "$output_root/topology-channel-retained/source-progpu/buttons.log"; then
  echo "Topology channel fixture was not applied." >&2
  exit 32
fi
topology_channel_fallback_nodes="$(
  read_fallback_nodes \
    "$output_root/topology-channel-retained/source-progpu/buttons.json"
)"
if [[ "$topology_channel_fallback_nodes" != "0" ]]; then
  echo "Topology channel used $topology_channel_fallback_nodes fallback nodes; expected zero." >&2
  exit 33
fi

page_filter='^AdornerLayer$'
run_profile \
  "$output_root/adorner-channel-retained" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_ADORNER_FIXTURE=1
run_profile \
  "$output_root/adorner-channel-flattened" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_RETAINED_SCENE=0 \
  PROGPU_AVALONIA_ADORNER_FIXTURE=1

adorner_channel_retained="$output_root/adorner-channel-retained/source-progpu/adornerlayer.png"
adorner_channel_flattened="$output_root/adorner-channel-flattened/source-progpu/adornerlayer.png"
if ! cmp -s \
  "$adorner_channel_retained" \
  "$adorner_channel_flattened"; then
  echo "Retained and flattened adorner channel pixels differ." >&2
  exit 34
fi
if ! grep -q \
    "typed retained adorner channel fixture" \
    "$output_root/adorner-channel-retained/source-progpu/adornerlayer.log"; then
  echo "Adorner channel fixture was not applied." >&2
  exit 35
fi
adorner_channel_fallback_nodes="$(
  read_fallback_nodes \
    "$output_root/adorner-channel-retained/source-progpu/adornerlayer.json"
)"
if [[ "$adorner_channel_fallback_nodes" != "0" ]]; then
  echo "Adorner channel used $adorner_channel_fallback_nodes fallback nodes; expected zero." >&2
  exit 36
fi

page_filter='^Buttons$'
run_profile \
  "$output_root/conic-mask-retained" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_BENCHMARK_ROOT_CONIC_OPACITY_MASK=1
run_profile \
  "$output_root/conic-mask-flattened" \
  PROGPU_AVALONIA_SKIP_BUILD=1 \
  PROGPU_AVALONIA_RETAINED_SCENE=0 \
  PROGPU_AVALONIA_BENCHMARK_ROOT_CONIC_OPACITY_MASK=1

conic_retained="$output_root/conic-mask-retained/source-progpu/buttons.png"
conic_flattened="$output_root/conic-mask-flattened/source-progpu/buttons.png"
if ! cmp -s "$conic_retained" "$conic_flattened"; then
  echo "Retained and flattened conic opacity-mask pixels differ." >&2
  exit 24
fi
if cmp -s \
  "$output_root/retained/source-progpu/buttons.png" \
  "$conic_retained"; then
  echo "Conic opacity-mask fixture did not alter the Buttons pixels." >&2
  exit 25
fi
if ! grep -q \
    "root conic opacity mask fixture angle=23" \
    "$output_root/conic-mask-retained/source-progpu/buttons.log"; then
  echo "Conic opacity-mask fixture was not applied." >&2
  exit 26
fi
conic_fallback_nodes="$(
  read_fallback_nodes \
    "$output_root/conic-mask-retained/source-progpu/buttons.json"
)"
if [[ "$conic_fallback_nodes" != "0" ]]; then
  echo "Conic opacity mask used $conic_fallback_nodes fallback nodes; expected zero." >&2
  exit 27
fi

for fixture in blur drop-shadow; do
  case "$fixture" in
    blur)
      fixture_variable="PROGPU_AVALONIA_BENCHMARK_TEXT_BLUR_EFFECT=1"
      fixture_log_pattern="text effect fixture blur radius=4"
      ;;
    drop-shadow)
      fixture_variable="PROGPU_AVALONIA_BENCHMARK_TEXT_DROP_SHADOW_EFFECT=1"
      fixture_log_pattern="text effect fixture drop-shadow"
      ;;
  esac

  retained_output="$output_root/effect-$fixture-retained"
  flattened_output="$output_root/effect-$fixture-flattened"
  run_profile \
    "$retained_output" \
    PROGPU_AVALONIA_SKIP_BUILD=1 \
    "$fixture_variable"
  run_profile \
    "$flattened_output" \
    PROGPU_AVALONIA_SKIP_BUILD=1 \
    PROGPU_AVALONIA_RETAINED_SCENE=0 \
    "$fixture_variable"

  retained_fixture="$retained_output/source-progpu/buttons.png"
  flattened_fixture="$flattened_output/source-progpu/buttons.png"
  if ! cmp -s "$retained_fixture" "$flattened_fixture"; then
    echo "Retained and flattened $fixture effect pixels differ." >&2
    exit 20
  fi
  if cmp -s \
    "$output_root/retained/source-progpu/buttons.png" \
    "$retained_fixture"; then
    echo "$fixture effect fixture did not alter the Buttons pixels." >&2
    exit 21
  fi
  if ! grep -q \
      "$fixture_log_pattern" \
      "$retained_output/source-progpu/buttons.log"; then
    echo "$fixture effect fixture was not applied." >&2
    exit 22
  fi
  fixture_fallback_nodes="$(
    read_fallback_nodes \
      "$retained_output/source-progpu/buttons.json"
  )"
  if [[ "$fixture_fallback_nodes" != "0" ]]; then
    echo "$fixture effect used $fixture_fallback_nodes fallback nodes; expected zero." >&2
    exit 23
  fi
done

page_filter='^BitmapCache$'
for fixture in scale snap cleartype-on cleartype-off; do
  case "$fixture" in
    scale)
      fixture_variable="PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_SCALE=2"
      fixture_log_pattern="bitmap cache fixture scale=2"
      ;;
    snap)
      fixture_variable="PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_SNAP=1"
      fixture_log_pattern="snap=True"
      ;;
    cleartype-on)
      fixture_variable="PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_CLEARTYPE=1"
      fixture_log_pattern="clearType=True"
      ;;
    cleartype-off)
      fixture_variable="PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_CLEARTYPE=0"
      fixture_log_pattern="clearType=False"
      ;;
  esac

  retained_output="$output_root/bitmap-cache-$fixture-retained"
  flattened_output="$output_root/bitmap-cache-$fixture-flattened"
  run_profile \
    "$retained_output" \
    PROGPU_AVALONIA_SKIP_BUILD=1 \
    "$fixture_variable"
  run_profile \
    "$flattened_output" \
    PROGPU_AVALONIA_SKIP_BUILD=1 \
    PROGPU_AVALONIA_RETAINED_SCENE=0 \
    "$fixture_variable"

  retained_fixture="$retained_output/source-progpu/bitmapcache.png"
  flattened_fixture="$flattened_output/source-progpu/bitmapcache.png"
  if ! cmp -s "$retained_fixture" "$flattened_fixture"; then
    echo "Retained and flattened BitmapCache $fixture pixels differ." >&2
    exit 17
  fi
  if ! grep -q \
      "$fixture_log_pattern" \
      "$retained_output/source-progpu/bitmapcache.log"; then
    echo "BitmapCache $fixture fixture was not applied." >&2
    exit 18
  fi
  fixture_fallback_nodes="$(
    read_fallback_nodes \
      "$retained_output/source-progpu/bitmapcache.json"
  )"
  if [[ "$fixture_fallback_nodes" != "0" ]]; then
    echo "BitmapCache $fixture used $fixture_fallback_nodes fallback nodes; expected zero." >&2
    exit 19
  fi
done

echo "Avalonia retained pixel contract passed: 9 zero-fallback pages, native linear/conic/picture opacity masks, transformed and changing adorner clip chains, incremental topology reparenting, blur/drop-shadow effects, geometry clipping, static and changing inherited drawing options, and BitmapCache scale/snap/ClearType."
