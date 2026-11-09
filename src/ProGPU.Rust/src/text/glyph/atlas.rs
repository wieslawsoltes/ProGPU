//! Original dynamic coverage atlas, source-instance keys and retained residency.
use super::*;
use crate::Error;
use std::collections::{HashMap, HashSet};
mod cache;
mod font;
mod packing;
use cache::{Cache, CachedGlyph, Key};
use packing::{Region, Shelf};

/// Original glyph dimensions, placement and normalized texture coordinates.
/// A zero-sized entry can mean no ink or a capacity fallback; it does not certify
/// ink availability. Consumers retain a residency set for actual atlas draws.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct GlyphInfo {
    /// Actual physical atlas texel origin.
    pub origin: [u32; 2],
    /// Actual physical coverage extent, independent of allocation-class padding.
    pub size: [u32; 2],
    /// Original screen-space offset from the glyph baseline.
    pub bearing: [f32; 2],
    /// Original optional bitmap render extent; zero for design-font coverage.
    pub render_size: [f32; 2],
    /// Original selected-font horizontal advance.
    pub advance: f32,
    /// Original optional physical bitmap/hinted scale; zero for design fonts.
    pub raster_scale: f32,
    /// True only for actual decoded color-bitmap coverage.
    pub is_color_bitmap: bool,
    /// Actual atlas-normalized near corner.
    pub uv_min: [f32; 2],
    /// Actual atlas-normalized far corner.
    pub uv_max: [f32; 2],
    region: Option<Region>,
}
impl GlyphInfo {
    /// Actual reserved class extent, or zero when this entry has no residency.
    pub fn allocation_size(&self) -> [u32; 2] {
        self.region.map_or([0; 2], |r| r.size)
    }
    fn update_uv(&mut self, atlas_size: u32) {
        let texel = 1.0 / atlas_size as f32;
        self.uv_min = self.origin.map(|v| v as f32 * texel);
        self.uv_max =
            std::array::from_fn(|axis| (self.origin[axis] + self.size[axis]) as f32 * texel);
    }
}
/// Original coverage budgets and explicit GPU raster execution configuration.
#[derive(Clone, Copy, Debug)]
pub struct GlyphAtlasOptions {
    /// Initial square coverage size, including the two-texel perimeter (260).
    pub initial_size: u32,
    /// Maximum square coverage size (2048).
    pub maximum_size: u32,
    /// Initial square RGBA color atlas size (64).
    pub initial_color_size: u32,
    /// Maximum square RGBA color atlas size (512).
    pub maximum_color_size: u32,
    /// Bounded original uniform/coverage rings and selected execution preference.
    pub rasterizer: GlyphRasterizerOptions,
}
impl Default for GlyphAtlasOptions {
    fn default() -> Self {
        Self {
            initial_size: 260,
            maximum_size: 2048,
            initial_color_size: 64,
            maximum_color_size: 512,
            rasterizer: GlyphRasterizerOptions::default(),
        }
    }
}
/// Original atlas revision/capacity counters, independent of GPU upload metrics.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GlyphAtlasStatistics {
    /// Changes when an entry is evicted, invalidated after failure, or cleared.
    pub generation: u64,
    /// Changes when growth replaces the actual texture and normalized UV scale.
    pub texture_revision: u64,
    /// Original sticky allocation-failure flag, reset by growth/reuse/clear.
    pub capacity_exceeded: bool,
    /// Actual LRU region evictions.
    pub eviction_count: u64,
    /// Explicit atlas clears.
    pub clear_count: u64,
    /// New GPU outline slots appended during the last completed outer batch.
    pub last_batch_new_glyph_count: u64,
}
/// Opaque actual glyph keys used by retained vertices. Keys retain their source
/// font identities; a foreign atlas cannot accept this set as its own residency.
#[derive(Clone, Debug)]
pub struct GlyphAtlasResidency {
    owner: Arc<()>,
    keys: Vec<Key>,
}
impl GlyphAtlasResidency {
    /// Number of distinct referenced entries.
    pub fn len(&self) -> usize {
        self.keys.len()
    }
    /// Whether no atlas entries are referenced.
    pub fn is_empty(&self) -> bool {
        self.keys.is_empty()
    }
    /// Actual retained key-array allocation, excluding shared font owners.
    pub fn byte_size(&self) -> usize {
        self.keys.capacity() * size_of::<Key>()
    }
}
/// Dynamic original grayscale/color glyph atlas with bounded GPU staging. Call
/// `begin_batch`, acquire glyphs, then `flush_pending` before submitting consumers
/// that sample its texture; `end_batch` completes the outermost batch.
///
/// Growth refreshes cached UVs and increments `texture_revision`; retained vertex
/// consumers must rebuild their UVs when it changes. A residency set protects
/// actual cached entries from current-frame LRU reuse, independently of growth.
///
/// SVG geometry, final color-layer drawing and native captured hinted-source
/// ownership remain separate integration work. No CPU coverage, surrogate
/// shaping or source raster-policy inference is introduced by bitmap decoding.
pub struct GlyphAtlas {
    owner: Arc<()>,
    options: GlyphAtlasOptions,
    rasterizer: GlyphRasterizer,
    coverage: GlyphCoverageTexture,
    color: GlyphColorTexture,
    cache: Cache,
    shelves: Vec<Shelf>,
    next_shelf_y: u32,
    color_shelves: Vec<Shelf>,
    next_color_shelf_y: u32,
    frame: u64,
    batch_depth: u32,
    new_glyphs: u64,
    usages: Vec<Key>,
    residency_scratch: HashSet<Key>,
    pending: Vec<GlyphRasterRequest>,
    pending_keys: Vec<Key>,
    pending_bytes: u64,
    failed: Vec<Region>,
    statistics: GlyphAtlasStatistics,
}
impl GlyphAtlas {
    /// Create an atlas on the exact supplied device/queue. The queue must belong
    /// to that device. All dimensions are validated before resource creation.
    pub async fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        options: GlyphAtlasOptions,
    ) -> Result<Self, Error> {
        if options.initial_size <= 4
            || options.initial_size > options.maximum_size
            || options.maximum_size > device.limits().max_texture_dimension_2d
            || options.initial_color_size <= 4
            || options.initial_color_size > options.maximum_color_size
            || options.maximum_color_size > device.limits().max_texture_dimension_2d
        {
            return Err(Error::InvalidInput);
        }
        let rasterizer = GlyphRasterizer::new(device, queue, options.rasterizer).await?;
        let coverage = rasterizer
            .create_coverage_texture([options.initial_size; 2])
            .await?;
        let color = rasterizer
            .create_color_texture([options.initial_color_size; 2])
            .await?;
        Ok(Self {
            owner: Arc::new(()),
            options,
            rasterizer,
            coverage,
            color,
            cache: Cache::default(),
            shelves: Vec::new(),
            next_shelf_y: 2,
            color_shelves: Vec::new(),
            next_color_shelf_y: 2,
            frame: 0,
            batch_depth: 0,
            new_glyphs: 0,
            usages: Vec::new(),
            residency_scratch: HashSet::new(),
            pending: Vec::new(),
            pending_keys: Vec::new(),
            pending_bytes: 0,
            failed: Vec::new(),
            statistics: GlyphAtlasStatistics::default(),
        })
    }
    /// Actual current R8 texture generation. Old clones survive atlas growth.
    pub fn coverage(&self) -> &GlyphCoverageTexture {
        &self.coverage
    }
    /// Actual separate RGBA8 texture generation, with retained growth lifetime.
    pub fn color(&self) -> &GlyphColorTexture {
        &self.color
    }
    /// Actual current square RGBA dimension.
    pub fn color_size(&self) -> u32 {
        self.color.size()[0]
    }
    /// Configured maximum square RGBA dimension.
    pub fn maximum_color_size(&self) -> u32 {
        self.options.maximum_color_size
    }
    /// Actual current square coverage dimension.
    pub fn size(&self) -> u32 {
        self.coverage.size()[0]
    }
    /// Configured maximum square coverage dimension.
    pub fn maximum_size(&self) -> u32 {
        self.options.maximum_size
    }
    /// Actual R8 plus RGBA texture bytes; ring/outline allocations are separate.
    pub fn persistent_texture_bytes(&self) -> u64 {
        u64::from(self.size()).pow(2) + u64::from(self.color_size()).pow(2) * 4
    }
    /// Current original atlas state/counters.
    pub fn statistics(&self) -> GlyphAtlasStatistics {
        self.statistics
    }
    /// Actual selected GPU execution route.
    pub fn execution_path(&self) -> GlyphExecutionPath {
        self.rasterizer.execution_path()
    }
    /// Actual cumulative GPU writes/submissions, not inferred frame performance.
    pub fn gpu_metrics(&self) -> GlyphGpuMetrics {
        self.rasterizer.metrics()
    }
    /// Actual retained outline record/segment buffer bytes.
    pub fn allocated_outline_bytes(&self) -> u64 {
        self.rasterizer.allocated_outline_bytes()
    }
    /// Number of cached entries, including empty/capacity sentinels.
    pub fn cached_glyph_count(&self) -> usize {
        self.cache.len()
    }
    /// Current original frame number; only an outer `begin_batch` advances it.
    pub fn frame_number(&self) -> u64 {
        self.frame
    }
    /// Current retained compilation cursor.
    pub fn batch_usage_count(&self) -> usize {
        self.usages.len()
    }
    /// Whether the caller currently owns an outer or nested acquisition batch.
    pub fn is_batch_active(&self) -> bool {
        self.batch_depth != 0
    }
    /// Original proactive fullness signal, evaluated only at the maximum size.
    pub fn is_almost_full(&self) -> bool {
        (self.size() >= self.maximum_size() && self.next_shelf_y as f32 > self.size() as f32 * 0.85)
            || (self.color_size() >= self.maximum_color_size()
                && self.next_color_shelf_y as f32 > self.color_size() as f32 * 0.85)
    }
    /// Begin/nest a frame batch. Outline trimming occurs only at the outer
    /// boundary, before any current-frame raster request can retain a slot.
    pub async fn begin_batch(&mut self) -> Result<(), Error> {
        let depth = self.batch_depth.checked_add(1).ok_or(Error::Capacity)?;
        if self.batch_depth == 0 {
            self.rasterizer.trim_outline_cache().await?;
            self.frame = self.frame.wrapping_add(1);
            self.new_glyphs = 0;
            self.usages.clear();
        }
        self.batch_depth = depth;
        Ok(())
    }
    /// Complete one nesting level; the outermost end submits pending coverage.
    /// A cancelled/failed submission invalidates its cached entries immediately.
    pub async fn end_batch(&mut self) -> Result<(), Error> {
        if self.batch_depth == 0 {
            return Ok(());
        }
        self.batch_depth -= 1;
        if self.batch_depth == 0 {
            self.flush_batch().await?;
            self.statistics.last_batch_new_glyph_count = self.new_glyphs;
        }
        Ok(())
    }
    /// Submit pending coverage before consumers sample the atlas. The current
    /// frame/nesting remains active. Cancellation abandons all pending entries;
    /// already submitted wgpu resources retain their actual submission lifetime.
    pub async fn flush_pending(&mut self) -> Result<(), Error> {
        if self.pending.is_empty() {
            return Ok(());
        }
        self.flush_batch().await
    }
    async fn flush_batch(&mut self) -> Result<(), Error> {
        if self.pending.is_empty() {
            // Source FlushBatchEncoder also retires a batch with no ring work.
            // An oversized immediate draw can still have a batch failure key.
            self.rasterizer.flush_outline_uploads();
            self.pending_keys.clear();
            return Ok(());
        }
        let mut guard = FlushGuard {
            atlas: self,
            completed: false,
        };
        let atlas = &mut *guard.atlas;
        atlas
            .rasterizer
            .rasterize_into(&atlas.coverage, &atlas.pending)
            .await?;
        atlas.pending.clear();
        atlas.pending_keys.clear();
        atlas.pending_bytes = 0;
        guard.completed = true;
        Ok(())
    }
    /// Explicitly abandon unfinished work, returning reserved regions for reuse.
    /// This never submits, polls, destroys a live texture or calls a font source.
    pub fn abandon_pending(&mut self) {
        self.abandon(None);
    }
    /// Clear residency and shelves while clearing the same texture attachment.
    /// Coverage keys invalidate before the GPU operation; a failed clear never
    /// leaves old cache entries claiming valid pixels. Font GPU slots survive.
    pub async fn clear(&mut self) -> Result<(), Error> {
        self.pending.clear();
        self.pending_keys.clear();
        self.pending_bytes = 0;
        self.cache.clear();
        self.failed.clear();
        self.shelves.clear();
        self.next_shelf_y = 2;
        self.color_shelves.clear();
        self.next_color_shelf_y = 2;
        self.statistics.capacity_exceeded = false;
        self.statistics.clear_count = self.statistics.clear_count.wrapping_add(1);
        self.statistics.generation = self.statistics.generation.wrapping_add(1);
        self.rasterizer
            .clear_coverage_texture(&self.coverage)
            .await?;
        self.rasterizer.clear_color_texture(&self.color).await
    }
    /// Capture distinct actual keys since a compilation cursor, optionally
    /// reusing the caller's previous allocation. Invalid cursors mutate nothing.
    pub fn capture_batch_residency(
        &mut self,
        start: usize,
        reusable: Option<GlyphAtlasResidency>,
    ) -> Result<GlyphAtlasResidency, Error> {
        let uses = self.usages.get(start..).ok_or(Error::InvalidInput)?;
        self.residency_scratch.clear();
        let mut result = reusable.unwrap_or_else(|| GlyphAtlasResidency {
            owner: self.owner.clone(),
            keys: Vec::new(),
        });
        result.keys.clear();
        for key in uses {
            if !self.residency_scratch.contains(key) {
                self.residency_scratch
                    .try_reserve(1)
                    .map_err(|_| Error::Capacity)?;
                result.keys.try_reserve(1).map_err(|_| Error::Capacity)?;
                self.residency_scratch.insert(key.clone());
                result.keys.push(key.clone());
            }
        }
        result.owner = self.owner.clone();
        // Scratch is not a hidden source-owner lifetime beyond this capture.
        self.residency_scratch.clear();
        Ok(result)
    }
    /// Validate the complete set before marking any entry used this frame.
    /// False means missing/capacity/foreign residency and makes no cache change.
    pub fn try_mark_retained_replay(
        &mut self,
        residency: &GlyphAtlasResidency,
    ) -> Result<bool, Error> {
        if !self.contains_residency(residency) {
            return Ok(false);
        }
        self.usages
            .try_reserve(residency.keys.len())
            .map_err(|_| Error::Capacity)?;
        for key in &residency.keys {
            self.cache
                .get_mut(key)
                .expect("whole residency preflight")
                .frame = self.frame;
            self.usages.push(key.clone());
        }
        Ok(true)
    }
    /// Read-only exact owner/key admission. Consumers also compare the captured
    /// generation/revision so eviction followed by reacquisition cannot revive
    /// vertices carrying an older physical atlas location.
    pub fn contains_residency(&self, residency: &GlyphAtlasResidency) -> bool {
        Arc::ptr_eq(&self.owner, &residency.owner)
            && residency.keys.iter().all(|key| {
                self.cache
                    .get(key)
                    .is_some_and(|entry| !entry.capacity_fallback && entry.info.region.is_some())
            })
    }
    /// Internal fresh-tree proof: every exact owner/key remains at a location
    /// protected by this active original frame. Not a lease for old generations.
    pub(crate) fn residency_is_pinned(&self, residency: &GlyphAtlasResidency) -> bool {
        self.is_batch_active()
            && self.contains_residency(residency)
            && residency.keys.iter().all(|key| {
                self.cache
                    .get(key)
                    .is_some_and(|entry| entry.frame == self.frame)
            })
    }
    fn record_usage(&mut self, key: &Key, info: GlyphInfo) {
        if self.batch_depth != 0 && info.region.is_some() {
            self.usages.push(key.clone());
        }
    }
    fn reserve_acquisition(&mut self) -> Result<(), Error> {
        self.cache.reserve()?;
        self.usages.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.pending.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.pending_keys
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        // Every unwind path can return all outstanding tickets without allocating.
        self.failed
            .try_reserve(
                self.pending_keys
                    .len()
                    .checked_add(1)
                    .ok_or(Error::Capacity)?,
            )
            .map_err(|_| Error::Capacity)?;
        Ok(())
    }
    fn abandon(&mut self, extra: Option<(&Key, Region)>) {
        let mut removed = false;
        let mut returned_extra = false;
        while let Some(key) = self.pending_keys.pop() {
            if let Some(entry) = self.cache.remove(&key) {
                if let Some(region) = entry.info.region {
                    self.return_failed(region);
                }
                removed = true;
                returned_extra |= extra.is_some_and(|(other, _)| *other == key);
            }
        }
        if let Some((key, region)) = extra
            && !returned_extra
        {
            removed |= self.cache.remove(key).is_some();
            self.return_failed(region);
        }
        if removed {
            self.statistics.generation = self.statistics.generation.wrapping_add(1);
        }
        self.pending.clear();
        self.pending_bytes = 0;
        self.rasterizer.invalidate_font_slots();
    }
    async fn stage(&mut self, request: GlyphRasterRequest) -> Result<bool, Error> {
        self.rasterizer.validate_request(&self.coverage, &request)?;
        let bytes = u64::from(request.size[0]).div_ceil(256) * 256 * u64::from(request.size[1]);
        let compute = self.execution_path() == GlyphExecutionPath::NativeCompute;
        let oversized = compute && bytes > u64::from(self.options.rasterizer.coverage_ring_bytes);
        let next = self.pending_bytes.div_ceil(512) * 512 + bytes;
        if oversized
            || (self.pending.len() as u64 + 1) * 256
                > u64::from(self.options.rasterizer.uniform_ring_bytes)
            || (compute && next > u64::from(self.options.rasterizer.coverage_ring_bytes))
        {
            self.flush_batch().await?;
        }
        if oversized || self.batch_depth == 0 {
            self.rasterizer
                .rasterize_into(&self.coverage, std::slice::from_ref(&request))
                .await?;
            // Source CacheGlyph registers this entry against the resumed batch
            // even when its oversized coverage was submitted immediately.
            return Ok(self.batch_depth != 0);
        }
        self.pending_bytes = self.pending_bytes.div_ceil(512) * 512 + bytes;
        self.pending.push(request);
        Ok(true)
    }
}
struct FlushGuard<'a> {
    atlas: &'a mut GlyphAtlas,
    completed: bool,
}
impl Drop for FlushGuard<'_> {
    fn drop(&mut self) {
        if !self.completed {
            self.atlas.abandon(None);
        }
    }
}
struct Acquisition<'a> {
    atlas: &'a mut GlyphAtlas,
    key: Key,
    region: Region,
    committed: bool,
}
impl Drop for Acquisition<'_> {
    fn drop(&mut self) {
        if !self.committed {
            self.atlas.abandon(Some((&self.key, self.region)));
        }
    }
}
