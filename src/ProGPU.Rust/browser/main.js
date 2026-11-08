import init, { create_renderer, decode_simple_glyph, decode_glyph_outline, decode_cff_outline, normalize_font_variation, item_variation_values, decode_cff2_outline, decode_variation_runs, interpolate_gvar_deltas, decode_gvar_metadata, apply_gvar_simple, gvar_component_metrics, horizontal_font_metrics, design_font_advance, vertical_font_metrics, simple_font_run, simple_font_advances, unicode_properties, decode_unicode_utf8, decode_unicode_utf16 } from './pkg/progpu.js';
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
  // Serialize mutable WASM borrows across asynchronous preparation and tests.
  let tail = Promise.resolve();
  const exclusive = action => {
    const next = tail.then(action);
    tail = next.catch(e => { errors.push(String(e)); status.textContent = String(e); });
    return next;
  };
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
      status.textContent = `${m.width} × ${m.height} · ${m.draws} draw · ${m.pathRasters} shared paths`;
    });
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
  window.addEventListener('pagehide', () => { stopped = true; });
  // Drain entered JavaScript work, then submit a final frame with an explicit
  // queue-completion signal. The host awaits that signal before compositor capture.
  const pause = () => { stopped = true; return exclusive(() => renderer.begin_capture()); };
  return { renderer, exclusive, errors, dimensions, pause, decode_simple_glyph, decode_glyph_outline, decode_cff_outline, normalize_font_variation, item_variation_values, decode_cff2_outline, decode_variation_runs, interpolate_gvar_deltas, decode_gvar_metadata, apply_gvar_simple, gvar_component_metrics, horizontal_font_metrics, design_font_advance, vertical_font_metrics, simple_font_run, simple_font_advances, unicode_properties, decode_unicode_utf8, decode_unicode_utf16 };
})();
window.progpuReady.catch(e => { status.textContent = String(e); });
