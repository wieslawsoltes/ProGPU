const key = 'progpu.suntrail.progress.v1';
export function loadProgress() {
  try { return Math.max(0, Math.min(7, Number.parseInt(localStorage.getItem(key), 10) || 0)); }
  catch { return 0; }
}
export function saveProgress(level) {
  try { localStorage.setItem(key, String(Math.max(0, Math.min(7, level)))); }
  catch { /* Play remains available when browser storage is disabled. */ }
}
