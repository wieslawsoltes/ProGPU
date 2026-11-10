//! Original ordered native execution state and actual target domains.
//!
//! These are CPU preparation records. They retain the admitted stream generation;
//! an adapter still admits actual providers, GPU resources and execution families.
use super::{NativeScene, wire};
use crate::Error;
use std::sync::Arc;
use wire::{WireRecord, constants::*};
pub mod budget;
mod flags;
pub use flags::validate_engine_flags;
mod replay_plan;
pub use replay_plan::{
    NativeReplayAdvanced, NativeReplayBundle, NativeReplayComposite, NativeReplayDraw,
    NativeReplayEffect, NativeReplayLayerPop, NativeReplayLayerPush, NativeReplayMetrics,
    NativeReplayOperation, NativeReplayPlan, NativeReplayRgb, NativeReplayShader,
};
mod draw_plan;
pub use draw_plan::{
    NativeDrawCommandCost, NativeDrawExecutionPlan, NativeDrawFamily, NativeDrawPlanMetrics,
};
mod resources;
pub use resources::{NativeSceneCompileOptions, NativeSceneRasterPages, NativeSceneResourcePlan};
mod clear;
pub use clear::NativeTargetClearOperation;
mod layer_plan;
pub use layer_plan::{
    NativeLayerExecutionPlan, NativeLayerOccurrence, NativeLayerPlanMetrics,
    NativeSceneBudgetInput, NativeSceneExecutionBudget, NativeTargetFormat,
};
mod frame;
mod scissor;
mod state;
mod target;
mod transform;
pub use frame::*;
pub use scissor::*;
pub use state::{
    NativeStateCursor, nearest_guideline_index, uniform_guideline_translation, wpf_guideline_offset,
};
pub use target::{NativeLayerTargetCursor, layer_output_frame, read_layer};
pub use transform::*;

/// Retains the exact stream and the resolved frame at one ordered command.
#[derive(Clone, Debug)]
pub struct NativeCommandFrame {
    pub(crate) scene: Arc<NativeScene>,
    pub(crate) command_index: u32,
    pub(crate) source_state: wire::State,
    pub(crate) source_state_dpi: [f32; 2],
    pub(crate) state: wire::State,
    pub(crate) target: NativeScissor,
    pub(crate) presentation: wire::Presentation,
    pub(crate) dpi_scale: f32,
    pub(crate) ignores_alpha: bool,
}
impl NativeCommandFrame {
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn command_index(&self) -> u32 {
        self.command_index
    }
    pub fn source_state(&self) -> wire::State {
        self.source_state
    }
    /// Initial source cursor DPI, independent of nested target presentations.
    pub fn source_state_dpi(&self) -> [f32; 2] {
        self.source_state_dpi
    }
    pub fn state(&self) -> wire::State {
        self.state
    }
    pub fn target(&self) -> NativeScissor {
        self.target
    }
    pub fn presentation(&self) -> wire::Presentation {
        self.presentation
    }
    pub fn dpi_scale(&self) -> f32 {
        self.dpi_scale
    }
    pub fn ignores_alpha(&self) -> bool {
        self.ignores_alpha
    }
    pub fn command(&self) -> super::CommandView<'_> {
        self.scene
            .envelope()
            .command(self.command_index)
            .expect("retained admitted command")
    }
}

