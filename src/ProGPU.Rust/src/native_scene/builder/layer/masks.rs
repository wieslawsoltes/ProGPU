//! Original eight mask producers, preserving selected auxiliary inventory order.
use super::*;
use crate::native_scene::semantic::layer_mask as scalar;

/// Borrowed complete source inventories for one immutable composite resource.
#[derive(Clone, Copy, Debug)]
pub struct NativeSceneCompositeMask<'a> {
    pub brush_masks: &'a [wire::LayerBrushMask],
    pub geometry_masks: &'a [wire::LayerGeometryMask],
    pub geometry_primitives: &'a [wire::GeometryPrimitive],
    pub picture_masks: &'a [wire::LayerPictureMask],
    pub picture_streams: &'a [u8],
    pub paths: &'a [wire::ClipPath],
    pub segments: &'a [wire::PathSegment],
    pub boolean_nodes: &'a [wire::PathBooleanNode],
    pub gradient_stops: &'a [wire::GradientStop],
    pub opacity: f32,
}
impl NativeSceneBuilder {
    pub fn add_axis_aligned_clip_mask(&mut self, bounds: wire::ImageRect) -> BuildResult<u32> {
        self.add_rounded_rectangle_mask(wire::LayerMask {
            bounds,
            transform: Self::identity_transform(),
            opacity: 1.0,
            flags: SCENE_LAYER_MASK_AXIS_CLIP_AREA,
            ..Default::default()
        })
    }
    pub fn add_rounded_rectangle_mask(&mut self, source: wire::LayerMask) -> BuildResult<u32> {
        let mask = canonical_analytic(source);
        if !scalar::analytic(&mask) || self.resources.len() >= MAX_RESOURCES as usize {
            return Err(B::InvalidArgument);
        }
        self.publish_layer_resource(SCENE_RESOURCE_LAYER_MASK, &mask, Vec::new(), false)
    }
    pub fn add_coverage_mask(
        &mut self,
        source: wire::LayerCoverageMask,
        coverage: &[u8],
    ) -> BuildResult<u32> {
        let mask = wire::LayerCoverageMask {
            struct_size: wire::LayerCoverageMask::WIRE_SIZE as u32,
            kind: SCENE_LAYER_MASK_COVERAGE_BITMAP,
            flags: 0,
            reserved0: 0,
            reserved1: 0,
            ..source
        };
        if coverage.len() > u32::MAX as usize
            || !scalar::coverage(&mask, coverage.len() as u32)
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        let auxiliary = copy_bytes(coverage)?;
        self.publish_layer_resource(SCENE_RESOURCE_LAYER_MASK, &mask, auxiliary, false)
    }
    pub fn add_brush_mask(
        &mut self,
        source: wire::LayerBrushMask,
        stops: &[wire::GradientStop],
    ) -> BuildResult<u32> {
        if stops.len() > SCENE_MAX_GRADIENT_STOPS as usize
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        let mut mask = wire::LayerBrushMask {
            struct_size: wire::LayerBrushMask::WIRE_SIZE as u32,
            kind: SCENE_LAYER_MASK_BRUSH,
            flags: 0,
            gradient_stop_count: stops.len() as u32,
            reserved0: 0,
            ..source
        };
        mask.brush.stop_offset = 0;
        if !admission::brush(&mask, stops)
            || mask.gradient_stop_count
                != crate::native_scene::semantic::stored_stop_count(&mask.brush)
        {
            return Err(B::InvalidArgument);
        }
        self.publish_layer_resource(SCENE_RESOURCE_LAYER_MASK, &mask, encode(stops)?, false)
    }
    pub fn add_geometry_mask(
        &mut self,
        source: wire::LayerGeometryMask,
        primitives: &[wire::GeometryPrimitive],
        stops: &[wire::GradientStop],
    ) -> BuildResult<u32> {
        if primitives.is_empty()
            || primitives.len() > u32::MAX as usize
            || stops.len() > SCENE_MAX_GRADIENT_STOPS as usize
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        let mut mask = wire::LayerGeometryMask {
            struct_size: wire::LayerGeometryMask::WIRE_SIZE as u32,
            kind: SCENE_LAYER_MASK_GEOMETRY,
            flags: 0,
            primitive_offset: 0,
            primitive_count: primitives.len() as u32,
            gradient_stop_count: stops.len() as u32,
            reserved0: 0,
            reserved1: 0,
            reserved2: 0,
            ..source
        };
        mask.brush.stop_offset = 0;
        if !admission::geometry(&mask, primitives, stops) {
            return Err(B::InvalidArgument);
        }
        let mut auxiliary = encode(primitives)?;
        append_encoded(&mut auxiliary, stops)?;
        self.publish_layer_resource(SCENE_RESOURCE_LAYER_MASK, &mask, auxiliary, false)
    }
    pub fn add_picture_mask(
        &mut self,
        source: wire::LayerPictureMask,
        nested_scene: &[u8],
    ) -> BuildResult<u32> {
        if nested_scene.is_empty()
            || nested_scene.len() > u32::MAX as usize
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        let mask = wire::LayerPictureMask {
            struct_size: wire::LayerPictureMask::WIRE_SIZE as u32,
            kind: SCENE_LAYER_MASK_PICTURE,
            stream_offset: 0,
            stream_size: nested_scene.len() as u32,
            ..source
        };
        // Original producer checks the nested header here. Full nested scene
        // admission belongs to the published stream, not this recording step.
        if !admission::picture(&mask, nested_scene) {
            return Err(B::InvalidArgument);
        }
        self.publish_layer_resource(
            SCENE_RESOURCE_LAYER_MASK,
            &mask,
            copy_bytes(nested_scene)?,
            false,
        )
    }
    pub fn add_analytic_mask_chain(&mut self, masks: &[wire::LayerMask]) -> BuildResult<u32> {
        if !(2..=SCENE_MAX_ANALYTIC_MASKS as usize).contains(&masks.len())
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        let mut chain = wire::LayerMaskChain {
            struct_size: wire::LayerMaskChain::WIRE_SIZE as u32,
            kind: SCENE_LAYER_MASK_ANALYTIC_CHAIN,
            mask_count: masks.len() as u32,
            ..Default::default()
        };
        for (destination, &source) in chain.masks.iter_mut().zip(masks) {
            *destination = canonical_analytic(source);
            if !scalar::analytic(destination) {
                return Err(B::InvalidArgument);
            }
        }
        self.publish_layer_resource(SCENE_RESOURCE_LAYER_MASK, &chain, Vec::new(), false)
    }
    pub fn add_vector_clip_mask(
        &mut self,
        paths: &[wire::ClipPath],
        segments: &[wire::PathSegment],
        nodes: &[wire::PathBooleanNode],
        opacity: f32,
        source_geometry_clip: bool,
    ) -> BuildResult<u32> {
        if paths.len() > u32::MAX as usize
            || segments.len() > u32::MAX as usize
            || nodes.len() > u32::MAX as usize
            || self.resources.len() >= MAX_RESOURCES as usize
            || source_geometry_clip && opacity != 1.0
            || !admission::vector(paths, segments, nodes, opacity)
        {
            return Err(B::InvalidArgument);
        }
        let mask = wire::LayerVectorMask {
            struct_size: wire::LayerVectorMask::WIRE_SIZE as u32,
            kind: SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN,
            path_count: paths.len() as u32,
            segment_count: segments.len() as u32,
            boolean_node_count: nodes.len() as u32,
            opacity,
            ..Default::default()
        };
        let mut auxiliary = encode(paths)?;
        append_encoded(&mut auxiliary, segments)?;
        append_encoded(&mut auxiliary, nodes)?;
        self.publish_layer_resource(
            SCENE_RESOURCE_LAYER_MASK,
            &mask,
            auxiliary,
            source_geometry_clip,
        )
    }
    pub fn add_composite_mask(&mut self, source: NativeSceneCompositeMask<'_>) -> BuildResult<u32> {
        let input = admission::Composite {
            brushes: source.brush_masks,
            geometry_masks: source.geometry_masks,
            primitives: source.geometry_primitives,
            pictures: source.picture_masks,
            streams: source.picture_streams,
            paths: source.paths,
            segments: source.segments,
            nodes: source.boolean_nodes,
            stops: source.gradient_stops,
            opacity: source.opacity,
        };
        if [
            input.brushes.len(),
            input.geometry_masks.len(),
            input.primitives.len(),
            input.pictures.len(),
            input.streams.len(),
            input.paths.len(),
            input.segments.len(),
            input.nodes.len(),
            input.stops.len(),
        ]
        .into_iter()
        .any(|n| n > u32::MAX as usize)
            || self.resources.len() >= MAX_RESOURCES as usize
            || !input.valid()
        {
            return Err(B::InvalidArgument);
        }
        let size = input
            .byte_size()
            .filter(|&n| n <= u32::MAX as usize)
            .ok_or(B::CapacityExceeded)?;
        let mask = wire::LayerCompositeMask {
            struct_size: wire::LayerCompositeMask::WIRE_SIZE as u32,
            kind: SCENE_LAYER_MASK_COMPOSITE,
            component_count: (input.brushes.len()
                + input.geometry_masks.len()
                + input.pictures.len()
                + usize::from(!input.paths.is_empty())) as u32,
            brush_mask_count: input.brushes.len() as u32,
            path_count: input.paths.len() as u32,
            segment_count: input.segments.len() as u32,
            boolean_node_count: input.nodes.len() as u32,
            gradient_stop_count: input.stops.len() as u32,
            opacity: input.opacity,
            geometry_mask_count: input.geometry_masks.len() as u32,
            geometry_primitive_count: input.primitives.len() as u32,
            picture_mask_count: input.pictures.len() as u32,
            picture_stream_bytes: input.streams.len() as u32,
            ..Default::default()
        };
        let mut auxiliary = Vec::new();
        reserve_append(&mut auxiliary, size)?;
        append_encoded(&mut auxiliary, input.brushes)?;
        append_encoded(&mut auxiliary, input.geometry_masks)?;
        append_encoded(&mut auxiliary, input.primitives)?;
        append_encoded(&mut auxiliary, input.pictures)?;
        auxiliary.extend_from_slice(input.streams);
        append_encoded(&mut auxiliary, input.paths)?;
        append_encoded(&mut auxiliary, input.segments)?;
        append_encoded(&mut auxiliary, input.nodes)?;
        append_encoded(&mut auxiliary, input.stops)?;
        self.publish_layer_resource(SCENE_RESOURCE_LAYER_MASK, &mask, auxiliary, false)
    }
    pub fn add_effect_chain(
        &mut self,
        sources: &[wire::GroupEffect],
        revision: u32,
    ) -> BuildResult<u32> {
        if sources.is_empty()
            || sources.len() > MAX_GROUP_EFFECTS as usize
            || revision == 0
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        let mut effects = Vec::new();
        reserve_append(&mut effects, sources.len())?;
        for source in sources {
            let effect = wire::GroupEffect {
                struct_size: wire::GroupEffect::WIRE_SIZE as u32,
                flags: 0,
                reserved: 0,
                reserved2: 0,
                ..*source
            };
            if !values::effect(&effect) {
                return Err(B::InvalidArgument);
            }
            effects.push(effect);
        }
        let chain = wire::EffectChain {
            struct_size: wire::EffectChain::WIRE_SIZE as u32,
            effect_count: effects.len() as u32,
            revision,
            reserved: 0,
        };
        self.publish_layer_resource(
            SCENE_RESOURCE_EFFECT_CHAIN,
            &chain,
            encode(&effects)?,
            false,
        )
    }
    pub fn add_tile_composite(&mut self, tile: wire::TileComposite) -> BuildResult<u32> {
        if !values::tile_composite(&tile) || self.resources.len() >= MAX_RESOURCES as usize {
            return Err(B::InvalidArgument);
        }
        self.publish_layer_resource(SCENE_RESOURCE_TILE_COMPOSITE, &tile, Vec::new(), false)
    }
    fn publish_layer_resource<T: WireRecord>(
        &mut self,
        kind: u32,
        record: &T,
        auxiliary: Vec<u8>,
        source_geometry_clip: bool,
    ) -> BuildResult<u32> {
        reserve_append(&mut self.resources, 1)?;
        let mut resource = self.new_resource(kind);
        resource.payload = encode(std::slice::from_ref(record))?;
        resource.auxiliary = auxiliary;
        resource.source_geometry_clip = source_geometry_clip;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
}
fn canonical_analytic(source: wire::LayerMask) -> wire::LayerMask {
    wire::LayerMask {
        struct_size: wire::LayerMask::WIRE_SIZE as u32,
        kind: SCENE_LAYER_MASK_ROUNDED_RECTANGLE,
        flags: source.flags & SCENE_LAYER_MASK_AXIS_CLIP_AREA,
        reserved: 0,
        reserved0: 0,
        reserved1: 0,
        reserved2: 0,
        ..source
    }
}
