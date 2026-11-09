import init, { create_renderer, decode_simple_glyph, decode_glyph_outline, decode_cff_outline, normalize_font_variation, item_variation_values, decode_cff2_outline, decode_variation_runs, interpolate_gvar_deltas, decode_gvar_metadata, apply_gvar_simple, gvar_component_metrics, horizontal_font_metrics, design_font_advance, vertical_font_metrics, simple_font_run, simple_font_advances, unicode_properties, script_properties, decode_unicode_utf8, decode_unicode_utf16, resolve_bidi_utf16, segment_graphemes_utf16, managed_grapheme_utf16, managed_prepare_text, managed_script_actions, grapheme_starts_utf16, resolve_line_breaks_utf16, normalize_utf16, normalize_utf16_profile, normalization_plan_lookup, layout_table_query, layout_digest_query, select_layout, select_layout_feature, select_managed_layout, managed_context_query, apply_gsub, apply_managed_gsub, managed_feature_query, apply_managed_stage, apply_managed_gpos, apply_managed_positioning, managed_variation_instance, managed_font_names, managed_font_state, managed_font_mapping, gdef_blocklisted } from './pkg/progpu.js';
const canvas = document.querySelector('canvas');
const status = document.querySelector('#status');
const errors = [];
window.addEventListener('unhandledrejection', e => errors.push(String(e.reason)));
window.progpuReady = (async () => {
  await init();
  if (!navigator.gpu) throw new Error('WebGPU is required');
  const dimensions = () => ({ width: Math.round(canvas.clientWidth * devicePixelRatio),
    height: Math.round(canvas.clientHeight * devicePixelRatio), dpi: devicePixelRatio });
  let size = dimensions();
  const renderer = await create_renderer(canvas, size.width, size.height, size.dpi);
  if (new URLSearchParams(location.search).get('scene') === 'materials') {
    renderer.select_material_scene(true);
    await renderer.resize(size.width, size.height, size.dpi);
  }
  if (new URLSearchParams(location.search).get('scene') === 'images') {
    renderer.select_image_scene(true);
    await renderer.resize(size.width, size.height, size.dpi);
  }
  // Serialize mutable WASM borrows across asynchronous preparation and tests.
  let tail = Promise.resolve();
  const exclusive = action => {
    const next = tail.then(action);
    tail = next.catch(e => { errors.push(String(e)); status.textContent = String(e); });
    return next;
  };
  document.querySelector('#text-font').addEventListener('change', event => {
    const file = event.target.files[0];
    if (!file) return;
    exclusive(async () => {
      renderer.load_text_scene(new Uint8Array(await file.arrayBuffer()), 0);
      const next = dimensions();
      await renderer.resize(next.width, next.height, next.dpi);
      size = next;
    }).catch(() => {}); // The serialized queue reports errors in the status line.
  });
  let stopped = false;
  async function frame() {
    if (stopped) return;
    await exclusive(async () => {
      const next = dimensions();
      if (next.width !== size.width || next.height !== size.height || next.dpi !== size.dpi) {
        await renderer.resize(next.width, next.height, next.dpi);
        size = next;
      }
      renderer.draw();
      const m = renderer.metrics();
      status.textContent = `${m.width} × ${m.height} · ${m.draws} draws · ${m.glyphInstances} glyphs · ${m.pathRasters} paths · ${m.materialBrushes} materials`;
    });
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
  window.addEventListener('pagehide', () => { stopped = true; });
  // Drain entered JavaScript work, then submit a final frame with an explicit
  // queue-completion signal. The host awaits that signal before compositor capture.
  const pause = () => { stopped = true; return exclusive(() => renderer.begin_capture()); };
  return { renderer, exclusive, errors, dimensions, pause, decode_simple_glyph, decode_glyph_outline, decode_cff_outline, normalize_font_variation, item_variation_values, decode_cff2_outline, decode_variation_runs, interpolate_gvar_deltas, decode_gvar_metadata, apply_gvar_simple, gvar_component_metrics, horizontal_font_metrics, design_font_advance, vertical_font_metrics, simple_font_run, simple_font_advances, unicode_properties, script_properties, decode_unicode_utf8, decode_unicode_utf16, resolve_bidi_utf16, segment_graphemes_utf16, managed_grapheme_utf16, managed_prepare_text, managed_script_actions, grapheme_starts_utf16, resolve_line_breaks_utf16, normalize_utf16, normalize_utf16_profile, normalization_plan_lookup, layout_table_query, layout_digest_query, select_layout, select_layout_feature, select_managed_layout, managed_context_query, apply_gsub, apply_managed_gsub, managed_feature_query, apply_managed_stage, apply_managed_gpos, apply_managed_positioning, managed_variation_instance, managed_font_names, managed_font_state, managed_font_mapping, gdef_blocklisted };
})();
window.progpuReady.catch(e => { status.textContent = String(e); });
