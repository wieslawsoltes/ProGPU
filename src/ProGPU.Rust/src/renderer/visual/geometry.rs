//! Original canonical visual clip recognition and physical mask composition.
use super::*;
use crate::vector::{FillRule, PathGeometry, RoundedRectangleContour};
impl Builder<'_> {
    pub(super) async fn rectangle_scope(
        &mut self,
        scene: &mut Scene,
        bounds: [f32; 4],
        transform: Matrix4,
        target: RenderFrame,
        clip: &mut Option<[f32; 4]>,
        masks: &mut Vec<Mask>,
    ) -> Result<(), Error> {
        let m = transform.0;
        if m[0][1].abs() <= 0.0001 && m[1][0].abs() <= 0.0001 {
            let (edges, _) = scene.push_visual_clip(bounds, transform)?;
            if let Some(edges) = edges {
                *clip = Some(intersection(*clip, edges));
            }
            Ok(())
        } else {
            let geometry = PathGeometry::rectangle(bounds[0], bounds[1], bounds[2], bounds[3]);
            self.path_scope(scene, &geometry, transform, target, masks)
                .await
        }
    }
    pub(super) async fn composite_scope(
        &mut self,
        scene: &mut Scene,
        value: &VisualCompositeClip,
        parent: Matrix4,
        target: RenderFrame,
        clip: &mut Option<[f32; 4]>,
        masks: &mut Vec<Mask>,
    ) -> Result<(), Error> {
        match value {
            VisualCompositeClip::Rectangle { bounds, transform } => {
                self.rectangle_scope(scene, *bounds, *transform * parent, target, clip, masks)
                    .await
            }
            VisualCompositeClip::Geometry {
                geometry,
                transform,
            } => {
                self.geometry_scope(scene, geometry, *transform * parent, target, clip, masks)
                    .await
            }
        }
    }
    pub(super) async fn geometry_scope(
        &mut self,
        scene: &mut Scene,
        geometry: &PathGeometry,
        transform: Matrix4,
        target: RenderFrame,
        clip: &mut Option<[f32; 4]>,
        masks: &mut Vec<Mask>,
    ) -> Result<(), Error> {
        if let Some([l, t, r, b]) = geometry.axis_aligned_rectangle_bounds() {
            self.rectangle_scope(scene, [l, t, r - l, b - t], transform, target, clip, masks)
                .await
        } else {
            self.path_scope(scene, geometry, transform, target, masks)
                .await
        }
    }
    async fn path_scope(
        &mut self,
        scene: &mut Scene,
        geometry: &PathGeometry,
        transform: Matrix4,
        target: RenderFrame,
        masks: &mut Vec<Mask>,
    ) -> Result<(), Error> {
        let canonical = if self.depth <= 1 {
            canonical(
                geometry,
                transform,
                target,
                masks,
                self.renderer.device.limits().max_texture_dimension_2d,
            )?
        } else {
            None
        };
        let mask = if let Some(mask) = canonical {
            mask
        } else {
            // Render the original path with the preceding coverage bound during
            // painting. Quantize the completed product once, as the original R8
            // mask pass; multiplying separately quantized masks is not equivalent.
            let mut source = Scene::default();
            for mask in masks.iter() {
                source.push_mask(mask.clone())?;
            }
            if let Some(path) = geometry.compile_raster_path()? {
                source.draw_path(crate::scene::PathDraw {
                    path,
                    transform: affine(transform)?,
                    color: [1.0; 4],
                    sampling: Default::default(),
                })?;
            }
            for _ in masks.iter() {
                source.pop_scope()?;
            }
            self.mask(source, target).await?
        };
        let replace = match &mask {
            Mask::RenderedTexture(v) => v.replaces_chain,
            Mask::FrameAnalytic(v) => v.replaces_chain,
            _ => false,
        };
        scene.push_mask(mask.clone())?;
        if replace {
            masks.clear();
        }
        masks.push(mask);
        Ok(())
    }
}
fn canonical(
    geometry: &PathGeometry,
    transform: Matrix4,
    target: RenderFrame,
    masks: &[Mask],
    maximum: u32,
) -> Result<Option<Mask>, Error> {
    if geometry.deferred.is_some() {
        return Ok(None);
    }
    let ring = uniform_ring(geometry);
    let hole = rectangular_hole(geometry);
    let contour = if let Some((outer, _)) = ring {
        outer
    } else if let Some((outer, _)) = hole {
        outer
    } else {
        let [figure] = geometry.figures.as_slice() else {
            return Ok(None);
        };
        let Some(contour) = RoundedRectangleContour::from_figure(figure) else {
            return Ok(None);
        };
        if !masks.is_empty() {
            return Ok(None);
        }
        contour
    };
    let Some(rows) = physical_rows(transform, target.dpi_scale)? else {
        return Ok(None);
    };
    let mut uniforms = MaskUniforms {
        coordinate0: rows[0],
        coordinate1: rows[1],
        bounds: contour.bounds(),
        radii_x: contour.radii_x(),
        radii_y: contour.radii_y(),
        options: [
            if hole.is_some() {
                4.0
            } else if ring.is_some() {
                3.0
            } else {
                2.0
            },
            1.0,
            ring.map_or(0.0, |v| v.1),
            0.0,
        ],
    };
    if let Some((_, inner)) = hole {
        let [l, t, r, b] = inner.bounds();
        uniforms.coordinate0[3] = l;
        uniforms.coordinate1[3] = t;
        uniforms.options[2] = r;
        uniforms.options[3] = b;
    }
    if !masks.is_empty() {
        let [Mask::FrameAnalytic(parent)] = masks else {
            return Ok(None);
        };
        let outer = parent.uniforms;
        let compose = hole.is_some()
            && outer.options[0] == 2.0
            && outer.coordinate0[..3] == uniforms.coordinate0[..3]
            && outer.coordinate1[..3] == uniforms.coordinate1[..3]
            && outer.bounds == contour.bounds();
        if compose {
            let hole = hole.ok_or(Error::ResourceDomain)?.1.bounds();
            uniforms = outer;
            uniforms.coordinate0[3] = hole[0];
            uniforms.coordinate1[3] = hole[1];
            uniforms.options = [4.0, outer.options[1], hole[2], hole[3]];
        } else if !(outer.options[0] == 2.0
            && outer.coordinate0 == uniforms.coordinate0
            && outer.coordinate1 == uniforms.coordinate1
            && outer.bounds == uniforms.bounds
            && outer.radii_x == uniforms.radii_x
            && outer.radii_y == uniforms.radii_y)
        {
            return Ok(None);
        }
    }
    let root = RenderFrame {
        frame: target.root,
        origin: [0; 2],
        ..target
    };
    Ok(Some(Mask::FrameAnalytic(FrameAnalyticMask {
        uniforms,
        size: [target.root.width, target.root.height],
        dpi: target.dpi_scale,
        projection: root.uniforms(maximum)?.projection,
        replaces_chain: !masks.is_empty(),
    })))
}
fn pair(geometry: &PathGeometry) -> Option<(RoundedRectangleContour, RoundedRectangleContour)> {
    if geometry.fill_rule != FillRule::EvenOdd {
        return None;
    }
    let [outer, inner] = geometry.figures.as_slice() else {
        return None;
    };
    Some((
        RoundedRectangleContour::from_figure(outer)?,
        RoundedRectangleContour::from_figure(inner)?,
    ))
}
fn uniform_ring(geometry: &PathGeometry) -> Option<(RoundedRectangleContour, f32)> {
    let (outer, inner) = pair(geometry)?;
    let a = outer.bounds();
    let b = inner.bounds();
    let left = b[0] - a[0];
    let top = b[1] - a[1];
    let right = a[2] - b[2];
    let bottom = a[3] - b[3];
    let tolerance = (a[2] - a[0]).max(a[3] - a[1]) * 0.0001 + 0.0001;
    if left <= tolerance
        || (top - left).abs() > tolerance
        || (right - left).abs() > tolerance
        || (bottom - left).abs() > tolerance
    {
        return None;
    }
    for (inner, outer) in [
        (inner.radii_x(), outer.radii_x()),
        (inner.radii_y(), outer.radii_y()),
    ] {
        if (0..4).any(|i| (inner[i] - (outer[i] - left).max(0.0)).abs() > tolerance) {
            return None;
        }
    }
    Some((outer, left))
}
fn rectangular_hole(
    geometry: &PathGeometry,
) -> Option<(RoundedRectangleContour, RoundedRectangleContour)> {
    let (outer, inner) = pair(geometry)?;
    if inner.radii_x() != [0.0; 4] && inner.radii_y() != [0.0; 4] {
        return None;
    }
    let a = outer.bounds();
    let b = inner.bounds();
    (b[0] >= a[0] && b[1] >= a[1] && b[2] <= a[2] && b[3] <= a[3] && b[0] < b[2] && b[1] < b[3])
        .then_some((outer, inner))
}
