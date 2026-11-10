//! Original dedicated direct-paint ABI; the legacy glyph layout is unchanged.
use super::*;
use bytemuck::{Pod, Zeroable};

/// Exact original 96-byte physical glyph paint record. The glyph instance's
/// reserved word carries this record's integer index without numeric conversion.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct GpuHintedGlyphPaint {
    /// Zero registered material; one actual texture material.
    pub kind: u32,
    /// Exact registered source-material index.
    pub brush_index: u32,
    /// Original premultiplied/bounded/cubic and sampling-mode bits.
    pub flags: u32,
    /// Original reserved zero word.
    pub reserved: u32,
    /// Source translation X/Y and independent texture opacity Z.
    pub source_offset_opacity: [f32; 4],
    /// Original normalized source texture endpoints.
    pub uv_bounds: [f32; 4],
    /// Actual source texture quadrilateral first pair.
    pub texture_quad_01: [f32; 4],
    /// Actual source texture quadrilateral second pair.
    pub texture_quad_23: [f32; 4],
    /// Original sampling coefficients and independent address modes.
    pub sampling: [f32; 4],
}
impl GpuHintedGlyphPaint {
    /// Original one triangle pair, or one bounded image pair per glyph triangle.
    pub fn vertex_count(&self) -> u32 {
        if self.kind == 1 && self.flags & 2 != 0 {
            12
        } else {
            6
        }
    }
}
const _: () = assert!(size_of::<GpuHintedGlyphPaint>() == 96);

/// Retained original spatial material prior to shared scene registration.
#[derive(Clone, Debug)]
pub struct HintedMaterialPaint {
    /// Source material generation, including its original opacity.
    pub brush: Option<Arc<Brush>>,
    /// Actual lowered image mapping/owner, independent of material registration.
    pub texture: Option<crate::image::ImageDraw>,
    /// Independent ancestor opacity applied by original material registration.
    pub semantic_opacity: f32,
    /// Immutable original direct-paint frame; registration fills only its index.
    pub record: GpuHintedGlyphPaint,
}
#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub(crate) struct HintedMaterialInstances {
    pub instances: Vec<GlyphInstance>,
    pub paint: HintedMaterialPaint,
}

impl HintedMaterialPaint {
    pub(crate) fn resolve_sampling(&mut self, path: crate::image::ImageSamplingPath) {
        use crate::image::{ImageSampling, ImageSamplingPath};
        if let Some(texture) = &self.texture {
            self.record.sampling[0] = match (texture.sampling, path) {
                (ImageSampling::Fant, ImageSamplingPath::ExplicitShader) => -256.0,
                (ImageSampling::Fant, _) => -32.0,
                (ImageSampling::Nearest, ImageSamplingPath::ExplicitShader) => -128.0,
                (ImageSampling::Linear, ImageSamplingPath::ExplicitShader) => -64.0,
                _ => 0.0,
            };
        }
    }
}
impl HintedReplayDraw {
    pub(super) fn texture_paint(
        &self,
        context: TextPaintContext,
    ) -> Result<Option<HintedMaterialPaint>, Error> {
        use crate::image::{ImageAlpha, ImageSampling, ImageSamplingPath};
        let Some(brush) = &self.image_brush else {
            return Ok(None);
        };
        let translation = identity_translation(context.transform)?;
        let mut texture = brush.for_fill(self.source_ink_bounds)?;
        if texture.ignore_source_alpha {
            return Err(Error::UnsupportedExecution);
        }
        // Original GpuTextureBrush uses its retained transform for bounded paint;
        // the extended mapping is already lowered into its actual fill domain.
        texture.transform.0[4] += translation[0];
        texture.transform.0[5] += translation[1];
        texture.cubic = [0.0, 0.5];
        let mesh = texture.compile(context.dpi_scale, ImageSamplingPath::NativeSampler)?;
        let vertices = mesh.vertices();
        let [p0, p1, p2, p3] = [
            vertices[0].position,
            vertices[1].position,
            vertices[2].position,
            vertices[3].position,
        ];
        let [width, height] = texture.image.size().map(|v| v as f32);
        let [x, y, w, h] = texture.source;
        let premultiplied = texture.image.alpha() == ImageAlpha::Premultiplied;
        let mut flags = (texture.sampling as u32) << 8;
        if premultiplied {
            flags |= 1;
        }
        if !brush.extend_to_fill_bounds {
            flags |= 2;
        }
        if texture.sampling == ImageSampling::Cubic {
            flags |= 4;
        }
        let opacity = texture.opacity * context.semantic_opacity;
        if !opacity.is_finite() {
            return Err(Error::InvalidInput);
        }
        let mut paint = HintedMaterialPaint {
            brush: None,
            semantic_opacity: context.semantic_opacity,
            record: GpuHintedGlyphPaint {
                kind: 1,
                flags,
                source_offset_opacity: [translation[0], translation[1], opacity, 0.0],
                uv_bounds: [x / width, y / height, (x + w) / width, (y + h) / height],
                texture_quad_01: [p0[0], p0[1], p1[0], p1[1]],
                texture_quad_23: [p2[0], p2[1], p3[0], p3[1]],
                sampling: [
                    0.0,
                    0.5,
                    texture.address[0] as u8 as f32,
                    texture.address[1] as u8 as f32,
                ],
                ..Default::default()
            },
            texture: Some(texture),
        };
        paint.resolve_sampling(ImageSamplingPath::NativeSampler);
        Ok(Some(paint))
    }
}
