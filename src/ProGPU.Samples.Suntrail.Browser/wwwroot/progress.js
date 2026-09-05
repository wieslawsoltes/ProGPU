const key = 'progpu.suntrail.progress.v1';
export function loadProgress() {
  try { return Math.max(0, Math.min(7, Number.parseInt(localStorage.getItem(key), 10) || 0)); }
  catch { return 0; }
}
export function saveProgress(level) {
  try { localStorage.setItem(key, String(Math.max(0, Math.min(7, level)))); }
  catch { /* Play remains available when browser storage is disabled. */ }
}

export function loadTouchOptions() {
  try {
    const value = Number.parseInt(localStorage.getItem('progpu.suntrail.touch.v1'), 10);
    return Number.isInteger(value) && value >= 0 && value <= 14 && (value & 3) !== 3 ? value : 12;
  } catch { return 12; }
}
export function saveTouchOptions(value) {
  try { localStorage.setItem('progpu.suntrail.touch.v1', String(value)); }
  catch { /* Controls stay available with blocked storage. */ }
}
