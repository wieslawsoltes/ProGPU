//! Original typed gradient and leased-texture material record lowering.
use super::*;
use crate::{image::ImageAlpha, image_effect::ImageEffectColorMatrix, vector::BrushKind};
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum MaterialBrushTarget3D {
    #[default]
    Color = 0,
    Specular = 1,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum MeshTextureTilingMode {
    #[default]
    None = 0,
    Tile = 1,
    Crop = 2,
    Clamp = 3,
}
/// Original normalized crop, clockwise quarter-turn and horizontal mirror state.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MeshTexturePresentation {
    normalized_source_rect: [f32; 4],
    clockwise_quarter_turns: u32,
    is_mirrored: bool,
}
impl Default for MeshTexturePresentation {
    fn default() -> Self {
        Self {
            normalized_source_rect: [0.0, 0.0, 1.0, 1.0],
            clockwise_quarter_turns: 0,
            is_mirrored: false,
        }
    }
}
impl MeshTexturePresentation {
    pub fn new(
        rect: [f32; 4],
        clockwise_quarter_turns: i32,
        is_mirrored: bool,
    ) -> Result<Self, Error> {
        if rect.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let start = wide::f32x4::from(rect)
            .max(wide::f32x4::ZERO)
            .min(wide::f32x4::ONE)
            .to_array();
        let size = wide::f32x4::from(rect)
            .max(wide::f32x4::ZERO)
            .min(wide::f32x4::from([
                1.0,
                1.0,
                1.0 - start[0],
                1.0 - start[1],
            ]))
            .to_array();
        let normalized_source_rect = if size[2] > 0.0 && size[3] > 0.0 {
            [start[0], start[1], size[2], size[3]]
        } else {
            [0.0, 0.0, 1.0, 1.0]
        };
        Ok(Self {
            normalized_source_rect,
            clockwise_quarter_turns: clockwise_quarter_turns.rem_euclid(4) as u32,
            is_mirrored,
        })
    }
    pub fn normalized_source_rect(self) -> [f32; 4] {
        self.normalized_source_rect
    }
    pub fn clockwise_quarter_turns(self) -> u32 {
        self.clockwise_quarter_turns
    }
    pub fn is_mirrored(self) -> bool {
        self.is_mirrored
    }
}
/// Raw original fused color/sampling effect values; GPU canonical shaders own
/// their operation ordering and fallback blur, not CPU pixel emulation.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MeshTextureEffect {
    pub brightness: f32,
    pub contrast: f32,
    pub saturation: f32,
    pub grayscale: f32,
    pub sepia: f32,
    pub invert: f32,
    pub blur_sigma: f32,
    pub color_matrix: Option<ImageEffectColorMatrix>,
    pub luminance_to_alpha: bool,
}
impl Default for MeshTextureEffect {
    fn default() -> Self {
        Self {
            brightness: 0.0,
            contrast: 1.0,
            saturation: 1.0,
            grayscale: 0.0,
            sepia: 0.0,
            invert: 0.0,
            blur_sigma: 0.0,
            color_matrix: None,
            luminance_to_alpha: false,
        }
    }
}
impl MeshTextureEffect {
    pub fn without_gaussian_blur(self) -> Self {
        Self {
            blur_sigma: 0.0,
            ..self
        }
    }
}
/// Actual material texture selection supplied by the GPU lease admission step.
#[derive(Clone, Copy, Debug)]
pub struct MeshMaterialState {
    pub size: [u32; 2],
    pub alpha: ImageAlpha,
    pub has_texture: bool,
    pub has_yuv: bool,
    pub prepared_blur: bool,
}
impl Default for MeshMaterialState {
    fn default() -> Self {
        Self {
            size: [1, 1],
            alpha: ImageAlpha::Straight,
            has_texture: false,
            has_yuv: false,
            prepared_blur: false,
        }
    }
}
pub(super) fn record(
    viewport: &Mesh3DViewport,
    mesh: &Mesh3DEntry,
    state: MeshMaterialState,
    opacity: f32,
) -> GpuMesh3DRecord {
    let effect = if state.has_texture {
        if state.prepared_blur {
            mesh.texture_effect.without_gaussian_blur()
        } else {
            mesh.texture_effect
        }
    } else {
        MeshTextureEffect::default()
    };
    let matrix = effect.color_matrix;
    let yuv = if state.has_yuv {
        mesh.yuv_conversion
    } else {
        None
    };
    let normal = crate::image_filter::FilterTransform::new(mesh.model_transform)
        .inverse_raw()
        .map(|v| std::array::from_fn(|r| std::array::from_fn(|c| v.0[c][r])))
        .unwrap_or(Matrix4::IDENTITY.0);
    let extend = super::records::extend;
    GpuMesh3DRecord {
        model_transform: mesh.model_transform.0,
        normal_transform: normal,
        color: mesh.color,
        light_direction: extend(viewport.light_direction, viewport.light_intensity),
        ambient_color: extend(viewport.ambient_color, viewport.ambient_intensity),
        specular_color: extend(mesh.specular_color, mesh.shininess),
        material_ambient: extend(mesh.ambient_color, mesh.self_illumination.clamp(0.0, 1.0)),
        opacity: mesh.opacity * opacity,
        render_mode: viewport.render_mode as u32 as f32,
        shading_mode: mesh.shading_mode_override.unwrap_or(viewport.shading_mode) as u32 as f32,
        texture_sampling_mode: if mesh.texture_sampling == crate::image::ImageSampling::Nearest {
            0.0
        } else {
            1.0
        } + 2.0 * mesh.texture_tiling as u32 as f32,
        texture_effects0: [
            effect.brightness,
            effect.contrast,
            effect.saturation,
            effect.grayscale,
        ],
        texture_effects1: [
            effect.sepia,
            effect.invert,
            effect.blur_sigma,
            flag(state.has_texture),
        ],
        texture_info: [
            state.size[0] as f32,
            state.size[1] as f32,
            flag(state.alpha == ImageAlpha::Premultiplied),
            flag(effect.luminance_to_alpha),
        ],
        color_matrix_red: matrix.map_or([0.0; 4], |v| v.red),
        color_matrix_green: matrix.map_or([0.0; 4], |v| v.green),
        color_matrix_blue: matrix.map_or([0.0; 4], |v| v.blue),
        color_matrix_alpha: matrix.map_or([0.0; 4], |v| v.alpha),
        color_matrix_offset: matrix.map_or([0.0; 4], |v| v.offset),
        texture_flags: [
            flag(matrix.is_some()),
            flag(yuv.is_some()),
            mesh.texture_presentation.clockwise_quarter_turns as f32,
            flag(mesh.texture_presentation.is_mirrored),
        ],
        yuv_range: yuv.map_or([0.0; 4], |v| v.range),
        yuv_red: yuv.map_or([0.0; 4], |v| v.red),
        yuv_green: yuv.map_or([0.0; 4], |v| v.green),
        yuv_blue: yuv.map_or([0.0; 4], |v| v.blue),
        texture_source_rect: mesh.texture_presentation.normalized_source_rect,
        light_offset: 0,
        light_count: viewport.lights.len() as u32,
        ..Default::default()
    }
}
fn flag(value: bool) -> f32 {
    if value { 1.0 } else { 0.0 }
}
struct Gradient<'a> {
    kind: u32,
    start: [f32; 2],
    end: [f32; 2],
    center: [f32; 2],
    radii: [f32; 2],
    options: crate::vector::GradientOptions,
    stops: &'a [crate::vector::GradientStop],
    opacity: f32,
}
fn gradient(
    brush: Option<&Brush>,
    target: MaterialBrushTarget3D,
) -> Result<Option<Gradient<'_>>, Error> {
    let Some(brush) = brush else {
        return if target == MaterialBrushTarget3D::Color {
            Ok(None)
        } else {
            Err(Error::InvalidInput)
        };
    };
    if !brush.opacity.is_finite() {
        return Err(Error::InvalidInput);
    }
    let result = match &brush.kind {
        BrushKind::Linear(v) => Gradient {
            kind: 1,
            start: v.start,
            end: v.end,
            center: [0.0; 2],
            radii: [0.0; 2],
            options: v.options,
            stops: &v.stops,
            opacity: brush.opacity,
        },
        BrushKind::Radial(v) => Gradient {
            kind: 2,
            start: v.origin,
            end: [0.0; 2],
            center: v.center,
            radii: v.radii,
            options: v.options,
            stops: &v.stops,
            opacity: brush.opacity,
        },
        _ => return Err(Error::UnsupportedExecution),
    };
    let m = result.options.transform.0;
    if result
        .start
        .iter()
        .chain(&result.end)
        .chain(&result.center)
        .chain(&result.radii)
        .any(|v| !v.is_finite())
        || m.iter().flatten().any(|v| !v.is_finite())
        || m[0][2] != 0.0
        || m[0][3] != 0.0
        || m[1][2] != 0.0
        || m[1][3] != 0.0
        || m[2] != [0.0, 0.0, 1.0, 0.0]
        || m[3][2] != 0.0
        || m[3][3] != 1.0
        || result.stops.is_empty()
        || result.stops.len() > 65536
        || result
            .stops
            .iter()
            .any(|s| !s.offset.is_finite() || s.color.iter().any(|v| !v.is_finite()))
    {
        return Err(Error::InvalidInput);
    }
    Ok(Some(result))
}
pub(super) fn validate_brush(
    brush: Option<&Brush>,
    target: MaterialBrushTarget3D,
) -> Result<usize, Error> {
    gradient(brush, target).map(|value| value.map_or(0, |gradient| gradient.stops.len()))
}
pub(super) fn apply_brush(
    brush: Option<&Brush>,
    target: MaterialBrushTarget3D,
    record: &mut GpuMesh3DRecord,
    stops: &mut Vec<MeshGradientStop>,
) -> Result<(), Error> {
    let Some(g) = gradient(brush, target)? else {
        return Ok(());
    };
    if g.stops.len() > 65536usize.saturating_sub(stops.len()) {
        return Err(Error::Capacity);
    }
    stops
        .try_reserve(g.stops.len())
        .map_err(|_| Error::Capacity)?;
    let offset = stops.len();
    stops.extend(g.stops.iter().map(|s| MeshGradientStop {
        color: s.color,
        offset: s.offset,
        padding: [0.0; 3],
    }));
    let m = g.options.transform.0;
    record.material_gradient_points = [g.start[0], g.start[1], g.end[0], g.end[1]];
    record.material_gradient_ellipse = [g.center[0], g.center[1], g.radii[0], g.radii[1]];
    record.material_brush_transform0 = [m[0][0], m[1][0], m[3][0], 0.0];
    record.material_brush_transform1 = [m[0][1], m[1][1], m[3][1], 0.0];
    record.material_brush_metadata = [
        g.kind as f32,
        g.opacity.clamp(0.0, 1.0),
        g.options.spread as u32 as f32,
        g.options.interpolation as u32 as f32,
    ];
    record.material_stop_metadata = [
        offset as f32,
        g.stops.len() as f32,
        target as u32 as f32,
        0.0,
    ];
    Ok(())
}
