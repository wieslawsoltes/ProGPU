//! Version-aware original shader resource admission and owned-picture dependencies.
use super::*;
use crate::shader_effect::{ShaderFrame, ShaderResource};

pub(super) struct ParsedShaderEffect {
    pub sampler: u32,
    pub input: u32,
    pub capture_size: [u32; 2],
}

pub(super) fn validate_shader_effect_resource(
    input: &Input<'_>,
    resource: &wire::Resource,
) -> TransportResult<ParsedShaderEffect> {
    let bad = || error(E::Value, resource.payload_offset);
    if !matches!(resource.payload_size, 544 | 560 | 576 | 648 | 704 | 720)
        || !(8..=65536).contains(&resource.auxiliary_size)
        || resource.auxiliary_size % 4 != 0
    {
        return Err(bad());
    }
    // Reuse the original version-aware byte reader and shader instruction parser.
    // This retains actual v5/v6 frame admission; older-frame fallbacks cannot
    // silently discard an input picture or an affine source coefficient.
    let payload = input.span(resource.payload_offset, resource.payload_size)?;
    let bytecode = input.span(resource.auxiliary_offset, resource.auxiliary_size)?;
    let mut owned_payload = reserve(payload.len(), resource.payload_offset)?;
    owned_payload.extend_from_slice(payload);
    let mut owned_bytecode = reserve(bytecode.len(), resource.payload_offset)?;
    owned_bytecode.extend_from_slice(bytecode);
    let parsed = ShaderResource::read(
        Arc::<[u8]>::from(owned_payload),
        Arc::<[u8]>::from(owned_bytecode),
    )
    .map_err(|failure| {
        let mut result = bad();
        result.out_of_memory = matches!(failure, crate::Error::Capacity);
        result
    })?;
    let capture_size = match parsed.frame() {
        ShaderFrame::Legacy => [0; 2],
        ShaderFrame::Capture(frame) => frame.capture_size,
        ShaderFrame::Samples(frame) => frame.capture_size,
        ShaderFrame::Affine(frame) => frame.placement.capture_size,
    };
    Ok(ParsedShaderEffect {
        sampler: parsed.sampler_resource_index().unwrap_or(SCENE_NO_INDEX),
        input: parsed.input_resource_index().unwrap_or(SCENE_NO_INDEX),
        capture_size,
    })
}
