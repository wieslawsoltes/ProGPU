use super::*;
use super::{
    values as value,
    xml::{Document, Node, local, trim},
};
use crate::vector::{ArcSegment, SweepDirection};
#[derive(Clone)]
struct State {
    transform: Affine,
    fill: Vec<u8>,
    opacity: f32,
    fill_opacity: f32,
}
impl Default for State {
    fn default() -> Self {
        Self {
            transform: Affine::IDENTITY,
            fill: b"black".to_vec(),
            opacity: 1.0,
            fill_opacity: 1.0,
        }
    }
}
fn state(node: &Node, parent: &State) -> Result<State> {
    let mut local_transform = node
        .attribute(b"transform")
        .map(value::transform)
        .unwrap_or(Affine::IDENTITY);
    if local(&node.name) == b"use" {
        let x = value::float(node, b"x", 0.0);
        let y = value::float(node, b"y", 0.0);
        if x != 0.0 || y != 0.0 {
            local_transform = value::multiply(Affine([1.0, 0.0, 0.0, 1.0, x, y]), local_transform);
        }
    }
    Ok(State {
        transform: value::multiply(local_transform, parent.transform),
        fill: copy(node.attribute(b"fill").unwrap_or(&parent.fill))?,
        opacity: parent.opacity * value::interval(node, b"opacity", 1.0),
        fill_opacity: if node.attribute(b"fill-opacity").is_some() {
            value::interval(node, b"fill-opacity", parent.fill_opacity)
        } else {
            parent.fill_opacity
        },
    })
}
#[derive(Default)]
pub(super) struct Decoded {
    pub layers: Vec<NativeSvgGlyphLayer>,
    pub segments: Vec<Segment>,
    pub brushes: Vec<GpuBrush>,
    pub stops: Vec<GpuGradientStop>,
}
impl Decoded {
    pub fn requirements(&self) -> NativeSvgGlyphRequirements {
        NativeSvgGlyphRequirements {
            layer_count: self.layers.len(),
            segment_count: self.segments.len(),
            brush_count: self.brushes.len(),
            gradient_stop_count: self.stops.len(),
        }
    }
}
fn geometry(node: &Node, output: &mut Vec<Segment>) -> Result<Option<NativeSvgPathRequirements>> {
    let name = local(&node.name);
    if name == b"path" {
        let Some(data) = node
            .attribute(b"d")
            .filter(|v| v.iter().any(|v| !matches!(v, b' ' | b'\t' | b'\r' | b'\n')))
        else {
            return Ok(None);
        };
        let Ok(mut required) = native_svg_path_requirements(data) else {
            return Ok(None);
        };
        let start = output.len();
        output
            .try_reserve(required.segment_count)
            .map_err(|_| FontError::Capacity)?;
        let end = start
            .checked_add(required.segment_count)
            .ok_or(FontError::Capacity)?;
        output.resize(end, Segment::line([0.0; 2], [0.0; 2]));
        if native_decode_svg_path(data, &mut output[start..], &mut required).is_err() {
            output.truncate(start);
            return Ok(None);
        }
        return Ok((required.segment_count != 0).then_some(required));
    }
    if name == b"rect" {
        let x = value::float(node, b"x", 0.0);
        let y = value::float(node, b"y", 0.0);
        let width = value::float(node, b"width", 0.0);
        let height = value::float(node, b"height", 0.0);
        if ![x, y, width, height].iter().all(|v| v.is_finite())
            || width <= 0.0001
            || height <= 0.0001
        {
            return Ok(None);
        }
        let points = [
            [x, y],
            [x + width, y],
            [x + width, y + height],
            [x, y + height],
        ];
        for index in 0..4 {
            push(
                output,
                Segment::line(points[index], points[(index + 1) % 4]),
            )?;
        }
        return Ok(Some(NativeSvgPathRequirements {
            segment_count: 4,
            bounds: [x, y, x + width, y + height],
            fill_rule: FillRule::NonZero,
        }));
    }
    if name == b"circle" || name == b"ellipse" {
        let cx = value::float(node, b"cx", 0.0);
        let cy = value::float(node, b"cy", 0.0);
        let rx = value::float(node, if name == b"circle" { b"r" } else { b"rx" }, 0.0);
        let ry = if name == b"circle" {
            rx
        } else {
            value::float(node, b"ry", 0.0)
        };
        if ![cx, cy, rx, ry].iter().all(|v| v.is_finite()) || rx <= 0.0001 || ry <= 0.0001 {
            return Ok(None);
        }
        let points = [[cx + rx, cy], [cx, cy + ry], [cx - rx, cy], [cx, cy - ry]];
        for index in 0..4 {
            let start = points[index];
            let end = points[(index + 1) % 4];
            let arc = ArcSegment {
                end,
                size: [rx, ry],
                rotation: 0.0,
                large: false,
                sweep: SweepDirection::Clockwise,
            };
            let segment = arc.center(start).map_or_else(
                || Segment::line(start, end),
                |resolved| Segment {
                    points: [start, end, resolved.center, resolved.radii],
                    kind: 3,
                    parameters: [
                        resolved.theta.to_bits(),
                        resolved.delta.to_bits(),
                        0.0f32.to_bits(),
                    ],
                },
            );
            push(output, segment)?;
        }
        return Ok(Some(NativeSvgPathRequirements {
            segment_count: 4,
            bounds: [cx - rx, cy - ry, cx + rx, cy + ry],
            fill_rule: FillRule::NonZero,
        }));
    }
    if name == b"polygon" {
        let Some(text) = node.attribute(b"points") else {
            return Ok(None);
        };
        let values = value::numbers(text)?;
        if values.len() < 6 || values.len() % 2 != 0 {
            return Ok(None);
        }
        let first = [values[0], values[1]];
        let mut bounds = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
        let mut current = first;
        for (index, pair) in values.chunks_exact(2).enumerate() {
            let next = [pair[0], pair[1]];
            if index != 0 {
                push(output, Segment::line(current, next))?;
            }
            for axis in 0..2 {
                if next[axis] < bounds[axis] {
                    bounds[axis] = next[axis];
                }
                if bounds[axis + 2] < next[axis] {
                    bounds[axis + 2] = next[axis];
                }
            }
            current = next;
        }
        push(output, Segment::line(current, first))?;
        return Ok(Some(NativeSvgPathRequirements {
            segment_count: values.len() / 2,
            bounds,
            fill_rule: FillRule::NonZero,
        }));
    }
    Ok(None)
}
fn url(text: &[u8]) -> Option<&[u8]> {
    let text = trim(text);
    if text.len() < 7 || !text[..5].eq_ignore_ascii_case(b"url(#") || text.last() != Some(&b')') {
        return None;
    }
    let id = trim(&text[5..text.len() - 1]);
    (!id.is_empty()).then_some(id)
}
fn identity_brush() -> GpuBrush {
    GpuBrush {
        opacity: 1.0,
        coordinate_transform: [[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0]],
        ..Default::default()
    }
}
fn gradient(
    source: &Document,
    node: &Node,
    opacity: f32,
    transform: Affine,
    units: u16,
    output: &mut Decoded,
) -> Result<Option<u32>> {
    let mut stops = Vec::new();
    for &child in &node.children {
        let child = &source.nodes[child];
        if local(&child.name) != b"stop" {
            continue;
        }
        let Some(mut color) = value::color(child.attribute(b"stop-color").unwrap_or(b"black"))
        else {
            continue;
        };
        color[3] *= opacity * value::interval(child, b"stop-opacity", 1.0);
        push(
            &mut stops,
            GpuGradientStop {
                color,
                offset: value::interval(child, b"offset", 0.0),
                padding: [0; 3],
            },
        )?;
    }
    if stops.is_empty() {
        return Ok(None);
    }
    stops.sort_by(|a, b| {
        a.offset
            .partial_cmp(&b.offset)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    let start = output.stops.len();
    let mut brush = identity_brush();
    brush.stop_offset = u32::try_from(start).map_err(|_| FontError::Capacity)?;
    brush.stop_count = u32::try_from(stops.len()).map_err(|_| FontError::Capacity)?;
    brush.spread = match node.attribute(b"spreadMethod") {
        Some(v) if v.eq_ignore_ascii_case(b"reflect") => 1,
        Some(v) if v.eq_ignore_ascii_case(b"repeat") => 2,
        _ => 0,
    };
    let gradient_transform = node
        .attribute(b"gradientTransform")
        .map(value::transform)
        .unwrap_or(Affine::IDENTITY);
    let transform = value::multiply(gradient_transform, transform);
    match local(&node.name) {
        b"linearGradient" => {
            brush.kind = 1;
            brush.start = transform.transform_point([
                value::coordinate(node, b"x1", 0.0, units),
                value::coordinate(node, b"y1", 0.0, units),
            ]);
            brush.end = transform.transform_point([
                value::coordinate(node, b"x2", f32::from(units), units),
                value::coordinate(node, b"y2", 0.0, units),
            ]);
        }
        b"radialGradient" => {
            brush.kind = 2;
            let cx = value::coordinate(node, b"cx", f32::from(units) * 0.5, units);
            let cy = value::coordinate(node, b"cy", f32::from(units) * 0.5, units);
            let radius = value::coordinate(node, b"r", f32::from(units) * 0.5, units);
            let fx = value::coordinate(node, b"fx", cx, units);
            let fy = value::coordinate(node, b"fy", cy, units);
            brush.center = transform.transform_point([cx, cy]);
            brush.start = transform.transform_point([fx, fy]);
            let rx = transform.transform_point([cx + radius, cy]);
            let ry = transform.transform_point([cx, cy + radius]);
            brush.radius = (rx[0] - brush.center[0]).hypot(rx[1] - brush.center[1]);
            brush.radius_y = (ry[0] - brush.center[0]).hypot(ry[1] - brush.center[1]);
        }
        _ => return Ok(None),
    }
    brush.offsets = [[0.0, 1.0, 1.0, 1.0], [1.0; 4]];
    for (index, stop) in stops.iter().take(8).enumerate() {
        brush.colors[index] = stop.color;
        brush.offsets[index / 4][index % 4] = stop.offset;
    }
    output
        .stops
        .try_reserve(stops.len())
        .map_err(|_| FontError::Capacity)?;
    output.stops.extend_from_slice(&stops);
    let index = u32::try_from(output.brushes.len()).map_err(|_| FontError::Capacity)?;
    push(&mut output.brushes, brush)?;
    Ok(Some(index))
}
fn brush(
    source: &Document,
    state: &State,
    units: u16,
    output: &mut Decoded,
) -> Result<Option<u32>> {
    let opacity = state.opacity * state.fill_opacity;
    if opacity <= 0.0 || state.fill.eq_ignore_ascii_case(b"none") {
        return Ok(None);
    }
    if let Some(id) = url(&state.fill) {
        return match source.ids.get(id) {
            Some(&index) => gradient(
                source,
                &source.nodes[index],
                opacity,
                state.transform,
                units,
                output,
            ),
            None => Ok(None),
        };
    }
    let Some(mut color) = value::color(&state.fill) else {
        return Ok(None);
    };
    color[3] *= opacity.clamp(0.0, 1.0);
    let mut brush = identity_brush();
    brush.colors[0] = color;
    let index = u32::try_from(output.brushes.len()).map_err(|_| FontError::Capacity)?;
    push(&mut output.brushes, brush)?;
    Ok(Some(index))
}
struct Renderer<'a> {
    source: &'a Document,
    units: u16,
    output: Decoded,
    active: Vec<bool>,
}
impl Renderer<'_> {
    fn render(
        &mut self,
        index: usize,
        parent: &State,
        depth: usize,
        allow_definition: bool,
    ) -> Result<()> {
        if depth > 64 {
            return Ok(());
        }
        let element = &self.source.nodes[index];
        let name = local(&element.name);
        if name == b"defs" && !allow_definition {
            return Ok(());
        }
        let state = state(element, parent)?;
        match name {
            b"svg" | b"g" | b"defs" => {
                for child in 0..element.children.len() {
                    self.render(
                        self.source.nodes[index].children[child],
                        &state,
                        depth + 1,
                        false,
                    )?;
                }
            }
            b"use" => {
                if let Some(href) = element
                    .attribute(b"href")
                    .filter(|v| v.len() >= 2 && v[0] == b'#')
                {
                    if let Some(&target) = self
                        .source
                        .ids
                        .get(&href[1..])
                        .filter(|&&target| !self.active[target])
                    {
                        self.active[target] = true;
                        let result = self.render(target, &state, depth + 1, true);
                        self.active[target] = false;
                        result?;
                    }
                }
            }
            b"path" | b"circle" | b"ellipse" | b"rect" | b"polygon" => {
                let start = self.output.segments.len();
                let Some(mut requirements) = geometry(element, &mut self.output.segments)? else {
                    self.output.segments.truncate(start);
                    return Ok(());
                };
                if self.output.segments.len() == start {
                    return Ok(());
                }
                if element
                    .attribute(b"fill-rule")
                    .is_some_and(|v| v.eq_ignore_ascii_case(b"evenodd"))
                {
                    requirements.fill_rule = FillRule::EvenOdd;
                }
                let Some(brush_index) = brush(self.source, &state, self.units, &mut self.output)?
                else {
                    self.output.segments.truncate(start);
                    return Ok(());
                };
                push(
                    &mut self.output.layers,
                    NativeSvgGlyphLayer {
                        segment_offset: start,
                        segment_count: self.output.segments.len() - start,
                        bounds: requirements.bounds,
                        transform: state.transform,
                        brush_index,
                        fill_rule: requirements.fill_rule,
                    },
                )?;
            }
            _ => {}
        }
        Ok(())
    }
}
pub(super) fn decode(xml: &[u8], glyph: u16, units: u16) -> Result<Decoded> {
    if units == 0 {
        return fail();
    }
    let source = Document::parse(xml)?;
    let glyph_id = format!("glyph{glyph}");
    let index = *source
        .ids
        .get(glyph_id.as_bytes())
        .ok_or(FontError::InvalidGlyph)?;
    let mut parent = source.nodes[index].parent;
    let mut ancestors = Vec::new();
    while let Some(index) = parent {
        push(&mut ancestors, index)?;
        parent = source.nodes[index].parent;
    }
    let mut initial = State::default();
    for index in ancestors.into_iter().rev() {
        initial = state(&source.nodes[index], &initial)?;
    }
    let mut active = Vec::new();
    active
        .try_reserve_exact(source.nodes.len())
        .map_err(|_| FontError::Capacity)?;
    active.resize(source.nodes.len(), false);
    let mut renderer = Renderer {
        source: &source,
        units,
        output: Decoded::default(),
        active,
    };
    renderer.render(index, &initial, 0, true)?;
    if renderer.output.layers.is_empty() {
        return fail();
    }
    Ok(renderer.output)
}
