use super::*;
use crate::vector::{
    GradientInterpolation, GradientOptions, GradientSpread, GradientStop, LinearGradient,
    RadialGradient, SweepGradient, TwoPointConicalGradient,
};
use wide::f32x4 as V;
/// Immutable original small-inline gradient stop inventory. No sorting,
/// monotonicity repair or premultiplication is performed by the factory.
#[derive(Clone, Debug)]
pub struct ShaderStops(Arc<Stops>);
#[derive(Debug)]
struct Stops {
    small: [GradientStop; 3],
    overflow: Option<Arc<[GradientStop]>>,
    count: usize,
    float_colors: bool,
}
impl ShaderStops {
    /// Exact original byte colors and optional same-length raw offsets.
    pub fn rgba8(colors: &[[u8; 4]], positions: Option<&[f32]>) -> Result<Self, Error> {
        Self::create(colors.len(), positions, false, |i| {
            (V::from(colors[i].map(f32::from)) / V::splat(255.0)).to_array()
        })
    }
    /// Original independently clamped float colors; selected linear color-space
    /// interpolation belongs to the shader options, not to this stop inventory.
    pub fn rgba_f(colors: &[[f32; 4]], positions: Option<&[f32]>) -> Result<Self, Error> {
        Self::create(colors.len(), positions, true, |i| {
            colors[i].map(|v| v.clamp(0.0, 1.0))
        })
    }
    fn create(
        count: usize,
        positions: Option<&[f32]>,
        float_colors: bool,
        mut color: impl FnMut(usize) -> [f32; 4],
    ) -> Result<Self, Error> {
        if count > 1_048_576 {
            return Err(Error::Capacity);
        }
        if positions.is_some_and(|p| p.len() != count) {
            return Err(Error::InvalidInput);
        }
        let mut small = [GradientStop::default(); 3];
        let mut overflow = Vec::new();
        if count > 3 {
            overflow
                .try_reserve_exact(count)
                .map_err(|_| Error::Capacity)?;
        }
        for i in 0..count {
            let stop = GradientStop {
                color: color(i),
                offset: positions.map_or_else(
                    || {
                        if count <= 1 {
                            0.0
                        } else {
                            i as f32 / (count - 1) as f32
                        }
                    },
                    |p| p[i],
                ),
            };
            if count <= 3 {
                small[i] = stop;
            } else {
                overflow.push(stop);
            }
        }
        Ok(Self(Arc::new(Stops {
            small,
            overflow: (count > 3).then(|| overflow.into()),
            count,
            float_colors,
        })))
    }
    /// Exact original inventory, including zero or one stop.
    pub fn values(&self) -> &[GradientStop] {
        self.0
            .overflow
            .as_deref()
            .unwrap_or(&self.0.small[..self.0.count.min(3)])
    }
    /// Whether the original producer supplied floating rather than byte colors.
    pub fn uses_float_colors(&self) -> bool {
        self.0.float_colors
    }
    /// Actual shared immutable stop identity.
    pub fn same_owner(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    /// Original ordered piecewise area average for a degenerate repeating sweep.
    /// Prefix accumulation stays sequential; independent RGBA lanes use SIMD.
    pub fn average_color(&self) -> [f32; 4] {
        let stops = self.values();
        let Some(first) = stops.first() else {
            return [0.0; 4];
        };
        let mut previous = clamp(first.offset, 0.0, 1.0);
        let mut average = V::from(first.color) * V::splat(previous);
        for pair in stops.windows(2) {
            let offset = clamp(pair[1].offset, previous, 1.0);
            average += (V::from(pair[0].color) + V::from(pair[1].color))
                * V::splat(0.5 * (offset - previous));
            previous = offset;
        }
        average += V::from(stops[stops.len() - 1].color) * V::splat(1.0 - previous);
        average.to_array().map(|v| clamp(v, 0.0, 1.0))
    }
    fn copy(&self) -> Result<Vec<GradientStop>, Error> {
        let mut result = Vec::new();
        result
            .try_reserve_exact(self.values().len())
            .map_err(|_| Error::Capacity)?;
        result.extend_from_slice(self.values());
        Ok(result)
    }
}
/// Original gradient spread, selected color-space policy and source local matrix.
#[derive(Clone, Debug)]
pub struct ShaderGradientOptions {
    /// Independent original tile policy; default Clamp.
    pub tile: ShaderTileMode,
    /// Complete original source local matrix; singular gradients become empty.
    pub local_matrix: CanvasMatrix,
    /// Only floating colors consult the source's canonical IsLinear metadata.
    pub color_space: Option<ColorSpace>,
}
impl Default for ShaderGradientOptions {
    fn default() -> Self {
        Self {
            tile: ShaderTileMode::Clamp,
            local_matrix: CanvasMatrix::IDENTITY,
            color_space: None,
        }
    }
}
#[derive(Debug)]
struct Transform {
    source: CanvasMatrix,
    inverse: CanvasMatrix,
}
thread_local! {static TRANSFORMS:std::cell::RefCell<([Option<Arc<Transform>>;8],usize)>=std::cell::RefCell::new((std::array::from_fn(|_|None),0));}
fn transform(matrix: CanvasMatrix) -> Option<Arc<Transform>> {
    if matrix == CanvasMatrix::IDENTITY {
        static ID: OnceLock<Arc<Transform>> = OnceLock::new();
        return Some(
            ID.get_or_init(|| {
                Arc::new(Transform {
                    source: CanvasMatrix::IDENTITY,
                    inverse: CanvasMatrix::IDENTITY,
                })
            })
            .clone(),
        );
    }
    TRANSFORMS.with(|cache| {
        let mut cache = cache.borrow_mut();
        if let Some(found) = cache.0.iter().flatten().find(|v| v.source == matrix) {
            return Some(found.clone());
        }
        let value = Arc::new(Transform {
            source: matrix,
            inverse: matrix.invert_full()?,
        });
        let index = cache.1;
        cache.0[index] = Some(value.clone());
        cache.1 = (index + 1) % 8;
        Some(value)
    })
}
fn options(stops: &ShaderStops, options: &ShaderGradientOptions) -> Option<GradientOptions> {
    let coordinate = transform(options.local_matrix)?;
    Some(GradientOptions {
        transform: coordinate.inverse.matrix4(),
        spread: match options.tile {
            ShaderTileMode::Clamp => GradientSpread::Pad,
            ShaderTileMode::Repeat => GradientSpread::Repeat,
            ShaderTileMode::Mirror => GradientSpread::Reflect,
            ShaderTileMode::Decal => GradientSpread::Decal,
        },
        interpolation: if stops.uses_float_colors()
            && options
                .color_space
                .as_ref()
                .is_some_and(ColorSpace::is_linear)
        {
            GradientInterpolation::ScRgbLinear
        } else {
            GradientInterpolation::SRgbLinear
        },
    })
}
impl Shader {
    /// Original retained linear gradient with unsorted immutable stop values.
    pub fn linear_gradient(
        start: [f32; 2],
        end: [f32; 2],
        stops: ShaderStops,
        source: ShaderGradientOptions,
    ) -> Result<Self, Error> {
        let Some(options) = options(&stops, &source) else {
            return Ok(Self::empty());
        };
        let mut gradient = LinearGradient::new(start, end, stops.copy()?);
        gradient.options = options;
        Ok(Self::material(Brush::new(BrushKind::Linear(gradient))))
    }
    /// Original circular radial gradient; raw radii are not normalized here.
    pub fn radial_gradient(
        center: [f32; 2],
        radius: f32,
        stops: ShaderStops,
        source: ShaderGradientOptions,
    ) -> Result<Self, Error> {
        let Some(options) = options(&stops, &source) else {
            return Ok(Self::empty());
        };
        let mut gradient = RadialGradient::circular(center, radius, stops.copy()?);
        gradient.options = options;
        Ok(Self::material(Brush::new(BrushKind::Radial(gradient))))
    }
    /// Original independent two centers and two radii, including degenerate data.
    pub fn two_point_conical_gradient(
        start: [f32; 2],
        start_radius: f32,
        end: [f32; 2],
        end_radius: f32,
        stops: ShaderStops,
        source: ShaderGradientOptions,
    ) -> Result<Self, Error> {
        let Some(options) = options(&stops, &source) else {
            return Ok(Self::empty());
        };
        let mut gradient =
            TwoPointConicalGradient::new(start, start_radius, end, end_radius, stops.copy()?);
        gradient.options = options;
        Ok(Self::material(Brush::new(BrushKind::TwoPointConical(
            gradient,
        ))))
    }
    /// Original sweep endpoint admission. Equal-angle Decal is empty, Repeat and
    /// Mirror use the exact source stop average, and Clamp retains its gradient.
    pub fn sweep_gradient(
        center: [f32; 2],
        angles: [f32; 2],
        stops: ShaderStops,
        source: ShaderGradientOptions,
    ) -> Result<Self, Error> {
        if angles.iter().any(|v| !v.is_finite())
            || angles[0] > angles[1]
            || (angles[0] == angles[1] && source.tile == ShaderTileMode::Decal)
        {
            return Ok(Self::empty());
        }
        let Some(options) = options(&stops, &source) else {
            return Ok(Self::empty());
        };
        if angles[0] == angles[1]
            && matches!(source.tile, ShaderTileMode::Repeat | ShaderTileMode::Mirror)
        {
            return Ok(Self::material(Brush::new(BrushKind::Solid(
                stops.average_color(),
            ))));
        }
        let mut gradient = SweepGradient::new(center, stops.copy()?);
        gradient.options = options;
        gradient.start_angle = angles[0];
        gradient.end_angle = angles[1];
        Ok(Self::material(Brush::new(BrushKind::Sweep(gradient))))
    }
}
fn clamp(value: f32, minimum: f32, maximum: f32) -> f32 {
    if value < minimum {
        minimum
    } else if value > maximum {
        maximum
    } else {
        value
    }
}
