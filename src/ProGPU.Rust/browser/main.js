import init, { create_renderer, decode_simple_glyph, decode_glyph_outline, decode_cff_outline, normalize_font_variation } from './pkg/progpu.js';
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
  // Capture the final presented frame without continuously submitting more work.
  // Wait for any already-entered asynchronous frame before returning to the host.
  const pause = () => { stopped = true; return exclusive(() => {}); };
  return { renderer, exclusive, errors, dimensions, pause, decode_simple_glyph, decode_glyph_outline, decode_cff_outline, normalize_font_variation };
})();
window.progpuReady.catch(e => { status.textContent = String(e); });
