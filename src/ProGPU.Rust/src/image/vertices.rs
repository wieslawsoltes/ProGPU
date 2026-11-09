//! Original native image attributes/placement and managed tile/brush mapping.
use super::*;
fn rect(r: [f32; 4], positive: bool) -> bool {
    r.iter().all(|v| v.is_finite()) && (!positive || (r[2] > 0.0 && r[3] > 0.0))
}
fn finite(t: Affine) -> bool {
    t.0.iter().all(|v| v.is_finite())
}
fn bounded(r: [f32; 4], size: [u32; 2]) -> bool {
    r[0] >= 0.0 && r[1] >= 0.0 && r[0] + r[2] <= size[0] as f32 && r[1] + r[3] <= size[1] as f32
}
pub(super) fn validate(draw: &ImageDraw) -> Result<(), Error> {
    let anisotropy = draw.max_anisotropy.max(1);
    if draw.image.size().iter().any(|v| *v == 0 || *v > 16384)
        || !rect(draw.source, true)
        || !rect(draw.destination, true)
        || !finite(draw.transform)
        || !draw.opacity.is_finite()
        || !(0.0..=1.0).contains(&draw.opacity)
        || anisotropy > 16
        || (draw.sampling != ImageSampling::LinearMipmap && anisotropy != 1)
        || (!draw.extended_source
            && (!bounded(draw.source, draw.image.size())
                || draw.address != [ImageAddress::Clamp; 2]))
        || (draw.sampling == ImageSampling::Cubic
            && draw.cubic.iter().any(|v| !v.is_finite() || v.abs() > 16.0))
    {
        return Err(Error::InvalidInput);
    }
    if let Some(quad) = draw.quad
        && (draw.patches.is_some()
            || quad
                .points
                .iter()
                .flatten()
                .chain(&quad.weights)
                .any(|v| !v.is_finite()))
    {
        return Err(Error::InvalidInput);
    }
    if let Some(patches) = &draw.patches {
        if patches.is_empty()
            || patches.len() > 65536
            || draw.extended_source
            || draw.ignore_source_alpha
        {
            return Err(Error::InvalidInput);
        }
        for patch in patches.iter() {
            if !rect(patch.source, false)
                || !rect(patch.destination, true)
                || !finite(patch.transform)
            {
                return Err(Error::InvalidInput);
            }
            match patch.kind {
                ImagePatchKind::FixedColor(color) => {
                    if color.iter().any(|v| !v.is_finite()) {
                        return Err(Error::InvalidInput);
                    }
                }
                _ => {
                    if !rect(patch.source, true) || !bounded(patch.source, draw.image.size()) {
                        return Err(Error::InvalidInput);
                    }
                    if let ImagePatchKind::AtlasColor { color, .. } = patch.kind
                        && color.iter().any(|v| !v.is_finite())
                    {
                        return Err(Error::InvalidInput);
                    }
                }
            }
        }
    }
    Ok(())
}
fn compose(a: Affine, b: Affine) -> Affine {
    let [a, b0, c, d, x, y] = a.0;
    let [e, f, g, h, u, v] = b.0;
    Affine([
        a * e + b0 * g,
        a * f + b0 * h,
        c * e + d * g,
        c * f + d * h,
        x * e + y * g + u,
        x * f + y * h + v,
    ])
}
fn points(rect: [f32; 4]) -> [[f32; 2]; 4] {
    let [x, y, w, h] = rect;
    [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]
}
fn transform(points: [[f32; 2]; 4], matrix: Affine) -> [[f32; 2]; 4] {
    let [a, b, c, d, tx, ty] = matrix.0;
    let x = wide::f32x4::from(points.map(|p| p[0]));
    let y = wide::f32x4::from(points.map(|p| p[1]));
    let px =
        (x * wide::f32x4::splat(a) + y * wide::f32x4::splat(c) + wide::f32x4::splat(tx)).to_array();
    let py =
        (x * wide::f32x4::splat(b) + y * wide::f32x4::splat(d) + wide::f32x4::splat(ty)).to_array();
    std::array::from_fn(|i| [px[i], py[i]])
}
fn coefficient(sampling: ImageSampling, path: ImageSamplingPath) -> f32 {
    match (sampling, path) {
        (ImageSampling::Fant, ImageSamplingPath::ExplicitShader) => -256.0,
        (ImageSampling::Fant, _) => -32.0,
        (ImageSampling::Nearest, ImageSamplingPath::ExplicitShader) => -128.0,
        (ImageSampling::Linear, ImageSamplingPath::ExplicitShader) => -64.0,
        _ => 0.0,
    }
}
pub(super) fn append(
    draw: &ImageDraw,
    mesh: &mut Mesh,
    dpi: f32,
    path: ImageSamplingPath,
) -> Result<(), Error> {
    if !dpi.is_finite() || dpi <= 0.0 {
        return Err(Error::InvalidInput);
    }
    let count = draw.patches.as_ref().map_or(1, |p| p.len());
    for index in 0..count {
        let patch = draw.patches.as_ref().map(|p| &p[index]);
        let (source, destination, matrix) =
            patch.map_or((draw.source, draw.destination, draw.transform), |p| {
                (
                    p.source,
                    p.destination,
                    compose(p.transform, draw.transform),
                )
            });
        let premultiplied = draw.image.alpha() == ImageAlpha::Premultiplied;
        let scale = if premultiplied { draw.opacity } else { 1.0 };
        let cubic = draw.sampling == ImageSampling::Cubic;
        let mut color = [
            scale,
            if premultiplied { 1.0 } else { 0.0 },
            if draw.ignore_source_alpha {
                -1.0
            } else {
                scale
            },
            if cubic { -draw.opacity } else { draw.opacity },
        ];
        let mut kind = 0.0;
        let mut blend = draw.address[0] as u8 as f32;
        let mut opacity = draw.address[1] as u8 as f32;
        if let Some(patch) = patch {
            // Native patch resolver has independent defaults, including sampled
            // patch V metadata 1. Do not repair it to manufacture source parity.
            blend = 0.0;
            opacity = 1.0;
            match patch.kind {
                ImagePatchKind::Texture => {}
                ImagePatchKind::FixedColor(value) => {
                    let alpha = value[3] * draw.opacity;
                    color = [
                        if premultiplied {
                            value[0] * alpha
                        } else {
                            value[0]
                        },
                        if premultiplied {
                            value[1] * alpha
                        } else {
                            value[1]
                        },
                        if premultiplied {
                            value[2] * alpha
                        } else {
                            value[2]
                        },
                        alpha,
                    ];
                    kind = if premultiplied { 2.0 } else { 1.0 };
                }
                ImagePatchKind::AtlasColor {
                    color: value,
                    blend: mode,
                } => {
                    let alpha = value[3];
                    color = [value[0] * alpha, value[1] * alpha, value[2] * alpha, alpha];
                    kind = if premultiplied { 4.0 } else { 3.0 };
                    blend = mode as u32 as f32;
                    opacity = if cubic { -draw.opacity } else { draw.opacity };
                }
            }
        }
        let mut positions = transform(
            draw.quad.map_or_else(|| points(destination), |q| q.points),
            matrix,
        );
        if draw.snap_to_pixels {
            for p in &mut positions {
                *p = p.map(|v| (v * dpi).round_ties_even() / dpi);
            }
        }
        let [w, h] = draw.image.size().map(|v| v as f32);
        let uv = if kind == 1.0 || kind == 2.0 {
            [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]]
        } else {
            let [x, y, sx, sy] = source;
            let l = x / w;
            let t = y / h;
            let r = (x + sx) / w;
            let b = (y + sy) / h;
            [[l, t], [r, t], [r, b], [l, b]]
        };
        let coefficients = if cubic {
            draw.cubic
        } else {
            [coefficient(draw.sampling, path), 0.5]
        };
        let q = draw.quad.map_or([0.0; 4], |q| q.weights);
        let vertices = std::array::from_fn(|i| VectorVertex {
            position: positions[i],
            color,
            texture_coordinate: uv[i],
            brush_index: kind,
            shape_size: coefficients,
            corner_radius: blend,
            stroke_thickness: opacity,
            shape_type: q[i],
        });
        if vertices.iter().any(|v| {
            bytemuck::cast_slice::<_, f32>(std::slice::from_ref(v))
                .iter()
                .any(|v| !v.is_finite())
        }) {
            return Err(Error::InvalidInput);
        }
        mesh.push_quad(vertices)?;
    }
    Ok(())
}

