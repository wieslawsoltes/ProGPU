use super::*;
#[derive(Clone, Copy)]
struct ActiveLayer {
    occurrence: usize,
    push: usize,
    target: NativeLayerTargetId,
}
struct Bundle {
    first: u32,
    last: u32,
    target: NativeLayerTargetId,
    scissor: NativeScissor,
    depth: bool,
    mask_index: u32,
    mask: Option<Arc<NativeMaskRequest>>,
    draws: Vec<NativeReplayDraw>,
    pending: Option<NativeReplayDraw>,
}
impl Bundle {
    fn flush(&mut self) -> Result<(), Error> {
        if let Some(draw) = self.pending.take() {
            push(&mut self.draws, draw)?;
        }
        Ok(())
    }
    fn draw(&mut self, draw: NativeReplayDraw) -> Result<(), Error> {
        let merged = match (self.pending.as_mut(), draw) {
            (
                Some(NativeReplayDraw::Vector { draw: a, .. }),
                NativeReplayDraw::Vector { draw: b, .. },
            ) => a.merge(b),
            (
                Some(NativeReplayDraw::Path { draw: a, .. }),
                NativeReplayDraw::Path { draw: b, .. },
            ) => a.merge(b),
            (
                Some(NativeReplayDraw::Glyph { draw: a, .. }),
                NativeReplayDraw::Glyph { draw: b, .. },
            ) => a.merge(b),
            _ => false,
        };
        if merged {
            return Ok(());
        }
        self.flush()?;
        if matches!(
            draw,
            NativeReplayDraw::Image(_) | NativeReplayDraw::ThreeD(_)
        ) {
            push(&mut self.draws, draw)
        } else {
            self.pending = Some(draw);
            Ok(())
        }
    }
}
fn finish(
    bundle: &mut Option<Bundle>,
    operations: &mut Vec<NativeReplayOperation>,
    metrics: &mut NativeReplayMetrics,
) -> Result<(), Error> {
    let Some(mut b) = bundle.take() else {
        return Ok(());
    };
    b.flush()?;
    metrics.bundle_count += 1;
    metrics.bundle_draw_count += b.draws.len() as u64;
    metrics.draw_calls += b.draws.len() as u64;
    push(
        operations,
        NativeReplayOperation::Bundle(NativeReplayBundle {
            first_command: b.first,
            last_command: b.last,
            target: b.target,
            scissor: b.scissor,
            uses_depth: b.depth,
            mask: b.mask,
            draws: b.draws.into(),
        }),
    )
}
impl NativeReplayPlan {
    /// Compile the whole immutable source ordering against its actual assigned
    /// target allocation plan. No cache is updated and no GPU callback is made.
    pub fn compile(
        source: Arc<NativeSceneResourcePlan>,
        targets: Arc<NativeLayerTargetPlan>,
    ) -> Result<Self, Error> {
        let root = source.frame();
        let assigned = targets.frame();
        if root.scene_id != assigned.scene_id
            || root.generation != assigned.generation
            || root.width != assigned.width
            || root.height != assigned.height
            || root.dpi_scale.to_bits() != assigned.dpi_scale.to_bits()
        {
            return Err(Error::ResourceDomain);
        }
        let expected_p = root.resolve_presentation().ok_or(Error::InvalidInput)?;
        let assigned_p = assigned.resolve_presentation().ok_or(Error::InvalidInput)?;
        if [
            expected_p.viewport_x,
            expected_p.viewport_y,
            expected_p.viewport_width,
            expected_p.viewport_height,
            expected_p.dpi_scale_x.to_bits(),
            expected_p.dpi_scale_y.to_bits(),
        ] != [
            assigned_p.viewport_x,
            assigned_p.viewport_y,
            assigned_p.viewport_width,
            assigned_p.viewport_height,
            assigned_p.dpi_scale_x.to_bits(),
            assigned_p.dpi_scale_y.to_bits(),
        ] {
            return Err(Error::ResourceDomain);
        }
        let expected = source.layers().cache_budget();
        let actual = targets.cache_budget();
        if expected.count != actual.count
            || (0..expected.count as usize).any(|i| {
                expected.identities[i] != actual.identities[i]
                    || expected.revisions[i] != actual.revisions[i]
                    || expected.widths[i] != actual.widths[i]
                    || expected.heights[i] != actual.heights[i]
                    || expected.effected[i] != actual.effected[i]
                    || expected.shared[i] != actual.shared[i]
            })
        {
            return Err(Error::ResourceDomain);
        }
        let mut operations = Vec::new();
        let mut vertices = Vec::new();
        let mut metrics = NativeReplayMetrics::default();
        let mut bundle = None;
        let mut scopes = Vec::new();
        let mut layers: Vec<ActiveLayer> = Vec::new();
        let mut next_layer = 0usize;
        let mut transient = 0u32;
        let (mut vector, mut path, mut glyph, mut image, mut three_d, mut rgb) =
            (0usize, 0usize, 0usize, 0usize, 0usize, 0usize);
        let mut previous_family = 0u32;
        let mut prior_presentation = source.layers().presentation();
        let partial = source
            .budget()
            .partial_damage
            .then_some(source.budget().damage);
        for frame in source.draws().ordered_frames() {
            let command = frame.command();
            let kind = command.record.kind;
            let parent_presentation = prior_presentation;
            prior_presentation = frame.presentation;
            let current_target = layers
                .last()
                .map_or(NativeLayerTargetId::Root, |l| l.target);
            if kind == SCENE_COMMAND_PUSH_LAYER {
                let occurrence = source
                    .layers()
                    .layers()
                    .get(next_layer)
                    .ok_or(Error::InvalidInput)?;
                if occurrence.push_command != frame.command_index {
                    return Err(Error::InvalidInput);
                }
                let index = next_layer;
                next_layer += 1;
                let layer = occurrence.layer;
                if layer.flags & SCENE_LAYER_CACHE_TILE != 0
                    && source.options().engine_flags & ENGINE_IMAGE_REQUIRE_NATIVE_SAMPLING != 0
                {
                    return Err(Error::UnsupportedExecution);
                }
                if scopes.len() >= SCENE_MAX_STACK_DEPTH as usize {
                    return Err(Error::Capacity);
                }
                push(&mut scopes, occurrence.materialized)?;
                if occurrence.materialized {
                    finish(&mut bundle, &mut operations, &mut metrics)?;
                    let target = if occurrence.cached {
                        let cache = targets.cache_budget();
                        let slot = (0..cache.count as usize)
                            .find(|&i| cache.identities[i] == layer.composite_revision)
                            .ok_or(Error::ResourceDomain)?;
                        NativeLayerTargetId::Slot(cache.slots[slot])
                    } else {
                        let slot = NativeLayerTargetId::Slot(transient);
                        transient = transient.checked_add(1).ok_or(Error::Capacity)?;
                        slot
                    };
                    let request = allocation(&targets, target)?;
                    if !request.color
                        || request.size[0] < occurrence.target.width
                        || request.size[1] < occurrence.target.height
                        || request.cache_identity
                            != if occurrence.cached {
                                layer.composite_revision
                            } else {
                                0
                            }
                    {
                        return Err(Error::ResourceDomain);
                    }
                    let parent_extent = if let Some(active) = layers.last() {
                        source.layers().layers()[active.occurrence].target
                    } else {
                        NativeScissor::full([root.width, root.height])
                    };
                    let backdrop = layer.flags
                        & (SCENE_LAYER_BACKDROP | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND)
                        != 0;
                    let effect = if backdrop {
                        effect::compile(
                            &source,
                            layer.effect_resource_index,
                            target,
                            occurrence.target,
                            parent_extent,
                            parent_presentation,
                            SCENE_NO_INDEX,
                            &mut metrics,
                        )?
                    } else {
                        None
                    };
                    let ignore = layer.flags & SCENE_LAYER_IGNORE_ALPHA != 0;
                    let effect_count = effect.as_ref().map_or(0, NativeReplayEffect::effect_count);
                    let resolve = if backdrop && (effect_count != 0 || ignore) {
                        metrics.draw_calls += 1;
                        Some(append_quad(
                            &mut vertices,
                            native_layer_quad(
                                occurrence.target,
                                occurrence.target,
                                request.size,
                                root.dpi_scale,
                                1.0,
                            )?,
                        )?)
                    } else {
                        None
                    };
                    let push_index = operations.len();
                    push(
                        &mut layers,
                        ActiveLayer {
                            occurrence: index,
                            push: push_index,
                            target,
                        },
                    )?;
                    push(
                        &mut operations,
                        NativeReplayOperation::PushLayer(NativeReplayLayerPush {
                            occurrence: index as u32,
                            command_index: frame.command_index,
                            operation_id: command.record.command_id,
                            target,
                            parent: current_target,
                            source_extent: occurrence.target,
                            parent_extent,
                            ignore_alpha: ignore,
                            backdrop,
                            backdrop_origin: [
                                occurrence.target.x.wrapping_sub(parent_extent.x),
                                occurrence.target.y.wrapping_sub(parent_extent.y),
                            ],
                            first_backdrop_resolve_vertex: resolve,
                            cache_content: occurrence.cached,
                            cache_identity: layer.composite_revision,
                            cache_content_revision: presentation_content_hash(
                                layer.content_revision,
                                [root.width, root.height],
                                root.dpi_scale,
                                &source.layers().presentation(),
                            ),
                            effect_cache_operation_id: 0,
                            can_skip_content_on_effect_cache: false,
                            final_sample_shader: effect
                                .as_ref()
                                .is_some_and(NativeReplayEffect::final_sample),
                            effect_count,
                            effect,
                            pop_operation: u32::MAX,
                        }),
                    )?;
                }
                continue;
            }
            if kind == SCENE_COMMAND_POP_LAYER {
                let materialized = scopes.pop().ok_or(Error::InvalidInput)?;
                if materialized {
                    finish(&mut bundle, &mut operations, &mut metrics)?;
                    let active = layers.pop().ok_or(Error::InvalidInput)?;
                    let occurrence = source
                        .layers()
                        .layers()
                        .get(active.occurrence)
                        .ok_or(Error::InvalidInput)?;
                    if occurrence.pop_command != frame.command_index {
                        return Err(Error::InvalidInput);
                    }
                    let layer = occurrence.layer;
                    if !occurrence.cached {
                        transient = transient.checked_sub(1).ok_or(Error::InvalidInput)?;
                    }
                    let target = layers
                        .last()
                        .map_or(NativeLayerTargetId::Root, |l| l.target);
                    let mut composite = composite::prepare(
                        &source,
                        &targets,
                        occurrence,
                        frame,
                        active.target,
                        &mut vertices,
                    )?;
                    let backdrop = layer.flags & SCENE_LAYER_BACKDROP != 0;
                    let effect = if backdrop {
                        None
                    } else {
                        effect::compile(
                            &source,
                            layer.effect_resource_index,
                            active.target,
                            occurrence.target,
                            frame.target,
                            frame.presentation,
                            layer.mask_resource_index,
                            &mut metrics,
                        )?
                    };
                    if let Some(effect) = &effect {
                        composite.drawable &= effect.composite_drawable();
                    }
                    let pop_index = u32::try_from(operations.len()).map_err(|_| Error::Capacity)?;
                    let Some(NativeReplayOperation::PushLayer(push_op)) =
                        operations.get_mut(active.push)
                    else {
                        return Err(Error::InvalidInput);
                    };
                    push_op.pop_operation = pop_index;
                    if let Some(effect) = &effect {
                        push_op.effect_cache_operation_id = command.record.command_id;
                        push_op.effect_count = effect.effect_count();
                        push_op.can_skip_content_on_effect_cache = !effect.final_sample();
                        if effect.final_sample() {
                            push_op.final_sample_shader = true;
                            push_op.effect = Some(effect.clone());
                        }
                    }
                    if occurrence.cached {
                        push_op.can_skip_content_on_effect_cache = true;
                        push_op.effect_cache_operation_id = layer.composite_revision;
                    }
                    let mut ancestors = Vec::new();
                    ancestors
                        .try_reserve_exact(layers.len())
                        .map_err(|_| Error::Capacity)?;
                    for parent in &layers {
                        ancestors.push(source.layers().layers()[parent.occurrence].layer);
                    }
                    composite::finish(
                        &source,
                        &targets,
                        occurrence,
                        frame,
                        active.target,
                        target,
                        &ancestors,
                        &mut composite,
                        &mut vertices,
                        &mut metrics,
                    )?;
                    if composite.drawable {
                        metrics.draw_calls += if composite.advanced.is_some() { 3 } else { 1 };
                    }
                    push(
                        &mut operations,
                        NativeReplayOperation::PopLayer(NativeReplayLayerPop {
                            occurrence: active.occurrence as u32,
                            command_index: frame.command_index,
                            operation_id: command.record.command_id,
                            source: active.target,
                            target,
                            target_ignores_alpha: frame.ignores_alpha,
                            source_extent: occurrence.target,
                            parent_extent: frame.target,
                            composite,
                            backdrop,
                            cache_content: occurrence.cached,
                            cache_identity: layer.composite_revision,
                            cache_content_revision: presentation_content_hash(
                                layer.content_revision,
                                [root.width, root.height],
                                root.dpi_scale,
                                &source.layers().presentation(),
                            ),
                            effect,
                            push_operation: active.push as u32,
                        }),
                    )?;
                }
                continue;
            }
            if kind == SCENE_COMMAND_CLEAR_TARGET {
                finish(&mut bundle, &mut operations, &mut metrics)?;
                let draw = NativeTargetClearOperation::compile(
                    frame.clone(),
                    root,
                    allocation(&targets, current_target)?,
                    partial,
                )?;
                if draw.is_some() {
                    metrics.draw_calls += 1;
                    metrics.target_clear_count += 1;
                }
                push(
                    &mut operations,
                    NativeReplayOperation::Clear {
                        command_index: frame.command_index,
                        draw,
                    },
                )?;
                continue;
            }
            if !(SCENE_COMMAND_DRAW_ANALYTIC..=SCENE_COMMAND_DRAW_SOURCE_PATH).contains(&kind) {
                continue;
            }
            let mut scissor = resolve_target_scissor(
                &frame.source_state,
                frame.target,
                [root.width, root.height],
                frame.presentation,
            );
            if current_target == NativeLayerTargetId::Root {
                if let Some(damage) = partial {
                    scissor = scissor.intersection(damage);
                }
            }
            if kind == SCENE_COMMAND_DRAW_RGB_GLYPH_RUN {
                finish(&mut bundle, &mut operations, &mut metrics)?;
                let page = source.rgb().get(rgb).ok_or(Error::InvalidInput)?;
                let page_index = rgb as u32;
                rgb += 1;
                let draw = if scissor.drawable {
                    let page = page.as_ref().ok_or(Error::InvalidInput)?;
                    if current_target == NativeLayerTargetId::Root
                        || !frame.ignores_alpha
                        || page.frame().command_index() != frame.command_index
                        || page.scissor() != scissor
                    {
                        return Err(Error::ResourceDomain);
                    }
                    let mask_index = if frame.source_state.flags & SCENE_STATE_MASK != 0 {
                        frame.source_state.mask_resource_index
                    } else {
                        SCENE_NO_INDEX
                    };
                    let mask = mask_request(&source, mask_index, frame, frame.target, None)?;
                    metrics.mask_request_count += u32::from(mask.is_some());
                    metrics.rgb_draw_count += 1;
                    note(kind, &mut previous_family, &mut metrics);
                    Some(NativeReplayRgb {
                        command_index: frame.command_index,
                        page_index,
                        target: current_target,
                        allocation: allocation(&targets, current_target)?,
                        scissor,
                        mask,
                    })
                } else {
                    if page.is_some() {
                        return Err(Error::InvalidInput);
                    }
                    None
                };
                push(
                    &mut operations,
                    NativeReplayOperation::Rgb {
                        command_index: frame.command_index,
                        draw,
                    },
                )?;
                continue;
            }
            let mask_index = if frame.source_state.flags & SCENE_STATE_MASK != 0 {
                frame.source_state.mask_resource_index
            } else {
                SCENE_NO_INDEX
            };
            let depth = matches!(
                kind,
                SCENE_COMMAND_DRAW_LINE_3D_BATCH | SCENE_COMMAND_DRAW_MESH_3D_BATCH
            );
            if scissor.drawable
                && bundle.as_ref().is_none_or(|b: &Bundle| {
                    b.scissor != scissor
                        || b.target != current_target
                        || b.mask_index != mask_index
                        || b.depth != depth
                })
            {
                finish(&mut bundle, &mut operations, &mut metrics)?;
                let mask = mask_request(&source, mask_index, frame, frame.target, None)?;
                metrics.mask_request_count += u32::from(mask.is_some());
                bundle = Some(Bundle {
                    first: frame.command_index,
                    last: frame.command_index,
                    target: current_target,
                    scissor,
                    depth,
                    mask_index,
                    mask,
                    draws: Vec::new(),
                    pending: None,
                });
            }
            let draw = match kind {
                SCENE_COMMAND_DRAW_ANALYTIC
                | SCENE_COMMAND_DRAW_GEOMETRY
                | SCENE_COMMAND_DRAW_POINT_BATCH
                | SCENE_COMMAND_DRAW_VERTEX_MESH
                | SCENE_COMMAND_DRAW_SOURCE_COVERAGE
                | SCENE_COMMAND_DRAW_STROKE_BATCH => {
                    let d = source
                        .vector()
                        .and_then(|p| p.draws().get(vector))
                        .ok_or(Error::InvalidInput)?;
                    vector += 1;
                    if d.command_index != frame.command_index {
                        return Err(Error::InvalidInput);
                    }
                    NativeReplayDraw::Vector {
                        command_index: frame.command_index,
                        draw: d.draw,
                    }
                }
                SCENE_COMMAND_DRAW_PATH | SCENE_COMMAND_DRAW_SOURCE_PATH => {
                    let d = source
                        .paths()
                        .and_then(|p| p.draws().get(path))
                        .ok_or(Error::InvalidInput)?;
                    path += 1;
                    if d.command_index != frame.command_index {
                        return Err(Error::InvalidInput);
                    }
                    NativeReplayDraw::Path {
                        command_index: frame.command_index,
                        draw: d.draw,
                    }
                }
                SCENE_COMMAND_DRAW_GLYPH_RUN | SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN => {
                    let d = source
                        .glyphs()
                        .and_then(|p| p.draws().get(glyph))
                        .ok_or(Error::InvalidInput)?;
                    glyph += 1;
                    if d.command_index != frame.command_index {
                        return Err(Error::InvalidInput);
                    }
                    NativeReplayDraw::Glyph {
                        command_index: frame.command_index,
                        draw: d.draw,
                    }
                }
                SCENE_COMMAND_DRAW_IMAGE => {
                    let d = source
                        .images()
                        .and_then(|p| p.draws().get(image))
                        .ok_or(Error::InvalidInput)?;
                    if d.command_index != frame.command_index {
                        return Err(Error::InvalidInput);
                    }
                    let draw = NativeReplayDraw::Image(image as u32);
                    image += 1;
                    draw
                }
                SCENE_COMMAND_DRAW_LINE_3D_BATCH | SCENE_COMMAND_DRAW_MESH_3D_BATCH => {
                    let d = source
                        .three_d()
                        .and_then(|p| p.draws().get(three_d))
                        .ok_or(Error::InvalidInput)?;
                    if d.frame.command_index() != frame.command_index {
                        return Err(Error::InvalidInput);
                    }
                    let draw = NativeReplayDraw::ThreeD(three_d as u32);
                    three_d += 1;
                    draw
                }
                _ => return Err(Error::InvalidInput),
            };
            if scissor.drawable {
                note(
                    if matches!(draw, NativeReplayDraw::Vector { .. }) {
                        SCENE_COMMAND_DRAW_ANALYTIC
                    } else {
                        kind
                    },
                    &mut previous_family,
                    &mut metrics,
                );
                let b = bundle.as_mut().ok_or(Error::InvalidInput)?;
                b.last = frame.command_index;
                b.draw(draw)?;
            }
        }
        let root_copy_vertex = if source.budget().destination_sampling {
            metrics.draw_calls += 1;
            let extent = NativeScissor::full([root.width, root.height]);
            Some(append_quad(
                &mut vertices,
                native_layer_quad(
                    extent,
                    extent,
                    allocation(&targets, NativeLayerTargetId::Root)?.size,
                    root.dpi_scale,
                    1.0,
                )?,
            )?)
        } else {
            None
        };
        let counts = source.draws().metrics();
        let lm = source.layers().metrics();
        let expected_quads = u64::from(lm.materialized_layer_count)
            + u64::from(lm.advanced_layer_count) * 2
            + u64::from(source.budget().destination_sampling)
            + u64::from(lm.effected_backdrop_layer_count);
        if vector != counts.vector_draw_count as usize
            || path != counts.path_draw_count as usize
            || glyph != counts.glyph_draw_count as usize
            || image != counts.image_draw_count as usize
            || three_d != counts.three_d_draw_count as usize
            || rgb != counts.rgb_draw_count as usize
            || next_layer != source.layers().layers().len()
            || !scopes.is_empty()
            || !layers.is_empty()
            || transient != 0
            || vertices.len() as u64 != expected_quads * 4
            || metrics.advanced_count != lm.advanced_layer_count
            || metrics.builtin_effect_count != lm.effect_node_count - lm.shader_effect_count
            || metrics.builtin_effect_uniform_bytes != source.budget().effect_uniform_bytes
        {
            return Err(Error::InvalidInput);
        }
        finish(&mut bundle, &mut operations, &mut metrics)?;
        metrics.layer_quad_count = vertices.len() as u64 / 4;
        Ok(Self {
            source,
            targets,
            operations: operations.into(),
            vertices: vertices.into(),
            root_copy_vertex,
            metrics,
        })
    }
}
fn note(family: u32, previous: &mut u32, metrics: &mut NativeReplayMetrics) {
    if family != *previous {
        metrics.family_switches += 1;
        *previous = family;
    }
}
