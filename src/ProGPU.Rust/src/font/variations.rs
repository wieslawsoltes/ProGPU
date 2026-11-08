//! Original fvar records and the independent fixed/design avar contracts.
//! Ported from `progpu_native_font_variations.cpp`; no font execution occurs.

use super::{FontError, SfntFace, Tag, bytes};

/// Original axis descriptor, retaining exact 16.16 endpoints and raw flags.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VariationAxis {
    /// Original axis tag.
    pub tag: Tag,
    /// Minimum design coordinate in signed 16.16 units.
    pub minimum_fixed: i32,
    /// Default design coordinate in signed 16.16 units.
    pub default_fixed: i32,
    /// Maximum design coordinate in signed 16.16 units.
    pub maximum_fixed: i32,
    /// Original flags, including unknown bits.
    pub flags: u16,
    /// Original axis name identifier.
    pub name_id: u16,
}

impl VariationAxis {
    /// Minimum converted with the original single-precision arithmetic.
    pub fn minimum(self) -> f32 {
        self.minimum_fixed as f32 / 65536.0
    }
    /// Default converted with the original single-precision arithmetic.
    pub fn default_value(self) -> f32 {
        self.default_fixed as f32 / 65536.0
    }
    /// Maximum converted with the original single-precision arithmetic.
    pub fn maximum(self) -> f32 {
        self.maximum_fixed as f32 / 65536.0
    }
    /// Whether the original hidden flag is set.
    pub fn hidden(self) -> bool {
        self.flags & 1 != 0
    }
    fn validate(self) -> Result<Self, FontError> {
        if self.minimum_fixed > self.default_fixed || self.default_fixed > self.maximum_fixed {
            Err(FontError::InvalidFace)
        } else {
            Ok(self)
        }
    }
}

/// Allocation-free iterator over the complete original fvar record inventory.
///
/// The full byte extent is validated before construction. As with native bulk
/// decoding, endpoint ordering is retained here; individual axis queries and
/// normalization additionally reject invalid ordering. The borrow retains the
/// immutable face that owns these records.
#[derive(Clone, Debug)]
pub struct VariationAxes<'a> {
    data: &'a [u8],
    offset: usize,
    stride: usize,
    remaining: u16,
}
impl Iterator for VariationAxes<'_> {
    type Item = VariationAxis;
    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining == 0 {
            return None;
        }
        let axis = read_axis(self.data, self.offset);
        self.offset += self.stride;
        self.remaining -= 1;
        Some(axis)
    }
    fn size_hint(&self) -> (usize, Option<usize>) {
        let count = usize::from(self.remaining);
        (count, Some(count))
    }
}
impl ExactSizeIterator for VariationAxes<'_> {}
impl std::iter::FusedIterator for VariationAxes<'_> {}

