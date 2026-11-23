//! Original command-family admission, primitive IDs and painter depth.
use super::*;
impl ArchiveHitTestCache {
    pub(super) fn command(
        &mut self,
        c: &ArchiveCommand,
        m: Matrix4,
        provider: Option<&ArchivePicture>,
        id: Option<i32>,
    ) -> Result<()> {
        let pen = c.pen.as_ref().map(|p| p.to_pen()).transpose()?;
        if let Some(p) = &pen {
            p.validate_join_semantics()?;
        }
        if c.kind == K::DrawHintedGlyphs {
            return Err(ArchiveError::Unsupported);
        }
        if self.image_depth != 0 {
            match c.kind {
                K::PushClip | K::PushGeometryClip => {
                    self.image_depth = self
                        .image_depth
                        .checked_add(1)
                        .ok_or(ArchiveError::Capacity)?
                }
                K::PopClip | K::PopGeometryClip => self.image_depth -= 1,
                _ => {}
            }
            return Ok(());
        }
        match c.kind {
            K::PopClip | K::PopGeometryClip => {
                self.pop_clip();
                return Ok(());
            }
            K::PushOpacity => {
                let old = self.active_opacity.unwrap_or(1.0);
                push(&mut self.opacities, old)?;
                self.active_opacity = Some(
                    old * if c.font_size.is_finite() {
                        c.font_size
                    } else {
                        1.0
                    },
                );
                return Ok(());
            }
            K::PopOpacity => {
                self.active_opacity = Some(self.opacities.pop().unwrap_or(1.0));
                return Ok(());
            }
            K::PushOpacityMask | K::PopOpacityMask => return Ok(()),
            _ => {}
        }
        if !invertible(m) {
            if matches!(c.kind, K::PushClip | K::PushGeometryClip) {
                push(&mut self.clips, Clip::empty())?;
            }
            return Ok(());
        }
        match c.kind {
            K::PushClip => return self.push_clip(c.rect, m),
            K::PushGeometryClip => return self.geometry_clip(c, m),
            _ => {}
        }
        if self.active_opacity.unwrap_or(1.0) <= 0.0001 || c.use_gpu_transforms {
            return Ok(());
        }
        let id = self.id(id, c.hit_test_id);
        let z = self.primitives.len() as f32;
        match c.kind {
            K::DrawRect | K::DrawRoundedRect | K::DrawEllipse | K::DrawCircle => {
                self.analytic(c, m, id, z, pen.as_ref())
            }
            K::DrawDotGrid | K::DrawDeviceDotGrid => {
                self.append(HitPrimitive::bounds(id, edges(c.rect), m, z))
            }
            K::DrawLine => self.line(c, m, id, z, pen.as_ref()),
            K::DrawBezier | K::DrawCubicBezier => {
                let Some(pen) = pen.as_ref() else {
                    return Ok(());
                };
                let Some(width) = local_width(c, pen) else {
                    return Ok(());
                };
                let mut path = PathGeometry::default();
                let mut figure = PathFigure::new(c.position, false);
                push(
                    &mut figure.segments,
                    PathSegment::new(if c.kind == K::DrawBezier {
                        PathSegmentKind::Quadratic([c.position2, c.position3])
                    } else {
                        PathSegmentKind::Cubic([c.position2, c.position3, c.position4])
                    }),
                )?;
                push(&mut path.figures, figure)?;
                self.prepared_stroke(&path, m, id, z, pen, width)
            }
            K::DrawPath => self.draw_path(c, m, id, z, pen.as_ref()),
            K::DrawTexture => {
                if let Some(patches) = &c.texture_patches {
                    for p in patches.iter() {
                        let transform = if p.has_destination_transform() {
                            Matrix4::from_affine(crate::geometry::Affine(p.destination_transform()))
                                * m
                        } else {
                            m
                        };
                        self.rectangle_coverage(p.destination(), transform, id, z)?;
                    }
                    Ok(())
                } else {
                    self.rectangle_coverage(c.rect, m, id, z)
                }
            }
            K::DrawText => self.text_bounds(c, m, id, z),
            K::DrawGlyphRun => self.glyph_bounds(c, m, id, z),
            K::FillTriangle => {
                if c.brush.is_some() {
                    self.triangle([c.position, c.position2, c.position3], m, id, z)?;
                }
                Ok(())
            }
            K::FillQuad => {
                if c.brush.is_some() {
                    self.triangle([c.position, c.position2, c.position3], m, id, z)?;
                    self.triangle([c.position, c.position3, c.position4], m, id, z + 0.125)?;
                }
                Ok(())
            }
            K::DrawVertexMesh => {
                if let Some(mesh) = c.vertex_mesh.as_ref().filter(|_| c.brush.is_some()) {
                    for i in 0..mesh.triangle_count() {
                        let Some(t) = mesh.triangle(i) else {
                            continue;
                        };
                        if t.iter().any(|i| *i >= mesh.positions().len()) {
                            continue;
                        }
                        self.triangle(t.map(|i| mesh.positions()[i]), m, id, z + i as f32 * 0.001)?;
                    }
                }
                Ok(())
            }
            K::DrawPointBatch => self.points(c, m, id, z, provider),
            K::DrawPolyline => {
                let points = points(c, provider)?;
                let Some(pen) = pen.as_ref().filter(|_| points.len() >= 2) else {
                    return Ok(());
                };
                let Some(width) = local_width(c, pen) else {
                    return Ok(());
                };
                self.prepared_stroke(&polygon(points, c.is_closed)?, m, id, z, pen, width)
            }
            K::DrawGpuLineSeries => self.line_series(c, m, id, z, provider),
            K::DrawGpuScatterSeries => self.scatter_series(c, m, id, z, provider),
            K::DrawExtension => match c.extension_id {
                4 => self.spline(c, m, id, z, provider, pen.as_ref()),
                5 => self.line_series(c, m, id, z, provider),
                6 => self.scatter_series(c, m, id, z, provider),
                _ => Ok(()),
            },
            // Source builder has no independent geometry for these commands.
            _ => Ok(()),
        }
    }
    fn analytic(
        &mut self,
        c: &ArchiveCommand,
        m: Matrix4,
        id: i32,
        mut z: f32,
        pen: Option<&Pen>,
    ) -> Result<()> {
        let ellipse = matches!(c.kind, K::DrawEllipse | K::DrawCircle);
        let radii = [
            c.radius_x,
            if c.kind == K::DrawCircle {
                c.radius_x
            } else {
                c.radius_y
            },
        ];
        let b = if ellipse {
            [
                c.position2[0] - radii[0],
                c.position2[1] - radii[1],
                c.position2[0] + radii[0],
                c.position2[1] + radii[1],
            ]
        } else {
            edges(c.rect)
        };
        let radius = if c.kind == K::DrawRoundedRect {
            radii
        } else {
            [0.0; 2]
        };
        if c.brush.is_some() {
            self.append(if ellipse {
                HitPrimitive::ellipse_fill(id, b, m, z)
            } else {
                HitPrimitive::rectangle_fill(id, b, radius, m, z)
            })?;
            z += 0.25;
        }
        let Some(pen) = pen else {
            return Ok(());
        };
        let Some(width) = local_width(c, pen) else {
            return Ok(());
        };
        if pen.has_dash_pattern()
            || device(pen)
            || (c.kind == K::DrawRect && pen.options.use_wpf_join_semantics)
        {
            let path = if ellipse {
                PathGeometry::ellipse(c.position2, radii[0], radii[1])
            } else if c.kind == K::DrawRoundedRect {
                PathGeometry::rounded_rectangle(
                    c.rect[0], c.rect[1], c.rect[2], c.rect[3], radii[0], radii[1],
                )
            } else {
                PathGeometry::rectangle(c.rect[0], c.rect[1], c.rect[2], c.rect[3])
            };
            self.prepared_stroke(&path, m, id, z, pen, width)
        } else {
            self.append(if ellipse {
                HitPrimitive::ellipse_stroke(id, b, width, 0.0, m, z)
            } else {
                HitPrimitive::rectangle_stroke(id, b, radius, width, 0.0, m, z)
            })
        }
    }
    fn line(
        &mut self,
        c: &ArchiveCommand,
        m: Matrix4,
        id: i32,
        z: f32,
        pen: Option<&Pen>,
    ) -> Result<()> {
        let Some(p) = pen else {
            return Ok(());
        };
        let Some(width) = local_width(c, p) else {
            return Ok(());
        };
        if p.has_dash_pattern() {
            return self.prepared_stroke(
                &polygon(&[c.position, c.position2], false)?,
                m,
                id,
                z,
                p,
                width,
            );
        }
        let (a, b, t, width) = if device(p) {
            (
                m.transform_point(c.position),
                m.transform_point(c.position2),
                Matrix4::IDENTITY,
                device_width(p),
            )
        } else {
            (c.position, c.position2, m, width)
        };
        self.append(HitPrimitive::line_stroke(
            id,
            a,
            b,
            width,
            [p.options.start_cap, p.options.end_cap],
            0.0,
            t,
            z,
        ))
    }
    pub(super) fn rectangle_coverage(
        &mut self,
        r: [f32; 4],
        m: Matrix4,
        id: i32,
        z: f32,
    ) -> Result<()> {
        if r[2] <= 0.0 || r[3] <= 0.0 {
            return Ok(());
        }
        self.append(HitPrimitive::rectangle_fill(id, edges(r), [0.0; 2], m, z))
    }
    fn text_bounds(&mut self, c: &ArchiveCommand, m: Matrix4, id: i32, z: f32) -> Result<()> {
        let Some(text) = c.text.as_ref().filter(|s| !s.is_empty()) else {
            return Ok(());
        };
        if c.font_size <= 0.0 {
            return Ok(());
        }
        let width = c
            .font_size
            .max(text.encode_utf16().count() as f32 * c.font_size * 0.6);
        self.append(HitPrimitive::bounds(
            id,
            [
                c.position[0],
                c.position[1],
                c.position[0] + width,
                c.position[1] + c.font_size,
            ],
            m,
            z,
        ))
    }
    fn glyph_bounds(&mut self, c: &ArchiveCommand, m: Matrix4, id: i32, z: f32) -> Result<()> {
        if !(c.rect[2] <= 0.0 || c.rect[3] <= 0.0) {
            return self.rectangle_coverage(c.rect, m, id, z);
        }
        let Some(positions) = c.glyph_positions.as_ref().filter(|p| !p.is_empty()) else {
            return self.text_bounds(c, m, id, z);
        };
        let mut b = [
            positions[0][0],
            positions[0][1],
            positions[0][0],
            positions[0][1],
        ];
        for p in &positions[1..] {
            b = union(b, [p[0], p[1], p[0], p[1]]);
        }
        let padding = 1.0f32.max(c.font_size);
        b[2] += padding;
        b[3] += padding;
        self.append(HitPrimitive::bounds(id, b, m, z))
    }
    fn points(
        &mut self,
        c: &ArchiveCommand,
        m: Matrix4,
        id: i32,
        z: f32,
        provider: Option<&ArchivePicture>,
    ) -> Result<()> {
        if c.brush.is_none() {
            return Ok(());
        }
        let hairline = c.radius_x <= 0.0;
        let radius = if hairline { 0.5 } else { c.radius_x };
        let diameter = radius * 2.0;
        for (i, &p) in points(c, provider)?.iter().enumerate() {
            let p = if hairline {
                let p = m.transform_point(p);
                if c.is_edge_aliased {
                    p.map(|x| x.floor() + 0.5)
                } else {
                    p
                }
            } else {
                p
            };
            let min = [p[0] - radius, p[1] - radius];
            let b = [min[0], min[1], min[0] + diameter, min[1] + diameter];
            let t = if hairline { Matrix4::IDENTITY } else { m };
            let z = z + i as f32 * 0.0001;
            self.append(if c.int_param != 0 {
                HitPrimitive::ellipse_fill(id, b, t, z)
            } else {
                HitPrimitive::rectangle_fill(id, b, [0.0; 2], t, z)
            })?;
        }
        Ok(())
    }
    pub(super) fn triangle(&mut self, p: [[f32; 2]; 3], m: Matrix4, id: i32, z: f32) -> Result<()> {
        if let Some(path) = self.compile_path(&polygon(&p, true)?)? {
            self.path_fill(path, m, id, z)?;
        }
        Ok(())
    }
}
pub(super) fn points<'a>(
    c: &'a ArchiveCommand,
    p: Option<&'a ArchivePicture>,
) -> Result<&'a [[f32; 2]]> {
    if let Some(points) = c.polyline_points.as_deref().filter(|p| !p.is_empty()) {
        Ok(points)
    } else if c.point_buffer_count > 0 {
        if let Some(p) = p {
            p.points(c.point_buffer_offset, c.point_buffer_count)
        } else {
            Ok(&[])
        }
    } else {
        Ok(&[])
    }
}
