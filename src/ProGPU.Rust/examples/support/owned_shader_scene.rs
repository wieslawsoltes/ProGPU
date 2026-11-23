//! Authored actual-target owned recipes, nested samplers and retained source scopes.
use progpu::{
    Error,
    composition::SourceRenderFrame,
    geometry::{Affine, AnalyticPrimitive},
    image::ImageSampling,
    renderer::ShaderEffectPreparationContext,
    scene::{LayerDraw, OwnedShaderEffectDraw, Scene},
    shader_effect::{
        ManagedShaderProgram, OwnedShaderEffectDescriptor, OwnedShaderEffectParameters,
        OwnedShaderEffectRecording, OwnedShaderEffectSampler, OwnedShaderEffectSource,
        OwnedShaderEffectTexture, ShaderEffectPreparation, SourceCapture,
    },
    vector::Matrix4,
};
use std::{future::Future, pin::Pin, sync::Arc};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
struct Recipe {
    program: Arc<ManagedShaderProgram>,
    sampler: Option<Arc<Scene>>,
}
impl ShaderEffectPreparation for Recipe {
    fn prepare<'a>(
        &'a self,
        context: ShaderEffectPreparationContext<'a>,
    ) -> Pin<Box<dyn Future<Output = std::result::Result<OwnedShaderEffectParameters, Error>> + 'a>>
    {
        Box::pin(async move {
            let mut descriptor = OwnedShaderEffectDescriptor::new(self.program.clone());
            descriptor.constants = Arc::from([0.2, 0.75, 1.0, 1.0]);
            if let Some(source) = &self.sampler {
                let frame = SourceRenderFrame::effect(
                    context.target.capture_frame,
                    context.target.suppress_cached_cleartype,
                )?;
                let capture = context
                    .renderer
                    .capture_source_scene(source.clone(), frame)
                    .await?;
                descriptor.samplers.push(OwnedShaderEffectSampler::new(
                    1,
                    Some(OwnedShaderEffectTexture::from_source_capture(capture)),
                    ImageSampling::Linear,
                )?);
            }
            OwnedShaderEffectParameters::new(descriptor)
        })
    }
}
fn source() -> std::result::Result<Scene, Error> {
    let mut scene = Scene::default();
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [0.0, 0.0, 144.0, 88.0],
        [0.08, 0.25, 0.5, 0.9],
    ))?;
    for i in 0..5 {
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [9.0 + i as f32 * 26.0, 8.5, 17.0, 70.0],
            [0.95, 0.2 + i as f32 * 0.13, 0.15, 0.8],
        ))?;
    }
    Ok(scene)
}
fn recording(
    scene: Arc<Scene>,
    program: &str,
    sampler: Option<Arc<Scene>>,
) -> std::result::Result<OwnedShaderEffectRecording, Error> {
    let recipe = Recipe {
        program: Arc::new(ManagedShaderProgram::new(program, program)),
        sampler,
    };
    let source = OwnedShaderEffectSource::new(
        SourceCapture {
            bounds: [0.0, 0.0, 144.0, 88.0],
            padding: [2.25, 3.5, 5.75, 4.25],
        },
        [0.0; 2],
        Box::new(recipe),
    )?;
    OwnedShaderEffectRecording::new(scene, source)
}
/// Twelve panels reuse exact recording identities across actual root and bounded
/// child targets; nested recipes materialize their own sources during preparation.
pub async fn owned_shader_scene() -> Result<Scene> {
    let source = Arc::new(source()?);
    let plain = recording(source.clone(), "", None)?;
    let tint = recording(
        source.clone(),
        "fn wpf_effect_main(uv: vec2<f32>, c: vec4<f32>) -> vec4<f32> { let t=wpf_constant(0u); return vec4<f32>(c.rgb*t.rgb,c.a); }",
        None,
    )?;
    let mut nested = Scene::default();
    nested.draw_owned_shader_effect(OwnedShaderEffectDraw::new(tint.clone()))?;
    let nested = Arc::new(nested);
    let outer = recording(nested.clone(), "", None)?;
    let sampler = recording(
        source,
        "fn wpf_effect_main(uv: vec2<f32>, c: vec4<f32>) -> vec4<f32> { return wpf_sample_register(1u,uv); }",
        Some(nested),
    )?;
    let recordings = [plain, tint, outer, sampler];
    let mut scene = Scene::default();
    for index in 0..12 {
        let x = 12.0 + (index % 4) as f32 * 236.0;
        let y = 12.0 + (index / 4) as f32 * 186.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 224.0, 174.0],
            [0.12, 0.16, 0.24, 1.0],
        ))?;
        let mut child = Scene::default();
        child.push_clip_rect([x + 5.0, y + 5.0, 214.0, 164.0])?;
        let mut draw = OwnedShaderEffectDraw::new(recordings[index % 4].clone());
        draw.transform = Matrix4::from_affine(Affine([
            1.2,
            if index >= 4 { -0.08 } else { 0.0 },
            0.06,
            1.4,
            x + 24.0,
            y + 25.0,
        ]));
        draw.opacity = if index >= 8 { 0.7 } else { 1.0 };
        child.draw_owned_shader_effect(draw)?;
        child.pop_scope()?;
        if index >= 8 {
            let mut layer = LayerDraw::new(child);
            layer.bounds = Some([x + 2.25, y + 2.5, 219.0, 167.0]);
            scene.draw_layer(layer)?;
        } else {
            let layer = LayerDraw::new(child);
            scene.draw_layer(layer)?;
        }
    }
    Ok(scene)
}
