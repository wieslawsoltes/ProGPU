//! Immutable original geometry generations and exact source normal/deindex rules.
use super::*;
use std::sync::OnceLock;
use wide::f32x4 as V;
/// Resolved original normal producer; GPU rendering has its independent policy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MeshNormalExecution {
    CpuSimd,
    ScalarReference,
}
impl MeshNormalExecution {
    fn select(preference: crate::path::ExecutionPreference) -> Result<Self, Error> {
        use crate::path::ExecutionPreference as P;
        let simd = cfg!(any(
            target_arch = "x86_64",
            target_arch = "aarch64",
            all(target_arch = "wasm32", target_feature = "simd128")
        ));
        match preference {
            P::Automatic => Ok(if simd {
                Self::CpuSimd
            } else {
                Self::ScalarReference
            }),
            P::CpuSimd if simd => Ok(Self::CpuSimd),
            P::ScalarReference => Ok(Self::ScalarReference),
            _ => Err(Error::UnsupportedExecution),
        }
    }
}
#[derive(Debug)]
struct Data {
    identity: Arc<()>,
    version: i32,
    positions: Arc<[[f32; 3]]>,
    normals: Arc<[[f32; 3]]>,
    indices: Arc<[i32]>,
    texture_coordinates: Arc<[[f32; 2]]>,
    edges: Arc<[MeshEdge3D]>,
    vertices: OnceLock<Arc<[GpuVertex3D]>>,
}
/// One actual source geometry identity and immutable array/version generation.
/// Earlier prepared frames keep their selected arrays after a replacement.
#[derive(Clone, Debug)]
pub struct MeshGeometry3D(Arc<Data>);
impl MeshGeometry3D {
    /// Explicit original CPU normal producer and diagnostics. Independent XYZ
    /// lanes use SIMD by default; unavailable forced GPU-normal kernels reject.
    pub fn prepare_normals(
        positions: &Arc<[[f32; 3]]>,
        normals: &Arc<[[f32; 3]]>,
        indices: &[i32],
        preference: crate::path::ExecutionPreference,
    ) -> Result<(Arc<[[f32; 3]]>, MeshNormalExecution), Error> {
        let path = MeshNormalExecution::select(preference)?;
        admit_lengths([positions.len(), normals.len(), indices.len(), 0, 0])?;
        let output = match path {
            MeshNormalExecution::CpuSimd => normals_simd(positions, normals, indices)?,
            MeshNormalExecution::ScalarReference => normals_scalar(positions, normals, indices)?,
        };
        Ok((output, path))
    }
    /// Original MeshGeometry3D producer: keep complete supplied normals when
    /// their length matches positions, otherwise compute the original normals.
    pub fn new(
        positions: Arc<[[f32; 3]]>,
        normals: Arc<[[f32; 3]]>,
        indices: Arc<[i32]>,
        texture_coordinates: Arc<[[f32; 2]]>,
        edges: Arc<[MeshEdge3D]>,
    ) -> Result<Self, Error> {
        let normals = normals_or_compute(&positions, &normals, &indices)?;
        Self::from_prepared(positions, normals, indices, texture_coordinates, edges)
    }
    /// Original already-prepared MeshCompilationEntry array contract. Short or
    /// invalid-index lanes retain Zero/UnitY/Zero independently at deindexing.
    pub fn from_prepared(
        positions: Arc<[[f32; 3]]>,
        normals: Arc<[[f32; 3]]>,
        indices: Arc<[i32]>,
        texture_coordinates: Arc<[[f32; 2]]>,
        edges: Arc<[MeshEdge3D]>,
    ) -> Result<Self, Error> {
        admit_lengths([
            positions.len(),
            normals.len(),
            indices.len(),
            texture_coordinates.len(),
            edges.len(),
        ])?;
        Ok(Self(Arc::new(Data {
            identity: Arc::new(()),
            version: 0,
            positions,
            normals,
            indices,
            texture_coordinates,
            edges,
            vertices: OnceLock::new(),
        })))
    }
    /// Actual source object identity, retained by every prepared geometry cache.
    pub fn identity(&self) -> usize {
        Arc::as_ptr(&self.0.identity) as usize
    }
    pub fn version(&self) -> i32 {
        self.0.version
    }
    pub fn positions(&self) -> &Arc<[[f32; 3]]> {
        &self.0.positions
    }
    pub fn normals(&self) -> &Arc<[[f32; 3]]> {
        &self.0.normals
    }
    pub fn indices(&self) -> &Arc<[i32]> {
        &self.0.indices
    }
    pub fn texture_coordinates(&self) -> &Arc<[[f32; 2]]> {
        &self.0.texture_coordinates
    }
    pub fn edges(&self) -> &Arc<[MeshEdge3D]> {
        &self.0.edges
    }
    /// Explicit selected source version without changing original array owners.
    pub fn with_version(&self, version: i32) -> Self {
        Self(Arc::new(Data {
            identity: self.0.identity.clone(),
            version,
            positions: self.0.positions.clone(),
            normals: self.0.normals.clone(),
            indices: self.0.indices.clone(),
            texture_coordinates: self.0.texture_coordinates.clone(),
            edges: self.0.edges.clone(),
            vertices: OnceLock::new(),
        }))
    }
    /// Publish a replacement of the same source object. Version increment wraps
    /// as the original unchecked int; prior generations are never overwritten.
    pub fn replaced(
        &self,
        positions: Arc<[[f32; 3]]>,
        normals: Arc<[[f32; 3]]>,
        indices: Arc<[i32]>,
        texture_coordinates: Arc<[[f32; 2]]>,
        edges: Arc<[MeshEdge3D]>,
    ) -> Result<Self, Error> {
        admit_lengths([
            positions.len(),
            normals.len(),
            indices.len(),
            texture_coordinates.len(),
            edges.len(),
        ])?;
        let normals = normals_or_compute(&positions, &normals, &indices)?;
        Ok(Self(Arc::new(Data {
            identity: self.0.identity.clone(),
            version: self.0.version.wrapping_add(1),
            positions,
            normals,
            indices,
            texture_coordinates,
            edges,
            vertices: OnceLock::new(),
        })))
    }
    /// Original indexed array expansion to triangle-list vertices. No triangle
    /// is synthesized or removed, including incomplete tails and bad indices.
    pub fn deindexed(&self) -> Result<Arc<[GpuVertex3D]>, Error> {
        if let Some(vertices) = self.0.vertices.get() {
            return Ok(vertices.clone());
        }
        let mut vertices = Vec::new();
        vertices
            .try_reserve_exact(self.0.indices.len())
            .map_err(|_| Error::Capacity)?;
        for &index in self.0.indices.iter() {
            let selected = usize::try_from(index).ok();
            vertices.push(GpuVertex3D {
                position: selected
                    .and_then(|i| self.0.positions.get(i))
                    .copied()
                    .unwrap_or([0.0; 3]),
                normal: selected
                    .and_then(|i| self.0.normals.get(i))
                    .copied()
                    .unwrap_or([0.0, 1.0, 0.0]),
                texture_coordinate: selected
                    .and_then(|i| self.0.texture_coordinates.get(i))
                    .copied()
                    .unwrap_or([0.0; 2]),
            });
        }
        let vertices: Arc<[GpuVertex3D]> = vertices.into();
        let _ = self.0.vertices.set(vertices.clone());
        Ok(self.0.vertices.get().cloned().unwrap_or(vertices))
    }
}
fn admit_lengths(lengths: [usize; 5]) -> Result<(), Error> {
    if lengths.iter().any(|v| *v > i32::MAX as usize) {
        Err(Error::Capacity)
    } else {
        Ok(())
    }
}
fn vector(v: [f32; 3]) -> V {
    V::from([v[0], v[1], v[2], 0.0])
}
fn array(v: V) -> [f32; 3] {
    let v = v.to_array();
    [v[0], v[1], v[2]]
}
fn length(v: V) -> f32 {
    let v = (v * v).to_array();
    (v[0] + v[1] + v[2]).sqrt()
}
fn normals_or_compute(
    positions: &Arc<[[f32; 3]]>,
    normals: &Arc<[[f32; 3]]>,
    indices: &[i32],
) -> Result<Arc<[[f32; 3]]>, Error> {
    MeshGeometry3D::prepare_normals(
        positions,
        normals,
        indices,
        crate::path::ExecutionPreference::Automatic,
    )
    .map(|v| v.0)
}
fn normals_simd(
    positions: &Arc<[[f32; 3]]>,
    normals: &Arc<[[f32; 3]]>,
    indices: &[i32],
) -> Result<Arc<[[f32; 3]]>, Error> {
    if normals.len() == positions.len() {
        return Ok(normals.clone());
    }
    if positions.is_empty() || indices.is_empty() {
        return Ok(Arc::from([]));
    }
    let mut computed = Vec::new();
    computed
        .try_reserve_exact(positions.len())
        .map_err(|_| Error::Capacity)?;
    computed.resize(positions.len(), [0.0; 3]);
    for triangle in indices.chunks_exact(3) {
        let [Some(i0), Some(i1), Some(i2)] = [triangle[0], triangle[1], triangle[2]]
            .map(|v| usize::try_from(v).ok().filter(|i| *i < positions.len()))
        else {
            continue;
        };
        let i = [i0, i1, i2];
        let u = array(vector(positions[i[1]]) - vector(positions[i[0]]));
        let v = array(vector(positions[i[2]]) - vector(positions[i[0]]));
        let mut normal = V::from([u[1], u[2], u[0], 0.0]) * V::from([v[2], v[0], v[1], 0.0])
            - V::from([u[2], u[0], u[1], 0.0]) * V::from([v[1], v[2], v[0], 0.0]);
        let len = length(normal);
        if len > 0.0001 {
            normal /= V::splat(len);
        }
        // Dependent duplicate-index accumulation and source triangle order remain
        // scalar; independent XYZ arithmetic uses SIMD without fused operations.
        for index in i {
            computed[index] = array(vector(computed[index]) + normal);
        }
    }
    for normal in &mut computed {
        let value = vector(*normal);
        let len = length(value);
        *normal = if len > 0.0001 {
            array(value / V::splat(len))
        } else {
            [0.0, 1.0, 0.0]
        };
    }
    Ok(computed.into())
}

