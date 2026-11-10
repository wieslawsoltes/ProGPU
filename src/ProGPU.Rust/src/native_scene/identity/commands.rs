//! Exact family-local command hashing and stable resource reference rewriting.
use super::*;
pub(super) fn command_record(hash: u64, scene: &NativeScene, command: wire::Command) -> u64 {
    let normalized = wire::Command {
        command_id: 0,
        state_index: 0,
        resource_index: 0,
        payload_offset: 0,
        ..command
    };
    reference(record(hash, normalized), scene, command.resource_index)
}
pub(super) fn command(
    hash: u64,
    scene: &NativeScene,
    command: CommandView<'_>,
    include_payload: bool,
) -> u64 {
    let hash = command_record(hash, scene, command.record);
    if include_payload {
        append(hash, command.payload)
    } else {
        hash
    }
}
pub(super) fn three_d(hash: u64, scene: &NativeScene, command: CommandView<'_>) -> u64 {
    let mut hash = command_record(hash, scene, command.record);
    let bytes = command.payload;
    let camera = wire::Camera3d::WIRE_SIZE;
    let size = wire::Mesh3dMaterials::WIRE_SIZE;
    if command.record.kind != SCENE_COMMAND_DRAW_MESH_3D_BATCH || bytes.len() < camera + size {
        return append(hash, bytes);
    }
    hash = append(hash, &bytes[..camera]);
    let mut materials: wire::Mesh3dMaterials = read(&bytes[camera..]);
    let brush = materials.brush_resource_index;
    materials.brush_resource_index = 0;
    hash = record(hash, materials);
    hash = reference(hash, scene, brush);
    append(hash, &bytes[camera + size..])
}
pub(super) fn scope(mut hash: u64, scene: &NativeScene, command: wire::Command) -> u64 {
    hash = command_record(hash, scene, command);
    let bytes = &scene.envelope().bytes()
        [command.payload_offset as usize..(command.payload_offset + command.payload_size) as usize];
    if command.kind != SCENE_COMMAND_PUSH_LAYER || bytes.len() < wire::Layer::WIRE_SIZE {
        return append(hash, bytes);
    }
    let mut layer: wire::Layer = read(bytes);
    let mask = layer.mask_resource_index;
    let effect = layer.effect_resource_index;
    let has_state =
        layer.flags & (SCENE_LAYER_CACHE_LOCAL_SPACE | SCENE_LAYER_COMPOSITE_STATE) != 0;
    let state = layer.reserved0;
    let tiled = layer.flags & SCENE_LAYER_CACHE_TILE != 0;
    let tile = layer.reserved1;
    if tiled {
        layer.reserved1 = 0;
    }
    layer.mask_resource_index = 0;
    layer.effect_resource_index = 0;
    if has_state {
        layer.reserved0 = 0;
    }
    hash = record(hash, layer);
    hash = reference(hash, scene, mask);
    hash = reference(hash, scene, effect);
    if tiled {
        hash = reference(hash, scene, tile);
    }
    if has_state {
        reference(hash, scene, state)
    } else {
        hash
    }
}
pub(super) fn active_layers(mut hash: u64, scene: &NativeScene, scopes: &[wire::Command]) -> u64 {
    for &command in scopes {
        if command.kind == SCENE_COMMAND_PUSH_LAYER {
            hash = scope(hash, scene, command);
        }
    }
    hash
}
pub(super) fn brush_mapping(
    mut hash: u64,
    scene: &NativeScene,
    index: u32,
    command: CommandView<'_>,
    state: u32,
) -> u64 {
    hash = record(hash, index);
    hash = record(hash, command.record.kind);
    hash = record(hash, command.record.flags);
    hash = reference(hash, scene, state);
    if command.payload.len() < wire::DrawBrushes::WIRE_SIZE {
        return hash;
    }
    let mut draw: wire::DrawBrushes = read(command.payload);
    let brush = draw.brush_resource_index;
    draw.brush_resource_index = 0;
    hash = record(hash, draw);
    hash = reference(hash, scene, brush);
    let tail = &command.payload[wire::DrawBrushes::WIRE_SIZE..];
    if draw.brush_count as usize <= tail.len() / 4 {
        hash = append(hash, &tail[..draw.brush_count as usize * 4]);
    }
    hash
}
pub(super) fn style_mapping(
    mut hash: u64,
    scene: &NativeScene,
    index: u32,
    command: CommandView<'_>,
    state: u32,
) -> u64 {
    hash = reference(record(hash, index), scene, state);
    if command.payload.len() < wire::GlyphDraw::WIRE_SIZE {
        return hash;
    }
    let draw: wire::GlyphDraw = read(command.payload);
    reference(
        record(hash, draw.style_index),
        scene,
        draw.style_resource_index,
    )
}
pub(super) fn glyph(hash: u64, scene: &NativeScene, command: CommandView<'_>) -> u64 {
    let mut hash = command_record(hash, scene, command.record);
    let bytes = command.payload;
    if command.record.kind == SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN
        && bytes.len() >= wire::PaintedGlyphDraw::WIRE_SIZE
    {
        let mut draw: wire::PaintedGlyphDraw = read(bytes);
        let paint = draw.paint_resource_index;
        draw.paint_resource_index = 0;
        hash = reference(record(hash, draw), scene, paint);
        return append(hash, &bytes[wire::PaintedGlyphDraw::WIRE_SIZE..]);
    }
    if command.record.flags & GLYPH_STYLED == 0 || bytes.len() < wire::GlyphDraw::WIRE_SIZE {
        return append(hash, bytes);
    }
    let mut draw: wire::GlyphDraw = read(bytes);
    let style = draw.style_resource_index;
    draw.style_resource_index = 0;
    hash = reference(record(hash, draw), scene, style);
    append(hash, &bytes[wire::GlyphDraw::WIRE_SIZE..])
}
