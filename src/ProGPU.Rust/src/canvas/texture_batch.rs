//! Original lattice/atlas calls record one complete immutable texture batch.
use super::*;
use crate::image::{
    Bitmap, ImageAtlasTransform, ImageBatchSource, ImageColorBlend, ImageLattice, ImagePatchBatch,
    RasterError, RasterFilterMode, RasterSampling,
};
impl Canvas {
    /// Original nine-patch draw. Center and destination are left/top/right/bottom;
    /// undersized destinations collapse the middle independently on each axis.
    pub fn draw_image_nine_patch(
        &mut self,
        source: impl Into<ImageBatchSource>,
        center: [i32; 4],
        destination: [f32; 4],
        filter: RasterFilterMode,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let source = source.into();
        let Some(layout) = ImagePatchBatch::nine_patch(source.size(), center, destination)? else {
            return Ok(());
        };
        self.draw_image_patch_batch(
            source,
            layout,
            RasterSampling {
                filter,
                ..RasterSampling::NEAREST
            },
            paint,
        )
    }
    /// Original nearest default independent of paint's legacy filter quality.
    pub fn draw_image_nine_patch_nearest(
        &mut self,
        source: impl Into<ImageBatchSource>,
        center: [i32; 4],
        destination: [f32; 4],
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_image_nine_patch(
            source,
            center,
            destination,
            RasterFilterMode::Nearest,
            paint,
        )
    }
    /// Freeze actual bitmap bytes and metadata before recording one nine-patch draw.
    pub fn draw_bitmap_nine_patch(
        &mut self,
        bitmap: &Bitmap,
        center: [i32; 4],
        destination: [f32; 4],
        filter: RasterFilterMode,
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        Ok(self.draw_image_nine_patch(bitmap.snapshot()?, center, destination, filter, paint)?)
    }
    /// Original bitmap convenience overload defaults to nearest sampling.
    pub fn draw_bitmap_nine_patch_nearest(
        &mut self,
        bitmap: &Bitmap,
        center: [i32; 4],
        destination: [f32; 4],
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        self.draw_bitmap_nine_patch(
            bitmap,
            center,
            destination,
            RasterFilterMode::Nearest,
            paint,
        )
    }
    /// Original complete lattice, including source bounds, omitted cells and
    /// fixed colors filtered independently before the source texture is filtered.
    pub fn draw_image_lattice(
        &mut self,
        source: impl Into<ImageBatchSource>,
        lattice: &ImageLattice,
        destination: [f32; 4],
        filter: RasterFilterMode,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let source = source.into();
        let Some(layout) = lattice.layout(
            source.size(),
            destination,
            paint.and_then(|p| p.color_filter.as_ref()),
        )?
        else {
            return Ok(());
        };
        self.draw_image_patch_batch(
            source,
            layout,
            RasterSampling {
                filter,
                ..RasterSampling::NEAREST
            },
            paint,
        )
    }
    /// Original nearest default independent of paint's filter-quality property.
    pub fn draw_image_lattice_nearest(
        &mut self,
        source: impl Into<ImageBatchSource>,
        lattice: &ImageLattice,
        destination: [f32; 4],
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_image_lattice(
            source,
            lattice,
            destination,
            RasterFilterMode::Nearest,
            paint,
        )
    }
    /// Original division-array overload freezes arrays before source lowering.
    pub fn draw_image_lattice_divisions(
        &mut self,
        source: impl Into<ImageBatchSource>,
        x: impl Into<Arc<[i32]>>,
        y: impl Into<Arc<[i32]>>,
        destination: [f32; 4],
        filter: RasterFilterMode,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_image_lattice(source, &ImageLattice::new(x, y), destination, filter, paint)
    }
    /// Original bitmap lattice snapshots source pixels without a GPU readback.
    pub fn draw_bitmap_lattice(
        &mut self,
        bitmap: &Bitmap,
        lattice: &ImageLattice,
        destination: [f32; 4],
        filter: RasterFilterMode,
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        Ok(self.draw_image_lattice(bitmap.snapshot()?, lattice, destination, filter, paint)?)
    }
    /// Original bitmap convenience overload defaults to nearest sampling.
    pub fn draw_bitmap_lattice_nearest(
        &mut self,
        bitmap: &Bitmap,
        lattice: &ImageLattice,
        destination: [f32; 4],
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        self.draw_bitmap_lattice(
            bitmap,
            lattice,
            destination,
            RasterFilterMode::Nearest,
            paint,
        )
    }
    /// Original bitmap division-array adapter freezes the same complete source.
    pub fn draw_bitmap_lattice_divisions(
        &mut self,
        bitmap: &Bitmap,
        x: impl Into<Arc<[i32]>>,
        y: impl Into<Arc<[i32]>>,
        destination: [f32; 4],
        filter: RasterFilterMode,
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        self.draw_bitmap_lattice(bitmap, &ImageLattice::new(x, y), destination, filter, paint)
    }
    /// Original atlas: all arrays retain matching original slots, actual sprite
    /// transforms, straight per-sprite colors and independent color blend mode.
    /// Cull is command metadata and never a substitute per-sprite clip.
    #[allow(clippy::too_many_arguments)]
    pub fn draw_atlas(
        &mut self,
        source: impl Into<ImageBatchSource>,
        sprites: &[[f32; 4]],
        transforms: &[ImageAtlasTransform],
        colors: Option<&[[u8; 4]]>,
        blend: ImageColorBlend,
        sampling: RasterSampling,
        cull: Option<[f32; 4]>,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let layout = ImagePatchBatch::atlas(
            sprites,
            transforms,
            colors,
            blend,
            paint.and_then(|p| p.color_filter.as_ref()),
            cull,
        )?;
        self.draw_image_patch_batch(source, layout, sampling, paint)
    }
    /// Original color-free overload selects Dst and explicit source sampling.
    pub fn draw_atlas_sprites(
        &mut self,
        source: impl Into<ImageBatchSource>,
        sprites: &[[f32; 4]],
        transforms: &[ImageAtlasTransform],
        sampling: RasterSampling,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_atlas(
            source,
            sprites,
            transforms,
            None,
            ImageColorBlend::Dst,
            sampling,
            None,
            paint,
        )
    }
    /// Original color-free legacy overload selects paint filter quality once.
    pub fn draw_atlas_sprites_legacy(
        &mut self,
        source: impl Into<ImageBatchSource>,
        sprites: &[[f32; 4]],
        transforms: &[ImageAtlasTransform],
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_atlas_legacy(
            source,
            sprites,
            transforms,
            None,
            ImageColorBlend::Dst,
            None,
            paint,
        )
    }
    /// Original deprecated quality overload, retaining unknown-quality nearest.
    #[allow(clippy::too_many_arguments)]
    pub fn draw_atlas_legacy(
        &mut self,
        source: impl Into<ImageBatchSource>,
        sprites: &[[f32; 4]],
        transforms: &[ImageAtlasTransform],
        colors: Option<&[[u8; 4]]>,
        blend: ImageColorBlend,
        cull: Option<[f32; 4]>,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_atlas(
            source,
            sprites,
            transforms,
            colors,
            blend,
            RasterSampling::from_quality(paint.map_or(0, |p| p.filter_quality)),
            cull,
            paint,
        )
    }
    /// Record one complete original patch generation. Layout must already include
    /// source fixed/atlas color filtering; paint filters the full image at prepare.
    pub fn draw_image_patch_batch(
        &mut self,
        source: impl Into<ImageBatchSource>,
        layout: ImagePatchBatch,
        sampling: RasterSampling,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        if layout.is_empty() {
            return Ok(());
        }
        self.matrix.affine()?;
        let source = source.into();
        layout.validate(source.size(), sampling)?;
        if let ImageBatchSource::Image(image) = &source {
            layout.image_draw(image.clone(), sampling)?;
        }
        self.push(Command::ImageBatch {
            source,
            layout,
            sampling,
            matrix: self.matrix,
            paint: paint.map(Paint::snapshot),
        })
    }
}
