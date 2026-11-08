//! Original full-payload preflight and ordered gvar delta application.
use super::{Font, PackedDeltas, PackedPoints, TupleHeaders, infer_untouched, tuple_scalar};
use crate::font::{FontError, OutlinePoint, SfntFace, SimpleGlyph, Span};

#[derive(Clone, Copy)]
struct Payload {
    points: Option<Span>,
    x: Span,
    y: Span,
    count: u32,
}
struct Prepared<'a> {
    font: &'a Font,
    headers: TupleHeaders,
    payloads: Vec<Payload>,
}
impl<'a> Prepared<'a> {
    fn new(font: &'a Font, glyph: u16, items: u32) -> Result<Self, FontError> {
        let headers = font.tuple_headers(glyph)?;
        let view = font.glyph_data(glyph)?;
        let data = view.bytes;
        let mut cursor = usize::from(view.serialized_data_offset);
        let mut shared = None;
        let mut shared_count = 0;
        if view.has_shared_point_numbers {
            let points = PackedPoints::parse(&data[cursor..])?;
            if !points.all_points() {
                shared = Some(Span {
                    offset: cursor,
                    length: points.bytes_consumed(),
                });
                shared_count = points.len();
            }
            cursor += points.bytes_consumed();
        }
        let mut payloads = Vec::new();
        payloads
            .try_reserve_exact(headers.headers().len())
            .map_err(|_| FontError::Capacity)?;
        for header in headers.headers() {
            let end = cursor
                .checked_add(usize::from(header.serialized_data_size))
                .filter(|end| *end <= data.len())
                .ok_or(FontError::InvalidGlyph)?;
            let mut points = shared;
            let mut count = if shared.is_some() {
                shared_count
            } else {
                items
            };
            if header.flags & 0x2000 != 0 {
                let private = PackedPoints::parse(&data[cursor..end])?;
                points = if private.all_points() {
                    None
                } else {
                    Some(Span {
                        offset: cursor,
                        length: private.bytes_consumed(),
                    })
                };
                count = if private.all_points() {
                    items
                } else {
                    private.len()
                };
                cursor += private.bytes_consumed();
            }
            let x = PackedDeltas::parse(&data[cursor..end], count)?;
            let x = Span {
                offset: cursor,
                length: x.bytes_consumed(),
            };
            cursor += x.length;
            let y = PackedDeltas::parse(&data[cursor..end], count)?;
            let y = Span {
                offset: cursor,
                length: y.bytes_consumed(),
            };
            payloads.push(Payload {
                points,
                x,
                y,
                count,
            });
            cursor = end;
        }
        // Native packed scratch has item_count slots only for nonempty tuple
        // inventories; a zero-tuple glyph still decodes its shared point set
        // against zero slots. Retain that rejection
        // even though encoded Rust iterators themselves need no point arrays.
        // Complete byte preflight precedes these original decode-capacity gates.
        let capacity = if payloads.is_empty() { 0 } else { items };
        if shared_count > capacity || payloads.iter().any(|p| p.count > capacity) {
            return Err(FontError::Capacity);
        }
        Ok(Self {
            font,
            headers,
            payloads,
        })
    }
    fn visit(
        &self,
        coordinates: &[i16],
        mut consume: impl FnMut(bool, Values<'_>, f32) -> Result<(), FontError>,
    ) -> Result<(), FontError> {
        let data = self.font.glyph_data(self.headers.glyph_index())?.bytes;
        let axes = usize::from(self.font.header().axis_count);
        for (header, payload) in self.headers.headers().iter().zip(&self.payloads) {
            let start = header.region_coordinate_offset as usize;
            let scalar = tuple_scalar(
                &coordinates[..axes],
                &self.headers.coordinates()[start..start + axes * 3],
            );
            // Every tuple, including inactive tuples, was completely preflighted.
            if scalar == 0.0 {
                continue;
            }
            let values = Values {
                points: payload.points.map(|p| {
                    PackedPoints::parse(p.get(data))
                        .expect("validated immutable points")
                        .iter()
                }),
                x: PackedDeltas::parse(payload.x.get(data), payload.count)
                    .expect("validated immutable tuple")
                    .iter(),
                y: PackedDeltas::parse(payload.y.get(data), payload.count)
                    .expect("validated immutable tuple")
                    .iter(),
                index: 0,
            };
            consume(payload.points.is_none(), values, scalar)?;
        }
        Ok(())
    }
}
struct Values<'a> {
    points: Option<super::PointIter<'a>>,
    x: super::DeltaIter<'a>,
    y: super::DeltaIter<'a>,
    index: u32,
}
impl Iterator for Values<'_> {
    type Item = (u32, i16, i16);
    fn next(&mut self) -> Option<Self::Item> {
        let x = self.x.next()?;
        let y = self.y.next()?;
        let index = match &mut self.points {
            Some(points) => points.next()?,
            None => self.index,
        };
        self.index += 1;
        Some((index, x, y))
    }
}
fn filled<T: Clone>(count: usize, value: T) -> Result<Vec<T>, FontError> {
    let mut out = Vec::new();
    out.try_reserve_exact(count)
        .map_err(|_| FontError::Capacity)?;
    out.resize(count, value);
    Ok(out)
}
fn contours(ends: &[u16], count: usize) -> Result<(), FontError> {
    let mut start = 0;
    for &end in ends {
        let end = usize::from(end);
        if end < start || end >= count {
            return Err(FontError::InvalidGlyph);
        }
        start = end + 1;
    }
    if (count == 0 && !ends.is_empty()) || (count != 0 && (ends.is_empty() || start != count)) {
        return Err(FontError::InvalidGlyph);
    }
    Ok(())
}
fn check_coordinates(font: &Font, coordinates: &[i16]) -> Result<(), FontError> {
    if coordinates.len() < usize::from(font.header().axis_count) {
        Err(FontError::Capacity)
    } else {
        Ok(())
    }
}
/// Apply original simple-glyph variation data to caller-supplied topology.
/// All tuple payloads, including inactive/later tuples, validate before output
/// publication. Duplicate sparse deltas accumulate before scaling. Interpolation
/// uses points already varied by preceding tuples, exactly as the native route.
/// Missing gvar retains unchanged float-converted points. No hinting is performed.
pub fn apply_simple(
    face: &SfntFace,
    glyph: u16,
    coordinates: &[i16],
    ends: &[u16],
    original: &[OutlinePoint],
) -> Result<Vec<[f32; 2]>, FontError> {
    let items = u32::try_from(original.len())
        .ok()
        .and_then(|n| n.checked_add(4))
        .ok_or(FontError::InvalidGlyph)?;
    contours(ends, original.len())?;
    let font = face.gvar()?;
    let prepared = if let Some(font) = &font {
        check_coordinates(font, coordinates)?;
        Some(Prepared::new(font, glyph, items)?)
    } else {
        None
    };
    let mut points = filled(original.len(), [0.0; 2])?;
    for (out, p) in points.iter_mut().zip(original) {
        *out = [p.x as f32, p.y as f32];
    }
    let Some(prepared) = prepared else {
        return Ok(points);
    };
    if prepared.payloads.is_empty() {
        return Ok(points);
    }
    let mut x = filled(points.len(), 0.0f32)?;
    let mut y = filled(points.len(), 0.0f32)?;
    let mut touched = filled(points.len(), 0u8)?;
    prepared.visit(coordinates, |all, values, scalar| {
        x.fill(0.0);
        y.fill(0.0);
        touched.fill(0);
        for (point, dx, dy) in values {
            let p = point as usize;
            if p >= points.len() {
                continue;
            }
            if all {
                x[p] = f32::from(dx);
                y[p] = f32::from(dy);
            } else {
                x[p] += f32::from(dx);
                y[p] += f32::from(dy);
            }
            touched[p] = 1;
        }
        if !all {
            infer_untouched(&points, ends, &mut x, &mut y, &touched)?;
        }
        for (i, p) in points.iter_mut().enumerate() {
            p[0] += x[i] * scalar;
            p[1] += y[i] * scalar;
        }
        Ok(())
    })?;
    Ok(points)
}
impl SimpleGlyph {
    /// Vary this owned original topology using the native gvar application route.
    /// Returned numeric positions are independent of the source owner's lifetime.
    pub fn varied_points(&self, coordinates: &[i16]) -> Result<Vec<[f32; 2]>, FontError> {
        apply_simple(
            self.source(),
            self.glyph_index(),
            coordinates,
            self.contour_end_points(),
            self.points(),
        )
    }
}
/// Original ordered component translation deltas. Duplicate component references
/// scale and accumulate separately; components are never interpolated. The final
/// four phantom slots and out-of-range component indices do not move geometry.
pub fn component_offsets(
    face: &SfntFace,
    glyph: u16,
    coordinates: &[i16],
    components: u32,
) -> Result<Vec<[f32; 2]>, FontError> {
    let font = face.gvar()?;
    if let Some(font) = &font {
        check_coordinates(font, coordinates)?;
    }
    let items = components
        .checked_add(4)
        .ok_or(FontError::InvalidArgument)?;
    let prepared = font
        .as_ref()
        .map(|font| Prepared::new(font, glyph, items))
        .transpose()?;
    let mut offsets = filled(components as usize, [0.0; 2])?;
    if let Some(prepared) = prepared {
        prepared.visit(coordinates, |_, values, scalar| {
            for (component, x, y) in values {
                if let Some(out) = offsets.get_mut(component as usize) {
                    out[0] += f32::from(x) * scalar;
                    out[1] += f32::from(y) * scalar;
                }
            }
            Ok(())
        })?;
    }
    Ok(offsets)
}
/// Original raw horizontal phantom deltas (left/right), before HVAR precedence.
/// Item counts below four preserve the original unconditional zero result.
pub fn horizontal_phantom_deltas(
    face: &SfntFace,
    glyph: u16,
    coordinates: &[i16],
    items: u32,
) -> Result<[f32; 2], FontError> {
    if items < 4 {
        return Ok([0.0; 2]);
    }
    let Some(font) = face.gvar()? else {
        return Ok([0.0; 2]);
    };
    check_coordinates(&font, coordinates)?;
    let prepared = Prepared::new(&font, glyph, items)?;
    let mut result = [0.0; 2];
    prepared.visit(coordinates, |_, values, scalar| {
        for (point, x, _) in values {
            if point == items - 4 {
                result[0] += f32::from(x) * scalar;
            } else if point == items - 3 {
                result[1] += f32::from(x) * scalar;
            }
        }
        Ok(())
    })?;
    Ok(result)
}