fn normals_scalar(
    positions: &Arc<[[f32; 3]]>,
    normals: &Arc<[[f32; 3]]>,
    indices: &[i32],
) -> Result<Arc<[[f32; 3]]>, Error> {
    if normals.len() == positions.len() {
        return Ok(normals.clone());
    }
    if positions.is_empty() || indices.is_empty() {
        return Ok(Arc::from([]));
    }
    let mut computed = Vec::new();
    computed
        .try_reserve_exact(positions.len())
        .map_err(|_| Error::Capacity)?;
    computed.resize(positions.len(), [0.0; 3]);
    let length = |v: [f32; 3]| (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    for triangle in indices.chunks_exact(3) {
        let [Some(a), Some(b), Some(c)] = [triangle[0], triangle[1], triangle[2]]
            .map(|v| usize::try_from(v).ok().filter(|i| *i < positions.len()))
        else {
            continue;
        };
        let u = std::array::from_fn::<_, 3, _>(|i| positions[b][i] - positions[a][i]);
        let v = std::array::from_fn::<_, 3, _>(|i| positions[c][i] - positions[a][i]);
        let mut normal = [
            u[1] * v[2] - u[2] * v[1],
            u[2] * v[0] - u[0] * v[2],
            u[0] * v[1] - u[1] * v[0],
        ];
        let len = length(normal);
        if len > 0.0001 {
            normal = normal.map(|v| v / len);
        }
        for index in [a, b, c] {
            for axis in 0..3 {
                computed[index][axis] += normal[axis];
            }
        }
    }
    for normal in &mut computed {
        let len = length(*normal);
        *normal = if len > 0.0001 {
            normal.map(|v| v / len)
        } else {
            [0.0, 1.0, 0.0]
        };
    }
    Ok(computed.into())
}
