//! Original ordered save/restore and explicit source-input scope ownership.
use super::*;

impl NativeSceneBuilder {
    pub fn save(&mut self, state_index: u32, options: NativeSceneSaveOptions) -> BuildResult<()> {
        if (options.point_only_rectangle && options.local_hit_rectangle.is_none())
            || (options.empty_point_region && !options.point_only_rectangle)
            || (options.render_only
                && (options.input_only || options.local_hit_rectangle.is_some()))
        {
            return Err(B::InvalidArgument);
        }
        let record_hit = options.local_hit_rectangle.is_some()
            && self
                .hit_test_owners
                .last()
                .is_some_and(|boundary| boundary.owner.is_some());
        if (record_hit
            && !finite_rect(
                options
                    .local_hit_rectangle
                    .expect("selected source rectangle"),
            ))
            || !self.valid_state_index(state_index, true)
        {
            return Err(B::InvalidArgument);
        }
        if self.stack_depth >= MAX_STACK_DEPTH || self.commands.len() >= MAX_COMMANDS as usize {
            return Err(B::CapacityExceeded);
        }
        reserve_append(&mut self.commands, 1)?;
        let command = self.new_command(
            SCENE_COMMAND_SAVE,
            Default::default(),
            state_index,
            NO_INDEX,
        );
        if record_hit && self.hit_rectangle_scopes.len() == self.hit_rectangle_scopes.capacity() {
            let capacity = 16.max(self.hit_rectangle_scopes.len() * 2);
            reserve_total(&mut self.hit_rectangle_scopes, capacity)?;
        }
        if options.input_only {
            reserve_append(&mut self.input_only_ranges, 1)?;
        }
        if options.render_only {
            reserve_append(&mut self.render_only_ranges, 1)?;
        }
        let index = self.commands.len();
        let depth = self.stack_depth as usize;
        if record_hit {
            self.hit_rectangle_scopes.push(HitRectangleScope {
                first_command: index,
                last_command: index,
                local_bounds: options
                    .local_hit_rectangle
                    .expect("selected source rectangle"),
                point_only: options.point_only_rectangle,
                empty_point_region: options.empty_point_region,
            });
        }
        self.hit_rectangle_stack[depth] = if record_hit {
            self.hit_rectangle_scopes.len()
        } else {
            0
        };
        if options.input_only {
            self.input_only_ranges.push(InputOnlyRange {
                first_command: index,
                last_command: index,
            });
        }
        self.input_only_stack[depth] = if options.input_only {
            self.input_only_ranges.len()
        } else {
            0
        };
        if options.render_only {
            self.render_only_ranges.push(InputOnlyRange {
                first_command: index,
                last_command: index,
            });
        }
        self.render_only_stack[depth] = if options.render_only {
            self.render_only_ranges.len()
        } else {
            0
        };
        self.commands.push(command);
        self.stack_kinds[depth] = 1;
        self.stack_state_indices[depth] = state_index;
        self.stack_depth += 1;
        self.maximum_stack_depth = self.maximum_stack_depth.max(self.stack_depth);
        Ok(())
    }

    pub fn restore(&mut self) -> BuildResult<()> {
        if self.stack_depth == 0 || self.stack_kinds[self.stack_depth as usize - 1] != 1 {
            return Err(B::UnbalancedStack);
        }
        if self.commands.len() >= MAX_COMMANDS as usize {
            return Err(B::CapacityExceeded);
        }
        reserve_append(&mut self.commands, 1)?;
        let command = self.new_command(
            SCENE_COMMAND_RESTORE,
            Default::default(),
            NO_INDEX,
            NO_INDEX,
        );
        self.commands.push(command);
        self.stack_depth -= 1;
        let depth = self.stack_depth as usize;
        let last = self.commands.len() - 1;
        let hit = self.hit_rectangle_stack[depth];
        if hit != 0 {
            self.hit_rectangle_scopes[hit - 1].last_command = last;
        }
        self.hit_rectangle_stack[depth] = 0;
        let input = self.input_only_stack[depth];
        if input != 0 {
            self.input_only_ranges[input - 1].last_command = last;
        }
        self.input_only_stack[depth] = 0;
        let render = self.render_only_stack[depth];
        if render != 0 {
            self.render_only_ranges[render - 1].last_command = last;
        }
        self.render_only_stack[depth] = 0;
        self.stack_kinds[depth] = 0;
        Ok(())
    }

    /// Target storage replacement; source transforms and opacity do not move it.
    pub fn clear_target(&mut self, color: wire::Color) -> BuildResult<()> {
        if !finite_color(color) || color.a < 0.0 || color.a > 1.0 {
            return Err(B::InvalidArgument);
        }
        if self.commands.len() >= MAX_COMMANDS as usize {
            return Err(B::CapacityExceeded);
        }
        let mut command = self.new_command(
            SCENE_COMMAND_CLEAR_TARGET,
            Default::default(),
            NO_INDEX,
            NO_INDEX,
        );
        command.payload = encode(&[color])?;
        reserve_append(&mut self.commands, 1)?;
        self.commands.push(command);
        Ok(())
    }

    pub fn draw_analytic(
        &mut self,
        primitives: &[wire::AnalyticPrimitive],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        state_index: u32,
    ) -> BuildResult<()> {
        if primitives.is_empty()
            || !finite_rect(bounds)
            || !self.valid_state_index(state_index, false)
            || (!brush_indices.is_empty() && brush_indices.len() != primitives.len())
            || self.resources.len() >= MAX_RESOURCES as usize
            || self.commands.len() >= MAX_COMMANDS as usize
            || !primitives.iter().all(finite_primitive)
            || brush_indices
                .iter()
                .any(|&i| i as usize >= self.brushes.len())
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        reserve_append(&mut self.commands, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_ANALYTIC_BATCH);
        resource.payload = encode(primitives)?;
        let mut command = self.new_command(
            SCENE_COMMAND_DRAW_ANALYTIC,
            bounds,
            state_index,
            self.resources.len() as u32,
        );
        command.payload = self.brush_payload(brush_indices, false)?;
        self.resources.push(resource);
        self.commands.push(command);
        Ok(())
    }
}

fn finite_primitive(p: &wire::AnalyticPrimitive) -> bool {
    p.kind <= PRIMITIVE_ROUNDED_RECTANGLE
        && p.flags
            & !(PRIMITIVE_FLAG_EDGE_ALIASED
                | PRIMITIVE_FLAG_HAIRLINE
                | PRIMITIVE_FLAG_FIXED_DEVICE_STROKE
                | PRIMITIVE_START_CAP_MASK
                | PRIMITIVE_END_CAP_MASK)
            == 0
        && [
            p.x,
            p.y,
            p.width,
            p.height,
            p.corner_radius,
            p.stroke_thickness,
        ]
        .into_iter()
        .all(f32::is_finite)
        && p.width >= 0.0
        && p.height >= 0.0
        && p.corner_radius >= 0.0
        && p.stroke_thickness >= 0.0
        && finite_color(p.color)
        && finite_transform(p.transform)
}
