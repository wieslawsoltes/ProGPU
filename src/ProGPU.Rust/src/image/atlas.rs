//! Original SKAtlasLayout and anchored rotation/scale source transforms.
use super::*;
use wide::f32x4 as V;
/// Original four-coefficient sprite transform, with exact independent source
/// trigonometric constructors and no inferred general affine decomposition.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct ImageAtlasTransform {
    /// Scale multiplied by source cosine.
    pub scaled_cosine: f32,
    /// Scale multiplied by source sine.
    pub scaled_sine: f32,
    /// Actual independent translation.
    pub translation: [f32; 2],
}
impl ImageAtlasTransform {
    /// Original identity (the Default/EMPTY value remains all zero).
    pub const IDENTITY: Self = Self {
        scaled_cosine: 1.0,
        scaled_sine: 0.0,
        translation: [0.0; 2],
    };
    /// Original zero transform.
    pub const EMPTY: Self = Self {
        scaled_cosine: 0.0,
        scaled_sine: 0.0,
        translation: [0.0; 2],
    };
    /// Original raw four-value constructor.
    pub const fn new(scaled_cosine: f32, scaled_sine: f32, translation: [f32; 2]) -> Self {
        Self {
            scaled_cosine,
            scaled_sine,
            translation,
        }
    }
    /// Original anchored constructor uses double trig then float scaling.
    pub fn create(scale: f32, radians: f32, translation: [f32; 2], anchor: [f32; 2]) -> Self {
        let sin = f64::from(radians).sin() as f32 * scale;
        let cos = f64::from(radians).cos() as f32 * scale;
        Self::new(
            cos,
            sin,
            [
                translation[0] - cos * anchor[0] + sin * anchor[1],
                translation[1] - sin * anchor[0] - cos * anchor[1],
            ],
        )
    }
    /// Original degrees-to-radians float expression before double trig.
    pub fn create_degrees(
        scale: f32,
        degrees: f32,
        translation: [f32; 2],
        anchor: [f32; 2],
    ) -> Self {
        Self::create(
            scale,
            degrees * (std::f32::consts::PI / 180.0),
            translation,
            anchor,
        )
    }
    /// Original independent translation.
    pub fn translation(value: [f32; 2]) -> Self {
        Self {
            translation: value,
            ..Self::IDENTITY
        }
    }
    /// Original uniform scale.
    pub fn scale(value: f32) -> Self {
        Self::new(value, 0.0, [0.0; 2])
    }
    /// Original anchored rotation.
    pub fn rotation(radians: f32, anchor: [f32; 2]) -> Self {
        Self::create(1.0, radians, [0.0; 2], anchor)
    }
    /// Original anchored degree rotation.
    pub fn rotation_degrees(degrees: f32, anchor: [f32; 2]) -> Self {
        Self::create_degrees(1.0, degrees, [0.0; 2], anchor)
    }
    /// Exact Matrix3x2 source placement; sprite origin is zero, independent of UVs.
    pub fn affine(self) -> Affine {
        Affine([
            self.scaled_cosine,
            self.scaled_sine,
            -self.scaled_sine,
            self.scaled_cosine,
            self.translation[0],
            self.translation[1],
        ])
    }
    /// Transform one original point without decomposing the retained coefficients.
    pub fn point(self, point: [f32; 2]) -> [f32; 2] {
        let p = (V::from([self.scaled_cosine, self.scaled_sine, 0.0, 0.0]) * V::splat(point[0])
            + V::from([-self.scaled_sine, self.scaled_cosine, 0.0, 0.0]) * V::splat(point[1])
            + V::from([self.translation[0], self.translation[1], 0.0, 0.0]))
        .to_array();
        [p[0], p[1]]
    }
    /// Original complete SKMatrix representation.
    pub fn matrix(self) -> crate::vector::EffectMatrix {
        crate::vector::EffectMatrix([
            self.scaled_cosine,
            -self.scaled_sine,
            self.translation[0],
            self.scaled_sine,
            self.scaled_cosine,
            self.translation[1],
            0.0,
            0.0,
            1.0,
        ])
    }
}
impl ImagePatchBatch {
    /// Original atlas layout. Sprites use left/top/right/bottom source coordinates;
    /// matching transforms/colors retain original slots when empty sprites skip.
    /// Explicit cull bounds replace only command metadata, never crop a sprite.
    pub fn atlas(
        sprites: &[[f32; 4]],
        transforms: &[ImageAtlasTransform],
        colors: Option<&[[u8; 4]]>,
        blend: ImageColorBlend,
        filter: Option<&ColorFilter>,
        cull: Option<[f32; 4]>,
    ) -> Result<Self, Error> {
        if transforms.len() != sprites.len() || colors.is_some_and(|c| c.len() != sprites.len()) {
            return Err(Error::InvalidInput);
        }
        if sprites.len() > 1_048_576 {
            return Err(Error::Capacity);
        }
        let count = sprites
            .iter()
            .filter(|s| s[2] - s[0] > 0.0 && s[3] - s[1] > 0.0)
            .count();
        if count > 65_536 {
            return Err(Error::Capacity);
        }
        if count == 0 {
            return Ok(Self::new_atlas(Vec::new(), cull.unwrap_or([0.0; 4])));
        }
        let mut patches = Vec::new();
        patches
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        let mut bounds: Option<[f32; 4]> = None;
        for (index, sprite) in sprites.iter().enumerate() {
            let w = sprite[2] - sprite[0];
            let h = sprite[3] - sprite[1];
            if w <= 0.0 || h <= 0.0 {
                continue;
            }
            // The original producer cannot publish a finite patch inventory from
            // nonfinite extents. Reject before indexing its sized candidate.
            if !w.is_finite() || !h.is_finite() {
                return Err(Error::InvalidInput);
            }
            let transform = transforms[index].affine();
            let kind = if let Some(colors) = colors {
                let color = colors[index];
                let color = filter.map_or(color, |f| f.apply_color(color));
                ImagePatchKind::AtlasColor {
                    color: super::patch_batch::color_components(color),
                    blend,
                }
            } else {
                ImagePatchKind::Texture
            };
            patches.push(ImagePatch {
                source: [sprite[0], sprite[1], w, h],
                destination: [0.0, 0.0, w, h],
                transform,
                kind,
            });
            // Independent four corner lanes; the subsequent bounds update keeps
            // original corner and sprite order, including NaN propagation.
            let [a, b, c, d, tx, ty] = transform.0;
            let x = V::from([0.0, w, w, 0.0]);
            let y = V::from([0.0, 0.0, h, h]);
            let px = (x * V::splat(a) + y * V::splat(c) + V::splat(tx)).to_array();
            let py = (x * V::splat(b) + y * V::splat(d) + V::splat(ty)).to_array();
            for i in 0..4 {
                bounds = Some(match bounds {
                    None => [px[i], py[i], px[i], py[i]],
                    Some(v) => [
                        min(v[0], px[i]),
                        min(v[1], py[i]),
                        max(v[2], px[i]),
                        max(v[3], py[i]),
                    ],
                });
            }
        }
        Ok(Self::new_atlas(
            patches,
            cull.or(bounds).unwrap_or([0.0; 4]),
        ))
    }
}
fn min(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else {
        a.min(b)
    }
}
fn max(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else {
        a.max(b)
    }
}
