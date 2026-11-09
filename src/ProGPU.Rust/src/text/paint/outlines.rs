use super::*;
use std::collections::HashMap;
use wide::f32x4;

#[derive(Clone, Debug)]
pub(super) enum Outline {
    Font(Arc<ManagedGlyphOutline>),
    Vector(Arc<PathGeometry>),
}
#[derive(Clone, Copy, PartialEq, Eq, Hash)]
struct Key {
    source: usize,
    font: bool,
    values: [u32; 5],
    svg: bool,
}
#[derive(Default)]
pub(super) struct Cache {
    paths: HashMap<Key, (Outline, Arc<PathGeometry>)>,
}
impl Cache {
    pub(super) fn len(&self) -> usize {
        self.paths.len()
    }
    pub(super) fn clear(&mut self) {
        self.paths.clear();
    }
    #[allow(clippy::too_many_arguments)] // Original independent placement/cache inputs.
    pub(super) fn position(
        &mut self,
        outline: &Outline,
        em: f32,
        position: [f32; 2],
        italic: f32,
        scale_x: f32,
        svg: bool,
        transform: Matrix4,
    ) -> Result<(Arc<PathGeometry>, Matrix4, [f32; 2])> {
        if !finite(position) {
            return Ok((
                Arc::new(positioned(outline, em, position, italic, scale_x, svg)?),
                transform,
                position,
            ));
        }
        let fractional = position.map(|v| v - v.floor());
        let phase = fractional.map(|v| {
            let value = (v * 128.0).round_ties_even() / 128.0;
            if value >= 1.0 { 0.0 } else { value }
        });
        let (source, font) = match outline {
            Outline::Font(p) => (Arc::as_ptr(p) as usize, true),
            Outline::Vector(p) => (Arc::as_ptr(p) as usize, false),
        };
        let key = Key {
            source,
            font,
            values: [em, phase[0], phase[1], italic, scale_x].map(key_float),
            svg,
        };
        let path = if let Some((_, path)) = self.paths.get(&key) {
            path.clone()
        } else {
            let path = Arc::new(positioned(outline, em, phase, italic, scale_x, svg)?);
            if self.paths.len() >= 4096 {
                self.paths.clear();
            }
            self.paths.try_reserve(1).map_err(|_| Error::Capacity)?;
            self.paths.insert(key, (outline.clone(), path.clone()));
            path
        };
        let placement =
            placement::translation([position[0] - phase[0], position[1] - phase[1]]) * transform;
        Ok((path, placement, phase))
    }
}
fn key_float(v: f32) -> u32 {
    if v == 0.0 {
        0
    } else if v.is_nan() {
        f32::NAN.to_bits()
    } else {
        v.to_bits()
    }
}
struct Mapping {
    em: f32,
    origin: [f32; 2],
    italic: f32,
    scale_x: f32,
    svg: bool,
}
impl Mapping {
    fn point(&self, p: [f32; 2]) -> [f32; 2] {
        self.points([p, p])[0]
    }
    fn points(&self, p: [[f32; 2]; 2]) -> [[f32; 2]; 2] {
        // Independent points/XY lanes preserve source intermediate rounding.
        // Y keeps its separate multiply/sign order; no affine-matrix reassociation.
        let skew = if self.svg { -self.italic } else { self.italic };
        let x = f32x4::new([p[0][0], p[1][0], 0.0, 0.0]);
        let y = f32x4::new([p[0][1], p[1][1], 0.0, 0.0]);
        let xx = (f32x4::splat(self.origin[0])
            + (x * f32x4::splat(self.scale_x) + y * f32x4::splat(skew)) * f32x4::splat(self.em)
            + f32x4::splat(0.0))
        .to_array();
        let yy = (f32x4::splat(self.origin[1])
            + y * f32x4::splat(self.em) * f32x4::splat(if self.svg { 1.0 } else { -1.0 }))
        .to_array();
        [[xx[0], yy[0]], [xx[1], yy[1]]]
    }
    fn segment(&self, segment: PathSegment) -> PathSegment {
        let kind = match segment.kind {
            PathSegmentKind::Line(p) => PathSegmentKind::Line(self.point(p)),
            PathSegmentKind::Quadratic(p) => PathSegmentKind::Quadratic(self.points(p)),
            PathSegmentKind::Cubic(p) => {
                let a = self.points([p[0], p[1]]);
                PathSegmentKind::Cubic([a[0], a[1], self.point(p[2])])
            }
            PathSegmentKind::RationalQuadratic { points, weight } => {
                PathSegmentKind::RationalQuadratic {
                    points: self.points(points),
                    weight,
                }
            }
            PathSegmentKind::RationalCubic { points, weights } => {
                let a = self.points([points[0], points[1]]);
                PathSegmentKind::RationalCubic {
                    points: [a[0], a[1], self.point(points[2])],
                    weights,
                }
            }
            PathSegmentKind::Arc(mut arc) => {
                arc.end = self.point(arc.end);
                arc.size = [
                    (arc.size[0] * self.em * self.scale_x).abs(),
                    (arc.size[1] * self.em).abs(),
                ];
                PathSegmentKind::Arc(arc)
            }
        };
        PathSegment { kind, ..segment }
    }
}
fn positioned(
    source: &Outline,
    em: f32,
    origin: [f32; 2],
    italic: f32,
    scale_x: f32,
    svg: bool,
) -> Result<PathGeometry> {
    let mapping = Mapping {
        em,
        origin,
        italic,
        scale_x,
        svg,
    };
    let mut output = PathGeometry::default();
    match source {
        Outline::Vector(path) => {
            output.fill_rule = path.fill_rule;
            output
                .figures
                .try_reserve_exact(path.figures.len())
                .map_err(|_| Error::Capacity)?;
            for figure in &path.figures {
                let mut target = PathFigure::new(mapping.point(figure.start), figure.is_closed);
                target.is_filled = figure.is_filled;
                target.start_cap = figure.start_cap;
                target.end_cap = figure.end_cap;
                target
                    .segments
                    .try_reserve_exact(figure.segments.len())
                    .map_err(|_| Error::Capacity)?;
                target
                    .segments
                    .extend(figure.segments.iter().map(|s| mapping.segment(*s)));
                output.figures.push(target);
            }
        }
        Outline::Font(path) => {
            output
                .figures
                .try_reserve_exact(path.figures().len())
                .map_err(|_| Error::Capacity)?;
            for figure in path.figures() {
                let mut target = PathFigure::new(mapping.point(figure.start), true);
                target
                    .segments
                    .try_reserve_exact(figure.segments.len())
                    .map_err(|_| Error::Capacity)?;
                for segment in &figure.segments {
                    let kind = match segment.kind {
                        0 => PathSegmentKind::Line(segment.points[1]),
                        1 => PathSegmentKind::Quadratic([segment.points[1], segment.points[2]]),
                        2 => PathSegmentKind::Cubic([
                            segment.points[1],
                            segment.points[2],
                            segment.points[3],
                        ]),
                        _ => return Err(Error::InvalidInput.into()),
                    };
                    target
                        .segments
                        .push(mapping.segment(PathSegment::new(kind)));
                }
                output.figures.push(target);
            }
        }
    }
    Ok(output)
}
pub(super) fn layer_brush(
    layer: &FontColorLayer,
    em: f32,
    position: [f32; 2],
) -> Result<Arc<Brush>> {
    let Some(brush) = &layer.brush else {
        return Ok(Arc::new(Brush::new(BrushKind::Solid(layer.color))));
    };
    if !layer.uses_svg_coordinates {
        return Ok(brush.clone());
    }
    let transform = |p: [f32; 2]| add(position, p.map(|v| v * em));
    let kind = match &brush.kind {
        BrushKind::Solid(c) => BrushKind::Solid(*c),
        BrushKind::Linear(g) => BrushKind::Linear(LinearGradient {
            start: transform(g.start),
            end: transform(g.end),
            stops: stops(&g.stops)?,
            options: GradientOptions {
                spread: g.options.spread,
                interpolation: g.options.interpolation,
                ..Default::default()
            },
        }),
        BrushKind::Radial(g) => BrushKind::Radial(RadialGradient {
            center: transform(g.center),
            origin: transform(g.origin),
            radii: g.radii.map(|v| v * em),
            stops: stops(&g.stops)?,
            options: GradientOptions {
                spread: g.options.spread,
                interpolation: g.options.interpolation,
                ..Default::default()
            },
        }),
        _ => return Ok(brush.clone()),
    };
    Ok(Arc::new(Brush {
        opacity: brush.opacity,
        kind,
    }))
}
fn stops(source: &[GradientStop]) -> Result<Vec<GradientStop>> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(source.len())
        .map_err(|_| Error::Capacity)?;
    output.extend_from_slice(source);
    Ok(output)
}
