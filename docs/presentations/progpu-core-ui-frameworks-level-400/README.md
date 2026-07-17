# ProGPU Core Rendering and UI Frameworks — Level 400

This directory contains the delivered 23-slide PowerPoint talk and its complete authored source set.

## Contents

- `ProGPU-Core-Rendering-and-UI-Frameworks-Level-400.pptx` — final editable presentation, including speaker notes on every slide.
- `source/build.mjs` — artifact-tool authoring source for the complete deck.
- `source/template-frame-map.json`, `template-audit.txt`, and `deviation-log.txt` — template-following decisions and provenance.
- `source/assets/*.dot` — Graphviz sources for the architecture, compositor, retained geometry, and framework-convergence diagrams.
- `source/source-notes.txt` — ProGPU, LibreWPF, and LibreWinForms implementation files used to ground the talk.
- `source/coverage-audit.txt` — content and validation coverage for the delivered deck.

The builder intentionally reuses the audited `template-starter.pptx` tracked by the browser Level 400 presentation. This keeps the two talks visually consistent without committing the same template binary twice.

## Rebuild

The deck uses the Codex presentation runtime's `@oai/artifact-tool`. Copy both source packages into one external workspace so the shared starter remains at the repository-relative location expected by the builder:

```bash
PRESENTATIONS_DIR="$PWD/docs/presentations"
WORK_DIR="$(mktemp -d)/presentations"
SKILL_DIR="/absolute/path/to/presentations-skill"
CORE_SOURCE="$WORK_DIR/progpu-core-ui-frameworks-level-400/source"

mkdir -p \
  "$CORE_SOURCE" \
  "$WORK_DIR/progpu-browser-rendering-level-400/source"

cp -R \
  "$PRESENTATIONS_DIR/progpu-core-ui-frameworks-level-400/source/." \
  "$CORE_SOURCE/"
cp -R \
  "$PRESENTATIONS_DIR/progpu-browser-rendering-level-400/source/." \
  "$WORK_DIR/progpu-browser-rendering-level-400/source/"

(cd "$CORE_SOURCE" && npm install)

node "$SKILL_DIR/container_tools/setup_artifact_tool_workspace.mjs" \
  --workspace "$CORE_SOURCE"

PROGPU_PRESENTATION_ASSETS="$WORK_DIR/generated-assets" \
  node "$CORE_SOURCE/render-graphs.mjs"

PROGPU_PRESENTATION_WORKSPACE="$WORK_DIR/qa" \
PROGPU_PRESENTATION_ASSETS="$WORK_DIR/generated-assets" \
  node "$CORE_SOURCE/build.mjs" \
  "$PRESENTATIONS_DIR/progpu-core-ui-frameworks-level-400/ProGPU-Core-Rendering-and-UI-Frameworks-Level-400.pptx"
```

The builder writes slide renders, layout data, a montage, and inspection output under `PROGPU_PRESENTATION_WORKSPACE`; these QA intermediates are intentionally not committed.

## Content architecture

The talk traces framework mutations through layout and visual invalidation, stable scene identity, `DrawingContext` and `RenderCommand`, compositor lowering, strict compiled-scene reuse, order-preserving batching, GPU resource lifetime, retained paths and glyphs, layers/effects/extensions, hit testing, and platform hosting. It then maps those shared mechanisms to ProGPU.WinUI, LibreWPF's typed portable/MIL replay bridge, and LibreWinForms' compatible paint and `System.Drawing` surface.
