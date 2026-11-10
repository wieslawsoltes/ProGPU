//! Original extension quad and QuadClipper arithmetic before GPU publication.
use super::*;
use crate::geometry::VectorVertex;
use bytemuck::Zeroable;
impl BackdropMaterialDraw {
    pub(crate) fn vertices(
        &self,
        clip: Option<[f32; 4]>,
    ) -> Result<Option<[VectorVertex; 4]>, Error> {
        self.validate()?;
        let p = &self.parameters;
        let [x, y, w, h] = p.rect;
        if w <= 0.0 || h <= 0.0 {
            return Ok(None);
        }
        let mut points = [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]
            .map(|v| self.transform.transform_point(v));
        let [l, t, r, b] = self.source_uv_rect();
        let mut uv = [[l, t], [r, t], [r, b], [l, b]];
        if points
            .iter()
            .flatten()
            .chain(uv.iter().flatten())
            .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        if let Some(clip) = clip
            && !clip_quad(clip, &mut points, &mut uv)
        {
            return Ok(None);
        }
        Ok(Some(std::array::from_fn(|i| VectorVertex {
            position: points[i],
            color: [1.0, 1.0, 1.0, self.opacity],
            texture_coordinate: uv[i],
            ..Zeroable::zeroed()
        })))
    }
    pub(crate) fn source_uv_rect(&self) -> [f32; 4] {
        let p = &self.parameters;
        let [x, y, w, h] = p.source_rect;
        if let Some(image) = &p.source_texture
            && w > 0.0
            && h > 0.0
        {
            let size = image.size().map(|v| (v as f32).max(1.0));
            [
                x / size[0],
                y / size[1],
                (x + w) / size[0],
                (y + h) / size[1],
            ]
        } else {
            [0.0, 0.0, 1.0, 1.0]
        }
    }
}
fn clip_quad(clip: [f32; 4], p: &mut [[f32; 2]; 4], uv: &mut [[f32; 2]; 4]) -> bool {
    const EPSILON: f32 = 0.0001;
    if (p[0][1] - p[1][1]).abs() > EPSILON
        || (p[2][1] - p[3][1]).abs() > EPSILON
        || (p[0][0] - p[3][0]).abs() > EPSILON
        || (p[1][0] - p[2][0]).abs() > EPSILON
    {
        return true;
    }
    let left = p[0][0].min(p[1][0]).min(p[2][0].min(p[3][0])).max(clip[0]);
    let right = p[0][0].max(p[1][0]).max(p[2][0].max(p[3][0])).min(clip[2]);
    let top = p[0][1].min(p[1][1]).min(p[2][1].min(p[3][1])).max(clip[1]);
    let bottom = p[0][1].max(p[1][1]).max(p[2][1].max(p[3][1])).min(clip[3]);
    if right <= left || bottom <= top {
        return false;
    }
    let [x0, y0] = p[0];
    let x1 = p[1][0];
    let y1 = p[3][1];
    if (x1 - x0).abs() <= EPSILON || (y1 - y0).abs() <= EPSILON {
        return false;
    }
    let nx0 = x0.clamp(left, right);
    let nx1 = x1.clamp(left, right);
    let ny0 = y0.clamp(top, bottom);
    let ny1 = y1.clamp(top, bottom);
    let u0 = (nx0 - x0) / (x1 - x0);
    let u1 = (nx1 - x0) / (x1 - x0);
    let v0 = (ny0 - y0) / (y1 - y0);
    let v1 = (ny1 - y0) / (y1 - y0);
    let original = *uv;
    *p = [[nx0, ny0], [nx1, ny0], [nx1, ny1], [nx0, ny1]];
    let lerp = |a: [f32; 2], b: [f32; 2], t: f32| {
        [a[0] * (1.0 - t) + b[0] * t, a[1] * (1.0 - t) + b[1] * t]
    };
    *uv = [[u0, v0], [u1, v0], [u1, v1], [u0, v1]].map(|[u, v]| {
        lerp(
            lerp(original[0], original[1], u),
            lerp(original[3], original[2], u),
            v,
        )
    });
    true
}
