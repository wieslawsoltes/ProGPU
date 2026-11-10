//! Original managed backdrop-material brush, source policy and retained draws.
#[cfg(feature = "gpu")]
use crate::{Error, vector::Matrix4};

/// Original material kind labels consumed directly by BackdropMaterial.wgsl.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum BackdropMaterialKind {
    #[default]
    Acrylic = 0,
    Mica = 1,
    Blur = 2,
    Tint = 3,
    Fallback = 4,
}
/// Original independent backdrop source policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum BackdropMaterialSource {
    None = 0,
    Texture = 1,
    #[default]
    HostBackdrop = 2,
}

/// Original raw brush properties. Values are sanitized only when preparing the
/// exact shader record, preserving source metadata and independent brush alpha.
#[derive(Clone, Debug, PartialEq)]
pub struct BackdropMaterialBrush {
    /// Original material operation; the discriminant reaches the canonical shader.
    pub kind: BackdropMaterialKind,
    /// Independent source policy; host capture is minted by the renderer.
    pub source: BackdropMaterialSource,
    /// Raw straight tint RGBA, sanitized at GPU preparation.
    pub tint_color: [f32; 4],
    /// Raw straight luminosity RGBA.
    pub luminosity_color: [f32; 4],
    /// Raw straight explicit fallback RGBA.
    pub fallback_color: [f32; 4],
    /// Original independent noise color.
    pub noise_color: [f32; 4],
    /// Raw independent tint multiplier.
    pub tint_opacity: f32,
    /// Raw independent luminosity multiplier.
    pub luminosity_opacity: f32,
    /// Raw final material coverage multiplier.
    pub material_opacity: f32,
    /// Raw independent procedural-noise opacity.
    pub noise_opacity: f32,
    /// Original fixed-footprint sample radius, clamped to 0..96 at preparation.
    pub blur_radius: f32,
    /// Original source saturation, clamped to 0..4 at preparation.
    pub saturation: f32,
    /// Explicit fallback selection, independent of material kind.
    pub use_fallback: bool,
    /// Original brush opacity, multiplied once by FromBrush.
    pub opacity: f32,
}
impl Default for BackdropMaterialBrush {
    fn default() -> Self {
        Self {
            kind: Default::default(),
            source: Default::default(),
            tint_color: [1.0; 4],
            luminosity_color: [0.96, 0.96, 0.96, 0.72],
            fallback_color: [0.96, 0.96, 0.96, 1.0],
            noise_color: [1.0; 4],
            tint_opacity: 1.0,
            luminosity_opacity: 1.0,
            material_opacity: 1.0,
            noise_opacity: 0.0225,
            blur_radius: 30.0,
            saturation: 1.25,
            use_fallback: false,
            opacity: 1.0,
        }
    }
}
impl BackdropMaterialBrush {
    /// Original light/dark Mica factory, retaining every independent alpha.
    pub fn mica(dark_theme: bool) -> Self {
        Self {
            kind: BackdropMaterialKind::Mica,
            tint_color: if dark_theme {
                [0.08, 0.08, 0.08, 0.62]
            } else {
                [0.96, 0.96, 0.96, 0.55]
            },
            luminosity_color: if dark_theme {
                [0.13, 0.13, 0.13, 0.82]
            } else {
                [0.94, 0.94, 0.94, 0.76]
            },
            fallback_color: if dark_theme {
                [0.13, 0.13, 0.13, 1.0]
            } else {
                [0.94, 0.94, 0.94, 1.0]
            },
            noise_opacity: 0.015,
            blur_radius: 48.0,
            saturation: 0.85,
            ..Default::default()
        }
    }
}

