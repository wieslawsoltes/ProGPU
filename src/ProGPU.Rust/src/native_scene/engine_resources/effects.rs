//! Original native retained built-in effect program. Source resources, mapped
//! parameters and GPU scratch ownership remain independently inspectable.
use super::*;
use crate::effects::{Effect, EffectChain, EffectPlan, EffectStep};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{
    NativeEffectEngine, NativeEffectMetrics, NativeEffectRequest, PreparedNativeEffects,
};

pub struct NativeEffectPlan {
    source: Arc<NativeScene>,
    resource_index: u32,
    header: wire::EffectChain,
    raw: Arc<[wire::GroupEffect]>,
    physical: Arc<[wire::GroupEffect]>,
    plan: EffectPlan,
    uniforms: Arc<[u8]>,
}
impl NativeEffectPlan {
    pub fn from_resource(
        source: Arc<NativeScene>,
        resource_index: u32,
        presentation: wire::Presentation,
    ) -> Result<Self, Error> {
        let resource = source
            .envelope()
            .resource(resource_index)
            .ok_or(Error::InvalidInput)?;
        if resource.record.kind != SCENE_RESOURCE_EFFECT_CHAIN {
            return Err(Error::InvalidInput);
        }
        let header = wire::EffectChain::read(resource.payload).ok_or(Error::InvalidInput)?;
        if header.effect_count == 0 || header.effect_count > MAX_GROUP_EFFECTS {
            return Err(Error::InvalidInput);
        }
        let mut raw = allocate(header.effect_count as usize)?;
        let mut physical = allocate(header.effect_count as usize)?;
        let mut effects = allocate(header.effect_count as usize)?;
        for bytes in resource
            .auxiliary
            .chunks_exact(wire::GroupEffect::WIRE_SIZE)
            .take(header.effect_count as usize)
        {
            let effect = wire::GroupEffect::read(bytes).ok_or(Error::InvalidInput)?;
            if !crate::native_scene::semantic::values::effect(&effect) {
                return Err(Error::InvalidInput);
            }
            physical.push(
                crate::native_scene::execution::resolve_effect(effect, presentation)
                    .ok_or(Error::InvalidInput)?,
            );
            raw.push(effect);
            effects.push(match effect.kind {
                GROUP_EFFECT_GAUSSIAN_BLUR => Effect::GaussianBlur {
                    sigma: [effect.sigma_x, effect.sigma_y],
                },
                GROUP_EFFECT_BOX_BLUR => Effect::BoxBlur {
                    radius: [effect.sigma_x, effect.sigma_y],
                },
                GROUP_EFFECT_DROP_SHADOW => Effect::DropShadow {
                    sigma: [effect.sigma_x, effect.sigma_y],
                    offset: [effect.offset_x, effect.offset_y],
                    color: [
                        effect.color_r,
                        effect.color_g,
                        effect.color_b,
                        effect.color_a,
                    ],
                },
                _ => return Err(Error::UnsupportedExecution),
            });
        }
        if raw.len() != header.effect_count as usize {
            return Err(Error::InvalidInput);
        }
        let plan = EffectChain::new(effects)?
            .plan([presentation.dpi_scale_x, presentation.dpi_scale_y])?;
        let mut uniforms = allocate(plan.pass_count() as usize * 256)?;
        uniforms.resize(plan.pass_count() as usize * 256, 0);
        let mut offset = 0;
        for step in plan.steps() {
            for record in step.blur_uniforms() {
                uniforms[offset..offset + 16].copy_from_slice(bytemuck::bytes_of(record));
                offset += 256;
            }
            if let Some(record) = step.shadow_uniforms() {
                uniforms[offset..offset + 32].copy_from_slice(bytemuck::bytes_of(record));
                offset += 256;
            }
        }
        Ok(Self {
            source,
            resource_index,
            header,
            raw: raw.into(),
            physical: physical.into(),
            plan,
            uniforms: uniforms.into(),
        })
    }
    pub fn source(&self) -> &Arc<NativeScene> {
        &self.source
    }
    pub fn resource_index(&self) -> u32 {
        self.resource_index
    }
    pub fn header(&self) -> wire::EffectChain {
        self.header
    }
    pub fn raw_effects(&self) -> &[wire::GroupEffect] {
        &self.raw
    }
    pub fn physical_effects(&self) -> &[wire::GroupEffect] {
        &self.physical
    }
    pub fn dpi(&self) -> [f32; 2] {
        self.plan.dpi()
    }
    pub fn steps(&self) -> &[EffectStep] {
        self.plan.steps()
    }
    pub fn pass_count(&self) -> u32 {
        self.plan.pass_count()
    }
    pub fn final_texture(&self) -> u8 {
        self.plan
            .steps()
            .last()
            .expect("admitted nonempty chain")
            .texture_slots()[2]
    }
    pub fn uniform_bytes(&self) -> &[u8] {
        &self.uniforms
    }
}
