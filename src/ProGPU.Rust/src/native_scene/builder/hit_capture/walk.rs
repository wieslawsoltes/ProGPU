//! Original command/scope ordering and explicit retained source-input ownership.
use super::*;
#[derive(Clone, Copy, Default)]
struct Frame {
    state: u32,
    layer: bool,
    clip: u32,
    input: u32,
    query: u32,
}
impl Capture<'_> {
    pub(super) fn walk(&mut self) -> BuildResult<()> {
        let builder = self.builder;
        let mut stack = [Frame::default(); MAX_STACK_DEPTH as usize];
        let (
            mut depth,
            mut boundary,
            mut glyph_bounds_index,
            mut rectangle_scope_index,
            mut render_scope_index,
            mut source_layer_index,
        ) = (0usize, 0usize, 0usize, 0usize, 0usize, 0usize);
        let mut current_state = NO_INDEX;
        let mut i = 0usize;
        while i < builder.commands.len() {
            let command_index = i;
            i += 1;
            while boundary < builder.hit_test_owners.len()
                && builder.hit_test_owners[boundary].first_command <= command_index
            {
                self.owner = builder.hit_test_owners[boundary].owner;
                boundary += 1;
            }
            let command = &builder.commands[command_index];
            let kind = command.record.kind;
            if kind == SCENE_COMMAND_SAVE {
                while render_scope_index < builder.render_only_ranges.len()
                    && builder.render_only_ranges[render_scope_index].first_command < command_index
                {
                    render_scope_index += 1;
                }
                if let Some(scope) = builder
                    .render_only_ranges
                    .get(render_scope_index)
                    .filter(|s| s.first_command == command_index)
                {
                    render_scope_index += 1;
                    if scope.last_command <= command_index
                        || scope.last_command >= builder.commands.len()
                        || builder.commands[scope.last_command].record.kind != SCENE_COMMAND_RESTORE
                    {
                        return Err(B::UnsupportedHitTest);
                    }
                    i = scope.last_command + 1;
                    continue;
                }
                if depth == stack.len() {
                    return Err(B::UnsupportedHitTest);
                }
                let saved = Frame {
                    state: current_state,
                    layer: false,
                    clip: self.layer_clip_scope,
                    input: self.input_frame,
                    query: self.query,
                };
                while rectangle_scope_index < builder.hit_rectangle_scopes.len()
                    && builder.hit_rectangle_scopes[rectangle_scope_index].first_command
                        < command_index
                {
                    rectangle_scope_index += 1;
                }
                if let Some(scope) = builder
                    .hit_rectangle_scopes
                    .get(rectangle_scope_index)
                    .filter(|s| s.first_command == command_index)
                {
                    rectangle_scope_index += 1;
                    if self.owner.is_none()
                        || scope.last_command <= command_index
                        || scope.last_command >= builder.commands.len()
                        || builder.commands[scope.last_command].record.kind != SCENE_COMMAND_RESTORE
                    {
                        return Err(B::UnsupportedHitTest);
                    }
                    let state_index = if command.record.state_index == NO_INDEX {
                        current_state
                    } else {
                        command.record.state_index
                    };
                    let state = self.state(state_index)?;
                    if scope.point_only {
                        if !scope.empty_point_region && self.query != HIT_TEST_REGION_ONLY {
                            self.query = HIT_TEST_POINT_ONLY;
                            self.rectangle(
                                scope.local_bounds,
                                state.transform,
                                &state,
                                state_index,
                            )?;
                        }
                        self.query = HIT_TEST_REGION_ONLY;
                    } else {
                        if self.source_geometry || state.opacity > 0.0001 {
                            self.rectangle(
                                scope.local_bounds,
                                state.transform,
                                &state,
                                state_index,
                            )?;
                        }
                        i = scope.last_command + 1;
                        continue;
                    }
                }
                stack[depth] = saved;
                depth += 1;
                if command.record.state_index != NO_INDEX {
                    current_state = command.record.state_index;
                }
                continue;
            }
            if kind == SCENE_COMMAND_RESTORE || kind == SCENE_COMMAND_POP_LAYER {
                let layer = kind == SCENE_COMMAND_POP_LAYER;
                if depth == 0 || stack[depth - 1].layer != layer {
                    return Err(B::UnsupportedHitTest);
                }
                depth -= 1;
                let frame = stack[depth];
                current_state = frame.state;
                self.layer_clip_scope = frame.clip;
                self.input_frame = frame.input;
                self.query = frame.query;
                continue;
            }
            if kind == SCENE_COMMAND_PUSH_LAYER {
                while source_layer_index < builder.source_geometry_hit_layers.len()
                    && builder.source_geometry_hit_layers[source_layer_index].command_index
                        < command_index
                {
                    source_layer_index += 1;
                }
                let input_layer = builder
                    .source_geometry_hit_layers
                    .get(source_layer_index)
                    .filter(|s| s.command_index == command_index)
                    .copied()
                    .ok_or(B::UnsupportedHitTest)?;
                if depth == stack.len() {
                    return Err(B::UnsupportedHitTest);
                }
                source_layer_index += 1;
                stack[depth] = Frame {
                    state: current_state,
                    layer: true,
                    clip: self.layer_clip_scope,
                    input: self.input_frame,
                    query: self.query,
                };
                let layer: wire::Layer = record(&command.payload, 0)?;
                if layer.flags & SCENE_LAYER_COMPOSITE_STATE != 0 || input_layer.changes_frame {
                    let composite: wire::State = record(
                        &builder
                            .resources
                            .get(layer.reserved0 as usize)
                            .ok_or(B::UnsupportedHitTest)?
                            .payload,
                        0,
                    )?;
                    if composite.flags & SCENE_STATE_CLIP_RECT != 0 {
                        let mut clip = self.map_rectangle(composite.clip_rect)?;
                        if self.layer_clip_scope != 0 {
                            clip = math::intersect(
                                clip,
                                self.layer_clips[self.layer_clip_scope as usize],
                            );
                        }
                        self.push_layer_clip(clip)?;
                    }
                }
                if input_layer.geometry_clip != NO_INDEX {
                    let geometry = self.vector_clip(input_layer.geometry_clip)?;
                    if !geometry.rectangle {
                        return Err(B::UnsupportedHitTest);
                    }
                    let mut clip = wire::ImageRect {
                        x: geometry.min.x,
                        y: geometry.min.y,
                        width: geometry.max.x - geometry.min.x,
                        height: geometry.max.y - geometry.min.y,
                    };
                    if self.layer_clip_scope != 0 {
                        clip =
                            math::intersect(clip, self.layer_clips[self.layer_clip_scope as usize]);
                    }
                    self.push_layer_clip(clip)?;
                }
                if input_layer.changes_frame {
                    if self.input_frames.is_empty() {
                        push(
                            &mut self.input_frames,
                            NativeSceneBuilder::identity_transform(),
                        )?;
                    }
                    let transform = math::input_frame(
                        input_layer.content_to_parent,
                        self.input_frames[self.input_frame as usize],
                    )
                    .ok_or(B::UnsupportedHitTest)?;
                    self.input_frame =
                        u32::try_from(self.input_frames.len()).map_err(|_| B::InvalidState)?;
                    push(&mut self.input_frames, transform)?;
                }
                depth += 1;
                continue;
            }
            if kind == SCENE_COMMAND_CLEAR_TARGET || self.owner.is_none() {
                continue;
            }
            let state_index = if command.record.state_index == NO_INDEX {
                current_state
            } else {
                command.record.state_index
            };
            let state = self.state(state_index)?;
            if !self.source_geometry && state.opacity <= 0.0001 {
                continue;
            }
            if kind == SCENE_COMMAND_DRAW_GLYPH_RUN {
                while glyph_bounds_index < builder.glyph_hit_bounds.len()
                    && builder.glyph_hit_bounds[glyph_bounds_index].command_index < command_index
                {
                    glyph_bounds_index += 1;
                }
                let bounds = builder
                    .glyph_hit_bounds
                    .get(glyph_bounds_index)
                    .filter(|b| b.command_index == command_index)
                    .ok_or(B::UnsupportedHitTest)?;
                self.rectangle(bounds.local_bounds, state.transform, &state, state_index)?;
                continue;
            }
            let resource = builder
                .resources
                .get(command.record.resource_index as usize)
                .ok_or(B::UnsupportedHitTest)?;
            match kind {
                SCENE_COMMAND_DRAW_IMAGE => self.image(command, resource, &state, state_index)?,
                SCENE_COMMAND_DRAW_GEOMETRY => self.geometry(resource, &state, state_index)?,
                SCENE_COMMAND_DRAW_STROKE_BATCH => self.strokes(resource, &state, state_index)?,
                _ => self.ordinary(kind, resource, &state, state_index)?,
            }
        }
        Ok(())
    }
    fn push_layer_clip(&mut self, clip: wire::ImageRect) -> BuildResult<()> {
        if !(clip.x + clip.width).is_finite() || !(clip.y + clip.height).is_finite() {
            return Err(B::UnsupportedHitTest);
        }
        self.layer_clip_scope =
            u32::try_from(self.layer_clips.len()).map_err(|_| B::InvalidState)?;
        push(&mut self.layer_clips, clip)
    }
}
