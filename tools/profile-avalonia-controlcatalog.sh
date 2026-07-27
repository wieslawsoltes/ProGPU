#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:-$repo_root/artifacts/avalonia-controlcatalog-profile}"
page_filter="${PROGPU_AVALONIA_PAGE_FILTER:-.*}"
warmup_frames="${PROGPU_AVALONIA_WARMUP_FRAMES:-120}"
measure_frames="${PROGPU_AVALONIA_MEASURE_FRAMES:-300}"
backend_list="${PROGPU_AVALONIA_BACKENDS:-source-progpu}"
capture_screenshots="${PROGPU_AVALONIA_SCREENSHOTS:-0}"
repeat_count="${PROGPU_AVALONIA_REPEATS:-1}"
layout_clip_fixture="${PROGPU_AVALONIA_LAYOUT_CLIP_FIXTURE:-0}"
geometry_clip_fixture="${PROGPU_AVALONIA_GEOMETRY_CLIP_FIXTURE:-0}"
bitmap_cache_fixture="${PROGPU_AVALONIA_BITMAP_CACHE_FIXTURE:-0}"
effect_fixture="${PROGPU_AVALONIA_EFFECT_FIXTURE:-0}"
opacity_mask_fixture="${PROGPU_AVALONIA_OPACITY_MASK_FIXTURE:-0}"
inherited_drawing_options_fixture="${PROGPU_AVALONIA_INHERITED_DRAWING_OPTIONS_FIXTURE:-0}"
topology_fixture="${PROGPU_AVALONIA_TOPOLOGY_FIXTURE:-0}"
adorner_fixture="${PROGPU_AVALONIA_ADORNER_FIXTURE:-0}"
skia_project="$repo_root/integration/AvaloniaSkiaControlCatalogReference/AvaloniaSkiaControlCatalogReference.csproj"
skia_app="$repo_root/integration/AvaloniaSkiaControlCatalogReference/bin/Release/net10.0/AvaloniaSkiaControlCatalogReference.dll"
source_project="$repo_root/integration/AvaloniaSourceControlCatalog/AvaloniaSourceControlCatalog.csproj"
source_app="$repo_root/integration/AvaloniaSourceControlCatalog/bin/Release/net10.0/AvaloniaSourceControlCatalog.dll"
avalonia_source_root="${PROGPU_AVALONIA_ROOT:-$repo_root/.worktrees/avalonia-12.0.5}"
analyzer_project="$repo_root/tools/ProGPU.SampleMemoryProfiler/ProGPU.SampleMemoryProfiler.csproj"
analyzer_app="$repo_root/tools/ProGPU.SampleMemoryProfiler/bin/Release/net10.0/ProGPU.SampleMemoryProfiler.dll"
page_source="$avalonia_source_root/samples/ControlCatalog/MainView.xaml"
failure_path="$output_root/failures.tsv"
host_kernel="$(uname -s)"
if command -v rg >/dev/null 2>&1; then
  search_text() {
    rg -q "$1" "$2"
  }
else
  search_text() {
    grep -Eq "$1" "$2"
  }
fi
case "$host_kernel" in
  Darwin)
    expected_native_presentation="DawnMetalIOSurface"
    ;;
  Linux)
    expected_native_presentation="DawnVulkanXlib"
    ;;
  MINGW*|MSYS*|CYGWIN*)
    expected_native_presentation="DawnD3D12HWND"
    ;;
  *)
    expected_native_presentation=""
    ;;
esac

mkdir -p "$output_root"
: > "$failure_path"

backends=()
IFS=',' read -r -a requested_backends <<< "$backend_list"
for backend in "${requested_backends[@]}"; do
  normalized="$(printf '%s' "$backend" | tr '[:upper:]' '[:lower:]' | xargs)"
  case "$normalized" in
    source-progpu|source-progpu-harfbuzz|source-progpu-native|source-progpu-native-harfbuzz|skia)
      backends+=("$normalized")
      ;;
    "")
      ;;
    *)
      echo "Unsupported backend '$backend'. Use source-progpu, source-progpu-harfbuzz, source-progpu-native, source-progpu-native-harfbuzz, or skia." >&2
      exit 2
      ;;
  esac
