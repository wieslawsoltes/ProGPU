//! Original WpfShaderEffect visual capture and actual-target owned preparation.
use super::*;
use crate::{
    composition::visual::WpfVisualEffect,
    renderer::{OwnedShaderTargetFrame, ShaderEffectPreparationContext},
    shader_effect::{
        ManagedShaderParameters, ManagedShaderQuad, OwnedShaderEffectParameters, SampleLattice,
        source_pixels_per_unit,
    },
};

pub(super) struct VisualShaderTarget {
    source: VisualSnapshot,
    effect: WpfVisualEffect,
    policy: VisualPolicy,
    context: OwnedShaderTargetFrame,
    parameters: ManagedShaderParameters,
    // Only an actual transferred result can supply completed producer ownership.
    // Borrowed generic parameters deliberately leave this owner absent.
    _owned_parameters: Option<OwnedShaderEffectParameters>,
    recording: Arc<Scene>,
    capture: CapturedScene,
    owner: Arc<()>,
    device: wgpu::Device,
    pages: Vec<(
        VisualIdentity,
        Arc<Scene>,
        crate::composition::visual::RenderPresentationState,
    )>,
}
impl VisualShaderTarget {
    pub(super) fn recording(&self) -> &Arc<Scene> {
        &self.recording
    }
    pub(super) fn capture(&self) -> &CapturedScene {
        &self.capture
    }
    fn matches(
        &self,
        node: &VisualSnapshot,
        effect: &WpfVisualEffect,
        policy: VisualPolicy,
        context: OwnedShaderTargetFrame,
        renderer: &Renderer,
    ) -> bool {
        self.source.identity() == node.identity()
            && self.source.versions() == node.versions()
            && self.effect == *effect
            && self.policy == policy
            && self.context.same_target(context)
            && Arc::ptr_eq(&self.owner, &renderer.owner)
            && self.device == renderer.device
            && same_pages(node, &self.pages)
    }
    fn draw(
        &self,
        transform: Matrix4,
        opacity: f32,
    ) -> Result<crate::scene::ManagedShaderEffectDraw, Error> {
        let frame = self.context.capture_frame;
        let mut quad = ManagedShaderQuad::new(frame.padded_bounds);
        quad.transform = affine(transform)?;
        quad.opacity = opacity;
        quad.uv = frame.texture_uv_bounds;
        // Returning to a legacy frame must clear physical output-edge/UV state.
        quad.output_edges = frame.has_physical_origin.then_some(frame.output_edges);
        let draw = crate::scene::ManagedShaderEffectDraw {
            parameters: self.parameters.clone(),
            quad,
            target: self.context.target,
            blend: LayerBlend::SrcOver,
            mask: None,
        };
        draw.validate()?;
        Ok(draw)
    }
}
impl Builder<'_> {
    pub(super) async fn shader(
        &mut self,
        node: &VisualSnapshot,
        effect: &WpfVisualEffect,
        parent: Matrix4,
        global: Matrix4,
        opacity: f32,
        target: RenderFrame,
        clip: Option<[f32; 4]>,
        masks: Vec<Mask>,
        suppress: bool,
        depth: u16,
    ) -> Result<Scene, Error> {
        effect.preflight_device(&self.renderer.device)?;
        if self.depth >= 16 || self.shaders.len() >= 16384 {
            return Err(Error::Capacity);
        }
        let Some(context) = target_context(node, effect, target, suppress, &self.renderer.device)?
        else {
            return Ok(Scene::default());
        };
        let policy = VisualPolicy::new(self.renderer, self.options);
        let cached = self
            .shaders
            .iter()
            .chain(self.renderer.visuals.shader_targets.iter())
            .find(|value| value.matches(node, effect, policy, context, self.renderer))
            .cloned();
        let generation = if let Some(generation) = cached {
            self.metrics.shader_cache_hits = self
                .metrics
                .shader_cache_hits
                .checked_add(1)
                .ok_or(Error::Capacity)?;
            generation
        } else {
            let actual_owner = self.renderer.owner.clone();
            let actual_device = self.renderer.device.clone();
            // Shared with ordered picture recipes: visual->picture->visual cycles
            // cannot escape through independent preparation registries.
            let _source_guard = effect
                .owned_source()
                .map(|source| self.renderer.begin_owned_shader_source(source))
                .transpose()?;
            let owned = if let Some(source) = effect.owned_source() {
                let value = source
                    .preparation()
                    .prepare(ShaderEffectPreparationContext {
                        renderer: &mut *self.renderer,
                        source: source.clone(),
                        target: context,
                    })
                    .await?;
                value.validate_device(&actual_device)?;
                self.metrics.shader_parameter_generations = self
                    .metrics
                    .shader_parameter_generations
                    .checked_add(1)
                    .ok_or(Error::Capacity)?;
                Some(value)
            } else {
                None
            };
            unchanged(
                self.renderer,
                &actual_owner,
                &actual_device,
                node,
                effect,
                target,
                suppress,
                context,
            )?;
            let parameters = match &owned {
                Some(value) => value.snapshot().clone(),
                None => effect.parameters().ok_or(Error::ResourceDomain)?.clone(),
            };
            let frame = SourceRenderFrame::effect(context.capture_frame, suppress)?;
            let input_target = self.renderer.source_target(frame)?;
            self.depth += 1;
            let recording = self
                .node(
                    node,
                    frame.source_to_raster(),
                    1.0,
                    input_target,
                    None,
                    Vec::new(),
                    suppress,
                    true,
                    effect.captures_source_visual_opacity(),
                    false,
                    depth + 1,
                )
                .await;
            self.depth -= 1;
            let recording = Arc::new(recording?);
            let prepared = self
                .renderer
                .prepare_target_scene_inner(&recording, input_target)
                .await?;
            let capture = self.renderer.capture(&prepared, None).await?;
            unchanged(
                self.renderer,
                &actual_owner,
                &actual_device,
                node,
                effect,
                target,
                suppress,
                context,
            )?;
            self.charge(capture.capture_bytes())?;
            self.metrics.shader_captures = self
                .metrics
                .shader_captures
                .checked_add(1)
                .ok_or(Error::Capacity)?;
            let parameters = parameters.with_captured_input(capture.image().clone());
            Arc::new(VisualShaderTarget {
                source: node.clone(),
                effect: effect.clone(),
                policy,
                context,
                parameters,
                _owned_parameters: owned,
                recording,
                capture,
                owner: actual_owner,
                device: actual_device,
                pages: retain_pages(node),
            })
        };
        let mut scene = Scene::default();
        let source_alpha = effect.captures_source_visual_opacity();
        let (scopes, _, _) = self
            .scopes(
                &mut scene,
                node,
                parent,
                global,
                target,
                clip,
                masks,
                suppress,
                if source_alpha {
                    VisualScope::GeometryOnly
                } else {
                    VisualScope::Full
                },
            )
            .await?;
        let opacity = if !source_alpha && node.properties().opacity < 1.0 {
            opacity * node.properties().opacity
        } else {
            opacity
        };
        scene.draw_managed_shader_effect(generation.draw(global, opacity)?)?;
        for _ in 0..scopes {
            scene.pop_scope()?;
        }
        self.metrics.shader_commands = self
            .metrics
            .shader_commands
            .checked_add(1)
            .ok_or(Error::Capacity)?;
        self.shaders.push(generation);
        Ok(scene)
    }
}
fn target_context(
    node: &VisualSnapshot,
    effect: &WpfVisualEffect,
    target: RenderFrame,
    suppress: bool,
    device: &wgpu::Device,
) -> Result<Option<OwnedShaderTargetFrame>, Error> {
    effect.validate()?;
    let cold = &node.properties().composite;
    let projection = target
        .uniforms(device.limits().max_texture_dimension_2d)?
        .projection;
    let viewport = [0.0, 0.0, target.width as f32, target.height as f32];
    let capture_frame = if let Some(source) = effect.source_capture() {
        let default_translation = effect
            .owned_source()
            .map_or([0.0; 2], |owner| owner.source_translation());
        let translation = cold
            .effect_source_translation
            .unwrap_or(default_translation);
        if effect.owned_source().is_some()
            && translation.map(f32::to_bits) != default_translation.map(f32::to_bits)
        {
            // An owned source recipe and its actual source rebase are one immutable
            // generation. Rebinding only the visual metadata would split that proof.
            return Err(Error::ResourceDomain);
        }
        if cold.effect_raster_padding.is_some() {
            ManagedCaptureFrame::source(
                source,
                translation,
                cold.effect_raster_padding,
                target.dpi_scale,
            )?
        } else {
            ManagedCaptureFrame::source_physical(
                source,
                translation,
                source_pixels_per_unit(projection, viewport)?,
                target.dpi_scale,
            )?
        }
    } else {
        let size = node.properties().size;
        let bounds = cold
            .effect_content_bounds
            .unwrap_or([0.0, 0.0, size[0], size[1]]);
        if bounds[2] <= 0.0 || bounds[3] <= 0.0 {
            return Ok(None);
        }
        ManagedCaptureFrame::legacy(
            bounds,
            effect.padding(),
            cold.effect_raster_padding,
            target.dpi_scale,
        )?
    };
    let maximum = device.limits().max_texture_dimension_2d;
    if capture_frame.pixel_size.iter().any(|v| *v > maximum) {
        return Err(Error::Capacity);
    }
    let bytes = u64::from(capture_frame.pixel_size[0]) * u64::from(capture_frame.pixel_size[1]) * 4;
    if bytes > 256 * 1024 * 1024 {
        return Err(Error::Capacity);
    }
    Ok(Some(OwnedShaderTargetFrame {
        projection,
        viewport,
        target: SampleLattice {
            origin: [
                i32::try_from(target.origin[0]).map_err(|_| Error::Capacity)?,
                i32::try_from(target.origin[1]).map_err(|_| Error::Capacity)?,
            ],
            size: [target.width, target.height],
        },
        dpi_scale: target.dpi_scale,
        capture_frame,
        suppress_cached_cleartype: suppress,
    }))
}
fn unchanged(
    renderer: &Renderer,
    owner: &Arc<()>,
    device: &wgpu::Device,
    node: &VisualSnapshot,
    effect: &WpfVisualEffect,
    target: RenderFrame,
    suppress: bool,
    context: OwnedShaderTargetFrame,
) -> Result<(), Error> {
    if renderer.device != *device
        || !Arc::ptr_eq(&renderer.owner, owner)
        || !target_context(node, effect, target, suppress, device)?
            .is_some_and(|next| next.same_target(context))
    {
        return Err(Error::ResourceDomain);
    }
    Ok(())
}
