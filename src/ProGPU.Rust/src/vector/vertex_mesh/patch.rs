//! Original adaptive Coons grid, local texture coordinates and premultiplied colors.
use super::*;
/// Twelve clockwise cubic boundary points with optional four clockwise corners.
#[derive(Clone, Debug)]
pub struct CoonsPatch {
    /// Top 0..3, right 3..6, bottom 9,8,7,6 and left 0,11,10,9.
    pub cubics: [[f32; 2]; 12],
    /// Original RGBA bytes; interpolate in premultiplied space.
    pub colors: Option<[[u8; 4]; 4]>,
    /// Original four corner shader coordinates, interpolated in local space.
    pub texture_coordinates: Option<[[f32; 2]; 4]>,
}
impl CoonsPatch {
    /// Original patch with no optional corner attributes.
    pub fn new(cubics: [[f32; 2]; 12]) -> Self {
        Self {
            cubics,
            colors: None,
            texture_coordinates: None,
        }
    }
    /// Original initial density; a nonfinite transformed control polygon is empty.
    /// Exceptional float-to-int overflow rejects instead of claiming runtime parity.
    pub fn level_of_detail(&self, transform: Matrix4) -> Result<[usize; 2], Error> {
        let mut lengths = [0.0; 4];
        for (i, slots) in [[0, 1, 2, 3], [3, 4, 5, 6], [9, 8, 7, 6], [0, 11, 10, 9]]
            .into_iter()
            .enumerate()
        {
            let p = slots.map(|j| point(transform, self.cubics[j]));
            lengths[i] = distance(p[0], p[1]) + distance(p[1], p[2]) + distance(p[2], p[3]);
        }
        if lengths.iter().any(|v| !v.is_finite()) {
            return Ok([0, 0]);
        }
        let values = [
            lengths[0].max(lengths[2]) / 10.0,
            lengths[3].max(lengths[1]) / 10.0,
        ];
        if values.iter().any(|v| f64::from(*v) >= f64::from(i32::MAX)) {
            return Err(Error::Capacity);
        }
        Ok(values.map(|v| (v as usize).max(8)))
    }
    /// Generate one owned indexed mesh using original device-space density,
    /// proportional cap, grid traversal, winding and local Coons arithmetic.
    pub fn mesh(&self, transform: Matrix4) -> Result<VertexMesh, Error> {
        let [mut nx, mut ny] = self.level_of_detail(transform)?;
        if nx == 0 || ny == 0 {
            return VertexMesh::from_owned(
                VertexMode::Triangles,
                Vec::new(),
                Vec::new(),
                Vec::new(),
                Vec::new(),
            );
        }
        let initial = nx
            .checked_add(1)
            .and_then(|x| ny.checked_add(1).and_then(|y| x.checked_mul(y)))
            .ok_or(Error::Capacity)?;
        if initial > i32::MAX as usize {
            return Err(Error::Capacity);
        }
        if initial > 10000 || nx > 200 || ny > 200 {
            let total = nx.checked_add(ny).ok_or(Error::Capacity)?;
            let weights = V::from([nx as f32, ny as f32, 0.0, 0.0]) / V::splat(total as f32);
            let capped = (weights * V::splat(200.0)).to_array();
            nx = (capped[0].floor() as usize).max(1);
            ny = (capped[1].floor() as usize).max(1);
        }
        let count = (nx + 1).checked_mul(ny + 1).ok_or(Error::Capacity)?;
        let mut positions = Vec::new();
        positions
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        let mut colors = Vec::new();
        if self.colors.is_some() {
            colors
                .try_reserve_exact(count)
                .map_err(|_| Error::Capacity)?;
        }
        let mut texture = Vec::new();
        if self.texture_coordinates.is_some() {
            texture
                .try_reserve_exact(count)
                .map_err(|_| Error::Capacity)?;
        }
        let mut indices = Vec::new();
        indices
            .try_reserve_exact(
                nx.checked_mul(ny)
                    .and_then(|v| v.checked_mul(6))
                    .ok_or(Error::Capacity)?,
            )
            .map_err(|_| Error::Capacity)?;
        let p = self.cubics.map(pair);
        let corners = self.colors.map(|c| {
            c.map(|v| {
                let alpha = f32::from(v[3]) / 255.0;
                let mut rgba =
                    ((V::from(v.map(f32::from)) / V::splat(255.0)) * V::splat(alpha)).to_array();
                rgba[3] = alpha;
                V::from(rgba)
            })
        });
        let uv = self.texture_coordinates.map(|v| v.map(pair));
        let stride = ny + 1;
        for x in 0..=nx {
            let u = x as f32 / nx as f32;
            let top = cubic([p[0], p[1], p[2], p[3]], u);
            let bottom = cubic([p[9], p[8], p[7], p[6]], u);
            for y in 0..=ny {
                let v = y as f32 / ny as f32;
                let left = cubic([p[0], p[11], p[10], p[9]], v);
                let right = cubic([p[3], p[4], p[5], p[6]], v);
                let q = (lerp(top, bottom, v) + lerp(left, right, u)
                    - bilerp([p[0], p[3], p[9], p[6]], u, v))
                .to_array();
                positions.push([q[0], q[1]]);
                if let Some(c) = corners {
                    let color = bilerp([c[0], c[1], c[3], c[2]], u, v).to_array();
                    colors.push(if color[3] > 0.0 {
                        let mut q = (V::from(color) / V::splat(color[3])).to_array();
                        q[3] = color[3];
                        q
                    } else {
                        [0.0; 4]
                    });
                }
                if let Some(t) = uv {
                    let q = bilerp([t[0], t[1], t[3], t[2]], u, v).to_array();
                    texture.push([q[0], q[1]]);
                }
                if x < nx && y < ny {
                    for i in [
                        x * stride + y,
                        x * stride + y + 1,
                        (x + 1) * stride + y + 1,
                        x * stride + y,
                        (x + 1) * stride + y + 1,
                        (x + 1) * stride + y,
                    ] {
                        indices.push(u16::try_from(i).map_err(|_| Error::Capacity)?);
                    }
                }
            }
        }
        VertexMesh::from_owned(VertexMode::Triangles, positions, texture, colors, indices)
    }
}
fn pair(p: [f32; 2]) -> V {
    V::from([p[0], p[1], 0.0, 0.0])
}
fn distance(a: [f32; 2], b: [f32; 2]) -> f32 {
    let d = pair(a) - pair(b);
    let q = (d * d).to_array();
    (q[0] + q[1]).sqrt()
}
fn lerp(a: V, b: V, t: f32) -> V {
    a * V::splat(1.0 - t) + b * V::splat(t)
}
fn bilerp(p: [V; 4], u: f32, v: f32) -> V {
    lerp(lerp(p[0], p[1], u), lerp(p[2], p[3], u), v)
}
fn cubic(p: [V; 4], t: f32) -> V {
    let u = 1.0 - t;
    let u2 = u * u;
    let t2 = t * t;
    p[0] * V::splat(u2 * u)
        + p[1] * V::splat(3.0 * u2 * t)
        + p[2] * V::splat(3.0 * u * t2)
        + p[3] * V::splat(t2 * t)
}
