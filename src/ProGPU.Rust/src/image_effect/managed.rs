//! Original managed image command snapshots and extension geometry admission.
use super::*;
use crate::{
    geometry::VectorVertex,
    image::{ImageSampling, ImageSource},
    scene::LayerBlend,
    vector::Matrix4,
};

/// Original five-row straight RGBA matrix; no color-space or alpha conversion is
/// inferred. The offset is added after the four original dot products.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ImageEffectColorMatrix {
    pub red: [f32; 4],
    pub green: [f32; 4],
    pub blue: [f32; 4],
    pub alpha: [f32; 4],
    pub offset: [f32; 4],
}

/// Original Scene spherical descriptor. Orientation is XYZW and is retained
/// verbatim: the media producer owns normalization, not this drawing extension.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ImageEffectSphericalProjection {
    /// Normalized source x/y/width/height, independent of the output quad.
    pub source_uv_rect: [f32; 4],
    pub view_orientation: [f32; 4],
    pub horizontal_field_of_view_radians: f32,
    pub output_aspect_ratio: f32,
}
impl ImageEffectSphericalProjection {
    fn rows(self) -> [[f32; 4]; 3] {
        // Matrix4x4.CreateFromQuaternion as selected by the original extension.
        // Keep the original row-vector convention and separate products.
        let [x, y, z, w] = self.view_orientation;
        let xx = x * x;
        let yy = y * y;
        let zz = z * z;
        let xy = x * y;
        let wz = z * w;
        let xz = z * x;
        let wy = y * w;
        let yz = y * z;
        let wx = x * w;
        [
            [1.0 - 2.0 * (yy + zz), 2.0 * (xy + wz), 2.0 * (xz - wy), 0.0],
            [2.0 * (xy - wz), 1.0 - 2.0 * (zz + xx), 2.0 * (yz + wx), 0.0],
            [2.0 * (xz + wy), 2.0 * (yz - wx), 1.0 - 2.0 * (yy + xx), 0.0],
        ]
    }
}

/// Owned original immutable-command values. Recording takes an independent
/// snapshot; mask handles retain their actual producer and device generation.
/// Planar conversion belongs to the complete `ImageEffectSource::Gpu` pair,
/// preventing mismatched independently borrowed chroma/descriptor publication.
#[derive(Clone, Debug)]
pub struct ImageEffectCommandData {
    pub brightness: f32,
    pub contrast: f32,
    pub saturation: f32,
    pub grayscale: f32,
    pub sepia: f32,
    pub invert: f32,
    /// Original finite sigma. Outside live-prepass admission, the canonical
    /// fragment shader retains its original bounded inline blur behavior.
    pub blur_sigma: f32,
    pub mask: Option<ImageSource>,
    pub color_matrix: Option<ImageEffectColorMatrix>,
    pub luminance_to_alpha: bool,
    pub spherical_projection: Option<ImageEffectSphericalProjection>,
}
impl Default for ImageEffectCommandData {
    fn default() -> Self {
        Self {
            brightness: 0.0,
            contrast: 1.0,
            saturation: 1.0,
            grayscale: 0.0,
            sepia: 0.0,
            invert: 0.0,
            blur_sigma: 0.0,
            mask: None,
            color_matrix: None,
            luminance_to_alpha: false,
            spherical_projection: None,
        }
    }
}
impl ImageEffectCommandData {
    /// Build the exact original 288-byte effect record. Device-dependent source
    /// alpha, YUV and effective mask flags are resolved at actual preparation.
    pub fn uniforms(&self) -> Result<ImageEffectUniforms, Error> {
        let mut result = ImageEffectUniforms {
            effects0: [
                self.brightness,
                self.contrast,
                self.saturation,
                self.grayscale,
            ],
            effects1: [self.sepia, self.invert, self.blur_sigma, 0.0],
            ..Default::default()
        };
        if let Some(matrix) = self.color_matrix {
            result.color_matrix_red = matrix.red;
            result.color_matrix_green = matrix.green;
            result.color_matrix_blue = matrix.blue;
            result.color_matrix_alpha = matrix.alpha;
            result.color_matrix_offset = matrix.offset;
            result.flags0[2] = 1.0;
        }
        result.flags0[3] = if self.luminance_to_alpha { 1.0 } else { 0.0 };
        if let Some(projection) = self.spherical_projection {
            finite(&projection.view_orientation)?;
            result.spherical0 = [
                1.0,
                projection.horizontal_field_of_view_radians,
                projection.output_aspect_ratio,
                0.0,
            ];
            result.spherical_uv_rect = projection.source_uv_rect;
            [
                result.spherical_rotation0,
                result.spherical_rotation1,
                result.spherical_rotation2,
            ] = projection.rows();
        } else {
            // Original nullable descriptor emits default UVs, while rotation
            // always comes from the independently selected identity matrix.
            result.spherical_uv_rect = [0.0; 4];
        }
        result.validate_common()?;
        Ok(result)
    }
    /// Original live-prepass command rewrite. The complete RGB source is supplied
    /// independently by the owned blur output; all other effect fields survive.
    pub fn without_blur(&self) -> Self {
        let mut result = self.clone();
        result.blur_sigma = 0.0;
        result
    }
}

