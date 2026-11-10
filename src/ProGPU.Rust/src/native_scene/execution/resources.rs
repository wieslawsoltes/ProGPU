//! Whole source admission and immutable native family compilation. No device
//! creation, font callbacks, pixel rendering or cache publication occurs here.
use super::*;
use crate::native_scene::{
    NativeSceneContentHashes,
    engine_materials::{NativeBrushPage, NativeTextStylePage},
    engine_resources::{
        NativeExternalImageInfo, NativeGlyphPage, NativeGlyphRasterPage, NativeImagePage,
        NativePathPage, NativePathRasterPage, NativeRgbGlyphPage, NativeThreeDPage,
        NativeVectorPage,
    },
};

/// Values from the actual engine/device and retained atlas generations. Native
/// provider/flag selection remains explicit; no browser or CPU fallback is inferred.
#[derive(Clone, Copy, Debug)]
pub struct NativeSceneCompileOptions {
    pub format: NativeTargetFormat,
    pub engine_flags: u32,
    pub resource_scope: u64,
    pub borrows_shared_vector_pipeline: bool,
    pub maximum_buffer_bytes: u64,
    pub maximum_storage_bytes: u64,
    pub antialias_padding_pixels: f32,
}

/// A single atomic candidate retains all original family pages and their common
/// source generation. Family slices index pages; `draws().ordered_frames()` is
/// the authoritative interleaving, including clipped and no-ink occurrences.
pub struct NativeSceneResourcePlan {
    options: NativeSceneCompileOptions,
    hashes: NativeSceneContentHashes,
    layers: Arc<NativeLayerExecutionPlan>,
    draws: Arc<NativeDrawExecutionPlan>,
    budget: NativeSceneExecutionBudget,
    materials: Arc<NativeBrushPage>,
    styles: Arc<NativeTextStylePage>,
    vector: Option<Arc<NativeVectorPage>>,
    paths: Option<Arc<NativePathPage>>,
    glyphs: Option<Arc<NativeGlyphPage>>,
    images: Option<Arc<NativeImagePage>>,
    three_d: Option<Arc<NativeThreeDPage>>,
    rgb: Arc<[Option<Arc<NativeRgbGlyphPage>>]>,
}
impl NativeSceneResourcePlan {
    pub fn compile(
        scene: Arc<NativeScene>,
        frame: NativeSceneFrame,
        external: &[NativeExternalImageInfo],
        options: NativeSceneCompileOptions,
    ) -> Result<Self, Error> {
        validate_engine_flags(u64::from(options.engine_flags))?;
        if options.maximum_buffer_bytes == 0
            || options.maximum_storage_bytes == 0
            || options.maximum_storage_bytes > options.maximum_buffer_bytes
            || !options.antialias_padding_pixels.is_finite()
            || options.antialias_padding_pixels <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        let layers = Arc::new(NativeLayerExecutionPlan::compile(
            scene.clone(),
            frame,
            options.format,
            options.borrows_shared_vector_pipeline,
        )?);
        let hashes = scene.content_hashes(options.resource_scope);
        // Original material family identities are independent. Both retain the
        // same source owner; they must never be equated to one scene-wide hash.
        let materials = Arc::new(NativeBrushPage::compile(scene.clone(), hashes.brush)?);
        let styles = Arc::new(NativeTextStylePage::compile(
            scene.clone(),
            hashes.text_style,
        )?);
        let draws = Arc::new(NativeDrawExecutionPlan::compile(
            scene.clone(),
            frame,
            external,
            &materials,
            &styles,
            options.engine_flags,
            options.maximum_buffer_bytes,
            options.antialias_padding_pixels,
        )?);
        if draws.metrics().rgb_draw_count != 0 && !options.format.is_linear_byte_color() {
            return Err(Error::UnsupportedExecution);
        }
        let budget = layers.finish_budget(draws.budget_input())?;
        draws.validate_page_limits()?;
        // Original whole-stream gates precede the first potentially large
        // geometry output. Failure drops the complete private candidate.
        let vector = if draws.vector_frames().is_empty() {
            None
        } else {
            Some(Arc::new(NativeVectorPage::compile(
                scene.clone(),
                draws.vector_frames(),
                &materials,
                options.antialias_padding_pixels,
            )?))
        };
        let paths = if draws.path_frames().is_empty() {
            None
        } else {
            Some(Arc::new(NativePathPage::compile(
                scene.clone(),
                draws.path_frames(),
                &materials,
            )?))
        };
        let glyphs = if draws.glyph_frames().is_empty() {
            None
        } else {
            Some(Arc::new(NativeGlyphPage::compile(
                scene.clone(),
                draws.glyph_frames(),
                &materials,
                &styles,
                external,
                options.engine_flags,
            )?))
        };
        let image_bytes = draws
            .costs()
            .iter()
            .filter(|c| c.family == NativeDrawFamily::Image)
            .try_fold(0u64, |bytes, cost| {
                bytes.checked_add(cost.vertex_bytes).ok_or(Error::Capacity)
            })?;
        let images = if draws.image_frames().is_empty() {
            None
        } else {
            Some(Arc::new(NativeImagePage::compile_frames(
                scene.clone(),
                draws.image_frames(),
                external,
                options.engine_flags & ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING != 0,
                image_bytes,
            )?))
        };
        let three_d = if draws.three_d_frames().is_empty() {
            None
        } else {
            Some(Arc::new(NativeThreeDPage::compile_frames(
                scene,
                frame,
                draws.three_d_frames(),
                external,
                options.maximum_storage_bytes,
            )?))
        };
        let mut rgb = Vec::new();
        rgb.try_reserve_exact(draws.rgb_frames().len())
            .map_err(|_| Error::Capacity)?;
        for current in draws.rgb_frames() {
            let scissor = resolve_target_scissor(
                &current.source_state(),
                current.target(),
                [frame.width, frame.height],
                current.presentation(),
            );
            // RGB admission requires a materialized opaque target, so root-only
            // partial damage never changes its original command-local scissor.
            // The original ordered replay closes the preceding bundle, then
            // skips packet allocation for an empty clip. Keep its source slot.
            rgb.push(if scissor.drawable {
                Some(Arc::new(NativeRgbGlyphPage::compile(
                    current.clone(),
                    scissor,
                    options.maximum_buffer_bytes,
                    options.engine_flags,
                )?))
            } else {
                None
            });
        }
        Ok(Self {
            options,
            hashes,
            layers,
            draws,
            budget,
            materials,
            styles,
            vector,
            paths,
            glyphs,
            images,
            three_d,
            rgb: rgb.into(),
        })
    }
    pub fn scene(&self) -> &Arc<NativeScene> {
        self.layers.scene()
    }
    pub fn frame(&self) -> NativeSceneFrame {
        self.layers.frame()
    }
    pub fn options(&self) -> NativeSceneCompileOptions {
        self.options
    }
    pub fn content_hashes(&self) -> NativeSceneContentHashes {
        self.hashes
    }
    pub fn layers(&self) -> &Arc<NativeLayerExecutionPlan> {
        &self.layers
    }
    pub fn draws(&self) -> &Arc<NativeDrawExecutionPlan> {
        &self.draws
    }
    pub fn budget(&self) -> NativeSceneExecutionBudget {
        self.budget
    }
    pub fn materials(&self) -> &Arc<NativeBrushPage> {
        &self.materials
    }
    pub fn text_styles(&self) -> &Arc<NativeTextStylePage> {
        &self.styles
    }
    pub fn vector(&self) -> Option<&Arc<NativeVectorPage>> {
        self.vector.as_ref()
    }
    pub fn paths(&self) -> Option<&Arc<NativePathPage>> {
        self.paths.as_ref()
    }
    pub fn glyphs(&self) -> Option<&Arc<NativeGlyphPage>> {
        self.glyphs.as_ref()
    }
    pub fn images(&self) -> Option<&Arc<NativeImagePage>> {
        self.images.as_ref()
    }
    pub fn three_d(&self) -> Option<&Arc<NativeThreeDPage>> {
        self.three_d.as_ref()
    }
    pub fn rgb(&self) -> &[Option<Arc<NativeRgbGlyphPage>>] {
        &self.rgb
    }

