//! Original native mask resources and actual target-local GPU production.
use super::*;
use crate::{
    mask::{MaskSampling, MaskUniforms},
    native_scene::execution::*,
};
use bytemuck::Zeroable;
mod brush;
mod clip;
#[cfg(feature = "gpu")]
mod gpu;
mod parse;
mod sampling;
#[cfg(feature = "gpu")]
pub use gpu::{
    NativeMaskEngine, NativePictureMaskCapture, NativePictureMaskResolver, PreparedNativeMask,
};
/// Exact parent target selection; source-local axis-clip layers supply their own
/// actual extent rather than inheriting the receiving attachment's dimensions.
#[derive(Clone, Copy, Debug)]
pub struct NativeMaskTarget {
    pub extent: NativeScissor,
    pub presentation: wire::Presentation,
    pub dpi_scale: f32,
    pub source_state_dpi: [f32; 2],
}
impl NativeMaskTarget {
    pub fn from_command(frame: &NativeCommandFrame) -> Self {
        Self {
            extent: frame.target,
            presentation: frame.presentation,
            dpi_scale: frame.dpi_scale,
            source_state_dpi: frame.source_state_dpi,
        }
    }
    pub fn size(self) -> [u32; 2] {
        [self.extent.width, self.extent.height]
    }
    fn validate(self) -> Result<(), Error> {
        let e = self.extent;
        let p = self.presentation;
        if self.size().into_iter().any(|n| n == 0 || n > 16384)
            || e.x > 16384 - e.width
            || e.y > 16384 - e.height
            || [
                self.dpi_scale,
                self.source_state_dpi[0],
                self.source_state_dpi[1],
                p.dpi_scale_x,
                p.dpi_scale_y,
            ]
            .into_iter()
            .any(|v| !v.is_finite() || v <= 0.0)
            || p.viewport_width == 0
            || p.viewport_height == 0
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
}
/// Immutable source descriptor, parsed payloads and original target identity.
#[derive(Clone, Debug)]
pub struct NativeMaskRequest {
    scene: Arc<NativeScene>,
    resource_index: u32,
    target: NativeMaskTarget,
    composite_state: Option<wire::State>,
    node: Arc<MaskNode>,
}
impl NativeMaskRequest {
    pub fn new(
        scene: Arc<NativeScene>,
        resource_index: u32,
        target: NativeMaskTarget,
        composite_state: Option<wire::State>,
    ) -> Result<Self, Error> {
        target.validate()?;
        let node = parse::resource(&scene, resource_index)?;
        let result = Self {
            scene,
            resource_index,
            target,
            composite_state,
            node: Arc::new(node),
        };
        result.preflight()?;
        Ok(result)
    }
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn resource_index(&self) -> u32 {
        self.resource_index
    }
    /// Original admitted resource kind; vector-mask proof remains independent
    /// of a generic sampled image or an unrelated coverage texture.
    pub fn kind(&self) -> u32 {
        match self.node.as_ref() {
            MaskNode::Analytic(_) => SCENE_LAYER_MASK_ROUNDED_RECTANGLE,
            MaskNode::Coverage(..) => SCENE_LAYER_MASK_COVERAGE_BITMAP,
            MaskNode::Chain(_) => SCENE_LAYER_MASK_ANALYTIC_CHAIN,
            MaskNode::Vector(_) => SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN,
            MaskNode::Brush(_) => SCENE_LAYER_MASK_BRUSH,
            MaskNode::Geometry(_) => SCENE_LAYER_MASK_GEOMETRY,
            MaskNode::Picture(..) => SCENE_LAYER_MASK_PICTURE,
            MaskNode::Composite { .. } => SCENE_LAYER_MASK_COMPOSITE,
        }
    }
    pub fn target(&self) -> NativeMaskTarget {
        self.target
    }
    pub fn composite_state(&self) -> Option<wire::State> {
        self.composite_state
    }
    /// Device-independent source planning. The engine's preflight adds live
    /// device limits, retained atlas extent and nested-source resource costs.
    pub fn preflight(&self) -> Result<NativeMaskMetrics, Error> {
        self.node.preflight(self)
    }
    fn cursor(&self) -> NativeStateCursor {
        NativeStateCursor::new(self.scene.clone(), self.target.source_state_dpi)
    }
}
/// Candidate costs from preflight; actual preparation work from a prepared mask.
/// Texture bytes retain live output/cache storage; uploads and cache hits remain
/// separate. Geometry upload bytes include original material/outline storage.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeMaskMetrics {
    pub texture_bytes: u64,
    /// Actual newly allocated storage, separate from retained clip-cache bytes.
    pub texture_allocation_bytes: u64,
    pub texture_allocations: u64,
    /// Actual local mask-engine submissions, excluding nested picture executors.
    pub submission_count: u64,
    pub texture_upload_bytes: u64,
    pub uniform_upload_bytes: u64,
    pub geometry_upload_bytes: u64,
    pub render_passes: u32,
    pub path_rasters: u32,
    pub picture_captures: u32,
    pub picture_cache_hits: u32,
    pub clip_cache_hits: u32,
    pub compute_passes: u32,
    pub coverage_staging_bytes: u64,
}
impl NativeMaskMetrics {
    pub fn add(&mut self, b: Self) -> Result<(), Error> {
        macro_rules! add {
            ($field:ident) => {
                self.$field = self.$field.checked_add(b.$field).ok_or(Error::Capacity)?;
            };
        }
        add!(texture_bytes);
        add!(texture_allocation_bytes);
        add!(texture_allocations);
        add!(submission_count);
        add!(texture_upload_bytes);
        add!(uniform_upload_bytes);
        add!(geometry_upload_bytes);
        add!(render_passes);
        add!(path_rasters);
        add!(picture_captures);
        add!(picture_cache_hits);
        add!(clip_cache_hits);
        add!(compute_passes);
        add!(coverage_staging_bytes);
        Ok(())
    }
}
/// Real child source generation, target frame and original sampling descriptor.
#[derive(Clone, Debug)]
pub struct NativePictureMaskSource {
    pub scene: Arc<NativeScene>,
    pub descriptor: wire::LayerPictureMask,
    pub frame: NativeSceneFrame,
}
#[derive(Clone, Debug)]
struct VectorMask {
    header: wire::LayerVectorMask,
    paths: Arc<[wire::ClipPath]>,
    segments: Arc<[wire::PathSegment]>,
    nodes: Arc<[wire::PathBooleanNode]>,
}
#[derive(Clone, Debug)]
struct BrushMask {
    header: wire::LayerBrushMask,
    stops: Arc<[wire::GradientStop]>,
}
#[derive(Clone, Debug)]
struct GeometryMask {
    header: wire::LayerGeometryMask,
    primitives: Arc<[wire::GeometryPrimitive]>,
    stops: Arc<[wire::GradientStop]>,
}
#[derive(Clone, Debug)]
enum MaskNode {
    Analytic(wire::LayerMask),
    Coverage(wire::LayerCoverageMask, Arc<[u8]>),
    Chain(wire::LayerMaskChain),
    Vector(VectorMask),
    Brush(BrushMask),
    Geometry(GeometryMask),
    Picture(wire::LayerPictureMask, Arc<NativeScene>),
    Composite {
        opacity: f32,
        children: Arc<[MaskNode]>,
    },
}
impl MaskNode {
    fn preflight(&self, request: &NativeMaskRequest) -> Result<NativeMaskMetrics, Error> {
        let area = request
            .target
            .size()
            .into_iter()
            .map(u64::from)
            .product::<u64>();
        let mut m = NativeMaskMetrics {
            uniform_upload_bytes: 96,
            ..Default::default()
        };
        match self {
            Self::Analytic(source) => {
                sampling::analytic(source, request.target)?;
            }
            Self::Chain(source) => {
                for mask in &source.masks[..source.mask_count as usize] {
                    sampling::analytic(mask, request.target)?;
                }
                m.uniform_upload_bytes += 288;
            }
            Self::Coverage(source, bytes) => {
                sampling::coverage(source, request.target)?;
                m.texture_bytes = u64::from(source.width) * u64::from(source.height);
                m.texture_upload_bytes = bytes.len() as u64;
            }
            Self::Vector(source) => {
                let plan = clip::plan(source, request.target)?;
                m.texture_bytes = area * 3 + u64::from(plan.atlas_size()).pow(2);
                m.render_passes = source
                    .header
                    .path_count
                    .checked_mul(2)
                    .ok_or(Error::Capacity)?;
                m.path_rasters = u32::try_from(plan.raster_count()).map_err(|_| Error::Capacity)?;
                m.uniform_upload_bytes += u64::from(source.header.path_count) * 256;
                m.geometry_upload_bytes = u64::from(source.header.path_count) * (64 + 24);
            }
            Self::Brush(source) => {
                let plan = brush::compile_brush(source, request)?;
                m.texture_bytes = area;
                m.render_passes = 1;
                m.uniform_upload_bytes += 224;
                m.geometry_upload_bytes = plan.byte_cost();
            }
            Self::Geometry(source) => {
                let plan = brush::compile_geometry(source, request)?;
                m.texture_bytes = area;
                m.render_passes = 1;
                m.uniform_upload_bytes += 224;
                m.geometry_upload_bytes = plan.byte_cost();
            }
            Self::Picture(source, scene) => {
                sampling::picture(source, scene.clone(), request)?;
                let frame = resolve_picture_frame(
                    source,
                    request.target.extent,
                    request.target.dpi_scale,
                    Some(request.target.presentation),
                )
                .ok_or(Error::InvalidInput)?;
                m.texture_bytes = u64::from(frame.width) * u64::from(frame.height) * 4;
                if m.texture_bytes > u64::from(SCENE_MAX_LAYER_BYTES) {
                    return Err(Error::Capacity);
                }
                m.picture_captures = 1;
            }
            Self::Composite { children, .. } => {
                m.texture_bytes = area * 2;
                m.render_passes = children.len() as u32;
                m.uniform_upload_bytes += (children.len() as u64) * 256;
                for child in children.iter() {
                    m.add(child.preflight(request)?)?;
                }
            }
        }
        Ok(m)
    }
}
