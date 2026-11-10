//! Actual-target immutable visual-tree lowering. Source callbacks run only while
//! freezing snapshots; encoded frames retain completed resources and never visit UI.
use super::*;
use crate::{
    composition::{
        SourceRenderFrame,
        visual::{
            CompositionEffectValue, Visual, VisualBlurKernel, VisualCompositeClip, VisualIdentity,
            VisualSnapshot, VisualVersions,
        },
    },
    geometry::{Affine, AnalyticPrimitive},
    image::{GpuImage, ImageDraw},
    mask::{FrameAnalyticMask, Mask, MaskChannel, MaskUniforms, RenderedTextureMask, TextureMask},
    scene::{LayerBlend, MaterialPaint},
    shader_effect::ManagedCaptureFrame,
    vector::Matrix4,
};
use std::{future::Future, pin::Pin};
mod compute;
pub(super) mod geometry;
mod shader;

/// Original compositor switches, independent of source cache requirements.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VisualRenderOptions {
    /// Permit optional CacheAsLayer optimizations. Required source caches stay enabled.
    pub enable_layer_cache: bool,
    /// Inherited source text policy; a selected layer may override it explicitly.
    pub suppress_cached_cleartype: bool,
    /// Cull only local pages against exact active binary clips, never descendants.
    pub enable_local_culling: bool,
}
impl Default for VisualRenderOptions {
    fn default() -> Self {
        Self {
            enable_layer_cache: true,
            suppress_cached_cleartype: false,
            enable_local_culling: true,
        }
    }
}
/// Actual visual preparation work. Captured-source detailed metrics remain owned
/// by each immutable capture; these counters do not claim GPU completion.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct VisualMetrics {
    /// All original snapshot nodes admitted, including invisible source dependencies.
    pub nodes: u32,
    /// Local source pages replayed in original painter order.
    pub pages: u32,
    /// Own pages culled by an actual binary clip.
    pub culled_pages: u32,
    /// Layer/effect capture generations reused unchanged.
    pub cache_hits: u32,
    /// Newly completed visual source captures.
    pub captures: u32,
    /// Actual opacity-mask picture/brush captures.
    pub mask_captures: u32,
    /// Source, mask and effect texture allocation bytes in this preparation.
    pub texture_bytes: u64,
    /// Original managed effect compute passes.
    pub effect_passes: u32,
    /// Actual Wpf visual output commands, including constant-output zero-alpha roots.
    pub shader_commands: u32,
    /// Exact actual-target Wpf source/parameter generations reused.
    pub shader_cache_hits: u32,
    /// Newly completed implicit inputs for visual shaders.
    pub shader_captures: u32,
    /// Actual owned recipe calls producing new immutable parameter generations.
    pub shader_parameter_generations: u32,
}
/// A prepared visual retains the exact snapshot, lowered scene, source captures
/// and effect resources. Later cache replacement cannot mutate this generation.
pub struct PreparedVisual {
    prepared: PreparedScene,
    snapshot: Arc<VisualSnapshot>,
    recording: Arc<Scene>,
    captures: Vec<Arc<VisualCapture>>,
    masks: Vec<CapturedScene>,
    shaders: Vec<Arc<shader::VisualShaderTarget>>,
    metrics: VisualMetrics,
}
impl PreparedVisual {
    /// Actual prepared scene, replayable through the ordinary renderer APIs.
    pub fn scene(&self) -> &PreparedScene {
        &self.prepared
    }
    /// Complete original immutable tree and page ownership.
    pub fn snapshot(&self) -> &Arc<VisualSnapshot> {
        &self.snapshot
    }
    /// Ordered lowered commands, retaining source image/resource owners.
    pub fn recording(&self) -> &Arc<Scene> {
        &self.recording
    }
    /// Actual visual preparation work for this generation.
    pub fn metrics(&self) -> VisualMetrics {
        self.metrics
    }
    /// Source capture metrics for retained layer/effect generations, including reused ones.
    pub fn source_captures(&self) -> impl Iterator<Item = &CapturedScene> {
        self.captures.iter().map(|v| &v.source)
    }
    /// Completed source command generations retained by each cache/effect texture.
    pub fn source_recordings(&self) -> impl Iterator<Item = &Arc<Scene>> {
        self.captures.iter().map(|v| &v.recording)
    }
    /// Original Wpf source command generations, before final shader evaluation.
    pub fn shader_source_recordings(&self) -> impl Iterator<Item = &Arc<Scene>> {
        self.shaders.iter().map(|v| v.recording())
    }
    /// Original Wpf implicit-input captures, retained independently of final output.
    pub fn shader_source_captures(&self) -> impl Iterator<Item = &CapturedScene> {
        self.shaders.iter().map(|v| v.capture())
    }
    /// Actual separately retained shadow-mask captures and their source recordings.
    pub fn shadow_mask_sources(&self) -> impl Iterator<Item = (&Arc<Scene>, &CapturedScene)> {
        self.captures.iter().filter_map(|v| {
            v.shadow_source
                .as_ref()
                .map(|(scene, capture)| (scene, capture))
        })
    }
    /// Actual independently owned picture/brush opacity masks.
    pub fn mask_captures(&self) -> &[CapturedScene] {
        &self.masks
    }
}
#[derive(Default)]
pub(super) struct VisualState {
    targets: Vec<Arc<VisualCapture>>,
    compute: compute::ComputeState,
    shader_targets: Vec<Arc<shader::VisualShaderTarget>>,
}
#[derive(Clone, Copy, PartialEq, Eq)]
struct VisualPolicy {
    options: VisualRenderOptions,
    text_sizes: [u32; 6],
    text_execution: crate::path::ExecutionPreference,
    image_sampling: Option<crate::image::ImageSamplingPath>,
    image_preference: crate::image::ImageSamplingPreference,
    winding: crate::path::SignedWindingPolicy,
}
impl VisualPolicy {
    fn new(renderer: &Renderer, options: VisualRenderOptions) -> Self {
        let text = renderer.text_options;
        Self {
            options,
            text_sizes: [
                text.initial_size,
                text.maximum_size,
                text.initial_color_size,
                text.maximum_color_size,
                text.rasterizer.uniform_ring_bytes,
                text.rasterizer.coverage_ring_bytes,
            ],
            text_execution: text.rasterizer.preference,
            image_sampling: renderer.image_sampling,
            image_preference: renderer.image_preference,
            winding: renderer.signed_winding_policy,
        }
    }
}
pub(super) struct VisualCacheCheckpoint {
    targets: Vec<Arc<VisualCapture>>,
    shader_targets: Vec<Arc<shader::VisualShaderTarget>>,
}
impl VisualState {
    pub(super) fn checkpoint(&self) -> VisualCacheCheckpoint {
        VisualCacheCheckpoint {
            targets: self.targets.clone(),
            shader_targets: self.shader_targets.clone(),
        }
    }
    pub(super) fn restore(&mut self, checkpoint: VisualCacheCheckpoint) {
        self.targets = checkpoint.targets;
        self.shader_targets = checkpoint.shader_targets;
    }
}
struct VisualCapture {
    policy: VisualPolicy,
    identity: VisualIdentity,
    versions: VisualVersions,
    frame: SourceRenderFrame,
    mask_depth: u16,
    effect: Option<(i64, CompositionEffectValue)>,
    // Full source recording ownership, not merely texture aliases.
    recording: Arc<Scene>,
    pages: Vec<(
        VisualIdentity,
        Arc<Scene>,
        crate::composition::visual::RenderPresentationState,
    )>,
    source: CapturedScene,
    shadow_source: Option<(Arc<Scene>, CapturedScene)>,
    computed: Option<compute::ComputedVisual>,
}
impl VisualCapture {
    fn image(&self) -> &GpuImage {
        self.computed
            .as_ref()
            .map_or(self.source.image(), |v| &v.image)
    }
    fn same(
        &self,
        node: &VisualSnapshot,
        frame: SourceRenderFrame,
        effect: Option<&(i64, CompositionEffectValue)>,
        policy: VisualPolicy,
        mask_depth: u16,
    ) -> bool {
        self.policy == policy
            && self.mask_depth == mask_depth
            && self.identity == *node.identity()
            && self.versions == node.versions()
            && self.frame.same_frame(frame)
            && self.effect.as_ref() == effect
            && effect.is_none_or(|(_, effect)| match effect {
                CompositionEffectValue::DropShadowSource { source, .. } => source.cacheable(),
                _ => true,
            })
            && same_pages(node, &self.pages)
    }
}
impl Renderer {
    /// Freeze callbacks once, then prepare the complete visual tree transactionally.
    pub async fn prepare_visual(
        &mut self,
        visual: &Visual,
        frame: Frame,
    ) -> Result<PreparedVisual, Error> {
        self.prepare_visual_with_options(visual, frame, Default::default())
            .await
    }
    /// Prepare original visual ordering, source scopes, effects and cache policy.
    pub async fn prepare_visual_with_options(
        &mut self,
        visual: &Visual,
        frame: Frame,
        options: VisualRenderOptions,
    ) -> Result<PreparedVisual, Error> {
        let snapshot = Arc::new(visual.snapshot()?);
        let prepared = self
            .prepare_visual_snapshot(snapshot, frame, options)
            .await?;
        visual.mark_snapshot_clean(&prepared.snapshot);
        Ok(prepared)
    }
    /// Prepare an already frozen generation without executing any UI/source callback
    /// from the visual tree. Typed nested CachedPicture sources retain their own
    /// explicitly preparation-only refresh contract.
    pub async fn prepare_visual_snapshot(
        &mut self,
        snapshot: Arc<VisualSnapshot>,
        frame: Frame,
        options: VisualRenderOptions,
    ) -> Result<PreparedVisual, Error> {
        frame.uniforms(self.device.limits().max_texture_dimension_2d)?;
        let nodes = preflight(&snapshot, &self.device, 0)?;
        let transaction = owned_shader::OwnedShaderFrame::begin(self)?;
        let mut builder = Builder {
            renderer: &mut *transaction.renderer,
            options,
            captures: Vec::new(),
            masks: Vec::new(),
            shaders: Vec::new(),
            metrics: VisualMetrics {
                nodes,
                ..Default::default()
            },
            depth: 0,
        };
        let recording = Arc::new(
            builder
                .node(
                    &snapshot,
                    Matrix4::IDENTITY,
                    1.0,
                    frame.into(),
                    None,
                    Vec::new(),
                    options.suppress_cached_cleartype,
                    false,
                    false,
                    false,
                    0,
                )
                .await?,
        );
        let prepared = builder
            .renderer
            .prepare_target_scene_inner(&recording, frame.into())
            .await?;
        // Complete candidates replace the mutable cache only after every source,
        // final consumer and ownership check has succeeded.
        builder.renderer.visuals.targets = builder.captures.clone();
        builder.renderer.visuals.shader_targets = builder.shaders.clone();
        let result = PreparedVisual {
            prepared,
            snapshot,
            recording,
            captures: builder.captures,
            masks: builder.masks,
            shaders: builder.shaders,
            metrics: builder.metrics,
        };
        transaction.commit()?;
        Ok(result)
    }
    /// Encode completed retained visual work; this invokes no source callbacks.
    pub fn encode_visual(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        visual: &PreparedVisual,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
    ) -> Result<(), Error> {
        self.encode(encoder, visual.scene(), target, clear)
    }
    /// Drop only renderer cache references; prepared generations remain alive.
    pub fn clear_visual_cache(&mut self) {
        self.visuals.targets.clear();
        self.visuals.shader_targets.clear();
    }
}
#[derive(Clone, Copy, PartialEq, Eq)]
enum VisualScope {
    Full,
    GeometryOnly,
    OpacityOnly,
}
struct Builder<'a> {
    renderer: &'a mut Renderer,
    options: VisualRenderOptions,
    captures: Vec<Arc<VisualCapture>>,
    masks: Vec<CapturedScene>,
    shaders: Vec<Arc<shader::VisualShaderTarget>>,
    metrics: VisualMetrics,
    depth: u8,
}
impl Builder<'_> {
    fn node<'a>(
        &'a mut self,
        node: &'a VisualSnapshot,
        parent: Matrix4,
        opacity: f32,
        target: RenderFrame,
        clip: Option<[f32; 4]>,
        masks: Vec<Mask>,
        suppress: bool,
        source_root: bool,
        capture_opacity: bool,
        skip_root_cache: bool,
        depth: u16,
    ) -> Pin<Box<dyn Future<Output = Result<Scene, Error>> + 'a>> {
        Box::pin(async move {
            let _target_depth = self.renderer.enter_target_depth(target);
            if depth >= 256 {
                return Err(Error::Capacity);
            }
            let properties = node.properties();
            let mut scene = Scene::default();
            let source_shader_output = !source_root
                && matches!(node.effect(),Some((_,CompositionEffectValue::WpfShader(effect))) if effect.captures_source_visual_opacity());
            if !properties.visible
                || opacity <= 0.0001
                || ((!source_root || capture_opacity)
                    && properties.opacity <= 0.0001
                    && !source_shader_output)
            {
                return Ok(scene);
            }
            let global = if source_root {
                parent
            } else {
                node.local_transform() * parent
            };
            affine(global)?;
            if !source_root
                && let Some((_, CompositionEffectValue::WpfShader(effect))) = node.effect()
            {
                return self
                    .shader(
                        node, effect, parent, global, opacity, target, clip, masks, suppress, depth,
                    )
                    .await;
            }
            if source_root
                && !skip_root_cache
                && properties.composite.cache_as_layer
                && (self.options.enable_layer_cache || properties.composite.requires_layer_cache)
            {
                // Original generic cached-root route keeps its historical local
                // transform. Explicit source-opacity input overrides that final
                // cache placement with this capture's translation only.
                let cache_global = if capture_opacity {
                    global
                } else {
                    node.local_transform()
                };
                return self
                    .isolated(
                        node,
                        Matrix4::IDENTITY,
                        cache_global,
                        opacity,
                        target,
                        clip,
                        masks,
                        suppress,
                        depth,
                        true,
                        capture_opacity,
                    )
                    .await;
            }
            if !source_root
                && (node.effect().is_some()
                    || (properties.composite.cache_as_layer
                        && (self.options.enable_layer_cache
                            || properties.composite.requires_layer_cache)))
            {
                return self
                    .isolated(
                        node, parent, global, opacity, target, clip, masks, suppress, depth, false,
                        false,
                    )
                    .await;
            }
            let (scope_count, next_clip, next_masks) = if source_root && !capture_opacity {
                (0, clip, masks)
            } else {
                self.scopes(
                    &mut scene,
                    node,
                    parent,
                    global,
                    target,
                    clip,
                    masks,
                    suppress,
                    if source_root {
                        VisualScope::OpacityOnly
                    } else {
                        VisualScope::Full
                    },
                )
                .await?
            };
            let opacity = if (source_root && !capture_opacity) || properties.opacity >= 1.0 {
                opacity
            } else {
                opacity * properties.opacity
            };
            if properties.has_template {
                for child in node.children() {
                    let draw = self
                        .node(
                            child,
                            global,
                            opacity,
                            target,
                            next_clip,
                            next_masks.clone(),
                            suppress,
                            false,
                            false,
                            false,
                            depth + 1,
                        )
                        .await?;
                    scene.append(&draw)?;
                }
            }
            if self.options.enable_local_culling
                && outside(node.page().local_bounds(), global, next_clip)
            {
                self.metrics.culled_pages = self
                    .metrics
                    .culled_pages
                    .checked_add(1)
                    .ok_or(Error::Capacity)?;
            } else {
                let page = node.page().scene().place_visual_page(
                    global,
                    target.dpi_scale,
                    suppress,
                    opacity,
                    target.source.is_some(),
                )?;
                scene.append(&page)?;
                self.metrics.pages = self.metrics.pages.checked_add(1).ok_or(Error::Capacity)?;
            }
            if !properties.has_template {
                for child in node.children() {
                    let draw = self
                        .node(
                            child,
                            global,
                            opacity,
                            target,
                            next_clip,
                            next_masks.clone(),
                            suppress,
                            false,
                            false,
                            false,
                            depth + 1,
                        )
                        .await?;
                    scene.append(&draw)?;
                }
            }
            for _ in 0..scope_count {
                scene.pop_scope()?;
            }
            Ok(scene)
        })
    }
    async fn isolated(
        &mut self,
        node: &VisualSnapshot,
        parent: Matrix4,
        mut global: Matrix4,
        opacity: f32,
        target: RenderFrame,
        clip: Option<[f32; 4]>,
        masks: Vec<Mask>,
        suppress: bool,
        depth: u16,
        ignore_effect: bool,
        opacity_only: bool,
    ) -> Result<Scene, Error> {
        if self.depth >= 16 {
            return Err(Error::Capacity);
        }
        let p = node.properties();
        let cold = &p.composite;
        let effect = if ignore_effect { None } else { node.effect() };
        let mut bounds = [0.0, 0.0, p.size[0], p.size[1]];
        let frame = if let Some((_, effect)) = effect {
            bounds = cold.effect_content_bounds.unwrap_or(bounds);
            if bounds[2] <= 0.0 || bounds[3] <= 0.0 {
                return Ok(Scene::default());
            }
            let padding = match effect {
                CompositionEffectValue::Blur { radius, kernel } => {
                    [match kernel {
                        VisualBlurKernel::Gaussian => (*radius * 2.0).ceil(),
                        VisualBlurKernel::Box => radius.ceil(),
                    }; 2]
                }
                CompositionEffectValue::DropShadow { radii, .. }
                | CompositionEffectValue::DropShadowSource { radii, .. } => {
                    radii.map(|r| (r.max(0.0) * 2.0).ceil())
                }
                _ => [0.0; 2],
            };
            let padding = cold.effect_raster_padding.map_or(padding, |value| {
                [if value.is_finite() {
                    value.max(0.0)
                } else {
                    0.0
                }; 2]
            });
            let frame = ManagedCaptureFrame::resolved(bounds, padding, target.dpi_scale)?;
            bounds = frame.padded_bounds;
            SourceRenderFrame::effect(frame, suppress)?
        } else {
            let suppress = cold.layer_cache_cleartype_policy.map_or(suppress, |v| !v);
            let Some(frame) = SourceRenderFrame::visual_layer(
                p.size,
                target.dpi_scale,
                cold.layer_cache_render_scale.unwrap_or(1.0),
                cold.requires_layer_cache,
                suppress,
            )?
            else {
                return Ok(Scene::default());
            };
            frame
        };
        let policy = VisualPolicy::new(self.renderer, self.options);
        let captured = self
            .captures
            .iter()
            .chain(self.renderer.visuals.targets.iter())
            .find(|v| {
                v.same(
                    node,
                    frame,
                    effect,
                    policy,
                    target.mask_depth.saturating_add(1),
                )
            })
            .cloned();
        let captured = if let Some(captured) = captured {
            self.metrics.cache_hits = self
                .metrics
                .cache_hits
                .checked_add(1)
                .ok_or(Error::Capacity)?;
            captured
        } else {
            let mut source_target = self.renderer.source_target(frame)?;
            source_target.mask_depth = target.mask_depth.checked_add(1).ok_or(Error::Capacity)?;
            self.depth += 1;
            let recording = self
                .node(
                    node,
                    frame.source_to_raster(),
                    1.0,
                    source_target,
                    None,
                    Vec::new(),
                    frame.suppress_cached_cleartype(),
                    true,
                    false,
                    effect.is_none(),
                    depth + 1,
                )
                .await;
            self.depth -= 1;
            let recording = Arc::new(recording?);
            let prepared = self
                .renderer
                .prepare_target_scene_inner(&recording, source_target)
                .await?;
            let source = self.renderer.capture(&prepared, None).await?;
            self.charge(source.capture_bytes())?;
            let shadow_source =
                if let Some((_, CompositionEffectValue::DropShadowSource { source: mask, .. })) =
                    effect
                {
                    let mask_recording =
                        if let crate::composition::visual::ShadowOpacitySource::Visual(mask) =
                            mask.as_ref()
                        {
                            self.depth += 1;
                            let result = self
                                .node(
                                    mask,
                                    frame.source_to_raster(),
                                    1.0,
                                    source_target,
                                    None,
                                    Vec::new(),
                                    frame.suppress_cached_cleartype(),
                                    true,
                                    false,
                                    false,
                                    depth + 1,
                                )
                                .await;
                            self.depth -= 1;
                            Arc::new(result?)
                        } else {
                            Arc::new(
                                mask.recording()?
                                    .ok_or(Error::ResourceDomain)?
                                    .place_source_capture(frame)?,
                            )
                        };
                    let prepared = self
                        .renderer
                        .prepare_target_scene_inner(&mask_recording, source_target)
                        .await?;
                    let capture = self.renderer.capture(&prepared, None).await?;
                    self.charge(capture.capture_bytes())?;
                    self.metrics.mask_captures = self
                        .metrics
                        .mask_captures
                        .checked_add(1)
                        .ok_or(Error::Capacity)?;
                    Some((mask_recording, capture))
                } else {
                    None
                };
            let computed = if let Some((_, value)) = effect {
                let mut compute = std::mem::take(&mut self.renderer.visuals.compute);
                let result = compute
                    .apply(
                        &self.renderer.device,
                        &self.renderer.queue,
                        shadow_source
                            .as_ref()
                            .map_or(source.image(), |(_, capture)| capture.image()),
                        value,
                        target.dpi_scale,
                    )
                    .await;
                self.renderer.visuals.compute = compute;
                result?
            } else {
                None
            };
            if let Some(compute) = &computed {
                self.charge(compute.bytes)?;
                self.metrics.effect_passes = self
                    .metrics
                    .effect_passes
                    .checked_add(compute.passes)
                    .ok_or(Error::Capacity)?;
            }
            self.metrics.captures = self
                .metrics
                .captures
                .checked_add(1)
                .ok_or(Error::Capacity)?;
            Arc::new(VisualCapture {
                policy,
                identity: node.identity().clone(),
                versions: node.versions(),
                frame,
                mask_depth: source_target.mask_depth,
                effect: effect.cloned(),
                recording,
                pages: retain_pages(node),
                source,
                shadow_source,
                computed,
            })
        };
        self.captures.push(captured.clone());
        if effect.is_none() && cold.layer_cache_snaps_to_device_pixels {
            let origin = global.transform_point([0.0; 2]);
            let physical = origin.map(|v| v * target.dpi_scale);
            let mut translation = Matrix4::IDENTITY;
            translation.0[3][0] = -(physical[0] - physical[0].floor()) / target.dpi_scale;
            translation.0[3][1] = -(physical[1] - physical[1].floor()) / target.dpi_scale;
            global = global * translation;
        }
        let mut scene = Scene::default();
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
                if opacity_only {
                    VisualScope::OpacityOnly
                } else {
                    VisualScope::Full
                },
            )
            .await?;
        let final_opacity = if p.opacity < 1.0 {
            opacity * p.opacity
        } else {
            opacity
        };
        let mut draw = ImageDraw::new(captured.image().clone(), bounds);
        draw.transform = affine(global)?;
        draw.opacity = final_opacity;
        match effect.map(|v| &v.1) {
            Some(CompositionEffectValue::DropShadow {
                offset,
                draw_source,
                ..
            })
            | Some(CompositionEffectValue::DropShadowSource {
                offset,
                draw_source,
                ..
            }) => {
                if let Some(frame) = captured.frame.effect_frame() {
                    draw.destination[2] = frame.logical_extent[0];
                    draw.destination[3] = frame.logical_extent[1];
                }
                draw.destination[0] += offset[0];
                draw.destination[1] += offset[1];
                scene.draw_image(draw)?;
                if *draw_source {
                    let mut source = ImageDraw::new(captured.source.image().clone(), bounds);
                    source.transform = affine(global)?;
                    source.opacity = final_opacity;
                    scene.draw_image(source)?;
                }
            }
            Some(CompositionEffectValue::ColorMatrix(matrix)) => {
                let mut effect =
                    crate::image_effect::ImageEffectDraw::new(captured.image().clone(), bounds);
                effect.blur_storage = crate::image_effect::ImageBlurPolicy::Managed;
                for vertex in &mut effect.vertices {
                    vertex.position = global.transform_point(vertex.position);
                    vertex.color[3] = final_opacity;
                }
                effect.effect.color_matrix_red = matrix.red;
                effect.effect.color_matrix_green = matrix.green;
                effect.effect.color_matrix_blue = matrix.blue;
                effect.effect.color_matrix_alpha = matrix.alpha;
                effect.effect.color_matrix_offset = matrix.offset;
                effect.effect.flags0[2] = 1.0;
                scene.draw_image_effect(effect)?;
            }
            Some(CompositionEffectValue::Blend(blend)) => scene.draw_visual_image(draw, *blend)?,
            _ => scene.draw_image(draw)?,
        }
        for _ in 0..scopes {
            scene.pop_scope()?;
        }
        Ok(scene)
    }
    fn charge(&mut self, bytes: u64) -> Result<(), Error> {
        self.metrics.texture_bytes = self
            .metrics
            .texture_bytes
            .checked_add(bytes)
            .ok_or(Error::Capacity)?;
        if self.metrics.texture_bytes > 512 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        Ok(())
    }
}

