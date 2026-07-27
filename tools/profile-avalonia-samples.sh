#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:-$repo_root/artifacts/avalonia-samples-profile}"
sample_filter="${PROGPU_AVALONIA_SAMPLE_FILTER:-.*}"
warmup_frames="${PROGPU_AVALONIA_SAMPLE_WARMUP_FRAMES:-120}"
measure_frames="${PROGPU_AVALONIA_SAMPLE_MEASURE_FRAMES:-300}"
text_shaper_list="${PROGPU_AVALONIA_SAMPLE_TEXT_SHAPERS:-progpu}"
repeat_count="${PROGPU_AVALONIA_SAMPLE_REPEATS:-1}"
sample_project="$repo_root/src/ProGPU.Samples.Avalonia/ProGPU.Samples.Avalonia.csproj"
sample_app="$repo_root/src/ProGPU.Samples.Avalonia/bin/Release/net10.0/ProGPU.Samples.Avalonia.dll"
analyzer_project="$repo_root/tools/ProGPU.SampleMemoryProfiler/ProGPU.SampleMemoryProfiler.csproj"
analyzer_app="$repo_root/tools/ProGPU.SampleMemoryProfiler/bin/Release/net10.0/ProGPU.SampleMemoryProfiler.dll"
failure_path="$output_root/failures.tsv"

samples=(
  Charting
  Dxf
  Drawing
  MotionMark
  Markdown
  Glyphs
  DataGrid
  Designer
)

text_shapers=()
IFS=',' read -r -a requested_text_shapers <<< "$text_shaper_list"
for text_shaper in "${requested_text_shapers[@]}"; do
  normalized="$(printf '%s' "$text_shaper" | tr '[:upper:]' '[:lower:]' | xargs)"
  case "$normalized" in
    progpu|harfbuzz)
      text_shapers+=("$normalized")
      ;;
    "")
      ;;
    *)
      echo "Unsupported text shaper '$text_shaper'. Use progpu or harfbuzz." >&2
      exit 2
      ;;
  esac
done

if [[ ${#text_shapers[@]} -eq 0 ]]; then
  echo "No text shapers were selected." >&2
  exit 2
fi
if [[ ! "$repeat_count" =~ ^[1-9][0-9]*$ ]]; then
  echo "PROGPU_AVALONIA_SAMPLE_REPEATS must be a positive integer." >&2
  exit 2
fi

mkdir -p "$output_root"
: > "$failure_path"

if [[ "${PROGPU_AVALONIA_SAMPLE_SKIP_BUILD:-0}" != "1" ]]; then
  dotnet restore "$sample_project"
  dotnet build "$sample_project" -c Release --no-restore
  dotnet restore "$analyzer_project"
  dotnet build "$analyzer_project" -c Release --no-restore
fi

completed=0
failed=0
sample_index=0
for sample in "${samples[@]}"; do
    if [[ ! "$sample" =~ $sample_filter ]]; then
      continue
    fi

    for ((run = 1; run <= repeat_count; run++)); do
      ordered_shapers=("${text_shapers[@]}")
      if [[ ${#ordered_shapers[@]} -eq 2 ]] &&
         (( (sample_index + run) % 2 == 0 )); then
        ordered_shapers=("${text_shapers[1]}" "${text_shapers[0]}")
      fi

      for text_shaper in "${ordered_shapers[@]}"; do
        shaper_output="$output_root/$text_shaper"
        mkdir -p "$shaper_output"
        slug="$(printf '%s' "$sample" | tr '[:upper:]' '[:lower:]')"
        json_path="$shaper_output/$slug-run-$run.json"
        log_path="$shaper_output/$slug-run-$run.log"
        app_args=(--sample "$sample")
        if [[ "$text_shaper" == "harfbuzz" ]]; then
          app_args+=(--harfbuzz)
        fi

        echo "[AvaloniaSampleProfile] textShaper=$text_shaper sample=$sample run=$run"
        if PROGPU_AVALONIA_SAMPLE_BENCHMARK_OUTPUT="$json_path" \
           PROGPU_AVALONIA_SAMPLE_BENCHMARK_WARMUP_FRAMES="$warmup_frames" \
           PROGPU_AVALONIA_SAMPLE_BENCHMARK_MEASURE_FRAMES="$measure_frames" \
           PROGPU_AVALONIA_SAMPLE_BENCHMARK_RUN="$run" \
           dotnet "$sample_app" "${app_args[@]}" 2>&1 | tee "$log_path"; then
          if [[ ! -s "$json_path" ]]; then
            printf '%s\t%s\t%s\t%s\n' \
              "$text_shaper" \
              "$sample" \
              "$run" \
              "benchmark produced no JSON result" \
              >> "$failure_path"
            failed=$((failed + 1))
            continue
          fi

          if ! jq -e \
            '.SchemaVersion >= 3 and
             .Run >= 1 and
             .FrameTimeSampleCount == .MeasuredFrames and
             .PresentationMode == "SameDeviceTexture" and
             .EmbeddedBackendKind == "SilkNative" and
             .PresentedTextureNonTransparentPixels > 0 and
             .PresentedTexturePixelsDifferentFromFirst > 0 and
             .RetainedCompositionFallbackNodes == 0 and
             .DrawCalls > 0 and
             .OuterFramesSeen > 0' \
            "$json_path" > /dev/null; then
            printf '%s\t%s\t%s\t%s\n' \
              "$text_shaper" \
              "$sample" \
              "$run" \
              "distribution, backend, presentation, draw-call, or fallback contract failed" \
              >> "$failure_path"
            failed=$((failed + 1))
            continue
          fi

          completed=$((completed + 1))
        else
          status=${PIPESTATUS[0]}
          printf '%s\t%s\t%s\tprocess exit %s\n' \
            "$text_shaper" \
            "$sample" \
            "$run" \
            "$status" \
            >> "$failure_path"
          failed=$((failed + 1))
        fi
      done
    done
    sample_index=$((sample_index + 1))
  done

if [[ $completed -eq 0 ]]; then
  echo "No samples completed. Filter: PROGPU_AVALONIA_SAMPLE_FILTER=$sample_filter" >&2
  exit 4
fi

dotnet "$analyzer_app" summarize-avalonia \
  "$output_root" \
  "$output_root/summary.json" \
  "$output_root/summary.md"

echo "[AvaloniaSampleProfile] completed=$completed failed=$failed report=$output_root/summary.md"
if [[ $failed -ne 0 ]]; then
  echo "[AvaloniaSampleProfile] failures=$failure_path" >&2
  exit 5
fi
