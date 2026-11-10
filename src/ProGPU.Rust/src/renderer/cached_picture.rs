//! Actual target cached-picture realization and generation-owned replay.
use super::*;
use crate::{
    composition::{
        SourceRenderFrame,
        cached_picture::{CachedPicture, CachedPictureGeneration},
    },
    image::{GpuImage, ImageDraw},
};
use std::collections::HashSet;
#[derive(Default)]
pub(super) struct CachedPictureState {
    pub(super) targets: Vec<Entry>,
    preparing: HashSet<usize>,
}
pub(super) struct Entry {
    raster: CachedPictureRaster,
    last_used: u64,
}
impl CachedPictureState {
    pub(super) fn sweep(&mut self, completed: u64) {
        self.targets
            .retain(|e| e.last_used == completed && e.raster.0.generation.is_current());
    }
}
struct Raster {
    generation: CachedPictureGeneration,
    capture: CapturedSourceScene,
    owner: Arc<()>,
    device: wgpu::Device,
}
/// Independently owned ordinary cached-picture result. Source callbacks and live
/// mutable lookup entries are never consulted while replaying this capture.
#[derive(Clone)]
pub struct CachedPictureRaster(Arc<Raster>);
impl CachedPictureRaster {
    /// Exact original recording/policy generation retained by this capture.
    pub fn generation(&self) -> &CachedPictureGeneration {
        &self.0.generation
    }
    /// Actual completed source texture and original preparation/replay metrics.
    pub fn capture(&self) -> &CapturedSourceScene {
        &self.0.capture
    }
    /// Actual premultiplied immutable texture; source retirement cannot replace it.
    pub fn image(&self) -> &GpuImage {
        self.0.capture.image()
    }
    /// Create a final image consumer in original source coordinates. Transforms,
    /// opacity, clips and destination blending remain outside this capture.
    pub fn image_draw(&self) -> ImageDraw {
        ImageDraw::new(self.image().clone(), self.generation().snapshot().bounds)
    }
}
/// Actual cached-source work for the prepared scene, separate from consumer draws.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CachedPictureMetrics {
    /// Original ordered cached-picture occurrences, including empty sources.
    pub commands: u32,
    /// Exact current generations reused without capture.
    pub hits: u32,
    /// New nonempty actual source captures.
    pub captures: u32,
    /// New capture allocation bytes, excluding source intermediates.
    pub capture_bytes: u64,
    /// Source draws actually submitted during these captures.
    pub source_draws: u64,
}
impl CachedPictureMetrics {
    pub(super) fn merge(&mut self, other: Self) -> Result<(), Error> {
        let mut candidate = *self;
        macro_rules! add { ($($f:ident),*) => { $(candidate.$f=candidate.$f.checked_add(other.$f).ok_or(Error::Capacity)?;)* }; }
        add!(commands, hits, captures, capture_bytes, source_draws);
        *self = candidate;
        Ok(())
    }
}
#[derive(Default)]
pub(super) struct PreparedCachedPictures {
    rasters: Vec<CachedPictureRaster>,
    metrics: CachedPictureMetrics,
}
impl PreparedScene {
    /// Actual direct cached source preparation. Replay reuses retained images.
    pub fn cached_picture_metrics(&self) -> CachedPictureMetrics {
        self.cached_pictures.metrics
    }
}
struct Preparing<'a> {
    renderer: &'a mut Renderer,
    identity: usize,
    checkpoint: usize,
    owner: Arc<()>,
    complete: bool,
}
impl Drop for Preparing<'_> {
    fn drop(&mut self) {
        if !Arc::ptr_eq(&self.owner, &self.renderer.owner) {
            return;
        }
        self.renderer
            .cached_pictures
            .preparing
            .remove(&self.identity);
        if !self.complete {
            self.renderer
                .cached_pictures
                .targets
                .truncate(self.checkpoint);
        }
    }
}
impl Renderer {
    pub(super) fn preflight_cached_pictures(
        &self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<(u64, u32), Error> {
        let mut bytes = 0u64;
        let mut count = 0u32;
        for draw in scene.cached_pictures() {
            draw.validate()?;
            count = count.checked_add(1).ok_or(Error::Capacity)?;
            if draw.picture.is_closed() {
                continue;
            }
            let generation = draw.picture.freeze()?;
            generation
                .snapshot()
                .scene
                .preflight_source_capture_devices(&self.device)?;
            if let Some(source) =
                SourceRenderFrame::cached_picture(generation.snapshot(), frame.dpi_scale)?
            {
                self.source_target(source)?;
                let [w, h] = source.size();
                bytes = bytes
                    .checked_add(u64::from(w) * u64::from(h) * 4)
                    .ok_or(Error::Capacity)?;
            }
        }
        if bytes > 512 * 1024 * 1024 || count > 16384 {
            return Err(Error::Capacity);
        }
        Ok((bytes, count))
    }
    /// Explicit ordinary picture realization using actual parent semantic DPI.
    /// Its logical-extent frame remains distinct from raw shader-cache sampling.
    pub async fn capture_cached_picture(
        &mut self,
        picture: &CachedPicture,
        dpi: f32,
    ) -> Result<Option<CachedPictureRaster>, Error> {
        let guard = owned_shader::OwnedShaderFrame::begin(self)?;
        let result = Box::pin(guard.renderer.realize_cached_picture(picture, dpi))
            .await?
            .0;
        let epoch = guard.renderer.owned_shaders.frame_number();
        guard.commit()?;
        if result.is_some() {
            self.owned_shader_completed
                .store(epoch, std::sync::atomic::Ordering::Relaxed);
        }
        Ok(result)
    }
    async fn realize_cached_picture(
        &mut self,
        picture: &CachedPicture,
        dpi: f32,
    ) -> Result<(Option<CachedPictureRaster>, bool), Error> {
        if picture.is_closed() {
            return Ok((None, false));
        }
        // Source refresh precedes sizing and empty/zero-scale decisions.
        let generation = picture.freeze()?;
        generation
            .snapshot()
            .scene
            .preflight_source_capture_devices(&self.device)?;
        let Some(frame) = SourceRenderFrame::cached_picture(generation.snapshot(), dpi)? else {
            return Ok((None, false));
        };
        let target = self.source_target(frame)?;
        target.uniforms(self.device.limits().max_texture_dimension_2d)?;
        let epoch = self.owned_shaders.frame_number();
        for entry in &mut self.cached_pictures.targets {
            if entry.raster.0.generation.same_generation(&generation)
                && entry.raster.capture().frame().same_frame(frame)
                && Arc::ptr_eq(&entry.raster.0.owner, &self.owner)
                && entry.raster.0.device == self.device
            {
                entry.last_used = epoch;
                return Ok((Some(entry.raster.clone()), true));
            }
        }
        let identity = picture.identity();
        if self.cached_pictures.preparing.len() >= 256
            || self.cached_pictures.targets.len() >= 16384
        {
            return Err(Error::Capacity);
        }
        self.cached_pictures
            .preparing
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        if !self.cached_pictures.preparing.insert(identity) {
            return Err(Error::ResourceDomain);
        }
        let mut guard = Preparing {
            checkpoint: self.cached_pictures.targets.len(),
            owner: self.owner.clone(),
            renderer: self,
            identity,
            complete: false,
        };
        let device = guard.renderer.device.clone();
        let capture = Box::pin(
            guard
                .renderer
                .capture_source_scene(generation.snapshot().scene.clone(), frame),
        )
        .await?;
        if !generation.is_current()
            || guard.renderer.device != device
            || !Arc::ptr_eq(&guard.owner, &guard.renderer.owner)
        {
            return Err(Error::ResourceDomain);
        }
        let raster = CachedPictureRaster(Arc::new(Raster {
            generation,
            capture,
            owner: guard.owner.clone(),
            device,
        }));
        guard
            .renderer
            .cached_pictures
            .targets
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        guard.renderer.cached_pictures.targets.push(Entry {
            raster: raster.clone(),
            last_used: epoch,
        });
        guard.complete = true;
        Ok((Some(raster), false))
    }
    pub(super) async fn prepare_cached_picture_scene(
        &mut self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<(Scene, PreparedCachedPictures), Error> {
        self.preflight_cached_pictures(scene, frame)?;
        let count = scene.cached_pictures().count();
        if count > 16384 {
            return Err(Error::Capacity);
        }
        let mut prepared = PreparedCachedPictures::default();
        let mut outputs = Vec::new();
        outputs
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        prepared
            .rasters
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        for draw in scene.cached_pictures() {
            draw.validate()?;
            let (raster, hit) =
                Box::pin(self.realize_cached_picture(&draw.picture, frame.dpi_scale)).await?;
            let mut metrics = CachedPictureMetrics {
                commands: 1,
                ..Default::default()
            };
            let output = if let Some(raster) = raster {
                let mut image = raster.image_draw();
                image.transform = draw.affine_transform()?;
                image.opacity = draw.opacity;
                image.validate()?;
                if hit {
                    metrics.hits = 1;
                } else {
                    metrics.captures = 1;
                    metrics.capture_bytes = raster.capture().capture().capture_bytes();
                    metrics.source_draws = u64::from(
                        raster
                            .capture()
                            .capture()
                            .source_preparation_metrics()
                            .draws,
                    );
                }
                prepared.rasters.push(raster);
                Some(image)
            } else {
                None
            };
            prepared.metrics.merge(metrics)?;
            if prepared.metrics.capture_bytes > 512 * 1024 * 1024 {
                return Err(Error::Capacity);
            }
            outputs.push(output);
        }
        if prepared
            .rasters
            .iter()
            .any(|r| !r.generation().is_current())
        {
            return Err(Error::ResourceDomain);
        }
        Ok((scene.replace_cached_pictures(outputs)?, prepared))
    }
}
