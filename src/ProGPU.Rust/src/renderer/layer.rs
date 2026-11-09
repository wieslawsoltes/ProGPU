//! Original bounded target mapping and bottom-up isolated scene compilation.
use super::*;
use crate::{
    image::{ImageDraw, ImageSampling},
    scene::LayerDraw,
};

/// Actual work preparing isolated subtrees. These are preparation costs, not
/// uploads or submissions repeated by replaying the final prepared scene.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LayerMetrics {
    /// Nonempty isolated captures, including nested captures.
    pub captures: u32,
    /// Source target allocations, including targets consumed by effects.
    pub capture_bytes: u64,
    /// Actual effect intermediate allocations, including final effect outputs.
    pub effect_texture_bytes: u64,
    /// Padded original effect uniform storage.
    pub effect_uniform_bytes: u64,
    /// Original ordered effect compute passes.
    pub effect_passes: u64,
    /// Actual subtree vertex/index/glyph payload uploaded during preparation.
    pub geometry_bytes: u64,
    /// Subtree frame and mask uniform payload, excluding effect uniforms.
    pub uniform_bytes: u64,
    /// Subtree original glyph outline/coverage/bitmap uploads.
    pub glyph_upload_bytes: u64,
    /// Subtree original image and coverage pixel uploads.
    pub image_mask_upload_bytes: u64,
    /// Ordered source draws across captured subtrees.
    pub source_draws: u64,
    /// Destination-aware operations inside captured subtrees.
    pub advanced_operations: u64,
    /// Actual three-pass operations and final subtree root copies.
    pub advanced_passes: u64,
    /// Actual destination-aware subtree scratch allocations.
    pub advanced_texture_bytes: u64,
    /// Original padded sampling slots and resolve/copy frames.
    pub advanced_uniform_bytes: u64,
    /// Original advanced resolve/copy geometry payloads.
    pub advanced_geometry_bytes: u64,
}

