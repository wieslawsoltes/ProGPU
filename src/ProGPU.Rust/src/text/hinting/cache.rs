//! Original context-scoped bounded FIFO face/latest-batch cache.
use super::*;
use std::sync::Mutex;

/// Original saturating counters. Captures count attempted backend batches;
/// creations count successfully created faces, even if their first capture fails.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeHintedCacheStatistics {
    /// Successfully created original face owners.
    pub face_creations: u64,
    /// Actual attempted captures after owned-key allocation succeeds.
    pub glyph_captures: u64,
    /// Exact latest-batch generation hits, including original ID repetitions.
    pub batch_hits: u64,
    /// Actual occupied face/cache entries, at most 16.
    pub live_entries: u32,
}
type BackendFace<F> =
    <<F as NativeHintedBackendFactory>::Library as NativeHintedLibraryBackend>::Face;
struct Entry<B: NativeHintedFaceBackend> {
    font: NativeHintedFace<B>,
    batch: Arc<NativeHintedBatch>,
    glyph_ids: Vec<u32>,
}
struct State<F: NativeHintedBackendFactory> {
    // Face owners retire before the factory's loaded-dependency owner.
    entries: [Option<Entry<BackendFace<F>>>; 16],
    cursor: usize,
    statistics: NativeHintedCacheStatistics,
    factory: F,
}
/// Complete original 16-entry cache, with one latest whole batch per exact
/// source/request. FIFO replacement never invalidates returned Arc generations.
/// The mutex spans original lookup/create/capture/publication, preserving exclusive
/// mutable driver state; callbacks into this same cache are not reentrant.
pub struct NativeHintedFontCache<F: NativeHintedBackendFactory> {
    state: Mutex<State<F>>,
}
impl<F: NativeHintedBackendFactory> NativeHintedFontCache<F> {
    /// Original fixed number of cache entries.
    pub const CAPACITY: usize = 16;
    /// Own the actual dependency factory; creates no face or implicit provider.
    pub fn new(factory: F) -> Self {
        Self {
            state: Mutex::new(State {
                entries: std::array::from_fn(|_| None),
                cursor: 0,
                statistics: NativeHintedCacheStatistics::default(),
                factory,
            }),
        }
    }
    /// Capture through an exclusive original cache lease. A failure preserves the
    /// old key/batch/slot and all previously returned generations. Exact hits reuse
    /// the original Arc rather than rebuilding points or shape metadata.
    pub fn capture_shared(
        &self,
        font: &SfntFace,
        request: &NativeHintedRequest,
        glyph_ids: &[u32],
    ) -> Result<Arc<NativeHintedBatch>, NativeHintedCaptureError> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| NativeHintedCaptureError::HintingFailed)?;
        let State {
            entries,
            cursor,
            statistics,
            factory,
        } = &mut *state;
        for cached in entries.iter_mut().flatten() {
            if !same_source(cached.batch.font(), font) || cached.batch.request() != request {
                continue;
            }
            if cached.glyph_ids == glyph_ids {
                statistics.batch_hits = statistics.batch_hits.saturating_add(1);
                return Ok(Arc::clone(&cached.batch));
            }
            let owned_ids = copy(glyph_ids)?;
            statistics.glyph_captures = statistics.glyph_captures.saturating_add(1);
            let candidate = cached.font.capture(glyph_ids)?;
            cached.glyph_ids = owned_ids;
            cached.batch = candidate;
            return Ok(Arc::clone(&cached.batch));
        }
        let mut selected = create_native_hinted_face(factory, font.clone(), request)?;
        statistics.face_creations = statistics.face_creations.saturating_add(1);
        let owned_ids = copy(glyph_ids)?;
        statistics.glyph_captures = statistics.glyph_captures.saturating_add(1);
        let batch = selected.capture(glyph_ids)?;
        let candidate = Entry {
            font: selected,
            batch: Arc::clone(&batch),
            glyph_ids: owned_ids,
        };
        if entries[*cursor].is_none() {
            statistics.live_entries += 1;
        }
        entries[*cursor] = Some(candidate);
        *cursor = (*cursor + 1) % Self::CAPACITY;
        Ok(batch)
    }
    /// Coherent original counter snapshot under the same context cache lock.
    pub fn statistics(&self) -> Result<NativeHintedCacheStatistics, NativeHintedCaptureError> {
        self.state
            .lock()
            .map(|state| state.statistics)
            .map_err(|_| NativeHintedCaptureError::HintingFailed)
    }
}
impl<F: NativeHintedBackendFactory> NativeHintedProvider for NativeHintedFontCache<F> {
    fn capture(
        &mut self,
        font: &SfntFace,
        request: &NativeHintedRequest,
        glyph_ids: &[u32],
    ) -> Result<Arc<NativeHintedBatch>, NativeHintedCaptureError> {
        self.capture_shared(font, request, glyph_ids)
    }
}
