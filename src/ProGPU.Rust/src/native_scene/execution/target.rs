//! Actual materialized target history, including independent raw-cache domains.
use super::*;
pub struct NativeLayerTargetCursor {
    scene: Arc<NativeScene>,
    size: [u32; 2],
    frame: NativeScissor,
    presentation: wire::Presentation,
    scope_materialized: [bool; SCENE_MAX_STACK_DEPTH as usize],
    extents: [NativeScissor; SCENE_MAX_MATERIALIZED_LAYERS as usize],
    presentations: [wire::Presentation; SCENE_MAX_MATERIALIZED_LAYERS as usize],
    ignores_alpha: [bool; SCENE_MAX_MATERIALIZED_LAYERS as usize],
    scope_depth: usize,
    materialized_depth: usize,
}
impl NativeLayerTargetCursor {
    pub fn new(scene: Arc<NativeScene>, size: [u32; 2], presentation: wire::Presentation) -> Self {
        Self {
            scene,
            size,
            frame: NativeScissor::full(size),
            presentation,
            scope_materialized: [false; SCENE_MAX_STACK_DEPTH as usize],
            extents: [NativeScissor::default(); SCENE_MAX_MATERIALIZED_LAYERS as usize],
            presentations: [wire::Presentation::default(); SCENE_MAX_MATERIALIZED_LAYERS as usize],
            ignores_alpha: [false; SCENE_MAX_MATERIALIZED_LAYERS as usize],
            scope_depth: 0,
            materialized_depth: 0,
        }
    }
    pub fn scalar(scene: Arc<NativeScene>, size: [u32; 2], dpi: f32) -> Self {
        Self::new(scene, size, scalar_presentation(size, dpi))
    }
    pub fn current(&self) -> NativeScissor {
        if self.materialized_depth == 0 {
            self.frame
        } else {
            self.extents[self.materialized_depth - 1]
        }
    }
    pub fn current_presentation(&self) -> wire::Presentation {
        if self.materialized_depth == 0 {
            self.presentation
        } else {
            self.presentations[self.materialized_depth - 1]
        }
    }
    pub fn current_ignores_alpha(&self) -> bool {
        self.materialized_depth != 0 && self.ignores_alpha[self.materialized_depth - 1]
    }
    pub fn advance(&mut self, command: wire::Command) -> Result<NativeScissor, Error> {
        if command.kind == SCENE_COMMAND_PUSH_LAYER {
            let layer = read_layer(&self.scene, command)?;
            let active = layer_requires_materialization(&layer);
            if self.scope_depth == self.scope_materialized.len()
                || active && self.materialized_depth == self.extents.len()
            {
                return Err(Error::InvalidInput);
            }
            if active {
                let mut p = self.current_presentation();
                let extent = if let Some(sample) = layer_output_frame(&self.scene, &layer) {
                    let edge = |v: i64| v.clamp(0, i64::from(u32::MAX)) as u32;
                    let left = edge(i64::from(sample.output_x) + i64::from(p.viewport_x));
                    let top = edge(i64::from(sample.output_y) + i64::from(p.viewport_y));
                    let right = edge(
                        i64::from(sample.output_x)
                            + i64::from(sample.output_width)
                            + i64::from(p.viewport_x),
                    );
                    let bottom = edge(
                        i64::from(sample.output_y)
                            + i64::from(sample.output_height)
                            + i64::from(p.viewport_y),
                    );
                    self.current().intersection(NativeScissor {
                        x: left,
                        y: top,
                        width: right.wrapping_sub(left),
                        height: bottom.wrapping_sub(top),
                        drawable: right > left && bottom > top,
                    })
                } else if layer.flags & SCENE_LAYER_CACHE_LOCAL_SPACE != 0 {
                    let extent = |v: f32, dpi: f32| {
                        (f64::from(v) * f64::from(dpi))
                            .ceil()
                            .min(f64::from(u32::MAX)) as u32
                    };
                    let width = extent(layer.bounds.width, p.dpi_scale_x);
                    let height = extent(layer.bounds.height, p.dpi_scale_y);
                    p.viewport_x = 0;
                    p.viewport_y = 0;
                    p.viewport_width = width;
                    p.viewport_height = height;
                    NativeScissor {
                        x: 0,
                        y: 0,
                        width,
                        height,
                        drawable: width != 0 && height != 0,
                    }
                } else {
                    let parent = self.current();
                    let size = [
                        target_domain_extent(self.size[0], parent.x, parent.width),
                        target_domain_extent(self.size[1], parent.y, parent.height),
                    ];
                    parent.intersection(resolve_layer_scissor(&layer, size, p))
                };
                self.extents[self.materialized_depth] = extent;
                self.ignores_alpha[self.materialized_depth] =
                    layer.flags & SCENE_LAYER_IGNORE_ALPHA != 0;
                self.presentations[self.materialized_depth] = p;
                self.materialized_depth += 1;
            }
            self.scope_materialized[self.scope_depth] = active;
            self.scope_depth += 1;
        } else if command.kind == SCENE_COMMAND_POP_LAYER {
            if self.scope_depth == 0 {
                return Err(Error::InvalidInput);
            }
            let active = self.scope_materialized[self.scope_depth - 1];
            if active && self.materialized_depth == 0 {
                return Err(Error::InvalidInput);
            }
            self.scope_depth -= 1;
            if active {
                self.materialized_depth -= 1;
            }
        }
        Ok(self.current())
    }
}
pub fn read_layer(scene: &NativeScene, command: wire::Command) -> Result<wire::Layer, Error> {
    if command.payload_size == 0 {
        return Ok(default_layer());
    }
    if command.payload_size != wire::Layer::WIRE_SIZE as u32 {
        return Err(Error::InvalidInput);
    }
    scene
        .envelope()
        .bytes()
        .get(command.payload_offset as usize..)
        .and_then(wire::Layer::read)
        .ok_or(Error::InvalidInput)
}
/// Only allocation and clip consumers use this common prefix; shader execution
/// retains its separate complete v5/v6 reader and exact frame type.
pub fn layer_output_frame(
    scene: &NativeScene,
    layer: &wire::Layer,
) -> Option<wire::ShaderSampleFrame> {
    let resource = scene.envelope().resource(layer.effect_resource_index)?;
    if resource.record.kind != SCENE_RESOURCE_WPF_SHADER_EFFECT {
        return None;
    }
    if resource.payload.len() == wire::ShaderEffectAffine::WIRE_SIZE {
        let source = wire::ShaderEffectAffine::read(resource.payload)?;
        return (source.struct_size == wire::ShaderEffectAffine::WIRE_SIZE as u32
            && source.version == 6)
            .then_some(source.frame.placement);
    }
    if resource.payload.len() != wire::ShaderEffectSamples::WIRE_SIZE {
        return None;
    }
    let source = wire::ShaderEffectSamples::read(resource.payload)?;
    (source.struct_size == wire::ShaderEffectSamples::WIRE_SIZE as u32 && source.version == 5)
        .then_some(source.frame)
}