#[derive(Clone, Copy)]
pub(super) struct RenderFrame {
    pub frame: Frame,
    root: Frame,
    pub origin: [u32; 2],
}
impl std::ops::Deref for RenderFrame {
    type Target = Frame;
    fn deref(&self) -> &Frame {
        &self.frame
    }
}
impl From<Frame> for RenderFrame {
    fn from(frame: Frame) -> Self {
        Self {
            frame,
            root: frame,
            origin: [0; 2],
        }
    }
}
impl RenderFrame {
    pub fn uniforms(self, maximum: u32) -> Result<Uniforms, Error> {
        self.frame.uniforms(maximum)?;
        let mut uniforms = self.root.uniforms(maximum)?;
        if self.origin == [0; 2] && self.frame == self.root {
            return Ok(uniforms);
        }
        // Original managed CreateMaskRenderUniforms: retain world geometry and
        // brush coordinates, mapping only the actual physical attachment.
        let sx = self.root.width as f32 / self.width as f32;
        let sy = self.root.height as f32 / self.height as f32;
        uniforms.projection[0] *= sx;
        uniforms.projection[5] *= sy;
        uniforms.projection[12] = (uniforms.projection[12] + 1.0) * sx
            - 1.0
            - 2.0 * self.origin[0] as f32 / self.width as f32;
        uniforms.projection[13] = uniforms.projection[13] * sy + 1.0 - sy
            + 2.0 * self.origin[1] as f32 / self.height as f32;
        uniforms.canvas_size = [self.width as f32, self.height as f32];
        uniforms.render_origin = self.origin.map(|v| v as f32);
        uniforms.pad1 = [-(self.width as f32), -(self.height as f32)];
        if !bytemuck::cast_slice::<_, f32>(std::slice::from_ref(&uniforms))
            .iter()
            .all(|v| v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        Ok(uniforms)
    }
    fn empty(self) -> bool {
        self.width == 0 || self.height == 0
    }
    fn child(self, layer: &LayerDraw) -> Result<Self, Error> {
        layer.validate()?;
        let Some([x, y, width, height]) = layer.bounds else {
            return Ok(self);
        };
        // Original native four independent presentation lanes, including +0.
        let edges: [f32; 4] = (wide::f32x4::from([x, y, x + width, y + height])
            * wide::f32x4::splat(self.dpi_scale)
            + wide::f32x4::splat(0.0))
        .into();
        if !edges.iter().all(|v| v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let extents = [
            self.root.width,
            self.root.height,
            self.root.width,
            self.root.height,
        ];
        let edges = std::array::from_fn::<_, 4, _>(|i| {
            let value = edges[i].clamp(0.0, extents[i] as f32);
            let rounded = value.round();
            if (value - rounded).abs() < 0.0001 {
                rounded
            } else {
                value
            }
        });
        let [left, top] = [edges[0].floor() as u32, edges[1].floor() as u32];
        let [right, bottom] = [edges[2].ceil() as u32, edges[3].ceil() as u32];
        let left = left.max(self.origin[0]).min(self.origin[0] + self.width);
        let top = top.max(self.origin[1]).min(self.origin[1] + self.height);
        let right = right.min(self.origin[0] + self.width);
        let bottom = bottom.min(self.origin[1] + self.height);
        let empty = edges[2] <= edges[0] || edges[3] <= edges[1];
        Ok(Self {
            root: self.root,
            origin: [left, top],
            frame: Frame {
                width: if empty { 0 } else { right.saturating_sub(left) },
                height: if empty { 0 } else { bottom.saturating_sub(top) },
                dpi_scale: self.dpi_scale,
            },
        })
    }
}

impl Renderer {
    pub(super) fn preflight_layers(&self, scene: &Scene, frame: RenderFrame) -> Result<(), Error> {
        let mut count = 0u32;
        let mut bytes = 0u64;
        let mut passes = 0u32;
        self.preflight_layer_tree(scene, frame, &mut count, &mut bytes, &mut passes)
    }
    fn preflight_layer_tree(
        &self,
        scene: &Scene,
        frame: RenderFrame,
        count: &mut u32,
        bytes: &mut u64,
        passes: &mut u32,
    ) -> Result<(), Error> {
        let (states, _) = scene.compile_states(frame.dpi_scale)?;
        self.validate_mask_states(&states)?;
        if scene.layer_depth() > 16 {
            return Err(Error::Capacity);
        }
        let mut advanced_source = [0u32; 2];
        for layer in scene.layers() {
            *count = count.checked_add(1).ok_or(Error::Capacity)?;
            if *count > 16 * 1024 {
                return Err(Error::Capacity);
            }
            if self.image_sampling.is_none() {
                return Err(Error::UnsupportedExecution);
            }
            let target = frame.child(layer)?;
            let effect = layer
                .effects
                .as_ref()
                .map(|chain| chain.plan([frame.dpi_scale; 2]))
                .transpose()?;
            if let Some(effect) = &effect {
                *passes = passes
                    .checked_add(effect.pass_count())
                    .ok_or(Error::Capacity)?;
                if *passes > 16 * 1024 {
                    return Err(Error::Capacity);
                }
            }
            if !target.empty() {
                if !layer.blend.is_fixed() {
                    advanced_source[0] = advanced_source[0].max(target.width);
                    advanced_source[1] = advanced_source[1].max(target.height);
                }
                target.uniforms(self.device.limits().max_texture_dimension_2d)?;
                let allocation = u64::from(target.width) * u64::from(target.height) * 4;
                if allocation > 256 * 1024 * 1024 {
                    return Err(Error::Capacity);
                }
                let effects = effect
                    .as_ref()
                    .map(|p| p.required_texture_bytes([target.width, target.height]))
                    .transpose()?
                    .unwrap_or(0);
                *bytes = bytes
                    .checked_add(allocation)
                    .and_then(|v| v.checked_add(effects))
                    .ok_or(Error::Capacity)?;
                // Conservative complete compilation allocation budget. Reusable
                // native pool residency is a separate optimization, not assumed.
                if *bytes > 512 * 1024 * 1024 {
                    return Err(Error::Capacity);
                }
            }
            self.preflight_layer_tree(&layer.scene, target, count, bytes, passes)?;
        }
        if advanced_source != [0; 2] {
            let advanced_bytes =
                advanced::advanced_texture_bytes([frame.width, frame.height], advanced_source)?;
            *bytes = bytes.checked_add(advanced_bytes).ok_or(Error::Capacity)?;
            if *bytes > 512 * 1024 * 1024 {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }
    pub(super) async fn prepare_layer_tree(
        &mut self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<PreparedScene, Error> {
        if scene.layer_depth() == 0 {
            return self.prepare_flat_scene(scene, frame).await;
        }
        let mut outputs = Vec::new();
        let mut metrics = LayerMetrics::default();
        for layer in scene.layers() {
            outputs.try_reserve(1).map_err(|_| Error::Capacity)?;
            let target = frame.child(layer)?;
            if target.empty() {
                outputs.push(None);
                continue;
            }
            // Render each complete child before preparing parent glyphs. A later
            // atlas growth cannot invalidate an already materialized texture.
            let source = Box::pin(self.prepare_layer_tree(&layer.scene, target)).await?;
            let capture = self.capture(&source, layer.effects.as_ref()).await?;
            metrics.add_source(&source, &capture)?;
            let [x, y] = target.origin.map(|v| v as f32 / frame.dpi_scale);
            let mut draw = ImageDraw::new(
                capture.into_image(),
                [
                    x,
                    y,
                    target.width as f32 / frame.dpi_scale,
                    target.height as f32 / frame.dpi_scale,
                ],
            );
            draw.opacity = layer.opacity;
            // Original advanced source resolve always uses the linear layer binding.
            draw.sampling = if layer.linear_sampling || !layer.blend.is_fixed() {
                ImageSampling::Linear
            } else {
                ImageSampling::Nearest
            };
            draw.validate()?;
            outputs.push(Some((
                draw,
                [
                    target.origin[0],
                    target.origin[1],
                    target.width,
                    target.height,
                ],
            )));
        }
        let resolved = scene.replace_layers(outputs)?;
        let mut prepared = self.prepare_flat_scene(&resolved, frame).await?;
        prepared.layers = metrics;
        Ok(prepared)
    }
}
impl LayerMetrics {
    fn add_source(&mut self, scene: &PreparedScene, capture: &CapturedScene) -> Result<(), Error> {
        let child = scene.layers;
        let source = scene.metrics;
        self.captures = self
            .captures
            .checked_add(child.captures)
            .and_then(|v| v.checked_add(1))
            .ok_or(Error::Capacity)?;
        macro_rules! add {
            ($field:ident, $value:expr) => {
                self.$field = self
                    .$field
                    .checked_add(child.$field)
                    .and_then(|v| v.checked_add($value))
                    .ok_or(Error::Capacity)?;
            };
        }
        add!(capture_bytes, capture.capture_bytes());
        add!(geometry_bytes, source.geometry_bytes);
        add!(uniform_bytes, source.uniform_bytes);
        add!(glyph_upload_bytes, source.glyph_upload_bytes);
        let image_mask_bytes = source
            .image_upload_bytes
            .checked_add(source.mask_upload_bytes)
            .ok_or(Error::Capacity)?;
        add!(image_mask_upload_bytes, image_mask_bytes);
        add!(source_draws, u64::from(source.draws));
        let advanced = scene.advanced_blend_metrics();
        add!(advanced_operations, u64::from(advanced.operations));
        add!(advanced_passes, u64::from(advanced.render_passes));
        add!(advanced_texture_bytes, advanced.texture_bytes);
        add!(advanced_uniform_bytes, advanced.uniform_bytes);
        add!(advanced_geometry_bytes, advanced.geometry_bytes);
        let effects = capture.effect_metrics().unwrap_or_default();
        add!(effect_texture_bytes, effects.texture_bytes);
        add!(effect_uniform_bytes, effects.uniform_bytes);
        add!(effect_passes, u64::from(effects.compute_passes));
        Ok(())
    }
}
