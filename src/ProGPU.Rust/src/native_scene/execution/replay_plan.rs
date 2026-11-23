//! Original retained operation order, separate from GPU bundle realization.
use super::*;
use crate::{
    geometry::VectorVertex,
    native_scene::{
        draw::{NativeAnalyticDraw, NativeGlyphDraw, NativePathDraw},
        engine_resources::{
            effects::NativeEffectPlan,
            layers::{
                NativeLayerTargetId, NativeLayerTargetPlan, NativeLayerTargetRequest,
                native_layer_quad, native_transformed_layer_quad,
            },
            masks::{NativeMaskRequest, NativeMaskTarget},
        },
        presentation_content_hash,
    },
};
mod compile;
mod composite;
mod effect;
pub use composite::{NativeReplayAdvanced, NativeReplayComposite};
pub use effect::{NativeReplayEffect, NativeReplayShader};

/// Every ordinary draw owns an exact range in its retained family page.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeReplayDraw {
    Vector {
        command_index: u32,
        draw: NativeAnalyticDraw,
    },
    Path {
        command_index: u32,
        draw: NativePathDraw,
    },
    Glyph {
        command_index: u32,
        draw: NativeGlyphDraw,
    },
    Image(u32),
    ThreeD(u32),
}
#[derive(Clone, Debug)]
pub struct NativeReplayBundle {
    pub first_command: u32,
    pub last_command: u32,
    pub target: NativeLayerTargetId,
    pub scissor: NativeScissor,
    pub uses_depth: bool,
    pub mask: Option<Arc<NativeMaskRequest>>,
    pub draws: Arc<[NativeReplayDraw]>,
}
/// A source scope remains represented even if its parent is not drawable.
#[derive(Clone)]
pub struct NativeReplayLayerPush {
    pub occurrence: u32,
    pub command_index: u32,
    pub operation_id: u64,
    pub target: NativeLayerTargetId,
    pub parent: NativeLayerTargetId,
    pub source_extent: NativeScissor,
    pub parent_extent: NativeScissor,
    pub ignore_alpha: bool,
    pub backdrop: bool,
    pub backdrop_origin: [u32; 2],
    pub first_backdrop_resolve_vertex: Option<u32>,
    pub cache_content: bool,
    pub cache_identity: u64,
    pub cache_content_revision: u64,
    pub effect_cache_operation_id: u64,
    pub can_skip_content_on_effect_cache: bool,
    pub final_sample_shader: bool,
    pub effect_count: u32,
    pub effect: Option<NativeReplayEffect>,
    pub pop_operation: u32,
}
#[derive(Clone)]
pub struct NativeReplayLayerPop {
    pub occurrence: u32,
    pub command_index: u32,
    pub operation_id: u64,
    pub source: NativeLayerTargetId,
    pub target: NativeLayerTargetId,
    pub target_ignores_alpha: bool,
    pub source_extent: NativeScissor,
    pub parent_extent: NativeScissor,
    pub composite: NativeReplayComposite,
    pub backdrop: bool,
    pub cache_content: bool,
    pub cache_identity: u64,
    pub cache_content_revision: u64,
    pub effect: Option<NativeReplayEffect>,
    pub push_operation: u32,
}
#[derive(Clone)]
pub struct NativeReplayRgb {
    pub command_index: u32,
    pub page_index: u32,
    pub target: NativeLayerTargetId,
    pub allocation: NativeLayerTargetRequest,
    pub scissor: NativeScissor,
    pub mask: Option<Arc<NativeMaskRequest>>,
}
/// Empty Clear/RGB variants retain original bundle barriers without GPU work.
#[derive(Clone)]
pub enum NativeReplayOperation {
    Bundle(NativeReplayBundle),
    PushLayer(NativeReplayLayerPush),
    PopLayer(NativeReplayLayerPop),
    Clear {
        command_index: u32,
        draw: Option<NativeTargetClearOperation>,
    },
    Rgb {
        command_index: u32,
        draw: Option<NativeReplayRgb>,
    },
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeReplayMetrics {
    /// Original ordinary replay compilation count. RGB direct-pass draws are
    /// reported separately when those actual GPU passes execute.
    pub draw_calls: u64,
    pub bundle_count: u32,
    pub bundle_draw_count: u64,
    pub family_switches: u64,
    pub layer_quad_count: u64,
    pub advanced_count: u32,
    pub builtin_effect_count: u32,
    pub builtin_effect_uniform_bytes: u64,
    pub shader_effect_count: u32,
    /// Original 528/592/608-byte bookkeeping, not actual shader buffer sizes.
    pub shader_uniform_bytes: u64,
    pub mask_request_count: u32,
    pub rgb_draw_count: u32,
    pub target_clear_count: u32,
}
/// Immutable CPU program. Cache eligibility retains matched source scopes;
/// cache hits require the actual submitted GPU output generation at replay.
pub struct NativeReplayPlan {
    source: Arc<NativeSceneResourcePlan>,
    targets: Arc<NativeLayerTargetPlan>,
    operations: Arc<[NativeReplayOperation]>,
    vertices: Arc<[VectorVertex]>,
    root_copy_vertex: Option<u32>,
    metrics: NativeReplayMetrics,
}
impl NativeReplayPlan {
    pub fn source(&self) -> &Arc<NativeSceneResourcePlan> {
        &self.source
    }
    pub fn targets(&self) -> &Arc<NativeLayerTargetPlan> {
        &self.targets
    }
    pub fn operations(&self) -> &[NativeReplayOperation] {
        &self.operations
    }
    pub fn vertices(&self) -> &[VectorVertex] {
        &self.vertices
    }
    pub fn root_copy_vertex(&self) -> Option<u32> {
        self.root_copy_vertex
    }
    pub fn metrics(&self) -> NativeReplayMetrics {
        self.metrics
    }
}
fn push<T>(values: &mut Vec<T>, value: T) -> Result<(), Error> {
    values.try_reserve(1).map_err(|_| Error::Capacity)?;
    values.push(value);
    Ok(())
}
fn append_quad(vertices: &mut Vec<VectorVertex>, quad: [VectorVertex; 4]) -> Result<u32, Error> {
    let first = u32::try_from(vertices.len()).map_err(|_| Error::Capacity)?;
    first.checked_add(4).ok_or(Error::Capacity)?;
    vertices.try_reserve(4).map_err(|_| Error::Capacity)?;
    vertices.extend_from_slice(&quad);
    Ok(first)
}
fn allocation(
    plan: &NativeLayerTargetPlan,
    target: NativeLayerTargetId,
) -> Result<NativeLayerTargetRequest, Error> {
    if let Some(request) = plan.requests().iter().find(|r| r.target == target) {
        return Ok(*request);
    }
    if target == NativeLayerTargetId::Root {
        let frame = plan.frame();
        return Ok(NativeLayerTargetRequest {
            target,
            size: [frame.width, frame.height],
            color: false,
            effects: false,
            depth: false,
            cache_identity: 0,
        });
    }
    Err(Error::ResourceDomain)
}
fn mask_request(
    source: &NativeSceneResourcePlan,
    resource: u32,
    frame: &NativeCommandFrame,
    extent: NativeScissor,
    state: Option<wire::State>,
) -> Result<Option<Arc<NativeMaskRequest>>, Error> {
    if resource == SCENE_NO_INDEX {
        return Ok(None);
    }
    Ok(Some(Arc::new(NativeMaskRequest::new(
        source.scene().clone(),
        resource,
        NativeMaskTarget {
            extent,
            presentation: frame.presentation,
            dpi_scale: frame.dpi_scale,
            source_state_dpi: frame.source_state_dpi,
        },
        state,
    )?)))
}
