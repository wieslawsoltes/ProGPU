//! Original image-filter result bounds and picture paint-layer producers.
use super::*;
use crate::{
    canvas::{Canvas, CanvasMatrix, CanvasPicture, SaveLayer},
    image::{RasterSampling, RenderSurface},
    image_filter::ImageFilter,
    paint::Paint,
};

/// One completed original ApplyImageFilter result. Bounds are the standardized
/// requested output rectangle, independently of the actual filtered ink.
#[derive(Clone, Debug)]
pub struct AppliedImageFilter {
    /// Actual immutable output generation in the selected output context.
    pub image: TextureImage,
    /// Original left/top/right/bottom output subset.
    pub out_subset: [i32; 4],
    /// Original integer placement of the result's top-left pixel.
    pub out_offset: [i32; 2],
}
impl AppliedImageFilter {
    /// Original floating offset overload converts the completed integer offset.
    pub fn float_offset(&self) -> [f32; 2] {
        self.out_offset.map(|v| v as f32)
    }
}
impl TextureImage {
    /// Execute the original subset -> translated SaveLayer -> filter -> snapshot
    /// sequence. The capture context is explicit instead of process-global Current;
    /// the optional output context retains its independent original validity gate.
    /// Empty bounds or an invalid requested output context return None, publishing
    /// no image or offset. Source/picture/filter owners are admitted before capture.
    pub async fn apply_image_filter_in(
        &self,
        capture_context: &ImageContext,
        filter: &ImageFilter,
        subset: [i32; 4],
        clip_bounds: [i32; 4],
        output_context: Option<&ImageContext>,
    ) -> Result<Option<AppliedImageFilter>, ImageResourceError> {
        let info = self.info();
        let source = [
            subset[0].max(0),
            subset[1].max(0),
            subset[2].min(info.width),
            subset[3].min(info.height),
        ];
        let output = [
            clip_bounds[0].min(clip_bounds[2]),
            clip_bounds[1].min(clip_bounds[3]),
            clip_bounds[0].max(clip_bounds[2]),
            clip_bounds[1].max(clip_bounds[3]),
        ];
        // Original SKRectI width/height and unary negation use signed wrapping
        // arithmetic before their independent positive-size and float conversions.
        let width = output[2].wrapping_sub(output[0]);
        let height = output[3].wrapping_sub(output[1]);
        if source[2] <= source[0]
            || source[3] <= source[1]
            || width <= 0
            || height <= 0
            || output_context.is_some_and(|context| {
                context.is_abandoned() || (self.is_texture_backed() && !self.is_valid(context))
            })
        {
            return Ok(None);
        }
        let mut canvas = Canvas::new([width as f32, height as f32])?;
        canvas.clear([0; 4])?;
        canvas.translate([
            output[0].wrapping_neg() as f32,
            output[1].wrapping_neg() as f32,
        ]);
        let mut paint = Paint::default();
        paint.image_filter = Some(filter.clone());
        canvas.save_layer(SaveLayer {
            bounds: Some(output.map(|v| v as f32)),
            paint: Some(paint.snapshot()),
            ..SaveLayer::default()
        })?;
        let source = source.map(|v| v as f32);
        canvas.draw_sampled_image(self, Some(source), source, RasterSampling::NEAREST, None)?;
        canvas.restore()?;
        let scene = canvas.finish()?.prepare_scene(capture_context).await?;
        let output_info = ImageInfo::with_format(
            width,
            height,
            PixelColorType::Rgba8888,
            info.alpha_type,
            info.color_space,
        );
        let mut surface = RenderSurface::new(capture_context, output_info)?;
        surface.record(&scene)?;
        let image = surface.snapshot().await?;
        let image = if let Some(context) =
            output_context.filter(|context| !context.same_context(image.context()))
        {
            // Original explicit transfer reads actual base-level RGBA pixels and
            // owns a new portable texture; ordinary implicit source reuse cannot.
            image.to_texture_image(context, false).await?
        } else {
            image
        };
        Ok(Some(AppliedImageFilter {
            image,
            out_subset: output,
            out_offset: [output[0], output[1]],
        }))
    }

    /// Capture an original Canvas picture, optionally through a full paint layer.
    /// This preserves color/image filters and independent layer alpha/blending;
    /// DrawPicture's narrower alpha-only paint handling is not substituted.
    pub async fn from_canvas_picture(
        context: &ImageContext,
        picture: &CanvasPicture,
        dimensions: [i32; 2],
        matrix: CanvasMatrix,
        paint: Option<&Paint>,
    ) -> Result<Self, ImageResourceError> {
        if dimensions.iter().any(|v| *v <= 0) {
            return Err(ImageResourceError::Bounds);
        }
        let mut canvas = Canvas::new(dimensions.map(|v| v as f32))?;
        canvas.clear([0; 4])?;
        canvas.set_matrix(matrix);
        if let Some(paint) = paint {
            canvas.save_layer(SaveLayer {
                paint: Some(paint.snapshot()),
                ..SaveLayer::default()
            })?;
        }
        canvas.draw_picture(picture.clone(), None)?;
        if paint.is_some() {
            canvas.restore()?;
        }
        let scene = canvas.finish()?.prepare_scene(context).await?;
        let mut surface =
            RenderSurface::new(context, ImageInfo::new(dimensions[0], dimensions[1]))?;
        surface.record(&scene)?;
        surface.snapshot().await
    }
}
