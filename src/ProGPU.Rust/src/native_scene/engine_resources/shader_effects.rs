//! Original native programmable effect resources at their actual layer boundary.
//! The replay plan retains scope admission; this adapter retains exact versioned
//! program bytes, ordered picture dependencies and target-specific GPU owners.
use super::*;
use crate::{
    native_scene::{
        engine_resources::layers::NativeLayerTargetId,
        execution::{NativeReplayShader, budget::MAX_TOTAL_COMPILED_BYTES},
    },
    shader_effect::{SampleLattice, ShaderFrame, ShaderResource},
};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{
    NativeShaderEffectEngine, NativeShaderEffectMetrics, NativeShaderPictureCapture,
    NativeShaderPictureResolver, PreparedNativeShaderEffect,
};

/// CPU-owned version-one through version-six native effect and its original
/// earlier-resource dependencies. Source callbacks are not invoked here.
pub struct NativeShaderEffectPlan {
    replay: Arc<NativeReplayShader>,
    resource: ShaderResource,
    pictures: Vec<NativePictureImageSource>,
    input: Option<usize>,
    sampler: Option<usize>,
    picture_bytes: u64,
}
impl NativeShaderEffectPlan {
    pub fn new(replay: Arc<NativeReplayShader>) -> Result<Self, Error> {
        let entry = replay
            .scene
            .envelope()
            .resource(replay.resource_index)
            .ok_or(Error::InvalidInput)?;
        if entry.record.kind != SCENE_RESOURCE_WPF_SHADER_EFFECT
            || replay.source == NativeLayerTargetId::Root
        {
            return Err(Error::ResourceDomain);
        }
        let resource = ShaderResource::read(
            Arc::<[u8]>::from(entry.payload),
            Arc::<[u8]>::from(entry.auxiliary),
        )?;
        let program_offset = entry
            .payload
            .len()
            .checked_sub(wire::ShaderEffect::WIRE_SIZE)
            .ok_or(Error::InvalidInput)?;
        if !record_matches(replay.program, &entry.payload[program_offset..])
            || resource.input_resource_index() != index(replay.input_resource_index)
            || resource.sampler_resource_index() != index(replay.sampler_resource_index)
            || resource.derivative_register() != index(replay.derivative_register)
            || replay.suppressed
                != (replay.final_sample()
                    && (!replay.parent_extent.drawable || !replay.source_extent.drawable))
        {
            return Err(Error::ResourceDomain);
        }
        let capture_size = match resource.frame() {
            ShaderFrame::Legacy => {
                if replay.capture.is_some() || replay.sample.is_some() || replay.affine.is_some() {
                    return Err(Error::ResourceDomain);
                }
                None
            }
            ShaderFrame::Capture(frame) => {
                if !replay
                    .capture
                    .is_some_and(|r| record_matches(r, &entry.payload[32..104]))
                    || replay.sample.is_some()
                    || replay.affine.is_some()
                    || frame.capture_size
                        != [replay.source_extent.width, replay.source_extent.height]
                {
                    return Err(Error::ResourceDomain);
                }
                None
            }
            ShaderFrame::Samples(frame) => {
                if replay.capture.is_some()
                    || replay.affine.is_some()
                    || !replay
                        .sample
                        .is_some_and(|r| record_matches(r, &entry.payload[32..160]))
                {
                    return Err(Error::ResourceDomain);
                }
                if !replay.suppressed {
                    frame.resolve()?.project(Self::lattice_for(&replay))?;
                }
                Some(frame.capture_size)
            }
            ShaderFrame::Affine(frame) => {
                if replay.capture.is_some()
                    || !replay
                        .sample
                        .is_some_and(|r| record_matches(r, &entry.payload[32..160]))
                    || !replay
                        .affine
                        .is_some_and(|r| record_matches(r, &entry.payload[32..176]))
                {
                    return Err(Error::ResourceDomain);
                }
                if !replay.suppressed {
                    frame.resolve()?.project(Self::lattice_for(&replay))?;
                }
                Some(frame.placement.capture_size)
            }
        };
        if replay.source_vector_mask != (replay.source_vector_mask_resource_index != SCENE_NO_INDEX)
        {
            return Err(Error::ResourceDomain);
        }
        // Legacy layer masks belong to the later ordinary composite. Only the
        // final-sample shader consumes this exact source vector-mask resource.
        if replay.final_sample() && replay.source_vector_mask {
            let mask = replay
                .scene
                .envelope()
                .resource(replay.source_vector_mask_resource_index)
                .ok_or(Error::InvalidInput)?;
            let vector = wire::LayerVectorMask::read(mask.payload).ok_or(Error::InvalidInput)?;
            if mask.record.kind != SCENE_RESOURCE_LAYER_MASK
                || vector.kind != SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN
                || vector.opacity != 1.0
            {
                return Err(Error::ResourceDomain);
            }
        }
        let mut pictures = allocate(2)?;
        let mut picture_bytes = 0u64;
        let mut input = None;
        let mut sampler = None;
        for (position, resource_index) in [
            resource.input_resource_index(),
            resource.sampler_resource_index(),
        ]
        .into_iter()
        .enumerate()
        {
            let Some(resource_index) = resource_index else {
                continue;
            };
            if position == 1 && resource.input_resource_index() == Some(resource_index) {
                sampler = input;
                continue;
            }
            if resource_index >= replay.resource_index {
                return Err(Error::ResourceDomain);
            }
            let picture =
                NativePictureImageSource::from_resource(replay.scene.clone(), resource_index)?;
            let size = [picture.descriptor.width, picture.descriptor.height];
            if picture.presentation.dpi_scale_x != 1.0
                || picture.presentation.dpi_scale_y != 1.0
                || (position == 0 && Some(size) != capture_size)
            {
                return Err(Error::UnsupportedExecution);
            }
            picture_bytes = picture_bytes
                .checked_add(u64::from(size[0]) * u64::from(size[1]) * 4)
                .filter(|v| *v <= MAX_TOTAL_COMPILED_BYTES)
                .ok_or(Error::Capacity)?;
            if position == 0 {
                input = Some(pictures.len());
            } else {
                sampler = Some(pictures.len());
            }
            pictures.push(picture);
        }
        Ok(Self {
            replay,
            resource,
            pictures,
            input,
            sampler,
            picture_bytes,
        })
    }
    pub fn replay(&self) -> &Arc<NativeReplayShader> {
        &self.replay
    }
    pub fn resource(&self) -> &ShaderResource {
        &self.resource
    }
    /// Source order is implicit input, then distinct explicit sampler. Missing
    /// sampler falls back to the input without a second capture.
    pub fn pictures(&self) -> &[NativePictureImageSource] {
        &self.pictures
    }
    pub fn picture_bytes(&self) -> u64 {
        self.picture_bytes
    }
    pub fn suppressed(&self) -> bool {
        self.replay.suppressed
    }
    pub fn final_sample(&self) -> bool {
        self.replay.final_sample()
    }
    pub fn target(&self) -> SampleLattice {
        Self::lattice_for(&self.replay)
    }
    pub fn actual_uniform_bytes(&self) -> u64 {
        if self.suppressed() {
            0
        } else {
            match self.resource.frame() {
                ShaderFrame::Affine(_) => 624,
                ShaderFrame::Samples(_) => 608,
                _ => 544,
            }
        }
    }
    fn lattice_for(replay: &NativeReplayShader) -> SampleLattice {
        SampleLattice {
            origin: replay.parent_lattice_origin,
            size: [replay.parent_extent.width, replay.parent_extent.height],
        }
    }
}
fn index(value: u32) -> Option<u32> {
    (value != SCENE_NO_INDEX).then_some(value)
}
fn record_matches<T: WireRecord>(record: T, bytes: &[u8]) -> bool {
    let mut raw = [0u8; 544];
    T::WIRE_SIZE <= raw.len()
        && bytes.len() == T::WIRE_SIZE
        && record.write_into(&mut raw[..T::WIRE_SIZE]).is_ok()
        && &raw[..T::WIRE_SIZE] == bytes
}