done

if [[ ${#backends[@]} -eq 0 ]]; then
  echo "No benchmark backends were selected." >&2
  exit 2
fi

if [[ ! "$repeat_count" =~ ^[1-9][0-9]*$ ]]; then
  echo "PROGPU_AVALONIA_REPEATS must be a positive integer." >&2
  exit 2
fi

if [[ "${PROGPU_AVALONIA_SKIP_BUILD:-0}" != "1" ]]; then
  dotnet restore "$analyzer_project"
  dotnet build "$analyzer_project" -c Release --no-restore
  source_built=0
  skia_built=0
  native_dawn_installed=0
  for backend in "${backends[@]}"; do
    if [[ "$backend" == source-progpu* ]]; then
      if [[ "$source_built" == "0" ]]; then
        "$repo_root/tools/prepare-avalonia-12.0.5-source.sh"
        dotnet restore "$source_project" \
          -p:ProGpuDependencyMode=Source \
          -p:ProGpuSourceRoot="$repo_root" \
          -p:ProGpuAvaloniaSourceRoot="$avalonia_source_root" \
          -p:PackAvaloniaNative=false \
          -p:UseSkiaSharpShim=true
        dotnet build "$source_project" \
          -c Release \
          --no-restore \
          -p:ProGpuDependencyMode=Source \
          -p:ProGpuSourceRoot="$repo_root" \
          -p:ProGpuAvaloniaSourceRoot="$avalonia_source_root" \
          -p:PackAvaloniaNative=false \
          -p:UseSkiaSharpShim=true
        source_built=1
      fi
      if [[ "$backend" == source-progpu-native* &&
            "$native_dawn_installed" == "0" ]]; then
        if [[ "$host_kernel" == "Darwin" ]]; then
          "$repo_root/tools/build-avalonia-native-dawn.sh"
        fi
        native_dawn_installed=1
      fi
    else
      if [[ "$skia_built" == "0" ]]; then
        "$repo_root/tools/prepare-avalonia-12.0.5-source.sh"
        dotnet restore "$skia_project" \
          -p:PackAvaloniaNative=false \
          -p:AvaloniaForkRoot="$avalonia_source_root"
        dotnet build "$skia_project" \
          -c Release \
          --no-restore \
          -p:PackAvaloniaNative=false \
          -p:AvaloniaForkRoot="$avalonia_source_root"
        if [[ "$host_kernel" == "Darwin" ]]; then
          source_native="$repo_root/integration/AvaloniaSourceControlCatalog/bin/Release/net10.0/runtimes/osx/native/libAvaloniaNative.dylib"
          skia_native="$repo_root/integration/AvaloniaSkiaControlCatalogReference/bin/Release/net10.0/runtimes/osx/native/libAvaloniaNative.dylib"
          mkdir -p "$(dirname "$skia_native")"
          if [[ "$native_dawn_installed" == "1" &&
                -f "$source_native" ]]; then
            cp "$source_native" "$skia_native"
          else
            "$repo_root/tools/build-avalonia-native-dawn.sh" \
              "$skia_native"
          fi
        fi
        skia_built=1
      fi
    fi
  done
elif [[ ! -f "$analyzer_app" ]]; then
  echo "PROGPU_AVALONIA_SKIP_BUILD=1 requires the profiler at $analyzer_app." >&2
  echo "Build $analyzer_project in Release before running this command." >&2
  exit 3
fi

if [[ "${PROGPU_AVALONIA_BUILD_ONLY:-0}" == "1" ]]; then
  echo "[AvaloniaProfile] build-only preparation complete"
  exit 0
fi

pages=()
while IFS= read -r page; do
  pages+=("$page")
done < <(sed -n 's/^[[:space:]]*<TabItem Header="\([^"]*\)".*/\1/p' "$page_source")

if [[ ${#pages[@]} -eq 0 ]]; then
  echo "No ControlCatalog pages were discovered in $page_source" >&2
  exit 3
fi

completed=0
failed=0
for page in "${pages[@]}"; do
  if [[ ! "$page" =~ $page_filter ]]; then
    continue
  fi

  slug="$(printf '%s' "$page" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-|-$//g')"
  for ((run = 1; run <= repeat_count; run++)); do
    for ((order = 0; order < ${#backends[@]}; order++)); do
      if ((run % 2 == 1)); then
        backend_index=$order
      else
        backend_index=$((${#backends[@]} - 1 - order))
      fi
      backend="${backends[$backend_index]}"

      use_harfbuzz=0
      use_native_windowing=0
      if [[ "$backend" == "source-progpu" ]]; then
        app="$source_app"
      elif [[ "$backend" == "source-progpu-harfbuzz" ]]; then
        app="$source_app"
        use_harfbuzz=1
      elif [[ "$backend" == "source-progpu-native" ]]; then
        app="$source_app"
        use_native_windowing=1
      elif [[ "$backend" == "source-progpu-native-harfbuzz" ]]; then
        app="$source_app"
        use_harfbuzz=1
        use_native_windowing=1
      else
        app="$skia_app"
      fi

      backend_output="$output_root/$backend"
      mkdir -p "$backend_output"
      if ((repeat_count == 1)); then
        result_name="$slug"
      else
        result_name="$(printf '%s-run-%02d' "$slug" "$run")"
      fi

      json_path="$backend_output/$result_name.json"
      log_path="$backend_output/$result_name.log"
      screenshot_path="$backend_output/$result_name.png"
      screenshot_variable=""
      custom_visual_fixture=0
      external_opengl_fixture=0
      app_args=(--page "$page")
      if [[ "$use_harfbuzz" == "1" ]]; then
        app_args+=(--harfbuzz)
      fi
      if [[ "$use_native_windowing" == "1" ]]; then
        app_args+=(--native-windowing)
      fi
      if [[ "$capture_screenshots" == "1" ]]; then
        screenshot_variable="$screenshot_path"
      fi
      app_root="$(dirname "$app")"
      native_arch="$(uname -m)"
      case "$native_arch" in
        x86_64|amd64)
          native_arch="x64"
          ;;
        arm64|aarch64)
          native_arch="arm64"
          ;;
      esac
      native_launch=(env)
      case "$host_kernel" in
        Darwin)
          native_root="$app_root/runtimes/osx-$native_arch/native"
          if [[ -d "$native_root" ]]; then
            native_launch+=(
              "DYLD_LIBRARY_PATH=$native_root${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}")
          fi
          ;;
        Linux)
          native_root="$app_root/runtimes/linux-$native_arch/native"
          if [[ -d "$native_root" ]]; then
            native_launch+=(
              "LD_LIBRARY_PATH=$native_root${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}")
          fi
          ;;
        MINGW*|MSYS*|CYGWIN*)
          native_root="$app_root/runtimes/win-$native_arch/native"
          if [[ -d "$native_root" ]]; then
            native_launch+=(
              "PATH=$native_root:$PATH")
          fi
          ;;
      esac
      if [[ "$page" == "Composition" ]]; then
        custom_visual_fixture=1
      fi
      if [[ "$page" == "OpenGL" ||
            "$page" == "OpenGL Lease" ]]; then
        # These upstream samples intentionally exercise Avalonia's external
        # OpenGL control/lease path rather than the selected 2D compositor.
        # Their process/frame telemetry is still part of the census, but a
        # retained ProGPU scene or Dawn presentation sample is not expected.
        external_opengl_fixture=1
      fi

      echo "[AvaloniaProfile] backend=$backend page=$page run=$run/$repeat_count"
      if PROGPU_AVALONIA_BENCHMARK_OUTPUT="$json_path" \
         PROGPU_AVALONIA_BENCHMARK_SCREENSHOT="$screenshot_variable" \
         PROGPU_AVALONIA_BENCHMARK_WARMUP_FRAMES="$warmup_frames" \
         PROGPU_AVALONIA_BENCHMARK_MEASURE_FRAMES="$measure_frames" \
         PROGPU_AVALONIA_BENCHMARK_RUN="$run" \
         PROGPU_AVALONIA_BENCHMARK_CUSTOM_VISUAL="$custom_visual_fixture" \
         PROGPU_AVALONIA_BENCHMARK_LAYOUT_CLIP="$layout_clip_fixture" \
         PROGPU_AVALONIA_BENCHMARK_GEOMETRY_CLIP="$geometry_clip_fixture" \
         PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_CHANNEL="$bitmap_cache_fixture" \
         PROGPU_AVALONIA_BENCHMARK_EFFECT_CHANNEL="$effect_fixture" \
         PROGPU_AVALONIA_BENCHMARK_OPACITY_MASK_CHANNEL="$opacity_mask_fixture" \
         PROGPU_AVALONIA_BENCHMARK_INHERITED_DRAWING_OPTIONS_CHANNEL="$inherited_drawing_options_fixture" \
         PROGPU_AVALONIA_BENCHMARK_TOPOLOGY_CHANNEL="$topology_fixture" \
         PROGPU_AVALONIA_BENCHMARK_ADORNER_CHANNEL="$adorner_fixture" \
         "${native_launch[@]}" \
         dotnet "$app" "${app_args[@]}" 2>&1 | tee "$log_path"; then
        if [[ -s "$json_path" ]]; then
          if ! search_text \
            '"SchemaVersion"[[:space:]]*:[[:space:]]*2' \
            "$json_path" ||
             ! search_text \
            "\"FrameTimeSampleCount\"[[:space:]]*:[[:space:]]*$measure_frames" \
            "$json_path"; then
            printf '%s\t%s\t%s\t%s\n' \
              "$backend" \
              "$page" \
              "$run" \
              "missing or incomplete frame-time distribution telemetry" \
              >> "$failure_path"
            failed=$((failed + 1))
            continue
          fi
          if [[ "$backend" == source-progpu* &&
                "$external_opengl_fixture" == "0" &&
                "${PROGPU_AVALONIA_RETAINED_SCENE:-1}" != "0" ]]; then
            if ! search_text \
              '"RetainedCompositionFallbackNodes"[[:space:]]*:[[:space:]]*0' \
              "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "missing or nonzero retained composition fallback telemetry" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if ! search_text \
              '"RetainedCompositionScenes"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
              "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "native retained composition scene telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if ! search_text \
              '"RetainedCompositionServerBackendRenders"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
              "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "typed composition server backend telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if [[ "$backend" == source-progpu-native* ]]; then
              if [[ -z "$expected_native_presentation" ]] ||
                 ! search_text \
                   "\"PresentationPath\"[[:space:]]*:[[:space:]]*\"$expected_native_presentation\"" \
                   "$json_path"; then
                printf '%s\t%s\t%s\t%s\n' \
                  "$backend" \
                  "$page" \
                  "$run" \
                  "strict native lane did not present through $expected_native_presentation" \
                  >> "$failure_path"
                failed=$((failed + 1))
                continue
              fi
            fi
            if [[ "$page" == "Composition" ]] &&
               { ! search_text \
                   '"RetainedCompositionCustomVisualNodes"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                   "$json_path" ||
                 ! search_text \
                   '"RetainedCompositionCustomVisualCompilations"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                   "$json_path"; }; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "native custom-visual node or compilation telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if [[ "$layout_clip_fixture" == "1" ]] &&
               ! search_text \
                 '"RetainedCompositionLayoutClipSynchronizations"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                 "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "compact retained layout-clip synchronization telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if [[ "$geometry_clip_fixture" == "1" ]] &&
               ! search_text \
                 '"RetainedCompositionGeometryClipSynchronizations"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                 "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "typed retained geometry-clip synchronization telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if [[ "$bitmap_cache_fixture" == "1" ]] &&
               ! search_text \
                 '"RetainedCompositionBitmapCacheSynchronizations"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                 "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "typed retained bitmap-cache synchronization telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if [[ "$effect_fixture" == "1" ]] &&
               ! search_text \
                 '"RetainedCompositionEffectSynchronizations"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                 "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "typed retained effect synchronization telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if [[ "$opacity_mask_fixture" == "1" ]] &&
               ! search_text \
                 '"RetainedCompositionOpacityMaskSynchronizations"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                 "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "typed retained opacity-mask synchronization telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if [[ "$inherited_drawing_options_fixture" == "1" ]] &&
               ! search_text \
                 '"RetainedCompositionInheritedDrawingOptionsSynchronizations"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                 "$json_path"; then
              printf '%s\t%s\t%s\t%s\n' \
                "$backend" \
                "$page" \
                "$run" \
                "typed inherited drawing-options synchronization telemetry missing" \
                >> "$failure_path"
              failed=$((failed + 1))
              continue
            fi
            if [[ "$topology_fixture" == "1" ]]; then
              if ! search_text \
                   '"RetainedCompositionTopologySynchronizationsDuringMeasurement"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                   "$json_path"; then
                printf '%s\t%s\t%s\t%s\n' \
                  "$backend" \
                  "$page" \
                  "$run" \
                  "typed retained topology synchronization telemetry missing" \
                  >> "$failure_path"
                failed=$((failed + 1))
                continue
              fi
              if ! search_text \
                   '"RetainedCompositionSceneFullSynchronizationsDuringMeasurement"[[:space:]]*:[[:space:]]*0([[:space:],]|$)' \
                   "$json_path"; then
                printf '%s\t%s\t%s\t%s\n' \
                  "$backend" \
                  "$page" \
                  "$run" \
                  "topology fixture triggered a full synchronization during measurement" \
                  >> "$failure_path"
                failed=$((failed + 1))
                continue
              fi
            fi
            if [[ "$adorner_fixture" == "1" ]]; then
              if ! search_text \
                   '"RetainedCompositionAdornerSynchronizationsDuringMeasurement"[[:space:]]*:[[:space:]]*[1-9][0-9]*' \
                   "$json_path"; then
                printf '%s\t%s\t%s\t%s\n' \
                  "$backend" \
                  "$page" \
                  "$run" \
                  "typed retained adorner synchronization telemetry missing" \
                  >> "$failure_path"
                failed=$((failed + 1))
                continue
              fi
              if ! search_text \
                   '"RetainedCompositionSceneFullSynchronizationsDuringMeasurement"[[:space:]]*:[[:space:]]*0([[:space:],]|$)' \
                   "$json_path"; then
                printf '%s\t%s\t%s\t%s\n' \
                  "$backend" \
                  "$page" \
                  "$run" \
                  "adorner fixture triggered a full synchronization during measurement" \
                  >> "$failure_path"
                failed=$((failed + 1))
                continue
              fi
            fi
          fi
          completed=$((completed + 1))
        else
          printf '%s\t%s\t%s\t%s\n' \
            "$backend" "$page" "$run" \
            "benchmark produced no JSON result" >> "$failure_path"
          failed=$((failed + 1))
        fi
      else
        status=${PIPESTATUS[0]}
        printf '%s\t%s\t%s\t%s\n' \
          "$backend" "$page" "$run" "process exit $status" >> "$failure_path"
        failed=$((failed + 1))
      fi
    done
  done
done

if [[ $completed -eq 0 ]]; then
  echo "No pages completed. Filter: PROGPU_AVALONIA_PAGE_FILTER=$page_filter" >&2
  exit 4
fi

dotnet "$analyzer_app" summarize-avalonia \
  "$output_root" \
  "$output_root/summary.json" \
  "$output_root/summary.md"

echo "[AvaloniaProfile] completed=$completed failed=$failed report=$output_root/summary.md"
if [[ $failed -ne 0 ]]; then
  echo "[AvaloniaProfile] failures=$failure_path" >&2
  exit 5
fi