impl Builder<'_> {
    async fn scopes(
        &mut self,
        scene: &mut Scene,
        node: &VisualSnapshot,
        parent: Matrix4,
        global: Matrix4,
        target: RenderFrame,
        mut clip: Option<[f32; 4]>,
        mut masks: Vec<Mask>,
        suppress: bool,
        mode: VisualScope,
    ) -> Result<(usize, Option<[f32; 4]>, Vec<Mask>), Error> {
        let cold = &node.properties().composite;
        let mut count = 0;
        if mode != VisualScope::OpacityOnly {
            if let Some(bounds) = cold.clip_bounds {
                self.rectangle_scope(scene, bounds, global, target, &mut clip, &mut masks)
                    .await?;
                count += 1;
            }
            if let Some(bounds) = cold.outer_clip_bounds {
                self.rectangle_scope(scene, bounds, parent, target, &mut clip, &mut masks)
                    .await?;
                count += 1;
            }
            if let Some(value) = &cold.local_composite_clip {
                self.composite_scope(scene, value, global, target, &mut clip, &mut masks)
                    .await?;
                count += 1;
            }
            for value in cold.outer_composite_clips.iter() {
                self.composite_scope(scene, value, parent, target, &mut clip, &mut masks)
                    .await?;
                count += 1;
            }
            if let Some(path) = &cold.geometry_clip {
                self.geometry_scope(scene, path, global, target, &mut clip, &mut masks)
                    .await?;
                count += 1;
            }
        }
        if mode == VisualScope::GeometryOnly {
            return Ok((count, clip, masks));
        }
        // Original opacity remains in the caller's active per-draw scalar. The
        // opacity mask is pushed last and is independent of that scalar.
        if let Some(bounds) = cold.opacity_mask_bounds {
            if let Some(picture) = &cold.opacity_mask_picture {
                let mut source = Scene::default();
                for mask in &masks {
                    source.push_mask(mask.clone())?;
                }
                source.push_visual_clip(bounds, global)?;
                source.append(&picture.place_visual_page(
                    global,
                    target.dpi_scale,
                    suppress,
                    1.0,
                    true,
                )?)?;
                source.pop_scope()?;
                for _ in &masks {
                    source.pop_scope()?;
                }
                let mask = self.mask(source, target).await?;
                scene.push_mask(mask.clone())?;
                masks = vec![mask];
                count += 1;
            } else if let Some(brush) = node.opacity_mask() {
                let analytic =
                    if target.mask_depth <= 1 && masks.is_empty() && brush.material().is_some() {
                        solid_mask(
                            brush.material().ok_or(Error::ResourceDomain)?,
                            bounds,
                            global,
                            target,
                            self.renderer.device.limits().max_texture_dimension_2d,
                        )?
                    } else {
                        None
                    };
                let mask = if let Some(mask) = analytic {
                    mask
                } else {
                    let mut source = Scene::default();
                    for mask in &masks {
                        source.push_mask(mask.clone())?;
                    }
                    let mut brush_scene = Scene::default();
                    brush.record(&mut brush_scene, bounds)?;
                    source.append(&brush_scene.place_visual_page(
                        global,
                        target.dpi_scale,
                        suppress,
                        1.0,
                        true,
                    )?)?;
                    for _ in &masks {
                        source.pop_scope()?;
                    }
                    self.mask(source, target).await?
                };
                let replace = matches!(&mask,Mask::RenderedTexture(v) if v.replaces_chain);
                scene.push_mask(mask.clone())?;
                if replace {
                    masks.clear();
                }
                masks.push(mask);
                count += 1;
            }
        }
        Ok((count, clip, masks))
    }
    async fn mask(&mut self, source: Scene, target: RenderFrame) -> Result<Mask, Error> {
        let target = RenderFrame {
            frame: target.root,
            origin: [0; 2],
            ..target
        };
        let prepared = self
            .renderer
            .prepare_target_scene_inner(&source, target)
            .await?;
        let capture = self.renderer.capture(&prepared, None).await?;
        self.charge(capture.capture_bytes())?;
        self.metrics.mask_captures = self
            .metrics
            .mask_captures
            .checked_add(1)
            .ok_or(Error::Capacity)?;
        let size = capture.image().size();
        let texture = TextureMask::new(
            capture.image().clone(),
            [0.0, 0.0, size[0] as f32, size[1] as f32],
            MaskChannel::Alpha,
        );
        let mask = Mask::RenderedTexture(RenderedTextureMask {
            texture,
            size,
            dpi: target.dpi_scale,
            projection: target
                .uniforms(self.renderer.device.limits().max_texture_dimension_2d)?
                .projection,
            replaces_chain: true,
        });
        self.masks.push(capture);
        Ok(mask)
    }
}
fn intersection(previous: Option<[f32; 4]>, next: [f32; 4]) -> [f32; 4] {
    let Some(previous) = previous else {
        return next;
    };
    let l = previous[0].max(next[0]);
    let t = previous[1].max(next[1]);
    [
        l,
        t,
        previous[2].min(next[2]).max(l),
        previous[3].min(next[3]).max(t),
    ]
}
fn outside(bounds: Option<[f32; 4]>, transform: Matrix4, clip: Option<[f32; 4]>) -> bool {
    let (Some([x, y, w, h]), Some(clip)) = (bounds, clip) else {
        return false;
    };
    let points =
        [[x, y], [x + w, y], [x, y + h], [x + w, y + h]].map(|p| transform.transform_point(p));
    if points.iter().flatten().any(|v| !v.is_finite()) {
        return false;
    }
    let min_x = points[0][0]
        .min(points[1][0])
        .min(points[2][0].min(points[3][0]));
    let max_x = points[0][0]
        .max(points[1][0])
        .max(points[2][0].max(points[3][0]));
    let min_y = points[0][1]
        .min(points[1][1])
        .min(points[2][1].min(points[3][1]));
    let max_y = points[0][1]
        .max(points[1][1])
        .max(points[2][1].max(points[3][1]));
    max_x <= clip[0] || min_x >= clip[2] || max_y <= clip[1] || min_y >= clip[3]
}
fn affine(transform: Matrix4) -> Result<Affine, Error> {
    let m = transform.0;
    if m.iter().flatten().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    if m[0][2] != 0.0
        || m[0][3] != 0.0
        || m[1][2] != 0.0
        || m[1][3] != 0.0
        || m[2] != [0.0, 0.0, 1.0, 0.0]
        || m[3][2] != 0.0
        || m[3][3] != 1.0
    {
        return Err(Error::UnsupportedExecution);
    }
    Ok(Affine([
        m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1],
    ]))
}
fn preflight(node: &VisualSnapshot, device: &wgpu::Device, depth: u16) -> Result<u32, Error> {
    if depth >= 256 {
        return Err(Error::Capacity);
    }
    affine(node.global_transform())?;
    node.page()
        .scene()
        .preflight_source_capture_devices(device)?;
    if let Some(brush) = node.opacity_mask() {
        brush.preflight_device(device)?;
    }
    if let Some(picture) = &node.properties().composite.opacity_mask_picture {
        picture.preflight_source_capture_devices(device)?;
    }
    if let Some((_, effect)) = node.effect() {
        match effect {
            CompositionEffectValue::WpfShader(effect) => effect.preflight_device(device)?,
            CompositionEffectValue::ColorMatrix(matrix) => {
                if matrix.managed_values().iter().any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
                if matrix.luminance_to_alpha {
                    return Err(Error::UnsupportedExecution);
                }
            }
            CompositionEffectValue::DropShadow { offset, color, .. }
            | CompositionEffectValue::DropShadowSource { offset, color, .. }
                if offset.iter().chain(color).any(|v| !v.is_finite()) =>
            {
                return Err(Error::InvalidInput);
            }
            _ => {}
        }
    }
    let mut count = 1u32;
    if let Some((_, CompositionEffectValue::DropShadowSource { source, .. })) = node.effect() {
        match source.as_ref() {
            crate::composition::visual::ShadowOpacitySource::Visual(mask) => {
                count = count
                    .checked_add(preflight(mask, device, depth + 1)?)
                    .ok_or(Error::Capacity)?;
            }
            crate::composition::visual::ShadowOpacitySource::Picture(scene) => {
                scene.preflight_source_capture_devices(device)?
            }
            crate::composition::visual::ShadowOpacitySource::Brush { brush, .. } => {
                brush.preflight_device(device)?;
                source.recording()?;
            }
        }
    }
    for child in node.children() {
        count = count
            .checked_add(preflight(child, device, depth + 1)?)
            .ok_or(Error::Capacity)?;
    }
    Ok(count)
}
fn solid_mask(
    brush: &crate::vector::Brush,
    bounds: [f32; 4],
    transform: Matrix4,
    target: RenderFrame,
    maximum: u32,
) -> Result<Option<Mask>, Error> {
    let crate::vector::BrushKind::Solid(color) = brush.kind else {
        return Ok(None);
    };
    let Some(rows) = physical_rows(transform, target.dpi_scale)? else {
        return Ok(None);
    };
    let source_alpha = color[3] * brush.opacity;
    let alpha = if source_alpha.is_finite() {
        source_alpha.clamp(0.0, 1.0)
    } else {
        0.0
    };
    let uniforms = MaskUniforms {
        coordinate0: rows[0],
        coordinate1: rows[1],
        bounds: [
            bounds[0],
            bounds[1],
            bounds[0] + bounds[2],
            bounds[1] + bounds[3],
        ],
        radii_x: [0.0; 4],
        radii_y: [0.0; 4],
        options: [2.0, alpha, 0.0, 0.0],
    };
    let root = RenderFrame {
        frame: target.root,
        origin: [0; 2],
        ..target
    };
    Ok(Some(Mask::FrameAnalytic(FrameAnalyticMask {
        uniforms,
        size: [target.root.width, target.root.height],
        dpi: target.dpi_scale,
        projection: root.uniforms(maximum)?.projection,
        replaces_chain: false,
    })))
}

