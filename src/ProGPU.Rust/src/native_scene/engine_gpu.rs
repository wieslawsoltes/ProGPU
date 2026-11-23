//! Native command-engine GPU bindings and demand-created render pipelines.
//!
//! These resources consume original native pages and the unchanged owned WGSL.
//! They do not infer layer targets, masks, source opacity or presentation from a
//! root clear. Ordered execution supplies those independently prepared owners.
use super::{engine_materials::*, engine_resources::NativeImageLayouts, wire};
use crate::{Error, shaders};
use std::{collections::HashMap, sync::Arc};
use wire::{WireRecord, constants::*};
mod backdrop;
mod bundle;
mod clear;
mod commands;
pub use backdrop::{NativeBackdropReplayMetrics, PreparedNativeBackdrop};
pub mod cache;
mod composite;
mod frames;
mod pass;
mod renderer;
mod replay;
pub use bundle::PreparedNativeReplayBundle;
pub use composite::PreparedNativeLayerDraw;
pub use frames::{NativeSceneFrameMetrics, NativeSceneFrames};
pub use renderer::{
    NativeRetainedCpuState, NativeSceneRecoveryError, NativeSceneRenderMetrics,
    NativeSceneRenderer, NativeSceneRendererOptions,
};
pub use replay::NativeReplayExecutionMetrics;
mod resources;
mod target;
pub use clear::NativeTargetClearEngine;
pub use resources::{NativeScenePageEngine, NativeScenePageMetrics, PreparedNativeScenePages};
pub use target::NativeRenderAttachment;
mod frame;
mod layout;
mod mask;
mod material;
mod pages;
mod pipeline;
pub use commands::PreparedNativeDraw;
pub use frame::{NativeFrameBindings, NativeGpuUniforms};
pub use layout::NativeRenderLayouts;
pub use mask::NativeMaskBinding;
pub use material::{NativeMaterialGpuMetrics, PreparedNativeMaterials};
pub use pages::{NativeGlyphFrameBindings, NativeGlyphRenderPage};
pub use pages::{NativePathRenderPage, NativeVectorGpuMetrics, NativeVectorRenderPage};
pub use pipeline::{
    NativeGroupPipeline, NativeMaskMode, NativePipelineCache, NativeRenderPipeline,
    NativeWriteChannels, is_advanced_group_blend,
};
