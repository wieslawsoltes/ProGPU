//! Exact v1–v6 shader descriptors with earlier immutable picture dependencies.
use super::*;
use crate::{
    native_scene::NativeSceneShader,
    shader_effect::{ShaderFrame, ShaderResource},
};
use std::sync::Arc;
impl NativeSceneBuilder {
    pub fn add_shader_effect(
        &mut self,
        effect: wire::ShaderEffect,
        bytecode: &[u8],
    ) -> BuildResult<u32> {
        self.add_shader_resource(NativeSceneShader::Program(effect), bytecode)
    }
    pub fn add_shader_effect_with_sampler(
        &mut self,
        effect: wire::ShaderEffect,
        bytecode: &[u8],
        sampler_picture: u32,
    ) -> BuildResult<u32> {
        self.add_shader_resource(
            NativeSceneShader::Picture(wire::ShaderEffectPicture {
                struct_size: wire::ShaderEffectPicture::WIRE_SIZE as u32,
                version: 2,
                sampler_resource_index: sampler_picture,
                reserved: 0,
                program: effect,
            }),
            bytecode,
        )
    }
    pub fn add_shader_effect_derivatives(
        &mut self,
        effect: wire::ShaderEffectDerivatives,
        bytecode: &[u8],
    ) -> BuildResult<u32> {
        self.add_shader_resource(NativeSceneShader::Derivatives(effect), bytecode)
    }
    pub fn add_shader_effect_capture(
        &mut self,
        effect: wire::ShaderEffectCapture,
        bytecode: &[u8],
    ) -> BuildResult<u32> {
        self.add_shader_resource(NativeSceneShader::Capture(effect), bytecode)
    }
    pub fn add_shader_effect_samples(
        &mut self,
        effect: wire::ShaderEffectSamples,
        bytecode: &[u8],
    ) -> BuildResult<u32> {
        self.add_shader_resource(NativeSceneShader::Samples(effect), bytecode)
    }
    pub fn add_shader_effect_affine(
        &mut self,
        effect: wire::ShaderEffectAffine,
        bytecode: &[u8],
    ) -> BuildResult<u32> {
        self.add_shader_resource(NativeSceneShader::Affine(effect), bytecode)
    }
    pub fn add_shader_resource(
        &mut self,
        effect: NativeSceneShader,
        bytecode: &[u8],
    ) -> BuildResult<u32> {
        // Preserve the original fields, including reserved words, instead of
        // normalizing a malformed program into an admitted descriptor.
        let payload = match effect {
            NativeSceneShader::Program(v) => encode(&[v])?,
            NativeSceneShader::Picture(v) => encode(&[v])?,
            NativeSceneShader::Derivatives(v) => encode(&[v])?,
            NativeSceneShader::Capture(v) => encode(&[v])?,
            NativeSceneShader::Samples(v) => encode(&[v])?,
            NativeSceneShader::Affine(v) => encode(&[v])?,
        };
        let parsed = ShaderResource::read(
            Arc::<[u8]>::from(copy_bytes(&payload)?),
            Arc::<[u8]>::from(copy_bytes(bytecode)?),
        )
        .map_err(|failure| {
            if matches!(failure, crate::Error::Capacity) {
                B::OutOfMemory
            } else {
                B::InvalidArgument
            }
        })?;
        if self.resources.len() >= MAX_RESOURCES as usize {
            return Err(B::InvalidArgument);
        }
        let capture_size = match parsed.frame() {
            ShaderFrame::Samples(frame) => Some(frame.capture_size),
            ShaderFrame::Affine(frame) => Some(frame.placement.capture_size),
            _ => None,
        };
        for index in [
            parsed.input_resource_index(),
            parsed.sampler_resource_index(),
        ]
        .into_iter()
        .flatten()
        {
            let resource = self
                .resources
                .get(index as usize)
                .ok_or(B::InvalidArgument)?;
            if resource.record.kind != SCENE_RESOURCE_IMAGE
                || resource.record.flags & SCENE_IMAGE_PICTURE == 0
            {
                return Err(B::InvalidArgument);
            }
            if let Some(size) = capture_size {
                let (retained, presentation) =
                    values::read_picture_image(&resource.payload).ok_or(B::InvalidArgument)?;
                if retained.width != size[0]
                    || retained.height != size[1]
                    || presentation.dpi_scale_x != 1.0
                    || presentation.dpi_scale_y != 1.0
                    || presentation.viewport_x != 0
                    || presentation.viewport_y != 0
                {
                    return Err(B::InvalidArgument);
                }
            }
        }
        reserve_append(&mut self.resources, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_WPF_SHADER_EFFECT);
        resource.payload = payload;
        resource.auxiliary = copy_bytes(bytecode)?;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
}
