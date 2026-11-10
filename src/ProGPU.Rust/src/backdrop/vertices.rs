//! Original extension quad and QuadClipper arithmetic before GPU publication.
use super::*;
use crate::geometry::VectorVertex;
use crate::image_effect::clip_quad;
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
