//! Two-pass canonical native SVG path writer; no intermediate tessellation.
use super::{FontError, NativeSvgPathRequirements, Result, number};
use crate::{
    path::{FillRule, Segment},
    vector::{ArcSegment, SweepDirection},
};
use std::f32::consts::PI;
fn skip(text: &[u8], index: &mut usize) {
    while text
        .get(*index)
        .is_some_and(|v| matches!(v, b',' | b' ' | b'\t' | b'\r' | b'\n' | 0x0c))
    {
        *index += 1;
    }
}
fn read(text: &[u8], index: &mut usize) -> Result<f32> {
    skip(text, index);
    number::parse(text, index).ok_or(FontError::InvalidGlyph)
}
fn point(text: &[u8], index: &mut usize) -> Result<[f32; 2]> {
    Ok([read(text, index)?, read(text, index)?])
}
fn add(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] + b[0], a[1] + b[1]]
}
fn reflected(current: [f32; 2], control: [f32; 2]) -> [f32; 2] {
    [current[0] * 2.0 - control[0], current[1] * 2.0 - control[1]]
}
struct Writer<'a> {
    output: Option<&'a mut [Segment]>,
    count: usize,
    bounds: [f32; 4],
    fill: FillRule,
}
impl Writer<'_> {
    fn include(&mut self, point: [f32; 2]) -> Result<()> {
        if !point.iter().all(|v| v.is_finite()) {
            return super::fail();
        }
        // Ordered source std::min/max retain the existing value on equality.
        for axis in 0..2 {
            if point[axis] < self.bounds[axis] {
                self.bounds[axis] = point[axis];
            }
            if self.bounds[axis + 2] < point[axis] {
                self.bounds[axis + 2] = point[axis];
            }
        }
        Ok(())
    }
    fn append(&mut self, segment: Segment) -> Result<()> {
        let next = self.count.checked_add(1).ok_or(FontError::Capacity)?;
        if let Some(output) = &mut self.output {
            *output.get_mut(self.count).ok_or(FontError::Capacity)? = segment;
        }
        self.count = next;
        Ok(())
    }
    fn line(&mut self, start: [f32; 2], end: [f32; 2]) -> Result<()> {
        self.append(Segment::line(start, end))?;
        self.include(start)?;
        self.include(end)
    }
    fn quad(&mut self, start: [f32; 2], control: [f32; 2], end: [f32; 2]) -> Result<()> {
        self.append(Segment::quadratic([start, control, end]))?;
        self.include(start)?;
        self.include(control)?;
        self.include(end)
    }
    fn cubic(&mut self, start: [f32; 2], a: [f32; 2], b: [f32; 2], end: [f32; 2]) -> Result<()> {
        self.append(Segment::cubic([start, a, b, end]))?;
        self.include(start)?;
        self.include(a)?;
        self.include(b)?;
        self.include(end)
    }
    fn arc(&mut self, start: [f32; 2], arc: ArcSegment) -> Result<()> {
        let Some(center) = arc.center(start) else {
            return if start != arc.end {
                self.line(start, arc.end)
            } else {
                self.include(arc.end)
            };
        };
        self.append(Segment {
            points: [start, arc.end, center.center, center.radii],
            kind: 3,
            parameters: [
                center.theta.to_bits(),
                center.delta.to_bits(),
                (arc.rotation * PI / 180.0).to_bits(),
            ],
        })?;
        self.include(start)?;
        self.include(arc.end)?;
        let phi = arc.rotation * PI / 180.0;
        let cosine = phi.cos();
        let sine = phi.sin();
        let x = (-center.radii[1] * sine).atan2(center.radii[0] * cosine);
        let y = (center.radii[1] * cosine).atan2(center.radii[0] * sine);
        for theta in [x, x + PI, y, y + PI] {
            if within_sweep(theta, center.theta, center.delta) {
                self.include(center.evaluate(arc.rotation, theta))?;
            }
        }
        Ok(())
    }
}
fn within_sweep(theta: f32, start: f32, delta: f32) -> bool {
    const EPSILON: f32 = 0.00001;
    const TWO_PI: f32 = 2.0 * PI;
    if !theta.is_finite() || !start.is_finite() || !delta.is_finite() || delta.abs() <= EPSILON {
        return false;
    }
    if delta.abs() >= TWO_PI - EPSILON {
        return true;
    }
    let mut distance = if delta > 0.0 {
        theta - start
    } else {
        start - theta
    } % TWO_PI;
    if distance < 0.0 {
        distance += TWO_PI;
    }
    distance <= delta.abs() + EPSILON
}
struct Figure {
    current: [f32; 2],
    start: [f32; 2],
    exists: bool,
    segments: bool,
}
impl Figure {
    fn begin(&mut self) {
        if !self.exists {
            self.start = self.current;
            self.exists = true;
        }
    }
    fn close(&mut self, writer: &mut Writer<'_>) -> Result<()> {
        if self.exists && self.segments && self.current != self.start {
            writer.line(self.current, self.start)?;
        }
        if self.exists {
            self.current = self.start;
        }
        self.segments = false;
        Ok(())
    }
}
fn parse(text: &[u8], output: Option<&mut [Segment]>) -> Result<NativeSvgPathRequirements> {
    let mut writer = Writer {
        output,
        count: 0,
        bounds: [f32::MAX, f32::MAX, f32::MIN, f32::MIN],
        fill: FillRule::NonZero,
    };
    let mut figure = Figure {
        current: [0.0; 2],
        start: [0.0; 2],
        exists: false,
        segments: false,
    };
    let mut last = 0u8;
    let mut last_control = [0.0; 2];
    let mut index = 0;
    skip(text, &mut index);
    while index < text.len() {
        let previous = last.to_ascii_uppercase();
        let command = if text[index].is_ascii_alphabetic() {
            let value = text[index];
            index += 1;
            last = value;
            value
        } else if last != 0 {
            last
        } else {
            return super::fail();
        };
        let relative = command.is_ascii_lowercase();
        let at = figure.current;
        let read_point = |index: &mut usize| -> Result<[f32; 2]> {
            let p = point(text, index)?;
            Ok(if relative { add(p, at) } else { p })
        };
        match command.to_ascii_uppercase() {
            b'F' => {
                writer.fill = if read(text, &mut index)? == 0.0 {
                    FillRule::EvenOdd
                } else {
                    FillRule::NonZero
                };
                last = 0;
            }
            b'M' => {
                let p = read_point(&mut index)?;
                figure.close(&mut writer)?;
                figure.current = p;
                figure.start = p;
                figure.exists = true;
                writer.include(p)?;
                last = if relative { b'l' } else { b'L' };
            }
            b'L' => {
                let end = read_point(&mut index)?;
                figure.begin();
                writer.line(at, end)?;
                figure.current = end;
                figure.segments = true;
            }
            b'H' | b'V' => {
                let axis = usize::from(command.to_ascii_uppercase() == b'V');
                let mut end = at;
                end[axis] = read(text, &mut index)?;
                if relative {
                    end[axis] += at[axis];
                }
                figure.begin();
                writer.line(at, end)?;
                figure.current = end;
                figure.segments = true;
            }
            b'Q' => {
                let control = read_point(&mut index)?;
                let end = read_point(&mut index)?;
                figure.begin();
                writer.quad(at, control, end)?;
                last_control = control;
                figure.current = end;
                figure.segments = true;
            }
            b'T' => {
                let end = read_point(&mut index)?;
                let control = if matches!(previous, b'Q' | b'T') {
                    reflected(at, last_control)
                } else {
                    at
                };
                figure.begin();
                writer.quad(at, control, end)?;
                last_control = control;
                figure.current = end;
                figure.segments = true;
            }
            b'C' => {
                let a = read_point(&mut index)?;
                let b = read_point(&mut index)?;
                let end = read_point(&mut index)?;
                figure.begin();
                writer.cubic(at, a, b, end)?;
                last_control = b;
                figure.current = end;
                figure.segments = true;
            }
            b'S' => {
                let b = read_point(&mut index)?;
                let end = read_point(&mut index)?;
                let a = if matches!(previous, b'C' | b'S') {
                    reflected(at, last_control)
                } else {
                    at
                };
                figure.begin();
                writer.cubic(at, a, b, end)?;
                last_control = b;
                figure.current = end;
                figure.segments = true;
            }
            b'A' => {
                let size = [read(text, &mut index)?, read(text, &mut index)?];
                let rotation = read(text, &mut index)?;
                let large = read(text, &mut index)? != 0.0;
                let clockwise = read(text, &mut index)? != 0.0;
                let end = read_point(&mut index)?;
                figure.begin();
                writer.arc(
                    at,
                    ArcSegment {
                        end,
                        size,
                        rotation,
                        large,
                        sweep: if clockwise {
                            SweepDirection::Clockwise
                        } else {
                            SweepDirection::Counterclockwise
                        },
                    },
                )?;
                figure.current = end;
                figure.segments = true;
            }
            b'Z' => {
                figure.close(&mut writer)?;
                last = 0;
            }
            _ => return super::fail(),
        }
        skip(text, &mut index);
    }
    figure.close(&mut writer)?;
    Ok(NativeSvgPathRequirements {
        segment_count: writer.count,
        bounds: if writer.count == 0 {
            [0.0; 4]
        } else {
            writer.bounds
        },
        fill_rule: writer.fill,
    })
}
/// Count the original auto-closed SVG path inventory without allocating output.
pub fn native_svg_path_requirements(path: &[u8]) -> Result<NativeSvgPathRequirements> {
    parse(path, None)
}
/// Validate/count the complete source before any caller write. The requirements
/// output is populated on Capacity, and reset on malformed input.
pub fn native_decode_svg_path(
    path: &[u8],
    output: &mut [Segment],
    required: &mut NativeSvgPathRequirements,
) -> Result<()> {
    *required = NativeSvgPathRequirements::default();
    let counts = parse(path, None)?;
    *required = counts;
    if output.len() < counts.segment_count {
        return Err(FontError::Capacity);
    }
    let decoded = parse(path, Some(output))?;
    if decoded.segment_count != counts.segment_count {
        *required = NativeSvgPathRequirements::default();
        return super::fail();
    }
    Ok(())
}