    /// Prepare the original coverage records only after complete scene admission.
    /// Actual shader encoding, submission order and residency are independent.
    pub fn raster_pages(
        self: &Arc<Self>,
        path_atlas_size: u32,
        glyph_atlas_size: u32,
        staged_signed_winding: bool,
    ) -> Result<NativeSceneRasterPages, Error> {
        let paths = self
            .paths
            .as_ref()
            .map(|page| {
                NativePathRasterPage::compile(page.clone(), path_atlas_size, staged_signed_winding)
                    .map(Arc::new)
            })
            .transpose()?;
        // Source alpha has already been captured independently in the flattened
        // glyph/style/paint pages. The native family frame applies no extra alpha.
        let glyphs = self
            .glyphs
            .as_ref()
            .map(|page| {
                NativeGlyphRasterPage::compile(page.clone(), glyph_atlas_size, 1.0).map(Arc::new)
            })
            .transpose()?;
        Ok(NativeSceneRasterPages {
            source: self.clone(),
            paths,
            glyphs,
        })
    }
}
pub struct NativeSceneRasterPages {
    source: Arc<NativeSceneResourcePlan>,
    paths: Option<Arc<NativePathRasterPage>>,
    glyphs: Option<Arc<NativeGlyphRasterPage>>,
}
impl NativeSceneRasterPages {
    pub fn source(&self) -> &Arc<NativeSceneResourcePlan> {
        &self.source
    }
    pub fn paths(&self) -> Option<&Arc<NativePathRasterPage>> {
        self.paths.as_ref()
    }
    pub fn glyphs(&self) -> Option<&Arc<NativeGlyphRasterPage>> {
        self.glyphs.as_ref()
    }
}