impl SfntFace {
    /// Borrow the complete original axis inventory. Missing fvar, zero axes and
    /// record sizes below 20 retain the native empty-inventory result.
    pub fn variation_axes(&self) -> Result<VariationAxes<'_>, FontError> {
        let Some(table) = self.table(Tag::new(*b"fvar")) else {
            return Ok(VariationAxes {
                data: &[],
                offset: 0,
                stride: 0,
                remaining: 0,
            });
        };
        let data = table.bytes;
        bytes::bytes(data, 0, 16).ok_or(FontError::InvalidFace)?;
        let offset = usize::from(bytes::u16(data, 4).unwrap());
        let count = bytes::u16(data, 8).unwrap();
        let stride = usize::from(bytes::u16(data, 10).unwrap());
        if count == 0 || stride < 20 {
            return Ok(VariationAxes {
                data,
                offset: 0,
                stride: 0,
                remaining: 0,
            });
        }
        bytes::bytes(data, offset, usize::from(count) * stride).ok_or(FontError::InvalidFace)?;
        Ok(VariationAxes {
            data,
            offset,
            stride,
            remaining: count,
        })
    }

    /// Original axis count after validating the complete declared record extent.
    pub fn variation_axis_count(&self) -> Result<u16, FontError> {
        Ok(self.variation_axes()?.remaining)
    }

    /// Query one axis and reject reversed minimum/default/maximum endpoints.
    pub fn variation_axis(&self, index: u16) -> Result<VariationAxis, FontError> {
        let axes = self.variation_axes()?;
        if index >= axes.remaining {
            return Err(FontError::InvalidArgument);
        }
        read_axis(axes.data, axes.offset + usize::from(index) * axes.stride).validate()
    }

    /// Normalize a signed 16.16 coordinate to F2DOT14 using the original legacy
    /// avar route. Clamps before normalization; initial ties round away from zero
    /// and mapped ties round to even. A mismatched avar axis count is ignored.
    /// This does not imply a font raster policy or execute variable outlines.
    pub fn normalize_variation_coordinate(
        &self,
        index: u16,
        user_fixed: i32,
    ) -> Result<i16, FontError> {
        let count = self.variation_axis_count()?;
        let axis = self.variation_axis(index)?;
        let value = user_fixed.clamp(axis.minimum_fixed, axis.maximum_fixed);
        let normalized = if value < axis.default_fixed {
            -((i64::from(axis.default_fixed) - i64::from(value)) as f64)
                / (i64::from(axis.default_fixed) - i64::from(axis.minimum_fixed)) as f64
        } else if value > axis.default_fixed {
            (i64::from(value) - i64::from(axis.default_fixed)) as f64
                / (i64::from(axis.maximum_fixed) - i64::from(axis.default_fixed)) as f64
        } else {
            0.0
        };
        self.apply_avar(count, index, normalized_coordinate(normalized), false)
    }

    /// Normalize a finite floating design coordinate against exact original
    /// fixed endpoints, without first quantizing it to 16.16. Strict avar v1
    /// validation covers every axis, including maps after the selected axis.
    pub fn normalize_variation_design_coordinate(
        &self,
        index: u16,
        user: f32,
    ) -> Result<i16, FontError> {
        if !user.is_finite() {
            return Err(FontError::InvalidArgument);
        }
        let count = self.variation_axis_count()?;
        let axis = self.variation_axis(index)?;
        let minimum = f64::from(axis.minimum_fixed) / 65536.0;
        let normal = f64::from(axis.default_fixed) / 65536.0;
        let maximum = f64::from(axis.maximum_fixed) / 65536.0;
        let value = f64::from(user).clamp(minimum, maximum);
        let normalized = if value < normal {
            (value - normal) / (normal - minimum)
        } else if value > normal {
            (value - normal) / (maximum - normal)
        } else {
            0.0
        };
        self.apply_avar(count, index, normalized_coordinate(normalized), true)
    }

    fn apply_avar(
        &self,
        count: u16,
        index: u16,
        coordinate: i16,
        strict: bool,
    ) -> Result<i16, FontError> {
        let Some(table) = self.table(Tag::new(*b"avar")) else {
            return Ok(coordinate);
        };
        let data = table.bytes;
        bytes::bytes(data, 0, 8).ok_or(FontError::InvalidFace)?;
        if strict && bytes::u32(data, 0) != Some(0x00010000) {
            return Err(FontError::InvalidFace);
        }
        let map_axes = bytes::u16(data, 6).unwrap();
        if map_axes != count {
            return if strict {
                Err(FontError::InvalidFace)
            } else {
                Ok(coordinate)
            };
        }
        let mut offset = 8;
        let mut mapped = coordinate;
        for axis in 0..map_axes {
            let pairs = usize::from(bytes::u16(data, offset).ok_or(FontError::InvalidFace)?);
            offset += 2;
            let map = bytes::bytes(data, offset, pairs * 4).ok_or(FontError::InvalidFace)?;
            if strict {
                validate_map(map)?;
            }
            if axis == index && pairs >= 2 {
                let mut previous_from = bytes::i16(map, 0).unwrap();
                let mut previous_to = bytes::i16(map, 2).unwrap();
                mapped = previous_to;
                for pair in 0..pairs {
                    let from = bytes::i16(map, pair * 4).unwrap();
                    let to = bytes::i16(map, pair * 4 + 2).unwrap();
                    if coordinate <= from {
                        mapped = if coordinate == from || pair == 0 {
                            to
                        } else {
                            let denominator = f32::from(from) - f32::from(previous_from);
                            if denominator == 0.0 {
                                return Err(FontError::InvalidFace);
                            }
                            let ratio =
                                (f32::from(coordinate) - f32::from(previous_from)) / denominator;
                            round_mapped(
                                f32::from(previous_to)
                                    + ratio * (f32::from(to) - f32::from(previous_to)),
                            )
                        };
                        break;
                    }
                    previous_from = from;
                    previous_to = to;
                    mapped = to;
                }
            }
            offset += pairs * 4;
        }
        Ok(mapped)
    }
}

fn read_axis(data: &[u8], offset: usize) -> VariationAxis {
    VariationAxis {
        tag: Tag(bytes::u32(data, offset).unwrap()),
        minimum_fixed: bytes::u32(data, offset + 4).unwrap() as i32,
        default_fixed: bytes::u32(data, offset + 8).unwrap() as i32,
        maximum_fixed: bytes::u32(data, offset + 12).unwrap() as i32,
        flags: bytes::u16(data, offset + 16).unwrap(),
        name_id: bytes::u16(data, offset + 18).unwrap(),
    }
}
fn normalized_coordinate(value: f64) -> i16 {
    (value * 16384.0).round().clamp(-16384.0, 16384.0) as i16
}
fn round_mapped(value: f32) -> i16 {
    let lower = value.floor();
    let fraction = value - lower;
    let rounded = if fraction < 0.5 {
        lower
    } else if fraction > 0.5 {
        lower + 1.0
    } else if lower % 2.0 == 0.0 {
        lower
    } else {
        lower + 1.0
    };
    rounded.clamp(-16384.0, 16384.0) as i16
}
fn validate_map(map: &[u8]) -> Result<(), FontError> {
    let count = map.len() / 4;
    if count < 3
        || bytes::i16(map, 0) != Some(-16384)
        || bytes::i16(map, 2) != Some(-16384)
        || bytes::i16(map, (count - 1) * 4) != Some(16384)
        || bytes::i16(map, (count - 1) * 4 + 2) != Some(16384)
    {
        return Err(FontError::InvalidFace);
    }
    let mut previous = -16384;
    let mut has_zero = false;
    for pair in 0..count {
        let from = bytes::i16(map, pair * 4).unwrap();
        let to = bytes::i16(map, pair * 4 + 2).unwrap();
        if (pair != 0 && from <= previous)
            || !(-16384..=16384).contains(&from)
            || !(-16384..=16384).contains(&to)
            || (from == 0 && to != 0)
        {
            return Err(FontError::InvalidFace);
        }
        has_zero |= from == 0;
        previous = from;
    }
    if has_zero {
        Ok(())
    } else {
        Err(FontError::InvalidFace)
    }
}
