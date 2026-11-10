//! Original SKPaint state and immutable retained drawing snapshots.
use crate::{
    Error,
    image::{ColorFilter, ImageSampling},
    image_filter::{FilterBlender, FilterShaderSource, ImageFilter},
    scene::LayerBlend,
    vector::{
        Brush, BrushKind, PathEffect, PathEffectKind, Pen, PenLineCap, PenLineJoin, PenOptions,
    },
};
use std::{ops::Deref, sync::Arc};
mod fill_path;
mod mask_filter;
pub use mask_filter::{MaskBlurStyle, PaintMaskFilter};
/// Original independent fill/stroke selection.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum PaintStyle {
    /// Fill only.
    #[default]
    Fill,
    /// Stroke only.
    Stroke,
    /// Fill followed by stroke.
    StrokeAndFill,
}
/// Mutable source paint. Recording snapshots copy scalar state and retain actual
/// immutable shader/filter/path-effect owners; later edits cannot alter a draw.
#[derive(Clone, Debug)]
pub struct Paint {
    /// Original fill/stroke selection.
    pub style: PaintStyle,
    /// Original byte RGBA, initially opaque black.
    pub color: [u8; 4],
    stroke_width: f32,
    /// Original miter limit; the Pen constructor normalizes independently.
    pub stroke_miter: f32,
    /// Original cap mapped directly to the existing source pen.
    pub stroke_cap: PenLineCap,
    /// Original join mapped directly to the existing source pen.
    pub stroke_join: PenLineJoin,
    /// Actual source shader generation; shader RGB replaces paint RGB.
    pub shader: Option<Arc<FilterShaderSource>>,
    /// Original immutable color-filter generation.
    pub color_filter: Option<ColorFilter>,
    /// Original immutable image-filter generation.
    pub image_filter: Option<ImageFilter>,
    /// Original immutable path-effect generation, including dash and all stamps.
    pub path_effect: Option<PathEffect>,
    /// Original independent coverage-only mask effect.
    pub mask_filter: Option<PaintMaskFilter>,
    blender: Option<FilterBlender>,
    blend_mode: LayerBlend,
    /// Original default false; unrelated to source text policies.
    pub antialias: bool,
    /// Original retained dither flag; most source drawing ignores it.
    pub dither: bool,
    /// Original legacy quality value, lowered only at image drawing.
    pub filter_quality: i32,
}
impl Default for Paint {
    fn default() -> Self {
        Self {
            style: PaintStyle::Fill,
            color: [0, 0, 0, 255],
            stroke_width: 0.0,
            stroke_miter: 4.0,
            stroke_cap: PenLineCap::Flat,
            stroke_join: PenLineJoin::Miter,
            shader: None,
            color_filter: None,
            image_filter: None,
            path_effect: None,
            mask_filter: None,
            blender: None,
            blend_mode: LayerBlend::SrcOver,
            antialias: false,
            dither: false,
            filter_quality: 0,
        }
    }
}
/// Frozen exact paint generation; cloning is O(1) and has no source callbacks.
#[derive(Clone, Debug)]
pub struct PaintSnapshot(Arc<Paint>);
impl Deref for PaintSnapshot {
    type Target = Paint;
    fn deref(&self) -> &Paint {
        &self.0
    }
}
impl Paint {
    /// Retain the complete immutable original shader owner without eager capture.
    pub fn set_shader(&mut self, shader: Option<&crate::shader::Shader>) {
        self.shader = shader.map(crate::shader::Shader::source);
    }
    /// Freeze all original scalar state and independently shared source owners.
    pub fn snapshot(&self) -> PaintSnapshot {
        PaintSnapshot(Arc::new(self.clone()))
    }
    /// Original nonnegative setter: NaN and negative inputs become zero.
    pub fn set_stroke_width(&mut self, value: f32) {
        self.stroke_width = if value >= 0.0 { value } else { 0.0 };
    }
    /// Exact retained stroke width; zero selects the original hairline.
    pub fn stroke_width(&self) -> f32 {
        self.stroke_width
    }
    /// Original legacy stroke flag treats StrokeAndFill as true.
    pub fn is_stroke(&self) -> bool {
        self.style != PaintStyle::Fill
    }
    /// Original legacy assignment selects exactly Stroke or Fill.
    pub fn set_stroke(&mut self, value: bool) {
        self.style = if value {
            PaintStyle::Stroke
        } else {
            PaintStyle::Fill
        };
    }
    /// Original byte-to-float division, not an independently rounded product.
    pub fn color_f(&self) -> [f32; 4] {
        self.color.map(|v| f32::from(v) / 255.0)
    }
    /// Original ties-to-even byte conversion and clamping.
    pub fn set_color_f(&mut self, color: [f32; 4]) {
        self.color = color.map(|v| (v * 255.0).round_ties_even().clamp(0.0, 255.0) as u8);
    }
    /// Original selected fixed mode, independent of arithmetic/runtime metadata.
    pub fn blend_mode(&self) -> LayerBlend {
        self.blend_mode
    }
    /// A fixed-mode edit clears the previous explicit blender owner.
    pub fn set_blend_mode(&mut self, mode: LayerBlend) {
        self.blend_mode = mode;
        self.blender = None;
    }
    /// Original arithmetic/runtime owners report SrcOver through BlendMode.
    pub fn set_blender(&mut self, blender: Option<FilterBlender>) {
        self.blend_mode = blender
            .as_ref()
            .and_then(FilterBlender::blend_mode)
            .unwrap_or_default();
        self.blender = blender;
    }
    /// Original lazy fixed-mode blender semantics, without mutating frozen state.
    pub fn blender(&self) -> Option<FilterBlender> {
        self.blender.clone().or_else(|| {
            (self.blend_mode != LayerBlend::SrcOver).then_some(FilterBlender::Mode(self.blend_mode))
        })
    }
    /// Original retained canvas refuses arithmetic blenders at the paint scope.
    pub fn canvas_blend(&self) -> Result<LayerBlend, Error> {
        if self
            .blender
            .as_ref()
            .is_some_and(|b| b.arithmetic_parameters().is_some())
        {
            Err(Error::UnsupportedExecution)
        } else {
            Ok(self.blend_mode)
        }
    }
    /// Restore all original defaults; retained snapshots remain unchanged.
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    /// Original image reconstruction selected by the legacy quality property.
    pub fn image_sampling(&self) -> Result<ImageSampling, Error> {
        Ok(match self.filter_quality {
            0 => ImageSampling::Nearest,
            1 => ImageSampling::Linear,
            2 => ImageSampling::LinearMipmap,
            3 => ImageSampling::Cubic,
            _ => return Err(Error::InvalidInput),
        })
    }
    /// Original bounds query. Final overflow/nonfinite miter output remains an
    /// admitted result, matching the source; unsupported effects return None.
    pub fn fast_bounds(&self, bounds: [f32; 4]) -> Option<[f32; 4]> {
        if bounds.iter().any(|v| !v.is_finite()) || self.image_filter.is_some() {
            return None;
        }
        let mut outset = 0.0;
        if self.style != PaintStyle::Fill {
            let radius = if self.stroke_width == 0.0 {
                0.5
            } else {
                self.stroke_width * 0.5
            };
            if !radius.is_finite() {
                return None;
            }
            outset = if self.stroke_join == PenLineJoin::Miter {
                radius
                    * if self.stroke_miter.is_nan() {
                        f32::NAN
                    } else {
                        self.stroke_miter.max(1.0)
                    }
            } else {
                radius
            };
        }
        if let Some(filter) = &self.mask_filter {
            match filter {
                PaintMaskFilter::Blur { sigma, .. } => outset += 3.0 * sigma,
                PaintMaskFilter::Shader(_) => return None,
                _ => {}
            }
        }
        let result = [
            bounds[0] - outset,
            bounds[1] - outset,
            bounds[2] + outset,
            bounds[3] + outset,
        ];
        Some(result)
    }
    /// Original solid color filtering happens before conversion to material floats.
    pub fn filtered_color(&self) -> [u8; 4] {
        self.color_filter
            .as_ref()
            .map_or(self.color, |f| f.apply_color(self.color))
    }
    /// Source shader plus original filter wrapper, preserving paint alpha as a
    /// separate final multiplication. GPU source graphs execute only at preparation.
    pub fn shader_source(&self) -> Option<Arc<FilterShaderSource>> {
        self.shader.as_ref().map(|shader| {
            if let Some(filter) = &self.color_filter {
                Arc::new(FilterShaderSource::ColorFilter {
                    source: shader.clone(),
                    filter: filter.clone(),
                })
            } else {
                shader.clone()
            }
        })
    }
    /// Exact ordinary material conversion. Complex image/picture/composed shaders
    /// have a separate actual-source canvas preparation path.
    pub fn fill_brush(&self) -> Result<Arc<Brush>, Error> {
        let brush = match self.shader.as_deref() {
            None => Brush::new(BrushKind::Solid(
                self.filtered_color().map(|v| f32::from(v) / 255.0),
            )),
            Some(source) => {
                if let FilterShaderSource::Brush(brush) = source {
                    if self.color_filter.is_none() && self.color[3] == 255 {
                        return Ok(brush.clone());
                    }
                }
                let mut brush = crate::shader::canvas_brush(source)?;
                if let Some(filter) = &self.color_filter {
                    crate::shader::filter_brush(&mut brush, filter);
                }
                brush.opacity *= f32::from(self.color[3]) / 255.0;
                brush
            }
        };
        // Immutable returned owner cannot be changed through a retained snapshot.
        Ok(Arc::new(brush))
    }
    /// Exact local-width source pen, including one-device-pixel hairline sentinel
    /// and original f32 interval/width division before conversion to double.
    pub fn local_pen(&self, brush: Arc<Brush>) -> Result<Option<Arc<Pen>>, Error> {
        if self.style == PaintStyle::Fill {
            return Ok(None);
        }
        let width = if self.stroke_width == 0.0 {
            1.0
        } else {
            self.stroke_width
        };
        let mut dash = None;
        let mut offset = 0.0;
        if let Some(effect) = &self.path_effect
            && let PathEffectKind::Dash { intervals, phase } = effect.kind()
        {
            if !width.is_finite()
                || width <= 0.0
                || intervals.values().is_empty()
                || intervals.values().len() % 2 != 0
                || !phase.is_finite()
                || intervals
                    .values()
                    .iter()
                    .any(|v| !v.is_finite() || *v < 0.0)
            {
                return Err(Error::InvalidInput);
            }
            dash = Some(
                intervals
                    .values()
                    .iter()
                    .map(|v| f64::from(*v / width))
                    .collect::<Vec<_>>(),
            );
            offset = f64::from(*phase / width);
        }
        let options = PenOptions {
            thickness: if self.stroke_width == 0.0 {
                Pen::HAIRLINE_THICKNESS
            } else {
                self.stroke_width
            },
            join: self.stroke_join,
            miter_limit: self.stroke_miter,
            start_cap: self.stroke_cap,
            end_cap: self.stroke_cap,
            dash_cap: self.stroke_cap,
            dash_offset: offset,
            ..Default::default()
        };
        Ok(Some(Arc::new(Pen::with_options(
            brush,
            options,
            dash.as_deref(),
        )?)))
    }
}
