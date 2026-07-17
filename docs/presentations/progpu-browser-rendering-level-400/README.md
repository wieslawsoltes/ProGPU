# ProGPU Browser Rendering — Level 400

This directory contains the delivered 23-slide PowerPoint talk and the complete authored source set used to build it.

## Contents

- `ProGPU-Browser-Rendering-Level-400.pptx` — final editable presentation, including speaker notes on every slide.
- `source/build.mjs` — artifact-tool authoring source for the complete deck.
- `source/template-starter.pptx` — exact 23-slide inherited-frame starter consumed by the builder.
- `source/template-frame-map.json`, `template-audit.txt`, and `deviation-log.txt` — template-following decisions and provenance.
- `source/assets/*.dot` — Graphviz architecture sources.
- `source/assets/*.svg` and `*.png` — generated diagram assets consumed by the deck builder.
- `source/source-notes.txt` — repository files used to ground the technical content.

## Rebuild

The deck uses the Codex presentation runtime's `@oai/artifact-tool`. Keep the runtime workspace outside this repository, then copy the tracked source package into it:

```bash
DECK_DIR="$PWD/docs/presentations/progpu-browser-rendering-level-400"
WORK_DIR="$(mktemp -d)/progpu-browser-rendering-level-400"
SKILL_DIR="/absolute/path/to/presentations-skill"

mkdir -p "$WORK_DIR"
cp -R "$DECK_DIR/source/." "$WORK_DIR/"

(cd "$WORK_DIR" && npm install)

node "$SKILL_DIR/container_tools/setup_artifact_tool_workspace.mjs" \
  --workspace "$WORK_DIR"

# Optional: regenerate SVG and PNG diagrams after editing a .dot source.
# PNG conversion uses the macOS `sips` utility.
node "$WORK_DIR/render-graphs.mjs"

PROGPU_PRESENTATION_WORKSPACE="$WORK_DIR/qa" \
  node "$WORK_DIR/build.mjs" \
  "$DECK_DIR/ProGPU-Browser-Rendering-Level-400.pptx"
```

The builder writes slide renders, layout data, a montage, and inspection output under `PROGPU_PRESENTATION_WORKSPACE`; these QA intermediates are intentionally not committed.

## Content architecture

The presentation is grounded in the repository implementation rather than external research. Its source map covers the shared retained compositor, `IWebGpuApi`, the browser binary protocol and handle model, JavaScript decoding into `navigator.gpu`, worker execution modes, physical framebuffer/DPI behavior, retained path and glyph atlases, async mapping, browser input batching, device loss, Hot Reload, and Release AOT publishing.