/// Same-device brush mapping from original GpuTextureBrush. The caller retains
/// the resulting image source through compilation/submission.
#[derive(Clone, Debug)]
pub struct ImageBrush {
    /// Original image draw, including source/destination/transform/opacity.
    pub draw: ImageDraw,
    /// Extend mapping across fill bounds using original GPU address modes.
    pub extend_to_fill_bounds: bool,
}
impl ImageBrush {
    /// Lower the original axis-preserving brush map into one image command.
    /// Nonextended brushes retain their actual destination and complete transform.
    pub fn for_fill(&self, fill: [f32; 4]) -> Result<ImageDraw, Error> {
        let mut admitted = self.draw.clone();
        if self.extend_to_fill_bounds {
            admitted.extended_source = true;
        }
        admitted.validate()?;
        if !rect(fill, true) || self.draw.patches.is_some() || self.draw.quad.is_some() {
            return Err(Error::InvalidInput);
        }
        if !self.extend_to_fill_bounds {
            return Ok(self.draw.clone());
        }
        let [a, b, c, d, x, y] = self.draw.transform.0;
        if b != 0.0 || c != 0.0 || a <= 0.0 || d <= 0.0 {
            return Err(Error::UnsupportedExecution);
        }
        let src = self.draw.source;
        let dst = self.draw.destination;
        let left = (fill[0] - x) / a;
        let top = (fill[1] - y) / d;
        let width = fill[2] / a;
        let height = fill[3] / d;
        let sx = src[2] / dst[2];
        let sy = src[3] / dst[3];
        let mut result = self.draw.clone();
        result.source = [
            src[0] + (left - dst[0]) * sx,
            src[1] + (top - dst[1]) * sy,
            width * sx,
            height * sy,
        ];
        result.destination = fill;
        result.transform = Affine::IDENTITY;
        result.extended_source = true;
        result.validate()?;
        Ok(result)
    }
}
/// Original bounded occupied-tile algorithms. These never replace cubic/mip modes.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TileImageSampling {
    /// Explicit nearest texel.
    Nearest,
    /// Explicit bilinear taps.
    Linear,
    /// Original bounded Fant footprint.
    Fant,
}
/// Explicit zero-origin premultiplied occupied tile, independent of pooled texture extent.
#[derive(Clone, Debug)]
pub struct TileImageDraw {
    /// Actual premultiplied texture generation.
    pub image: ImageSource,
    /// Occupied width/height excluding allocation padding.
    pub occupied: [u32; 2],
    /// Logical output x/y/width/height.
    pub output: [f32; 4],
    /// Output-to-normalized-base-tile affine transform.
    pub output_to_tile: Affine,
    /// Independent per-tap addressing.
    pub address: [ImageAddress; 2],
    /// Explicit occupied-tile reconstruction policy.
    pub sampling: TileImageSampling,
    /// Finite source opacity 0..1.
    pub opacity: f32,
}
impl TileImageDraw {
    /// Emit the exact four original tile-page vertices transactionally.
    pub fn vertices(&self) -> Result<[VectorVertex; 4], Error> {
        let size = self.image.size();
        if self.image.alpha() != ImageAlpha::Premultiplied
            || self.occupied.contains(&0)
            || (0..2).any(|i| self.occupied[i] > size[i] || self.occupied[i] > (1 << 24))
            || !rect(self.output, true)
            || !finite(self.output_to_tile)
            || !self.opacity.is_finite()
            || !(0.0..=1.0).contains(&self.opacity)
        {
            return Err(Error::InvalidInput);
        }
        let positions = points(self.output);
        let uv = transform(positions, self.output_to_tile);
        if positions
            .iter()
            .flatten()
            .chain(uv.iter().flatten())
            .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        let coefficient = match self.sampling {
            TileImageSampling::Nearest => -128.0,
            TileImageSampling::Linear => -64.0,
            TileImageSampling::Fant => -32.0,
        };
        Ok(std::array::from_fn(|i| VectorVertex {
            position: positions[i],
            color: [
                self.occupied[0] as f32,
                self.occupied[1] as f32,
                0.0,
                self.opacity,
            ],
            texture_coordinate: uv[i],
            brush_index: -2.0,
            shape_size: [coefficient, 0.0],
            corner_radius: self.address[0] as u8 as f32,
            stroke_thickness: self.address[1] as u8 as f32,
            shape_type: 0.0,
        }))
    }
}