/// Lockstep original target/state traversal, with no callbacks or GPU mutation.
pub struct NativeExecutionCursor {
    scene: Arc<NativeScene>,
    frame: NativeSceneFrame,
    index: u32,
    source_state_dpi: [f32; 2],
    state: NativeStateCursor,
    target: NativeLayerTargetCursor,
}
impl NativeExecutionCursor {
    pub fn new(scene: Arc<NativeScene>, frame: NativeSceneFrame) -> Result<Self, Error> {
        let presentation = frame.resolve_presentation().ok_or(Error::InvalidInput)?;
        Ok(Self {
            state: NativeStateCursor::new(
                scene.clone(),
                [presentation.dpi_scale_x, presentation.dpi_scale_y],
            ),
            target: NativeLayerTargetCursor::new(
                scene.clone(),
                [frame.width, frame.height],
                presentation,
            ),
            source_state_dpi: [presentation.dpi_scale_x, presentation.dpi_scale_y],
            scene,
            frame,
            index: 0,
        })
    }
    pub fn next_command(&mut self) -> Result<Option<NativeCommandFrame>, Error> {
        if self.index == self.scene.envelope().header().command_count {
            return Ok(None);
        }
        let index = self.index;
        let command = self
            .scene
            .envelope()
            .command(index)
            .ok_or(Error::InvalidInput)?
            .record;
        let target = self.target.advance(command)?;
        let source_state = self.state.advance(command)?;
        let presentation = self.target.current_presentation();
        let state = localize_state(source_state, target, presentation, self.frame.dpi_scale);
        self.index += 1;
        Ok(Some(NativeCommandFrame {
            scene: self.scene.clone(),
            command_index: index,
            source_state,
            source_state_dpi: self.source_state_dpi,
            state,
            target,
            presentation,
            dpi_scale: self.frame.dpi_scale,
            ignores_alpha: self.target.current_ignores_alpha(),
        }))
    }
    pub fn state_cursor(&self) -> &NativeStateCursor {
        &self.state
    }
    pub fn target_cursor(&self) -> &NativeLayerTargetCursor {
        &self.target
    }
}
pub fn identity_state() -> wire::State {
    wire::State {
        struct_size: wire::State::WIRE_SIZE as u32,
        transform: identity_transform(),
        opacity: 1.0,
        ..Default::default()
    }
}
pub fn identity_transform() -> wire::Affine2d {
    wire::Affine2d {
        m11: 1.0,
        m22: 1.0,
        ..Default::default()
    }
}
pub fn default_layer() -> wire::Layer {
    wire::Layer {
        struct_size: wire::Layer::WIRE_SIZE as u32,
        opacity: 1.0,
        blend_mode: BLEND_SRC_OVER,
        mask_resource_index: SCENE_NO_INDEX,
        effect_resource_index: SCENE_NO_INDEX,
        ..Default::default()
    }
}
pub fn layer_requires_materialization(layer: &wire::Layer) -> bool {
    layer.flags
        & (SCENE_LAYER_BACKDROP
            | SCENE_LAYER_FORCE_ISOLATION
            | SCENE_LAYER_CACHE_CONTENT
            | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
            | SCENE_LAYER_IGNORE_ALPHA)
        != 0
        || layer.opacity != 1.0
        || layer.blend_mode != BLEND_SRC_OVER
        || layer.mask_resource_index != SCENE_NO_INDEX
        || layer.effect_resource_index != SCENE_NO_INDEX
}
fn scale_translate_four(coordinates: [f32; 4], scales: [f32; 4], origins: [f32; 4]) -> [f32; 4] {
    (wide::f32x4::from(coordinates) * wide::f32x4::from(scales) + wide::f32x4::from(origins))
        .to_array()
}
fn presentation_edges(bounds: wire::ImageRect, p: wire::Presentation) -> [f32; 4] {
    scale_translate_four(
        [
            bounds.x,
            bounds.y,
            bounds.x + bounds.width,
            bounds.y + bounds.height,
        ],
        [p.dpi_scale_x, p.dpi_scale_y, p.dpi_scale_x, p.dpi_scale_y],
        [
            p.viewport_x as f32,
            p.viewport_y as f32,
            p.viewport_x as f32,
            p.viewport_y as f32,
        ],
    )
}
fn scalar_presentation(size: [u32; 2], dpi: f32) -> wire::Presentation {
    wire::Presentation {
        struct_size: wire::Presentation::WIRE_SIZE as u32,
        viewport_width: size[0],
        viewport_height: size[1],
        dpi_scale_x: dpi,
        dpi_scale_y: dpi,
        ..Default::default()
    }
}
fn target_domain_extent(frame: u32, origin: u32, extent: u32) -> u32 {
    frame.max((u64::from(origin) + u64::from(extent)).min(u64::from(u32::MAX)) as u32)
}
