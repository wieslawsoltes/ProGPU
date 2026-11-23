//! Original one-pass family dependencies, effective states and active layers.
use super::*;

impl NativeScene {
    pub fn content_hashes(&self, resource_scope: u64) -> NativeSceneContentHashes {
        let mut brush_commands = FNV_OFFSET;
        let mut style_commands = FNV_OFFSET;
        let mut analytic_commands = FNV_OFFSET;
        let mut path_commands = FNV_OFFSET;
        let mut glyph_commands = FNV_OFFSET;
        let mut image_commands = FNV_OFFSET;
        let mut three_d_commands = FNV_OFFSET;
        let mut glyph_styles = false;
        let mut glyph_materials = false;
        let mut scopes = [wire::Command::default(); MAX_STACK_DEPTH as usize];
        let mut states = [0_u32; MAX_STACK_DEPTH as usize];
        let mut depth = 0;
        let mut current_state = NO_INDEX;
        for (index, command) in self.commands().enumerate() {
            let kind = command.record.kind;
            if matches!(
                kind,
                SCENE_COMMAND_SAVE
                    | SCENE_COMMAND_RESTORE
                    | SCENE_COMMAND_PUSH_LAYER
                    | SCENE_COMMAND_POP_LAYER
            ) {
                if kind == SCENE_COMMAND_SAVE || kind == SCENE_COMMAND_PUSH_LAYER {
                    if depth == MAX_STACK_DEPTH as usize {
                        return Default::default();
                    }
                    scopes[depth] = command.record;
                    states[depth] = current_state;
                    depth += 1;
                    if command.record.state_index != NO_INDEX {
                        current_state = command.record.state_index;
                    }
                } else {
                    if depth == 0 {
                        return Default::default();
                    }
                    depth -= 1;
                    current_state = states[depth];
                }
                continue;
            }
            let state = if command.record.state_index == NO_INDEX {
                current_state
            } else {
                command.record.state_index
            };
            if analytic_command(kind) {
                analytic_commands =
                    commands::active_layers(analytic_commands, self, &scopes[..depth]);
                analytic_commands = commands::command(analytic_commands, self, command, false);
                if kind == SCENE_COMMAND_DRAW_SOURCE_COVERAGE {
                    analytic_commands = append(
                        analytic_commands,
                        &command.payload
                            [command.payload.len() - wire::SourceCoverageFrame::WIRE_SIZE..],
                    );
                }
                analytic_commands = reference(analytic_commands, self, state);
                brush_commands =
                    commands::brush_mapping(brush_commands, self, index as u32, command, state);
            } else if matches!(
                kind,
                SCENE_COMMAND_DRAW_PATH | SCENE_COMMAND_DRAW_SOURCE_PATH
            ) {
                path_commands = commands::active_layers(path_commands, self, &scopes[..depth]);
                path_commands = commands::command(path_commands, self, command, false);
                if kind == SCENE_COMMAND_DRAW_SOURCE_PATH {
                    path_commands = append(
                        path_commands,
                        &command.payload
                            [command.payload.len() - wire::SourceCoverageFrame::WIRE_SIZE..],
                    );
                }
                path_commands = reference(path_commands, self, state);
                brush_commands =
                    commands::brush_mapping(brush_commands, self, index as u32, command, state);
            } else if matches!(
                kind,
                SCENE_COMMAND_DRAW_GLYPH_RUN
                    | SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN
                    | SCENE_COMMAND_DRAW_RGB_GLYPH_RUN
            ) {
                glyph_commands = commands::active_layers(glyph_commands, self, &scopes[..depth]);
                glyph_commands = commands::glyph(glyph_commands, self, command);
                glyph_commands = reference(glyph_commands, self, state);
                if kind == SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN {
                    brush_commands = record(brush_commands, index as u32);
                    brush_commands = commands::glyph(brush_commands, self, command);
                    brush_commands = reference(brush_commands, self, state);
                    let paint: wire::GlyphPaint =
                        read(&command.payload[wire::PaintedGlyphDraw::WIRE_SIZE..]);
                    glyph_materials |= paint.kind == SCENE_GLYPH_PAINT_MATERIAL;
                }
                if command.record.flags & GLYPH_STYLED != 0 {
                    style_commands =
                        commands::style_mapping(style_commands, self, index as u32, command, state);
                }
            } else if kind == SCENE_COMMAND_DRAW_IMAGE {
                image_commands = commands::active_layers(image_commands, self, &scopes[..depth]);
                image_commands = commands::command(image_commands, self, command, true);
                image_commands = reference(image_commands, self, state);
            } else if three_d_command(kind) {
                three_d_commands =
                    commands::active_layers(three_d_commands, self, &scopes[..depth]);
                three_d_commands = commands::three_d(three_d_commands, self, command);
                three_d_commands = reference(three_d_commands, self, state);
            }
            glyph_styles |=
                kind == SCENE_COMMAND_DRAW_GLYPH_RUN && command.record.flags & GLYPH_STYLED != 0;
        }
        let mut brushes = FNV_OFFSET;
        let mut styles = FNV_OFFSET;
        let mut analytics = FNV_OFFSET;
        let mut paths = FNV_OFFSET;
        let mut glyphs = FNV_OFFSET;
        let mut images = FNV_OFFSET;
        let mut three_d = FNV_OFFSET;
        let mut hit_tests = FNV_OFFSET;
        for view in self.envelope().resources() {
            let r = view.record;
            let selected = match r.kind {
                SCENE_RESOURCE_BRUSH_TABLE => &mut brushes,
                SCENE_RESOURCE_TEXT_STYLE_TABLE => &mut styles,
                kind if analytic_resource(kind) => &mut analytics,
                SCENE_RESOURCE_PATH_BATCH => &mut paths,
                SCENE_RESOURCE_GLYPH_RUN => &mut glyphs,
                SCENE_RESOURCE_IMAGE => &mut images,
                kind if three_d_resource(kind) => &mut three_d,
                SCENE_RESOURCE_HIT_TEST_INDEX => &mut hit_tests,
                _ => continue,
            };
            *selected = resource_identity(*selected, r);
        }
        let mut seed = record(FNV_OFFSET, self.envelope().header().scene_id);
        if resource_scope != 0 {
            seed = record(seed, resource_scope);
        }
        let brush = combine(seed ^ 0x01, brush_commands, brushes, 0);
        let text_style = combine(seed ^ 0x02, style_commands, styles, 0);
        let mut glyph = combine(seed ^ 0x05, glyph_commands, glyphs, 0);
        if glyph_styles {
            glyph = finish(record(glyph, text_style));
        }
        if glyph_materials {
            glyph = finish(record(glyph, brush));
        }
        NativeSceneContentHashes {
            brush,
            text_style,
            glyph,
            analytic: combine(seed ^ 0x03, analytic_commands, analytics, brush),
            path: combine(seed ^ 0x04, path_commands, paths, brush),
            image: combine(seed ^ 0x06, image_commands, images, 0),
            three_d: combine(seed ^ 0x07, three_d_commands, three_d, 0),
            hit_test: finish(record(record(seed ^ 0x08, FNV_OFFSET), hit_tests)),
        }
    }
}
fn combine(seed: u64, commands: u64, primary: u64, secondary: u64) -> u64 {
    let mut hash = record(record(seed, commands), primary);
    if secondary != 0 {
        hash = record(hash, secondary);
    }
    finish(hash)
}