/// Original immutable extension parameters. Explicit source and actual host
/// capture are distinct contracts; only the renderer can produce a host capture.
#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub struct BackdropMaterialParameters {
    /// Original logical X, Y, width and height.
    pub rect: [f32; 4],
    /// Original ordered top-left, top-right, bottom-right, bottom-left radii.
    pub corner_radii_x: [f32; 4],
    /// Independent vertical corner radii in the same original order.
    pub corner_radii_y: [f32; 4],
    /// Frozen material fields after the original brush-opacity multiplication.
    pub brush: BackdropMaterialBrush,
    /// Actual optional same-device texture owner; never a host-capture claim.
    pub source_texture: Option<crate::image::GpuImage>,
    /// Original source X, Y, width and height in texture pixels.
    pub source_rect: [f32; 4],
    /// Original source sampler selection, with unit anisotropy.
    pub sampling: crate::image::ImageSampling,
}
#[cfg(feature = "gpu")]
impl BackdropMaterialParameters {
    /// Original FromBrush snapshot multiplies brush opacity exactly once.
    pub fn from_brush(
        brush: &BackdropMaterialBrush,
        rect: [f32; 4],
        corner_radii_x: [f32; 4],
        corner_radii_y: [f32; 4],
    ) -> Self {
        let mut brush = brush.clone();
        brush.material_opacity *= brush.opacity;
        brush.opacity = 1.0;
        Self {
            rect,
            corner_radii_x,
            corner_radii_y,
            brush,
            source_texture: None,
            source_rect: [0.0; 4],
            sampling: crate::image::ImageSampling::Linear,
        }
    }
    /// Original host predicate; source textures and explicit fallback suppress it.
    pub fn captures_host_backdrop(&self) -> bool {
        self.brush.source == BackdropMaterialSource::HostBackdrop
            && self.source_texture.is_none()
            && !self.brush.use_fallback
    }
    /// Translation affects only the original rectangle, not source UV metadata.
    pub fn translated(&self, translation: [f32; 2]) -> Self {
        let mut result = self.clone();
        result.rect[0] += translation[0];
        result.rect[1] += translation[1];
        result
    }
}
/// Ordered source command with its complete original placement and active alpha.
#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub struct BackdropMaterialDraw {
    /// Complete immutable extension parameters.
    pub parameters: BackdropMaterialParameters,
    /// Original logical source placement before vertex compilation.
    pub transform: Matrix4,
    /// Original brush opacity, multiplied once by FromBrush.
    pub opacity: f32,
    /// Requested fixed premultiplied blend; actual host capture uses Src.
    pub blend: crate::scene::LayerBlend,
}
#[cfg(feature = "gpu")]
impl BackdropMaterialDraw {
    /// Retain parameters with identity placement, unit active opacity and SrcOver.
    pub fn new(parameters: BackdropMaterialParameters) -> Self {
        Self {
            parameters,
            transform: Matrix4::IDENTITY,
            opacity: 1.0,
            blend: crate::scene::LayerBlend::SrcOver,
        }
    }
    /// Whole source-owner and geometry admission; raw material values retain the
    /// original finite sanitization policy in the GPU uniform builder.
    pub fn validate(&self) -> Result<(), Error> {
        let p = &self.parameters;
        if p.rect.iter().any(|v| !v.is_finite())
            || p.source_rect.iter().any(|v| !v.is_finite())
            || self.transform.0.iter().flatten().any(|v| !v.is_finite())
            || !self.opacity.is_finite()
        {
            return Err(Error::InvalidInput);
        }
        let m = self.transform.0;
        if m[0][2] != 0.0
            || m[0][3] != 0.0
            || m[1][2] != 0.0
            || m[1][3] != 0.0
            || m[2] != [0.0, 0.0, 1.0, 0.0]
            || m[3][2] != 0.0
            || m[3][3] != 1.0
            || !self.blend.is_fixed()
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
    pub(crate) fn preflight_device(&self, device: &wgpu::Device) -> Result<(), Error> {
        self.validate()?;
        if let Some(image) = &self.parameters.source_texture {
            if image.device() != device {
                return Err(Error::ResourceDomain);
            }
            if image
                .size()
                .iter()
                .any(|v| *v > device.limits().max_texture_dimension_2d)
            {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }
}
#[cfg(feature = "gpu")]
mod vertices;

#[cfg(feature = "gpu")]
impl Default for BackdropMaterialParameters {
    fn default() -> Self {
        Self::from_brush(
            &BackdropMaterialBrush::default(),
            [0.0; 4],
            [0.0; 4],
            [0.0; 4],
        )
    }
}
