use super::*;
/// Complete original shader version/frame inventory. Capture callbacks consume
/// this owned source request at the actual GPU target boundary.
#[derive(Clone)]
pub struct NativeReplayShader {
    pub scene: Arc<NativeScene>,
    pub resource_index: u32,
    pub program: wire::ShaderEffect,
    pub source: NativeLayerTargetId,
    pub source_extent: NativeScissor,
    pub parent_extent: NativeScissor,
    pub parent_presentation: wire::Presentation,
    pub dpi_scale: f32,
    pub source_state_dpi: [f32; 2],
    pub parent_lattice_origin: [i32; 2],
    pub sampler_resource_index: u32,
    pub input_resource_index: u32,
    pub derivative_register: u32,
    pub capture: Option<wire::ShaderCaptureFrame>,
    pub sample: Option<wire::ShaderSampleFrame>,
    pub affine: Option<wire::ShaderAffineFrame>,
    pub source_vector_mask: bool,
    pub source_vector_mask_resource_index: u32,
    pub suppressed: bool,
    /// Original replay accounting estimate (528/592/608), never buffer sizing.
    /// The source GPU records include an additional 16-byte sample extent;
    /// actual adapter upload metrics retain their 544/608/624-byte sizes.
    pub uniform_bytes: u32,
}
impl NativeReplayShader {
    pub fn final_sample(&self) -> bool {
        self.input_resource_index != SCENE_NO_INDEX
    }
    pub fn bytecode(&self) -> &[u8] {
        self.scene
            .envelope()
            .resource(self.resource_index)
            .expect("retained admitted shader resource")
            .auxiliary
    }
}
#[derive(Clone)]
pub enum NativeReplayEffect {
    Builtin(Arc<NativeEffectPlan>),
    Shader(Arc<NativeReplayShader>),
}
impl NativeReplayEffect {
    pub fn effect_count(&self) -> u32 {
        match self {
            Self::Builtin(p) => p.raw_effects().len() as u32,
            Self::Shader(_) => 1,
        }
    }
    pub fn final_sample(&self) -> bool {
        matches!(self,Self::Shader(p) if p.final_sample())
    }
    pub fn composite_drawable(&self) -> bool {
        !matches!(self,Self::Shader(p) if p.suppressed)
    }
    pub fn final_texture(&self) -> u8 {
        match self {
            Self::Builtin(p) => p.final_texture(),
            Self::Shader(_) => 0,
        }
    }
}
#[allow(clippy::too_many_arguments)]
pub(super) fn compile(
    source: &NativeSceneResourcePlan,
    resource_index: u32,
    source_layer: NativeLayerTargetId,
    source_extent: NativeScissor,
    parent_extent: NativeScissor,
    parent_presentation: wire::Presentation,
    source_vector_mask_resource_index: u32,
    metrics: &mut NativeReplayMetrics,
) -> Result<Option<NativeReplayEffect>, Error> {
    if resource_index == SCENE_NO_INDEX {
        return Ok(None);
    }
    let resource = source
        .scene()
        .envelope()
        .resource(resource_index)
        .ok_or(Error::InvalidInput)?;
    if resource.record.kind != SCENE_RESOURCE_WPF_SHADER_EFFECT {
        let plan = Arc::new(NativeEffectPlan::from_resource(
            source.scene().clone(),
            resource_index,
            source.layers().presentation(),
        )?);
        metrics.builtin_effect_count = metrics
            .builtin_effect_count
            .checked_add(plan.raw_effects().len() as u32)
            .ok_or(Error::Capacity)?;
        metrics.builtin_effect_uniform_bytes = metrics
            .builtin_effect_uniform_bytes
            .checked_add(plan.uniform_bytes().len() as u64)
            .ok_or(Error::Capacity)?;
        return Ok(Some(NativeReplayEffect::Builtin(plan)));
    }
    if source_layer == NativeLayerTargetId::Root {
        return Err(Error::ResourceDomain);
    }
    let p = source.layers().presentation();
    let mut shader = NativeReplayShader {
        scene: source.scene().clone(),
        resource_index,
        program: Default::default(),
        source: source_layer,
        source_extent,
        parent_extent,
        parent_presentation,
        dpi_scale: source.frame().dpi_scale,
        source_state_dpi: [p.dpi_scale_x, p.dpi_scale_y],
        parent_lattice_origin: [
            (parent_extent.x as i32).wrapping_sub(p.viewport_x as i32),
            (parent_extent.y as i32).wrapping_sub(p.viewport_y as i32),
        ],
        sampler_resource_index: SCENE_NO_INDEX,
        input_resource_index: SCENE_NO_INDEX,
        derivative_register: SCENE_NO_INDEX,
        capture: None,
        sample: None,
        affine: None,
        source_vector_mask: source_vector_mask_resource_index != SCENE_NO_INDEX,
        source_vector_mask_resource_index,
        suppressed: false,
        uniform_bytes: 0,
    };
    let bytes = resource.payload;
    match bytes.len() {
        544 => shader.program = wire::ShaderEffect::read(bytes).ok_or(Error::InvalidInput)?,
        560 => {
            let r = wire::ShaderEffectPicture::read(bytes).ok_or(Error::InvalidInput)?;
            shader.program = r.program;
            shader.sampler_resource_index = r.sampler_resource_index;
        }
        576 => {
            let r = wire::ShaderEffectDerivatives::read(bytes).ok_or(Error::InvalidInput)?;
            shader.program = r.program;
            shader.sampler_resource_index = r.sampler_resource_index;
            shader.derivative_register = r.derivative_register;
        }
        648 => {
            let r = wire::ShaderEffectCapture::read(bytes).ok_or(Error::InvalidInput)?;
            shader.program = r.program;
            shader.sampler_resource_index = r.sampler_resource_index;
            shader.derivative_register = r.derivative_register;
            shader.capture = Some(r.frame);
        }
        704 => {
            let r = wire::ShaderEffectSamples::read(bytes).ok_or(Error::InvalidInput)?;
            shader.program = r.program;
            shader.sampler_resource_index = r.sampler_resource_index;
            shader.derivative_register = r.derivative_register;
            shader.input_resource_index = r.input_resource_index;
            shader.sample = Some(r.frame);
        }
        720 => {
            let r = wire::ShaderEffectAffine::read(bytes).ok_or(Error::InvalidInput)?;
            shader.program = r.program;
            shader.sampler_resource_index = r.sampler_resource_index;
            shader.derivative_register = r.derivative_register;
            shader.input_resource_index = r.input_resource_index;
            shader.sample = Some(r.frame.placement);
            shader.affine = Some(r.frame);
        }
        _ => return Err(Error::InvalidInput),
    }
    if shader.capture.is_some_and(|c| {
        c.capture_width != 0
            && (c.capture_width != source_extent.width || c.capture_height != source_extent.height)
    }) {
        return Err(Error::InvalidInput);
    }
    shader.suppressed =
        shader.final_sample() && (!source_extent.drawable || !parent_extent.drawable);
    if !shader.suppressed {
        shader.uniform_bytes = if shader.affine.is_some() {
            608
        } else if shader.final_sample() {
            592
        } else {
            528
        };
    }
    metrics.shader_effect_count = metrics
        .shader_effect_count
        .checked_add(1)
        .ok_or(Error::Capacity)?;
    metrics.shader_uniform_bytes = metrics
        .shader_uniform_bytes
        .checked_add(u64::from(shader.uniform_bytes))
        .ok_or(Error::Capacity)?;
    Ok(Some(NativeReplayEffect::Shader(Arc::new(shader))))
}
