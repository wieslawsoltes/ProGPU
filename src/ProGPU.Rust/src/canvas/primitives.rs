//! Original Canvas arc, point, difference and transformed-picture adapters.
use super::*;
use crate::vector::{BooleanOperation, PathBuilder, PathDirection};
use std::f32::consts::PI;
impl Canvas {
    /// Original finite arc admission, signed sweep clamp and endpoint-arc path.
    /// A centered full turn retains its two explicit half arcs and radial seam;
    /// an uncentered full turn selects the original closed oval producer.
    pub fn draw_arc(
        &mut self,
        oval: [f32; 4],
        start_degrees: f32,
        sweep_degrees: f32,
        use_center: bool,
        paint: &Paint,
    ) -> Result<(), Error> {
        if oval == [0.0; 4]
            || oval.iter().any(|v| !v.is_finite())
            || !start_degrees.is_finite()
            || !sweep_degrees.is_finite()
            || sweep_degrees == 0.0
        {
            return Ok(());
        }
        let width = oval[2] - oval[0];
        let height = oval[3] - oval[1];
        let radii = [width * 0.5, height * 0.5];
        let center = [oval[0] + width / 2.0, oval[1] + height / 2.0];
        let start_radians = start_degrees * (PI / 180.0);
        let sweep = sweep_degrees.clamp(-360.0, 360.0);
        let start = [
            center[0] + start_radians.cos() * radii[0],
            center[1] + start_radians.sin() * radii[1],
        ];
        let direction = if sweep >= 0.0 {
            PathDirection::Clockwise
        } else {
            PathDirection::CounterClockwise
        };
        let mut path = PathBuilder::new();
        if sweep.abs() >= 360.0 && !use_center {
            path.add_oval(oval, direction)?;
        } else {
            if use_center {
                path.move_to(center)?;
                path.line_to(start)?;
            } else {
                path.move_to(start)?;
            }
            if sweep.abs() >= 360.0 {
                let opposite = (start_degrees + sweep * 0.5) * (PI / 180.0);
                path.arc_to(
                    radii,
                    0.0,
                    false,
                    direction,
                    [
                        center[0] + opposite.cos() * radii[0],
                        center[1] + opposite.sin() * radii[1],
                    ],
                )?;
                path.arc_to(radii, 0.0, false, direction, start)?;
            } else {
                let end = (start_degrees + sweep) * (PI / 180.0);
                path.arc_to(
                    radii,
                    0.0,
                    sweep.abs() > 180.0,
                    direction,
                    [
                        center[0] + end.cos() * radii[0],
                        center[1] + end.sin() * radii[1],
                    ],
                )?;
            }
            if use_center {
                path.close()?;
            }
        }
        self.draw_retained_path(&path.detach(), paint)
    }
    /// Original point route selects a private stroke paint and a zero-length line.
    pub fn draw_point(&mut self, point: [f32; 2], paint: &Paint) -> Result<(), Error> {
        self.draw_points(CanvasPointMode::Points, &[point], paint)
    }
    /// Original color-only point overload independently selects Src blend.
    pub fn draw_point_color(&mut self, point: [f32; 2], color: [u8; 4]) -> Result<(), Error> {
        let mut paint = Paint::default();
        paint.color = color;
        paint.set_blend_mode(LayerBlend::Src);
        self.draw_point(point, &paint)
    }
    /// Original center/radii overload reconstructs the source edge rectangle
    /// before selecting the ordinary ellipse command and its midpoint arithmetic.
    pub fn draw_oval_center(
        &mut self,
        center: [f32; 2],
        radii: [f32; 2],
        paint: &Paint,
    ) -> Result<(), Error> {
        self.draw_oval(
            [
                center[0] - radii[0],
                center[1] - radii[1],
                center[0] + radii[0],
                center[1] + radii[1],
            ],
            paint,
        )
    }
    /// Original ordered Boolean difference of the actual rounded source paths.
    /// Inner geometry is not assumed contained, and no even-odd approximation is used.
    pub fn draw_round_rect_difference(
        &mut self,
        outer: RoundRect,
        inner: RoundRect,
        paint: &Paint,
    ) -> Result<(), Error> {
        let mut a = PathBuilder::new();
        let mut b = PathBuilder::new();
        a.add_round_rect(outer, PathDirection::Clockwise)?;
        b.add_round_rect(inner, PathDirection::Clockwise)?;
        let path = PathGeometry::combine_deferred(
            a.detach().geometry()?,
            b.detach().geometry()?,
            BooleanOperation::Difference,
        );
        self.draw_path(Arc::new(path), false, paint)
    }
    /// Original explicit picture matrix composes previous Canvas matrix first.
    /// Recording the result directly preserves caller state even on allocation failure.
    pub fn draw_picture_transformed(
        &mut self,
        picture: CanvasPicture,
        matrix: CanvasMatrix,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let opacity = paint.map_or(1.0, |p| f32::from(p.color[3]) / 255.0);
        let blend = paint.map_or(Ok(LayerBlend::SrcOver), Paint::canvas_blend)?;
        self.push(Command::Picture {
            picture,
            matrix: self.matrix.concat(matrix),
            opacity,
            blend,
        })
    }
    /// Original position overload constructs an actual translation descriptor.
    pub fn draw_picture_at(
        &mut self,
        picture: CanvasPicture,
        origin: [f32; 2],
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_picture_transformed(picture, CanvasMatrix::translation(origin), paint)
    }
}