/// Original DrawImageWithEffect/DrawPlanarImageWithEffect and legacy-parameter
/// lowering. No source sampling occurs until the resulting Scene is prepared.
#[derive(Clone, Debug)]
pub struct ManagedImageEffectCommand {
    pub source: ImageEffectSource,
    /// Destination x/y/width/height; endpoint addition precedes transformation.
    pub rect: [f32; 4],
    /// Pixel x/y/width/height. Nonpositive width or height selects full-source UVs.
    pub source_rect: [f32; 4],
    pub sampling: ImageSampling,
    pub effect: ImageEffectCommandData,
    pub transform: Matrix4,
    /// Original active compositor opacity, independent of effect source alpha.
    pub opacity: f32,
}
impl ManagedImageEffectCommand {
    pub fn new(source: impl Into<ImageEffectSource>, rect: [f32; 4]) -> Self {
        Self {
            source: source.into(),
            rect,
            source_rect: [0.0; 4],
            sampling: ImageSampling::Linear,
            effect: Default::default(),
            transform: Matrix4::IDENTITY,
            opacity: 1.0,
        }
    }
    /// Freeze original quad/UV arithmetic and all actual sources into the existing
    /// ordered Scene command. Active logical clipping remains target-time work.
    pub fn into_draw(self) -> Result<ImageEffectDraw, Error> {
        let geometry = ImageEffectGeometry {
            rect: self.rect,
            source_rect: self.source_rect,
            transform: self.transform,
            opacity: self.opacity,
        };
        let vertices = geometry.vertices(self.source.size())?;
        let result = ImageEffectDraw {
            source: self.source,
            vertices,
            managed_geometry: Some(geometry),
            effect: self.effect.uniforms()?,
            sampling: self.sampling,
            address: [crate::image::ImageAddress::Clamp; 2],
            mask: self.effect.mask,
            blur_storage: ImageBlurPolicy::Managed,
            policy: ImageEffectDrawPolicy::ManagedExtension,
        };
        result.validate()?;
        Ok(result)
    }
    /// Publish a complete owned command atomically at its current painter position.
    pub fn record(self, scene: &mut crate::scene::Scene) -> Result<(), Error> {
        scene.draw_image_effect(self.into_draw()?)
    }
}
/// Original managed source rectangle and Matrix4 transform retained until the
/// actual target's clipping/geometry preparation, independently of pixel storage.
#[derive(Clone, Copy, Debug)]
pub struct ImageEffectGeometry {
    pub rect: [f32; 4],
    pub source_rect: [f32; 4],
    pub transform: Matrix4,
    pub opacity: f32,
}
impl ImageEffectGeometry {
    pub(crate) fn vertices(self, size: [u32; 2]) -> Result<[VectorVertex; 4], Error> {
        finite(&self.rect)?;
        finite(&self.source_rect)?;
        finite(self.transform.0.as_flattened())?;
        finite(&[self.opacity])?;
        if size.contains(&0) {
            return Err(Error::InvalidInput);
        }
        let [x, y, w, h] = self.rect;
        let points = [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]
            .map(|p| self.transform.transform_point(p));
        let [sx, sy, sw, sh] = self.source_rect;
        let [l, t, r, b] = if sw > 0.0 && sh > 0.0 {
            let [tw, th] = size.map(|v| v as f32);
            [sx / tw, sy / th, (sx + sw) / tw, (sy + sh) / th]
        } else {
            [0.0, 0.0, 1.0, 1.0]
        };
        let uv = [[l, t], [r, t], [r, b], [l, b]];
        let vertices = std::array::from_fn(|i| VectorVertex {
            position: points[i],
            color: [1.0, 1.0, 1.0, self.opacity],
            texture_coordinate: uv[i],
            ..Zeroable::zeroed()
        });
        finite(bytemuck::cast_slice(&vertices))?;
        Ok(vertices)
    }
}
impl ImageEffectDraw {
    pub(crate) fn place_source(&mut self, transform: Matrix4) -> Result<(), Error> {
        if let Some(geometry) = &mut self.managed_geometry {
            geometry.transform = geometry.transform * transform;
            self.vertices = geometry.vertices(self.source.size())?;
        } else {
            for vertex in &mut self.vertices {
                vertex.position = transform.transform_point(vertex.position);
            }
        }
        self.validate()
    }
    pub(crate) fn apply_source_opacity(&mut self, opacity: f32) -> Result<(), Error> {
        if let Some(geometry) = &mut self.managed_geometry {
            geometry.opacity *= opacity;
        }
        for vertex in &mut self.vertices {
            vertex.color[3] *= opacity;
        }
        self.validate()
    }
    /// Original extension pipeline output-alpha selection, independent of the
    /// vector/text family and the semantic isolated-layer blend implementation.
    pub fn managed_output_alpha(
        source: crate::image::ImageAlpha,
        blend: LayerBlend,
    ) -> crate::image::ImageAlpha {
        if matches!(
            blend,
            LayerBlend::DstOver | LayerBlend::Multiply | LayerBlend::Screen
        ) {
            crate::image::ImageAlpha::Premultiplied
        } else {
            source
        }
    }
    /// Original GetTextureSampler fallback: this extension has no explicit Cubic
    /// or Fant entry, so both select the original linear atlas sampler.
    pub fn effective_sampling(&self) -> ImageSampling {
        if self.policy == ImageEffectDrawPolicy::ManagedExtension
            && matches!(self.sampling, ImageSampling::Cubic | ImageSampling::Fant)
        {
            ImageSampling::Linear
        } else {
            self.sampling
        }
    }
    pub(crate) fn clipped_vertices(
        &self,
        clip: Option<[f32; 4]>,
    ) -> Result<Option<[VectorVertex; 4]>, Error> {
        self.validate()?;
        let mut vertices = match self.managed_geometry {
            Some(geometry) => geometry.vertices(self.source.size())?,
            None => self.vertices,
        };
        if self.policy == ImageEffectDrawPolicy::ManagedExtension
            && let Some(clip) = clip
        {
            let mut points = vertices.map(|v| v.position);
            let mut uv = vertices.map(|v| v.texture_coordinate);
            if !clip_quad(clip, &mut points, &mut uv) {
                return Ok(None);
            }
            for i in 0..4 {
                vertices[i].position = points[i];
                vertices[i].texture_coordinate = uv[i];
            }
            finite(bytemuck::cast_slice(&vertices))?;
        }
        Ok(Some(vertices))
    }
}

