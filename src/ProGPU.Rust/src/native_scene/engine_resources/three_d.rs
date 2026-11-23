//! Original native retained 3D pages, independent of managed Viewport3D.
use super::*;
use crate::native_scene::execution::{NativeCommandFrame, NativeSceneFrame};
mod compile;
mod records;
pub use records::{NativeThreeDCamera, NativeThreeDEdge, NativeThreeDLine, NativeThreeDMesh};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{NativeThreeDDepthTarget, NativeThreeDPageEngine, PreparedNativeThreeDPage};

/// Resolved CPU record production; actual native drawing remains on the GPU.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeThreeDLoweringPath {
    CpuSimd,
    ScalarReference,
}
impl NativeThreeDLoweringPath {
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
/// Original command batch, including its actual ordered source/target frame.
#[derive(Clone, Debug)]
pub struct NativeThreeDDraw {
    pub frame: NativeCommandFrame,
    pub first_record: u32,
    pub record_count: u32,
}
/// Per-mesh original replay values, after triangle-strip expansion.
#[derive(Clone, Copy, Debug)]
pub struct NativeThreeDMeshDraw {
    pub topology: u32,
    pub flags: u32,
    pub index_count: u32,
    pub edge_offset: u32,
    pub edge_count: u32,
    pub edge_vertex_count: u32,
    /// Exact selected primary image binding. None selects the original sentinel.
    pub material_image: Option<usize>,
}
/// Actual storage/upload costs, distinct from the original coarse admission estimate.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeThreeDMetrics {
    pub command_draws: u64,
    pub gpu_draws: u64,
    pub storage_bytes: u64,
    pub upload_bytes: u64,
    pub buffer_allocations: u64,
    pub texture_bytes: u64,
    pub texture_allocations: u64,
    pub bind_groups: u64,
    pub pipelines_created: u64,
    pub native_admission_bytes: u64,
}
/// Immutable native records retain the admitted scene, exact frame and sideband
/// image generation. Constructing this page performs no GPU work.
#[derive(Clone, Debug)]
pub struct NativeThreeDPage {
    owner: Arc<NativeScene>,
    frame: NativeSceneFrame,
    draws: Arc<[NativeThreeDDraw]>,
    cameras: Arc<[NativeThreeDCamera]>,
    lines: Arc<[NativeThreeDLine]>,
    meshes: Arc<[NativeThreeDMesh]>,
    vertices: Arc<[wire::Mesh3dVertex]>,
    indices: Arc<[u32]>,
    lights: Arc<[wire::Light3d]>,
    materials: Arc<[wire::Brush]>,
    stops: Arc<[wire::GradientStop]>,
    edges: Arc<[NativeThreeDEdge]>,
    mesh_draws: Arc<[NativeThreeDMeshDraw]>,
    external: Arc<[NativeExternalImageInfo]>,
    metrics: NativeThreeDMetrics,
    lowering: NativeThreeDLoweringPath,
}
impl NativeThreeDPage {
    pub fn lowering_path(&self) -> NativeThreeDLoweringPath {
        self.lowering
    }
    pub fn owner(&self) -> &Arc<NativeScene> {
        &self.owner
    }
    pub fn frame(&self) -> NativeSceneFrame {
        self.frame
    }
    pub fn draws(&self) -> &[NativeThreeDDraw] {
        &self.draws
    }
    pub fn cameras(&self) -> &[NativeThreeDCamera] {
        &self.cameras
    }
    pub fn lines(&self) -> &[NativeThreeDLine] {
        &self.lines
    }
    pub fn meshes(&self) -> &[NativeThreeDMesh] {
        &self.meshes
    }
    pub fn vertices(&self) -> &[wire::Mesh3dVertex] {
        &self.vertices
    }
    pub fn indices(&self) -> &[u32] {
        &self.indices
    }
    pub fn lights(&self) -> &[wire::Light3d] {
        &self.lights
    }
    pub fn materials(&self) -> &[wire::Brush] {
        &self.materials
    }
    pub fn gradient_stops(&self) -> &[wire::GradientStop] {
        &self.stops
    }
    pub fn edges(&self) -> &[NativeThreeDEdge] {
        &self.edges
    }
    pub fn mesh_draws(&self) -> &[NativeThreeDMeshDraw] {
        &self.mesh_draws
    }
    pub fn external_images(&self) -> &[NativeExternalImageInfo] {
        &self.external
    }
    pub fn metrics(&self) -> NativeThreeDMetrics {
        self.metrics
    }
    /// Exact native family identity supplied by the shared original hash code.
    pub fn content_hash(&self, resource_scope: u64) -> u64 {
        self.owner.content_hashes(resource_scope).three_d
    }
    pub fn storage_sizes(&self) -> [u64; 9] {
        if self.draws.is_empty() {
            return [0; 9];
        }
        let lengths = [
            self.cameras.len(),
            self.lines.len(),
            self.meshes.len(),
            self.vertices.len(),
            self.indices.len(),
            self.lights.len(),
            self.materials.len(),
            self.stops.len(),
            self.edges.len(),
        ];
        std::array::from_fn(|i| (lengths[i] as u64 * MINIMUM_SIZES[i]).max(MINIMUM_SIZES[i]))
    }
}
const MINIMUM_SIZES: [u64; 9] = [176, 128, 272, 48, 4, 80, 256, 32, 80];
fn read<T: WireRecord>(bytes: &[u8], offset: usize) -> Result<T, Error> {
    T::read(bytes.get(offset..).ok_or(Error::InvalidInput)?).ok_or(Error::InvalidInput)
}
fn count(value: usize) -> Result<u32, Error> {
    u32::try_from(value).map_err(|_| Error::Capacity)
}
fn add(a: usize, b: usize) -> Result<usize, Error> {
    a.checked_add(b).ok_or(Error::Capacity)
}
fn bytes(count: usize, stride: usize) -> Result<usize, Error> {
    count.checked_mul(stride).ok_or(Error::Capacity)
}
