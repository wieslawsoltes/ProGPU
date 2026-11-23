//! Original native source capture and independent ideal-unit measurement policies.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeSourceEmPolicy {
    #[default]
    Exact26_6 = 0,
    NearestHalfUp = 1,
    NearestTiesToEven = 2,
    FloatCaptureNearestHalfUp = 3,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeSourceAdvancePolicy {
    #[default]
    Unchanged = 0,
    PhysicalTiesToEven = 1,
    SourceIdealUnits = 2,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeSourceOffsetPolicy {
    #[default]
    Unchanged = 0,
    SourceIdealUnits = 1,
}
/// Original source doubles and explicit physical capture/projection choices.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourcePolicy {
    pub em_size: f64,
    pub pixels_per_dip: f64,
    pub em_policy: NativeSourceEmPolicy,
    pub advance_policy: NativeSourceAdvancePolicy,
    pub offset_policy: NativeSourceOffsetPolicy,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceDeviceSelection {
    pub pixels_per_em_26_6: u32,
    pub logical_units_per_physical_pixel: f32,
}
/// Original sequential line measure. IdealUnits is independent of draw advances.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub enum NativeSourceWidthPolicy {
    #[default]
    DoubleAdvances,
    IdealUnits {
        pixels_per_dip: f64,
    },
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceWidthAccumulator {
    pub width: f64,
    pub ideal_width: i64,
}
fn max(a: f64, b: f64) -> f64 {
    if a < b { b } else { a }
}
fn representable(v: f64) -> bool {
    v.is_finite() && v.abs() <= f64::from(f32::MAX)
}
pub(super) fn ties_even(v: f64) -> f64 {
    if v.abs() >= 4503599627370496.0 {
        return v;
    }
    let mut integral = v.floor();
    let fraction = v - integral;
    if fraction > 0.5 || fraction == 0.5 && integral % 2.0 != 0.0 {
        integral += 1.0;
    }
    if integral == 0.0 {
        0.0f64.copysign(v)
    } else {
        integral
    }
}
impl NativeSourceWidthPolicy {
    pub fn is_valid(self) -> bool {
        match self {
            Self::DoubleAdvances => true,
            Self::IdealUnits { pixels_per_dip } => {
                pixels_per_dip.is_finite() && pixels_per_dip > 0.0
            }
        }
    }
    pub fn select(styles: &[NativeSourcePolicy]) -> Self {
        let Some(first) = styles.first() else {
            return Self::DoubleAdvances;
        };
        if styles.iter().all(|s| {
            s.advance_policy == NativeSourceAdvancePolicy::SourceIdealUnits
                && s.pixels_per_dip == first.pixels_per_dip
        }) {
            Self::IdealUnits {
                pixels_per_dip: first.pixels_per_dip,
            }
        } else {
            Self::DoubleAdvances
        }
    }
}
impl NativeSourceWidthAccumulator {
    /// Append atomically; never round a completed floating line sum.
    pub fn append(
        &mut self,
        advance: f64,
        policy: NativeSourceWidthPolicy,
    ) -> Result<(), FontError> {
        if !policy.is_valid() || !advance.is_finite() {
            return Err(FontError::InvalidArgument);
        }
        let mut c = *self;
        match policy {
            NativeSourceWidthPolicy::DoubleAdvances => c.width += advance,
            NativeSourceWidthPolicy::IdealUnits { pixels_per_dip } => {
                let units = ties_even(advance * 300.0);
                if !units.is_finite()
                    || units < f64::from(i32::MIN)
                    || units > f64::from(i32::MAX)
                    || self.ideal_width < i64::from(i32::MIN)
                    || self.ideal_width > i64::from(i32::MAX)
                {
                    return Err(FontError::InvalidArgument);
                }
                c.ideal_width += i64::from(units as i32);
                if c.ideal_width < i64::from(i32::MIN) || c.ideal_width > i64::from(i32::MAX) {
                    return Err(FontError::InvalidArgument);
                }
                let physical = (c.ideal_width as f64 * (1.0 / 300.0)) * pixels_per_dip;
                if !physical.is_finite() {
                    return Err(FontError::InvalidArgument);
                }
                c.width = ties_even(physical) / pixels_per_dip;
                if c.ideal_width > 0 {
                    c.width = max(c.width, 1.0 / 300.0);
                }
            }
        }
        if !representable(c.width) {
            return Err(FontError::InvalidArgument);
        }
        *self = c;
        Ok(())
    }
    pub fn fits(self, maximum_width: f64, policy: NativeSourceWidthPolicy) -> bool {
        if !policy.is_valid() || !maximum_width.is_finite() || maximum_width < 0.0 {
            return false;
        }
        if maximum_width == 0.0 {
            return true;
        }
        match policy {
            NativeSourceWidthPolicy::DoubleAdvances => self.width <= maximum_width,
            NativeSourceWidthPolicy::IdealUnits { .. } => {
                self.ideal_width as f64 <= (maximum_width * 300.0).floor()
            }
        }
    }
}
impl NativeSourcePolicy {
    /// Resolve only the captured device frame; source em/DPI identity is unchanged.
    pub fn resolve_device(self) -> Result<NativeSourceDeviceSelection, FontError> {
        if !self.em_size.is_finite()
            || self.em_size <= 0.0
            || !self.pixels_per_dip.is_finite()
            || self.pixels_per_dip <= 0.0
        {
            return Err(FontError::InvalidArgument);
        }
        let physical = if self.em_policy == NativeSourceEmPolicy::FloatCaptureNearestHalfUp {
            f64::from(self.em_size as f32 * self.pixels_per_dip as f32)
        } else {
            self.em_size * self.pixels_per_dip
        };
        if !physical.is_finite() || physical <= 0.0 {
            return Err(FontError::InvalidArgument);
        }
        let units = if self.em_policy == NativeSourceEmPolicy::Exact26_6 {
            let units = physical * 64.0;
            if !units.is_finite() || units.trunc() != units {
                return Err(FontError::InvalidArgument);
            }
            units
        } else {
            let mut integral = physical.floor();
            let fraction = physical - integral;
            if fraction > 0.5
                || fraction == 0.5
                    && (self.em_policy != NativeSourceEmPolicy::NearestTiesToEven
                        || integral % 2.0 != 0.0)
            {
                integral += 1.0;
            }
            integral * 64.0
        };
        if !units.is_finite() || units < 1.0 || units > f64::from(i32::MAX) {
            return Err(FontError::InvalidArgument);
        }
        let scale = (1.0 / self.pixels_per_dip) as f32;
        if !scale.is_finite() || scale <= 0.0 || !(scale / 64.0).is_finite() || scale / 64.0 <= 0.0
        {
            return Err(FontError::InvalidArgument);
        }
        Ok(NativeSourceDeviceSelection {
            pixels_per_em_26_6: units as u32,
            logical_units_per_physical_pixel: scale,
        })
    }
    /// Preserve raw identity/flags/offsets; only the explicit physical policy rounds X.
    pub fn project_advance(self, original: ShapingGlyph) -> Result<ShapingGlyph, FontError> {
        let mut g = original;
        if self.advance_policy == NativeSourceAdvancePolicy::PhysicalTiesToEven {
            let value = i64::from(original.advance_x);
            let magnitude = value.abs();
            let mut pixels = magnitude / 64;
            let remainder = magnitude % 64;
            if remainder > 32 || remainder == 32 && pixels & 1 != 0 {
                pixels += 1;
            }
            let rounded = (if value < 0 { -pixels } else { pixels }) * 64;
            g.advance_x = i32::try_from(rounded).map_err(|_| FontError::InvalidArgument)?;
        }
        Ok(g)
    }
    /// Original independent double SIMD lanes, then explicit ideal-unit policies.
    pub fn project_geometry(self, g: ShapingGlyph) -> Result<NativeSourceGlyphMetrics, FontError> {
        let dpi = self.pixels_per_dip;
        if !dpi.is_finite() || dpi <= 0.0 {
            return Err(FontError::InvalidArgument);
        }
        let advance = (wide::f64x2::from([f64::from(g.advance_x), f64::from(g.advance_y)])
            * wide::f64x2::splat(1.0 / 64.0)
            / wide::f64x2::splat(dpi))
        .to_array();
        let offset = (wide::f64x2::from([f64::from(g.offset_x), f64::from(g.offset_y)])
            * wide::f64x2::splat(1.0 / 64.0)
            / wide::f64x2::splat(dpi))
        .to_array();
        let mut m = NativeSourceGlyphMetrics {
            advance_x: advance[0],
            advance_y: advance[1],
            offset_x: offset[0],
            offset_y: offset[1],
        };
        if ![m.advance_x, m.advance_y, m.offset_x, m.offset_y]
            .into_iter()
            .all(representable)
        {
            return Err(FontError::InvalidArgument);
        }
        if self.advance_policy == NativeSourceAdvancePolicy::SourceIdealUnits {
            m.advance_x = ideal(f64::from(g.advance_x), dpi, true)?;
            m.advance_y = -ideal(-f64::from(g.advance_y), dpi, true)?;
        }
        if self.offset_policy == NativeSourceOffsetPolicy::SourceIdealUnits {
            m.offset_x = ideal(f64::from(g.offset_x), dpi, false)?;
            m.offset_y = -ideal(-f64::from(g.offset_y), dpi, false)?;
        }
        Ok(m)
    }
    pub(super) fn wire(self, raw: ShapingGlyph) -> Result<ShapingGlyph, FontError> {
        let mut g = self.project_advance(raw)?;
        g.advance_y = g
            .advance_y
            .checked_neg()
            .ok_or(FontError::InvalidArgument)?;
        g.offset_y = g.offset_y.checked_neg().ok_or(FontError::InvalidArgument)?;
        Ok(g)
    }
}
fn ideal(units: f64, dpi: f64, advance: bool) -> Result<f64, FontError> {
    let capture = dpi as f32;
    if !capture.is_finite() || capture <= 0.0 {
        return Err(FontError::InvalidArgument);
    }
    let dip = ((units / 64.0) / f64::from(capture)) as f32;
    let scaled = f64::from(dip) * 300.0;
    if !scaled.is_finite() {
        return Err(FontError::InvalidArgument);
    }
    let integral = if advance {
        ties_even(scaled)
    } else {
        scaled.trunc()
    };
    if integral < f64::from(i32::MIN) || integral > f64::from(i32::MAX) {
        return Err(FontError::InvalidArgument);
    }
    let ideal = integral as i32;
    let unrounded = f64::from(ideal) * (1.0 / 300.0);
    let physical = unrounded * dpi;
    if !physical.is_finite() {
        return Err(FontError::InvalidArgument);
    }
    let mut projected = ties_even(physical) / dpi;
    if ideal > 0 {
        projected = max(projected, 1.0 / 300.0);
    }
    if !representable(projected) {
        return Err(FontError::InvalidArgument);
    }
    Ok(projected)
}
