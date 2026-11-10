//! Exact original compositor vertices for archived analytic vector commands.
use super::{ArchiveBrush, ArchiveCommand, ArchiveCommandKind};
use crate::{
    Error,
    geometry::{Mesh, VectorVertex},
    vector::{Brush, BrushKind, Matrix4},
};
use std::{ops::Range, sync::Arc};
use wide::f32x4 as V;

/// Retains the actual raw command and side-point generation. Only these original
/// direct vector families are compiled here; ellipse/circle pens remain separate.
#[derive(Clone, Debug)]
pub struct ArchiveVectorDraw {
    command: Arc<ArchiveCommand>,
    points: Arc<[[f32; 2]]>,
    selected: Range<usize>,
    inline: bool,
    transform: Matrix4,
    opacity: f32,
    brush: Option<Arc<Brush>>,
    stroke: bool,
}
impl ArchiveVectorDraw {
    pub fn new(
        command: Arc<ArchiveCommand>,
        points: Arc<[[f32; 2]]>,
        transform: Matrix4,
        opacity: f32,
    ) -> Result<Self, Error> {
        use ArchiveCommandKind::*;
        if !matches!(
            command.kind,
            DrawRect
                | DrawRoundedRect
                | FillTriangle
                | FillQuad
                | DrawPointBatch
                | DrawDotGrid
                | DrawDeviceDotGrid
                | DrawEllipse
                | DrawCircle
        ) {
            return Err(Error::UnsupportedExecution);
        }
        let inline = command.kind == DrawPointBatch
            && command
                .polyline_points
                .as_ref()
                .is_some_and(|p| !p.is_empty());
        let selected = if command.kind != DrawPointBatch {
            0..0
        } else if inline {
            0..command.polyline_points.as_ref().unwrap().len()
        } else if command.point_buffer_count > 0 {
            let start =
                usize::try_from(command.point_buffer_offset).map_err(|_| Error::InvalidInput)?;
            let end = start
                .checked_add(command.point_buffer_count as usize)
                .ok_or(Error::Capacity)?;
            if end > points.len() {
                return Err(Error::InvalidInput);
            }
            start..end
        } else {
            0..0
        };
        let brush = match command.brush.as_deref() {
            Some(ArchiveBrush::Vector(b)) => Some(Arc::new(b.clone())),
            _ => None,
        };
        let draw = Self {
            command,
            points,
            selected,
            inline,
            transform,
            opacity,
            brush,
            stroke: false,
        };
        draw.validate()?;
        Ok(draw)
    }
    /// Select the original independent analytic pen quad, retaining the same
    /// source command and its legacy local/device-width recovery metadata.
    pub fn with_pen(mut self) -> Result<Self, Error> {
        if !matches!(
            self.command.kind,
            ArchiveCommandKind::DrawRect
                | ArchiveCommandKind::DrawRoundedRect
                | ArchiveCommandKind::DrawEllipse
                | ArchiveCommandKind::DrawCircle
        ) {
            return Err(Error::UnsupportedExecution);
        }
        self.brush = match self.command.pen.as_ref() {
            Some(p) => {
                let p = p.to_pen().map_err(super::drawing_error)?;
                p.validate_join_semantics()?;
                Some(p.brush)
            }
            None => None,
        };
        self.stroke = true;
        self.validate()?;
        Ok(self)
    }
    /// Whether this record is the original separate pen quad.
    pub fn uses_pen(&self) -> bool {
        self.stroke
    }
    fn stroke_state(&self) -> Option<[f32; 2]> {
        let pen = self.command.pen.as_ref()?.to_pen().ok()?;
        if !pen.options.thickness.is_finite()
            || (!pen.is_hairline() && pen.options.thickness <= 0.0)
            || !invertible(self.transform)
        {
            return None;
        }
        let mut local = if pen.is_hairline() {
            1.0
        } else {
            pen.options.thickness
        };
        let recorded = self.command.transform;
        if !pen.is_hairline()
            && !self.command.is_pen_thickness_local
            && recorded != Matrix4([[0.0; 4]; 4])
        {
            if !invertible(recorded) {
                return None;
            }
            local /= recorded.stroke_scales()?[0];
        }
        let [maximum, minimum] = self.transform.stroke_scales()?;
        let (geometry, bounds, device) = if pen.is_hairline() {
            (1.0 / maximum, 1.0 / minimum, 1.0)
        } else if pen.is_fixed() {
            (local / maximum, local / minimum, local)
        } else {
            (local, local, local * maximum)
        };
        if [local, geometry, bounds, device]
            .iter()
            .any(|v| !v.is_finite() || *v <= 0.0)
        {
            return None;
        }
        let shader = if pen.is_hairline() {
            -1.0
        } else if pen.is_fixed() {
            -(device + 1.0).max(f32::from_bits(1.0f32.to_bits() + 1))
        } else {
            local
        };
        Some([bounds, shader])
    }
    pub fn command(&self) -> &Arc<ArchiveCommand> {
        &self.command
    }
    /// Full actual side buffer; selected source intervals retain their offsets.
    pub fn points(&self) -> &Arc<[[f32; 2]]> {
        &self.points
    }
    pub fn transform(&self) -> Matrix4 {
        self.transform
    }
    pub fn opacity(&self) -> f32 {
        self.opacity
    }
    /// Resolve an explicit actual theme brush before recording; never substitute
    /// a fallback color for an unresolved source resource.
    pub fn with_brush(mut self, brush: Arc<Brush>) -> Result<Self, Error> {
        self.brush = Some(brush);
        self.validate()?;
        Ok(self)
    }
    pub fn brush(&self) -> Result<Arc<Brush>, Error> {
        self.brush.clone().ok_or(Error::UnsupportedExecution)
    }
    pub fn placed(&self, transform: Matrix4) -> Result<Self, Error> {
        let mut draw = self.clone();
        draw.transform = draw.transform * transform;
        draw.validate()?;
        Ok(draw)
    }
    pub fn with_opacity(&self, multiplier: f32) -> Result<Self, Error> {
        let mut draw = self.clone();
        draw.opacity *= multiplier;
        draw.validate()?;
        Ok(draw)
    }
    pub fn validate(&self) -> Result<(), Error> {
        if !self.opacity.is_finite() {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    fn selected_points(&self) -> &[[f32; 2]] {
        if self.inline {
            &self.command.polyline_points.as_ref().unwrap()[self.selected.clone()]
        } else {
            &self.points[self.selected.clone()]
        }
    }
    /// Original command-specific early-outs, including device-grid source frame
    /// admission. Other families retain their original later finite-output check.
    pub(crate) fn is_renderable(&self) -> bool {
        use ArchiveCommandKind::*;
        let c = &self.command;
        if self.stroke {
            return self.stroke_state().is_some();
        }
        if c.brush.is_none() {
            return false;
        }
        match c.kind {
            DrawPointBatch => !self.selected_points().is_empty(),
            DrawDotGrid => {
                !(c.rect[2] <= 0.0 || c.rect[3] <= 0.0 || c.radius_x <= 0.0 || c.radius_y <= 0.0)
            }
            DrawDeviceDotGrid => {
                c.rect.iter().all(|v| v.is_finite())
                    && c.rect[2] > 0.0
                    && c.rect[3] > 0.0
                    && c.position2.iter().all(|v| v.is_finite() && *v > 0.0)
                    && c.radius_x.is_finite()
                    && c.radius_x > 0.0
                    && c.radius_y.is_finite()
                    && c.radius_y >= 0.0
                    && (c.radius_y == 0.0
                        || (c.radius_y <= 100.0 && c.radius_y == c.radius_y.round_ties_even()))
                    && invertible(self.transform)
            }
            _ => true,
        }
    }
    pub(crate) fn append(&self, target: &mut Mesh, brush_index: f32) -> Result<(), Error> {
        self.validate()?;
        if !self.is_renderable() {
            return Ok(());
        }
        let brush = self.brush()?;
        let solid = match &brush.kind {
            BrushKind::Solid(color) => Some(*color),
            _ => None,
        };
        use ArchiveCommandKind::*;
        let c = &self.command;
        let shape = |kind: f32| {
            if c.is_edge_aliased {
                kind + 1000.0
            } else {
                kind
            }
        };
        let vertex = |local: [f32; 2],
                      uv: [f32; 2],
                      color: [f32; 4],
                      size: [f32; 2],
                      radius: f32,
                      stroke: f32,
                      kind: f32| VectorVertex {
            position: point(self.transform, local),
            color,
            texture_coordinate: uv,
            brush_index,
            shape_size: size,
            corner_radius: radius,
            stroke_thickness: stroke,
            shape_type: shape(kind),
        };
        match c.kind {
            FillTriangle | FillQuad => {
                let color = solid.unwrap_or([1.0; 4]);
                let points = [c.position, c.position2, c.position3, c.position4];
                let vertices = points.map(|p| vertex(p, p, color, [0.0; 2], 0.0, 0.0, 7.0));
                if c.kind == FillTriangle {
                    target.push_indexed(&vertices[..3], &[0, 1, 2])
                } else {
                    target.push_quad(vertices)
                }
            }
            DrawPointBatch => {
                let points = self.selected_points();
                let count = points.len().checked_mul(4).ok_or(Error::Capacity)?;
                let index_count = points.len().checked_mul(6).ok_or(Error::Capacity)?;
                u32::try_from(count).map_err(|_| Error::Capacity)?;
                let hairline = c.radius_x <= 0.0;
                let radius = if hairline { 0.5 } else { c.radius_x };
                let size = [radius * 2.0; 2];
                let padding = if c.is_edge_aliased { 0.0 } else { 1.5 };
                let extent = radius + padding;
                let kind = if hairline {
                    if c.int_param != 0 { 20.0 } else { 19.0 }
                } else if c.int_param != 0 {
                    1.0
                } else {
                    0.0
                };
                let local = [
                    [-extent, -extent],
                    [extent, -extent],
                    [extent, extent],
                    [-extent, extent],
                ];
                let vertices = (0..count).map(|i| {
                    let center = points[i / 4];
                    let uv = local[i % 4];
                    let color = solid.unwrap_or([center[0], center[1], 0.0, 0.0]);
                    let position = if hairline {
                        point(self.transform, center)
                    } else {
                        point(self.transform, [center[0] + uv[0], center[1] + uv[1]])
                    };
                    VectorVertex {
                        position,
                        color,
                        texture_coordinate: uv,
                        brush_index,
                        shape_size: size,
                        corner_radius: 0.0,
                        stroke_thickness: 0.0,
                        shape_type: shape(kind),
                    }
                });
                let indices =
                    (0..points.len()).flat_map(|i| [0, 1, 2, 0, 2, 3].map(|n| i as u32 * 4 + n));
                target.extend_generated(vertices, indices, index_count)
            }
            DrawDotGrid | DrawDeviceDotGrid => {
                let [x, y, w, h] = c.rect;
                let local = [[x, y], [x + w, y], [x + w, y + h], [x, y + h]];
                let (size, radius, stroke, kind) = if c.kind == DrawDotGrid {
                    (
                        [c.radius_x, c.radius_y],
                        c.position2[0],
                        c.position2[1],
                        21.0,
                    )
                } else {
                    (
                        c.position2,
                        if c.radius_y > 0.0 {
                            -c.radius_x
                        } else {
                            c.radius_x
                        },
                        c.radius_y,
                        25.0,
                    )
                };
                target.push_quad(
                    local.map(|p| {
                        vertex(p, p, solid.unwrap_or([1.0; 4]), size, radius, stroke, kind)
                    }),
                )
            }
            DrawRect | DrawRoundedRect | DrawEllipse | DrawCircle => {
                let ellipse = matches!(c.kind, DrawEllipse | DrawCircle);
                let [x, y, w, h] = c.rect;
                let center = if ellipse {
                    c.position2
                } else {
                    [x + w / 2.0, y + h / 2.0]
                };
                let rx = if ellipse { c.radius_x } else { w / 2.0 };
                let ry = if c.kind == DrawCircle {
                    rx
                } else if ellipse {
                    c.radius_y
                } else {
                    h / 2.0
                };
                let [stroke_bounds, shader_thickness] = if self.stroke {
                    self.stroke_state().ok_or(Error::InvalidInput)?
                } else {
                    [0.0, 0.0]
                };
                let pad = self.transform.stroke_scales().map_or(1.5, |s| {
                    let p = 1.5 / s[1];
                    if p.is_finite() && p > 0.0 { p } else { 1.5 }
                });
                let pad = stroke_bounds / 2.0 + pad;
                // Keep the source's separate center +/- radius +/- pad expressions.
                let local = if ellipse {
                    [
                        [center[0] - rx - pad, center[1] - ry - pad],
                        [center[0] + rx + pad, center[1] - ry - pad],
                        [center[0] + rx + pad, center[1] + ry + pad],
                        [center[0] - rx - pad, center[1] + ry + pad],
                    ]
                } else {
                    [
                        [x - pad, y - pad],
                        [x + w + pad, y - pad],
                        [x + w + pad, y + h + pad],
                        [x - pad, y + h + pad],
                    ]
                };
                let uv = [
                    [-rx - pad, -ry - pad],
                    [rx + pad, -ry - pad],
                    [rx + pad, ry + pad],
                    [-rx - pad, ry + pad],
                ];
                let color = solid.unwrap_or([center[0], center[1], 0.0, 0.0]);
                target.push_quad(std::array::from_fn(|i| {
                    vertex(
                        local[i],
                        uv[i],
                        color,
                        if ellipse {
                            [2.0 * rx, 2.0 * ry]
                        } else {
                            [w, h]
                        },
                        if c.kind == DrawRoundedRect {
                            c.radius_x
                        } else {
                            0.0
                        },
                        shader_thickness,
                        if ellipse {
                            1.0
                        } else if c.kind == DrawRoundedRect {
                            2.0
                        } else {
                            0.0
                        },
                    )
                }))
            }
            _ => Err(Error::UnsupportedExecution),
        }
    }
}
fn point(matrix: Matrix4, p: [f32; 2]) -> [f32; 2] {
    let m = matrix.0;
    let v = (V::splat(p[0]) * V::from([m[0][0], m[0][1], 0.0, 0.0])
        + V::splat(p[1]) * V::from([m[1][0], m[1][1], 0.0, 0.0])
        + V::from([m[3][0], m[3][1], 0.0, 0.0]))
    .to_array();
    [v[0], v[1]]
}
fn invertible(matrix: Matrix4) -> bool {
    let m = matrix.0;
    let determinant = m[0][0] * m[1][1] - m[0][1] * m[1][0];
    [m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]
        .iter()
        .all(|v| v.is_finite())
        && [
            m[0][2],
            m[0][3],
            m[1][2],
            m[1][3],
            m[2][0],
            m[2][1],
            m[2][3],
            m[3][3] - 1.0,
        ]
        .iter()
        .all(|v| v.abs() <= 0.0001)
        && determinant.is_finite()
        && determinant != 0.0
}