fn retain_pages(
    node: &VisualSnapshot,
) -> Vec<(
    VisualIdentity,
    Arc<Scene>,
    crate::composition::visual::RenderPresentationState,
)> {
    let mut pages = Vec::new();
    node.visit_pages(&mut |node| {
        pages.push((
            node.identity().clone(),
            node.page().scene().clone(),
            node.page().presentation_state(),
        ))
    });
    pages
}
fn same_pages(
    node: &VisualSnapshot,
    pages: &[(
        VisualIdentity,
        Arc<Scene>,
        crate::composition::visual::RenderPresentationState,
    )],
) -> bool {
    let mut index = 0;
    let mut same = true;
    node.visit_pages(&mut |node| {
        same &= node.page().can_cache_incremental_page()
            && node.page().scene().visual_page_cacheable()
            && pages
                .get(index)
                .is_some_and(|(identity, scene, presentation)| {
                    *identity == *node.identity()
                        && Arc::ptr_eq(scene, node.page().scene())
                        && *presentation == node.page().presentation_state()
                });
        index += 1;
    });
    same && index == pages.len()
}

fn physical_rows(transform: Matrix4, dpi: f32) -> Result<Option<[[f32; 4]; 2]>, Error> {
    let [a, b, c, d, x, y] = affine(transform)?.0;
    // Original float Matrix3x2 multiplication then inversion. The translation
    // includes the actual full-root viewport origin (zero for these owned targets).
    let [a, b, c, d, x, y] = [
        a * dpi + b * 0.0,
        a * 0.0 + b * dpi,
        c * dpi + d * 0.0,
        c * 0.0 + d * dpi,
        (x * dpi + y * 0.0) + 0.0,
        (x * 0.0 + y * dpi) + 0.0,
    ];
    let determinant = a * d - b * c;
    if determinant.abs() < f32::from_bits(1) {
        return Ok(None);
    }
    let inverse = 1.0 / determinant;
    let rows = [
        [d * inverse, -c * inverse, (c * y - x * d) * inverse, 0.0],
        [-b * inverse, a * inverse, (x * b - a * y) * inverse, 0.0],
    ];
    if rows.iter().flatten().any(|v| !v.is_finite()) {
        return Ok(None);
    }
    Ok(Some(rows))
}
