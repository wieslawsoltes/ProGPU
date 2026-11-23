//! Original GPU input queries and explicit execution policy.
//! Source geometry comes from an immutable retained index, not raster envelopes.
use bytemuck::{Pod, Zeroable};

#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{GpuHitIndex, HitQueryMetrics, HitQueryOutput, HitQueryWork};

pub const MAX_HIT_RESULTS: usize = 256;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum HitIntersectionDetail {
    NotCalculated = 0,
    Empty = 1,
    FullyInside = 2,
    FullyContains = 3,
    Intersects = 4,
}

#[derive(Debug, thiserror::Error)]
pub enum HitQueryError {
    #[error(transparent)]
    Drawing(#[from] crate::Error),
    #[error("invalid hit-query execution preference")]
    InvalidPreference,
    #[error("GPU hit-query candidate capacity overflow; no results published")]
    CandidateOverflow,
    #[error("GPU hit-query readback failed: {0}")]
    Readback(String),
}
pub type Result<T> = std::result::Result<T, HitQueryError>;

/// Original compute algorithms. Ordered stages preserve the single-pass order
/// and geometry; they never substitute a CPU or bounds-only query.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum HitQueryExecution {
    #[default]
    Automatic,
    SinglePass,
    OrderedStages,
}
impl std::str::FromStr for HitQueryExecution {
    type Err = HitQueryError;
    fn from_str(value: &str) -> Result<Self> {
        match value.trim().to_ascii_lowercase().as_str() {
            "" | "auto" => Ok(Self::Automatic),
            "single-pass" => Ok(Self::SinglePass),
            "ordered-stages" => Ok(Self::OrderedStages),
            _ => Err(HitQueryError::InvalidPreference),
        }
    }
}
impl HitQueryExecution {
    /// Explicit opt-in environment read; construction never changes process state.
    pub fn from_environment() -> Result<Self> {
        match std::env::var("PROGPU_HIT_TEST_EXECUTION") {
            Ok(value) => value.parse(),
            Err(std::env::VarError::NotPresent) => Ok(Self::Automatic),
            Err(_) => Err(HitQueryError::InvalidPreference),
        }
    }
    pub fn resolve(self, identity: HitQueryDeviceIdentity) -> Self {
        match self {
            Self::Automatic if identity.is_owned_d3d12() => Self::OrderedStages,
            Self::Automatic => Self::SinglePass,
            explicit => explicit,
        }
    }
}

/// Actual creator-supplied compiler provenance. Unknown/borrowed devices keep
/// the original automatic single-pass policy. Never infer these facts from OS,
/// adapter names or the success of a previous shader compilation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum HitQueryDeviceIdentity {
    #[default]
    Unknown,
    OtherBackend,
    OwnedD3d12Fxc,
    OwnedD3d12Dxc,
}
impl HitQueryDeviceIdentity {
    fn is_owned_d3d12(self) -> bool {
        matches!(self, Self::OwnedD3d12Fxc | Self::OwnedD3d12Dxc)
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct HitQueryComputeLimits {
    pub max_storage_buffer_binding_size: u64,
    pub max_storage_buffers_per_shader_stage: u32,
    pub max_compute_invocations_per_workgroup: u32,
    pub max_compute_workgroup_size_x: u32,
    pub max_compute_workgroups_per_dimension: u32,
}
impl HitQueryComputeLimits {
    pub fn admits_ordered(self, references: u32, max_buffer_size: u64) -> bool {
        let bytes = 32 + 8 * u64::from(references);
        references != 0
            && references != u32::MAX
            && bytes <= u64::from(u32::MAX)
            && bytes <= max_buffer_size
            && bytes <= self.max_storage_buffer_binding_size
            && self.max_storage_buffers_per_shader_stage >= 7
            && self.max_compute_invocations_per_workgroup >= 64
            && self.max_compute_workgroup_size_x >= 64
            && u64::from(references).div_ceil(64)
                <= u64::from(self.max_compute_workgroups_per_dimension)
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum HitQueryShape {
    Point([f32; 2]),
    Bounds { min: [f32; 2], max: [f32; 2] },
    Ellipse { min: [f32; 2], max: [f32; 2] },
}
impl HitQueryShape {
    #[cfg(feature = "gpu")]
    fn parameters(self) -> ([f32; 2], [f32; 2], u32, usize) {
        match self {
            Self::Point(point) => (point, point, 0, 0),
            Self::Bounds { min, max } => (min, max, 0x8000_0000, 1),
            Self::Ellipse { min, max } => (min, max, 0xc000_0000, 2),
        }
    }
}

/// Original exact 32-byte output. The summary's hit count can exceed retained
/// capacity and counts participating primitives, including duplicate owners.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct HitResult {
    pub hit: u32,
    pub id: i32,
    pub primitive_index: u32,
    pub z_index: f32,
    pub candidate_count: u32,
    pub nodes_visited: u32,
    pub precise_tests: u32,
    pub intersection_detail: u32,
}
impl HitResult {
    pub const MISS: Self = Self {
        hit: 0,
        id: -1,
        primitive_index: u32::MAX,
        z_index: f32::NEG_INFINITY,
        candidate_count: 0,
        nodes_visited: 0,
        precise_tests: 0,
        intersection_detail: 0,
    };
    pub fn has_hit(self) -> bool {
        self.hit != 0
    }
    pub fn detail(self) -> Option<HitIntersectionDetail> {
        use HitIntersectionDetail as D;
        match self.intersection_detail {
            0 => Some(D::NotCalculated),
            1 => Some(D::Empty),
            2 => Some(D::FullyInside),
            3 => Some(D::FullyContains),
            4 => Some(D::Intersects),
            _ => None,
        }
    }
}
#[cfg(feature = "gpu")]
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Query {
    point: [f32; 2],
    region_max: [f32; 2],
    root_node_index: u32,
    primitive_count: u32,
    node_count: u32,
    primitive_index_count: u32,
    flags: u32,
    path_segment_count: u32,
}
#[cfg(feature = "gpu")]
const _: () = assert!(size_of::<Query>() == 40);
const _: () = assert!(size_of::<HitResult>() == 32);