// Original QuadClipper shared by both managed extension families. Original four
// far edges remain independent; no edge/width/edge reconstruction is introduced.
pub(crate) fn clip_quad(clip: [f32; 4], p: &mut [[f32; 2]; 4], uv: &mut [[f32; 2]; 4]) -> bool {
    const EPSILON: f32 = 0.0001;
    if (p[0][1] - p[1][1]).abs() > EPSILON
        || (p[2][1] - p[3][1]).abs() > EPSILON
        || (p[0][0] - p[3][0]).abs() > EPSILON
        || (p[1][0] - p[2][0]).abs() > EPSILON
    {
        return true;
    }
    let left = p[0][0].min(p[1][0]).min(p[2][0].min(p[3][0])).max(clip[0]);
    let right = p[0][0].max(p[1][0]).max(p[2][0].max(p[3][0])).min(clip[2]);
    let top = p[0][1].min(p[1][1]).min(p[2][1].min(p[3][1])).max(clip[1]);
    let bottom = p[0][1].max(p[1][1]).max(p[2][1].max(p[3][1])).min(clip[3]);
    if right <= left || bottom <= top {
        return false;
    }
    let [x0, y0] = p[0];
    let x1 = p[1][0];
    let y1 = p[3][1];
    if (x1 - x0).abs() <= EPSILON || (y1 - y0).abs() <= EPSILON {
        return false;
    }
    let nx0 = x0.clamp(left, right);
    let nx1 = x1.clamp(left, right);
    let ny0 = y0.clamp(top, bottom);
    let ny1 = y1.clamp(top, bottom);
    let u0 = (nx0 - x0) / (x1 - x0);
    let u1 = (nx1 - x0) / (x1 - x0);
    let v0 = (ny0 - y0) / (y1 - y0);
    let v1 = (ny1 - y0) / (y1 - y0);
    let original = *uv;
    *p = [[nx0, ny0], [nx1, ny0], [nx1, ny1], [nx0, ny1]];
    let lerp = |a: [f32; 2], b: [f32; 2], t: f32| {
        [a[0] * (1.0 - t) + b[0] * t, a[1] * (1.0 - t) + b[1] * t]
    };
    *uv = [[u0, v0], [u1, v0], [u1, v1], [u0, v1]].map(|[u, v]| {
        lerp(
            lerp(original[0], original[1], u),
            lerp(original[3], original[2], u),
            v,
        )
    });
    true
}
