//! The retained scene stream, written in Rust: the counterpart of the C++
//! `semantic_scene_builder` for the calls a render backend makes.
//!
//! The stream is a header, the command records, the resource records and an arena. In the arena
//! come the payload and the auxiliary data of each resource, in order, then the payload of each
//! command, every block aligned to 8 bytes. Recording keeps the blocks in two byte buffers and
//! `build` lays them out, so a frame that fits the capacity of the frames before it allocates
//! nothing.
//!
//! The records are canonicalized as the C++ builder does (sizes, kinds and reserved fields), so
//! both write the same bytes. The values of the records are not checked here: the engine validates
//! every stream it receives.

use progpu_native_sys::progpu::*;
use std::fmt;
use std::mem::size_of;
use std::ops::Range;

const NO_INDEX: u32 = PROGPU_NATIVE_SCENE_NO_INDEX as u32;
const REQUIRED: u32 = PROGPU_NATIVE_SCENE_RECORD_REQUIRED as u32;
const MAX_STACK: usize = PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH as usize;
const MAX_COMMANDS: usize = PROGPU_NATIVE_SCENE_MAX_COMMANDS as usize;
const MAX_RESOURCES: usize = PROGPU_NATIVE_SCENE_MAX_RESOURCES as usize;
const MAX_STREAM: u64 = PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES as u64;
const MAX_BRUSHES: usize = PROGPU_NATIVE_SCENE_MAX_BRUSHES as usize;
const MAX_TEXT_STYLES: usize = PROGPU_NATIVE_SCENE_MAX_TEXT_STYLES as usize;
const MAX_PATCHES: u32 = PROGPU_NATIVE_SCENE_MAX_IMAGE_PATCHES as u32;
const PATCH_BATCH: u32 = PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH as u32;
const PATCH_EXCLUDED: u32 = (PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX | PROGPU_NATIVE_SCENE_IMAGE_EFFECT) as u32;
const AXIS_CLIP: u32 = PROGPU_NATIVE_SCENE_LAYER_MASK_AXIS_CLIP_AREA as u32;

/// Why a call of the writer was refused. The writer is unchanged by a refused call.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SceneWriteError {
    /// An index that names nothing of its kind, or a call without the data it needs.
    InvalidArgument,
    /// More commands, resources, brushes, styles, depth or bytes than the format holds.
    CapacityExceeded,
    /// A restore or a pop without its save or push, or a stream built with an open scope.
    UnbalancedStack,
}

impl fmt::Display for SceneWriteError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            SceneWriteError::InvalidArgument => "an argument of a scene call is invalid",
            SceneWriteError::CapacityExceeded => "the scene exceeds a capacity of the stream format",
            SceneWriteError::UnbalancedStack => "the saves and layers of the scene are not balanced",
        })
    }
}

impl std::error::Error for SceneWriteError {}

/// The bytes of records of the C ABI.
///
/// # Safety
/// Implemented only for `repr(C)` records of plain numbers without padding bytes.
unsafe trait Record: Copy {}

macro_rules! records {
    ($($name:ty),* $(,)?) => {
        $(
            // SAFETY: a record of the stream format: `repr(C)`, numbers only, every byte named
            // by a field (the format spells its padding as reserved fields).
            unsafe impl Record for $name {}
        )*
    };
}

records!(
    u32,
    f64,
    progpu_native_color,
    progpu_native_point,
    progpu_native_analytic_primitive,
    progpu_native_path_segment,
    progpu_native_positioned_glyph,
    progpu_native_group_effect,
    progpu_native_scene_header,
    progpu_native_scene_command,
    progpu_native_scene_resource,
    progpu_native_scene_state,
    progpu_native_scene_brush,
    progpu_native_scene_gradient_stop,
    progpu_native_scene_text_style,
    progpu_native_scene_draw_brushes,
    progpu_native_scene_layer,
    progpu_native_scene_layer_mask,
    progpu_native_scene_layer_mask_chain,
    progpu_native_scene_layer_brush_mask,
    progpu_native_scene_layer_vector_mask,
    progpu_native_scene_clip_path,
    progpu_native_scene_effect_chain,
    progpu_native_scene_path_fill,
    progpu_native_scene_stroke,
    progpu_native_scene_image_draw,
    progpu_native_scene_image_sampling_options,
    progpu_native_scene_image_patch,
    progpu_native_scene_image_patch_batch,
    progpu_native_scene_glyph_outline,
    progpu_native_scene_glyph_key,
    progpu_native_scene_glyph_draw,
    progpu_native_scene_rgb_glyph_draw,
    progpu_native_scene_rgb_glyph_tile,
);

fn bytes_of<T: Record>(values: &[T]) -> &[u8] {
    // SAFETY: `Record` says every byte of `T` is initialized; `u8` has no alignment to keep.
    unsafe { std::slice::from_raw_parts(values.as_ptr() as *const u8, std::mem::size_of_val(values)) }
}

fn one<T: Record>(value: &T) -> &[u8] {
    bytes_of(std::slice::from_ref(value))
}

fn read<T: Record>(bytes: &[u8]) -> T {
    assert!(bytes.len() >= size_of::<T>());
    // SAFETY: the bytes are initialized and long enough, the read is unaligned, and `Record`
    // types take any bit pattern.
    unsafe { std::ptr::read_unaligned(bytes.as_ptr() as *const T) }
}

fn zeroed<T: Record>() -> T {
    // SAFETY: `Record` types are plain numbers, for which all-zero is a value.
    unsafe { std::mem::zeroed() }
}

const fn align8(value: u64) -> u64 {
    (value + 7) & !7
}

/// Where a table the writer keeps apart goes in the arena.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Table {
    None,
    Brushes,
    TextStyles,
}

/// How a resource can be drawn.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Content {
    Other,
    /// Pixels of the stream or an external image, with their layout.
    Image,
    /// Glyph outlines with their segments.
    GlyphOutlines(u32),
    /// Glyphs named by key.
    GlyphKeys(u32),
    /// A batch of paths.
    Paths(u32),
}

#[derive(Clone)]
struct Resource {
    record: progpu_native_scene_resource,
    payload: Range<usize>,
    auxiliary: Range<usize>,
    table: Table,
    content: Content,
}

#[derive(Clone)]
struct Command {
    record: progpu_native_scene_command,
    payload: Range<usize>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Scope {
    Save,
    Layer,
}

/// A writer of the retained scene stream. One writer is reused from frame to frame.
pub struct SceneWriter {
    scene_id: u64,
    generation: u64,
    resources: Vec<Resource>,
    commands: Vec<Command>,
    /// The payloads and the auxiliary data of the resources.
    resource_bytes: Vec<u8>,
    /// The payloads of the commands.
    command_bytes: Vec<u8>,
    brushes: Vec<progpu_native_scene_brush>,
    gradient_stops: Vec<progpu_native_scene_gradient_stop>,
    text_styles: Vec<progpu_native_scene_text_style>,
    brush_table: u32,
    text_style_table: u32,
    stack: Vec<Scope>,
    stream: Vec<u8>,
}

impl SceneWriter {
    /// A writer for `generation` of the scene `scene_id` (neither is zero).
    pub fn new(scene_id: u64, generation: u64) -> Result<Self, SceneWriteError> {
        if scene_id == 0 || generation == 0 {
            return Err(SceneWriteError::InvalidArgument);
        }
        Ok(SceneWriter {
            scene_id,
            generation,
            resources: Vec::new(),
            commands: Vec::new(),
            resource_bytes: Vec::new(),
            command_bytes: Vec::new(),
            brushes: Vec::new(),
            gradient_stops: Vec::new(),
            text_styles: Vec::new(),
            brush_table: NO_INDEX,
            text_style_table: NO_INDEX,
            stack: Vec::with_capacity(MAX_STACK),
            stream: Vec::new(),
        })
    }

    /// Starts `generation` of `scene_id` with nothing recorded, keeping the capacity.
    pub fn reset(&mut self, scene_id: u64, generation: u64) -> Result<(), SceneWriteError> {
        if scene_id == 0 || generation == 0 {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.scene_id = scene_id;
        self.generation = generation;
        self.resources.clear();
        self.commands.clear();
        self.resource_bytes.clear();
        self.command_bytes.clear();
        self.brushes.clear();
        self.gradient_stops.clear();
        self.text_styles.clear();
        self.brush_table = NO_INDEX;
        self.text_style_table = NO_INDEX;
        self.stack.clear();
        Ok(())
    }

    /// The identifier of the scene.
    pub fn scene_id(&self) -> u64 {
        self.scene_id
    }

    /// The generation being recorded.
    pub fn generation(&self) -> u64 {
        self.generation
    }

    fn resource_record(&self, kind: u32, flags: u32) -> progpu_native_scene_resource {
        let mut record: progpu_native_scene_resource = zeroed();
        record.struct_size = size_of::<progpu_native_scene_resource>() as u32;
        record.kind = kind;
        record.flags = REQUIRED | flags;
        record.resource_id = self.resources.len() as u64 + 1;
        record.generation = self.generation;
        record
    }

    fn command_record(&self, kind: u32, flags: u32, state: u32, resource: u32) -> progpu_native_scene_command {
        let mut record: progpu_native_scene_command = zeroed();
        record.struct_size = size_of::<progpu_native_scene_command>() as u32;
        record.kind = kind;
        record.flags = REQUIRED | flags;
        record.command_id = self.commands.len() as u64 + 1;
        record.state_index = state;
        record.resource_index = resource;
        record
    }

    fn room_for_resource(&self) -> Result<(), SceneWriteError> {
        if self.resources.len() >= MAX_RESOURCES {
            return Err(SceneWriteError::CapacityExceeded);
        }
        Ok(())
    }

    fn room_for_command(&self) -> Result<(), SceneWriteError> {
        if self.commands.len() >= MAX_COMMANDS {
            return Err(SceneWriteError::CapacityExceeded);
        }
        Ok(())
    }

    /// Adds a resource with the given blocks of bytes; returns its index.
    fn push_resource(
        &mut self,
        kind: u32,
        flags: u32,
        content: Content,
        payload: &[&[u8]],
        auxiliary: &[&[u8]],
    ) -> Result<u32, SceneWriteError> {
        self.room_for_resource()?;
        let record = self.resource_record(kind, flags);
        let start = self.resource_bytes.len();
        for block in payload {
            self.resource_bytes.extend_from_slice(block);
        }
        let middle = self.resource_bytes.len();
        for block in auxiliary {
            self.resource_bytes.extend_from_slice(block);
        }
        let end = self.resource_bytes.len();
        let index = self.resources.len() as u32;
        self.resources.push(Resource {
            record,
            payload: start..middle,
            auxiliary: middle..end,
            table: Table::None,
            content,
        });
        Ok(index)
    }

    fn push_table(&mut self, kind: u32, table: Table) -> Result<u32, SceneWriteError> {
        let index = self.push_resource(kind, 0, Content::Other, &[], &[])?;
        self.resources[index as usize].table = table;
        Ok(index)
    }

    fn push_command(&mut self, record: progpu_native_scene_command, payload: &[&[u8]]) -> Result<(), SceneWriteError> {
        self.room_for_command()?;
        let start = self.command_bytes.len();
        for block in payload {
            self.command_bytes.extend_from_slice(block);
        }
        self.commands.push(Command { record, payload: start..self.command_bytes.len() });
        Ok(())
    }

    fn valid_state(&self, index: u32) -> bool {
        index == NO_INDEX
            || self
                .resources
                .get(index as usize)
                .is_some_and(|resource| resource.record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE as u32)
    }

    fn valid_brushes(&self, brush_indices: &[u32]) -> bool {
        brush_indices.iter().all(|&index| (index as usize) < self.brushes.len())
    }

    /// The prefix of a draw that names its brushes.
    fn draw_brushes(&self, brush_indices: &[u32]) -> progpu_native_scene_draw_brushes {
        progpu_native_scene_draw_brushes {
            struct_size: size_of::<progpu_native_scene_draw_brushes>() as u32,
            brush_resource_index: self.brush_table,
            brush_count: brush_indices.len() as u32,
            reserved: 0,
        }
    }

    fn set_bounds(record: &mut progpu_native_scene_command, bounds: progpu_native_image_rect) {
        record.bounds_x = bounds.x;
        record.bounds_y = bounds.y;
        record.bounds_width = bounds.width;
        record.bounds_height = bounds.height;
    }

    /// A draw of `resource` with its optional brushes.
    fn push_brushed_draw(
        &mut self,
        kind: u32,
        resource: u32,
        brush_indices: &[u32],
        bounds: progpu_native_image_rect,
        state: u32,
    ) -> Result<(), SceneWriteError> {
        let mut record = self.command_record(kind, 0, state, resource);
        Self::set_bounds(&mut record, bounds);
        if brush_indices.is_empty() {
            self.push_command(record, &[])
        } else {
            let draw = self.draw_brushes(brush_indices);
            self.push_command(record, &[one(&draw), bytes_of(brush_indices)])
        }
    }

    /// Adds a solid brush, or finds the same one; returns its index in the brush table.
    pub fn add_solid_brush(&mut self, color: progpu_native_color, opacity: f32) -> Result<u32, SceneWriteError> {
        let same = |a: f32, b: f32| a.to_bits() == b.to_bits();
        let found = self.brushes.iter().position(|brush| {
            let existing = brush.colors[0];
            brush.type_ == PROGPU_NATIVE_SCENE_BRUSH_SOLID as u32
                && same(existing.r, color.r)
                && same(existing.g, color.g)
                && same(existing.b, color.b)
                && same(existing.a, color.a)
                && same(brush.opacity, opacity)
        });
        if let Some(index) = found {
            return Ok(index as u32);
        }
        if self.brushes.len() >= MAX_BRUSHES {
            return Err(SceneWriteError::CapacityExceeded);
        }
        self.ensure_brush_table()?;
        let mut brush: progpu_native_scene_brush = zeroed();
        brush.type_ = PROGPU_NATIVE_SCENE_BRUSH_SOLID as u32;
        brush.opacity = opacity;
        brush.colors[0] = color;
        brush.coordinate_transform0[0] = 1.0;
        brush.coordinate_transform1[1] = 1.0;
        self.brushes.push(brush);
        Ok(self.brushes.len() as u32 - 1)
    }

    fn ensure_brush_table(&mut self) -> Result<(), SceneWriteError> {
        if self.brush_table == NO_INDEX {
            self.brush_table = self.push_table(PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE as u32, Table::Brushes)?;
        }
        Ok(())
    }

    /// Adds a brush with its gradient stops; returns its index in the brush table.
    pub fn add_brush(
        &mut self,
        source: &progpu_native_scene_brush,
        stops: &[progpu_native_scene_gradient_stop],
    ) -> Result<u32, SceneWriteError> {
        if self.brushes.len() >= MAX_BRUSHES
            || stops.len() > (PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS as usize).saturating_sub(self.gradient_stops.len())
        {
            return Err(SceneWriteError::CapacityExceeded);
        }
        self.ensure_brush_table()?;
        let mut brush = *source;
        if !stops.is_empty() {
            brush.stop_offset = self.gradient_stops.len() as u32;
            self.gradient_stops.extend_from_slice(stops);
        } else if brush.type_ != PROGPU_NATIVE_SCENE_BRUSH_TILE_PATTERN as u32 {
            brush.stop_offset = 0;
        }
        self.brushes.push(brush);
        Ok(self.brushes.len() as u32 - 1)
    }

    /// Adds a transform, clip and opacity state; returns its resource.
    pub fn add_state(&mut self, source: &progpu_native_scene_state) -> Result<u32, SceneWriteError> {
        let mask = source.flags & PROGPU_NATIVE_SCENE_STATE_MASK as u32 != 0;
        let guideline = source.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET as u32 != 0;
        let kind_of = |index: u32| self.resources.get(index as usize).map(|resource| resource.record.kind);
        if (mask && kind_of(source.mask_resource_index) != Some(PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK as u32))
            || (guideline
                && kind_of(source.guideline_resource_index) != Some(PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET as u32))
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        let mut state = *source;
        state.struct_size = size_of::<progpu_native_scene_state>() as u32;
        state.reserved = 0;
        if !mask {
            state.mask_resource_index = 0;
        }
        if !guideline {
            state.guideline_resource_index = 0;
        }
        self.push_resource(PROGPU_NATIVE_SCENE_RESOURCE_STATE as u32, 0, Content::Other, &[one(&state)], &[])
    }

    /// Opens a scope with `state`.
    pub fn save(&mut self, state: u32) -> Result<(), SceneWriteError> {
        if !self.valid_state(state) {
            return Err(SceneWriteError::InvalidArgument);
        }
        if self.stack.len() >= MAX_STACK {
            return Err(SceneWriteError::CapacityExceeded);
        }
        let record = self.command_record(PROGPU_NATIVE_SCENE_COMMAND_SAVE as u32, 0, state, NO_INDEX);
        self.push_command(record, &[])?;
        self.stack.push(Scope::Save);
        Ok(())
    }

    /// Closes the scope of the last [`save`](Self::save).
    pub fn restore(&mut self) -> Result<(), SceneWriteError> {
        if self.stack.last() != Some(&Scope::Save) {
            return Err(SceneWriteError::UnbalancedStack);
        }
        let record = self.command_record(PROGPU_NATIVE_SCENE_COMMAND_RESTORE as u32, 0, NO_INDEX, NO_INDEX);
        self.push_command(record, &[])?;
        self.stack.pop();
        Ok(())
    }

    /// Replaces the target with `color`, through the clip in force.
    pub fn clear_target(&mut self, color: progpu_native_color) -> Result<(), SceneWriteError> {
        let record = self.command_record(PROGPU_NATIVE_SCENE_COMMAND_CLEAR_TARGET as u32, 0, NO_INDEX, NO_INDEX);
        self.push_command(record, &[one(&color)])
    }

    /// Opens a layer.
    pub fn push_layer(&mut self, source: &progpu_native_scene_layer) -> Result<(), SceneWriteError> {
        let valid = |index: u32, kinds: &[u32]| {
            index == NO_INDEX
                || self.resources.get(index as usize).is_some_and(|resource| kinds.contains(&resource.record.kind))
        };
        if !valid(source.mask_resource_index, &[PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK as u32])
            || !valid(
                source.effect_resource_index,
                &[
                    PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN as u32,
                    PROGPU_NATIVE_SCENE_RESOURCE_WPF_SHADER_EFFECT as u32,
                ],
            )
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        if self.stack.len() >= MAX_STACK {
            return Err(SceneWriteError::CapacityExceeded);
        }
        let mut layer = *source;
        layer.struct_size = size_of::<progpu_native_scene_layer>() as u32;
        let keeps_state = layer.flags
            & (PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE | PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) as u32
            != 0;
        if !keeps_state {
            layer.reserved0 = 0;
        }
        if layer.flags & PROGPU_NATIVE_SCENE_LAYER_CACHE_TILE as u32 == 0 {
            layer.reserved1 = 0;
        }
        let record = self.command_record(PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER as u32, 0, NO_INDEX, NO_INDEX);
        self.push_command(record, &[one(&layer)])?;
        self.stack.push(Scope::Layer);
        Ok(())
    }

    /// Closes the last [`push_layer`](Self::push_layer).
    pub fn pop_layer(&mut self) -> Result<(), SceneWriteError> {
        if self.stack.last() != Some(&Scope::Layer) {
            return Err(SceneWriteError::UnbalancedStack);
        }
        let record = self.command_record(PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER as u32, 0, NO_INDEX, NO_INDEX);
        self.push_command(record, &[])?;
        self.stack.pop();
        Ok(())
    }

    fn canonical_mask(source: &progpu_native_scene_layer_mask) -> progpu_native_scene_layer_mask {
        let mut mask = *source;
        mask.struct_size = size_of::<progpu_native_scene_layer_mask>() as u32;
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE as u32;
        mask.flags &= AXIS_CLIP;
        mask.reserved = 0;
        mask.reserved0 = 0;
        mask.reserved1 = 0;
        mask.reserved2 = 0;
        mask
    }

    /// Adds the mask of an axis-aligned clip area; returns its resource.
    pub fn add_axis_aligned_clip_mask(&mut self, bounds: progpu_native_image_rect) -> Result<u32, SceneWriteError> {
        let mut mask: progpu_native_scene_layer_mask = zeroed();
        mask.bounds = bounds;
        mask.transform = progpu_native_affine_2d { m11: 1.0, m12: 0.0, m21: 0.0, m22: 1.0, m31: 0.0, m32: 0.0 };
        mask.opacity = 1.0;
        mask.flags = AXIS_CLIP;
        self.add_rounded_rectangle_mask(&mask)
    }

    /// Adds a rounded rectangle mask; returns its resource.
    pub fn add_rounded_rectangle_mask(
        &mut self,
        source: &progpu_native_scene_layer_mask,
    ) -> Result<u32, SceneWriteError> {
        let mask = Self::canonical_mask(source);
        self.push_resource(PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK as u32, 0, Content::Other, &[one(&mask)], &[])
    }

    /// Adds a chain of two or more analytic masks; returns its resource.
    pub fn add_analytic_mask_chain(
        &mut self,
        masks: &[progpu_native_scene_layer_mask],
    ) -> Result<u32, SceneWriteError> {
        if masks.len() < 2 || masks.len() > PROGPU_NATIVE_SCENE_MAX_ANALYTIC_MASKS as usize {
            return Err(SceneWriteError::InvalidArgument);
        }
        let mut chain: progpu_native_scene_layer_mask_chain = zeroed();
        chain.struct_size = size_of::<progpu_native_scene_layer_mask_chain>() as u32;
        chain.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN as u32;
        chain.mask_count = masks.len() as u32;
        for (slot, mask) in chain.masks.iter_mut().zip(masks) {
            *slot = Self::canonical_mask(mask);
        }
        self.push_resource(PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK as u32, 0, Content::Other, &[one(&chain)], &[])
    }

    /// Adds a mask of a gradient brush; returns its resource.
    pub fn add_brush_mask(
        &mut self,
        source: &progpu_native_scene_layer_brush_mask,
        stops: &[progpu_native_scene_gradient_stop],
    ) -> Result<u32, SceneWriteError> {
        if stops.len() > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS as usize {
            return Err(SceneWriteError::InvalidArgument);
        }
        let mut mask = *source;
        mask.struct_size = size_of::<progpu_native_scene_layer_brush_mask>() as u32;
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH as u32;
        mask.flags = 0;
        mask.gradient_stop_count = stops.len() as u32;
        mask.reserved0 = 0;
        mask.brush.stop_offset = 0;
        self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK as u32,
            0,
            Content::Other,
            &[one(&mask)],
            &[bytes_of(stops)],
        )
    }

    /// Adds a mask of clip paths; returns its resource.
    pub fn add_vector_clip_mask(
        &mut self,
        paths: &[progpu_native_scene_clip_path],
        segments: &[progpu_native_path_segment],
        opacity: f32,
    ) -> Result<u32, SceneWriteError> {
        let mut mask: progpu_native_scene_layer_vector_mask = zeroed();
        mask.struct_size = size_of::<progpu_native_scene_layer_vector_mask>() as u32;
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN as u32;
        mask.path_count = paths.len() as u32;
        mask.segment_count = segments.len() as u32;
        mask.opacity = opacity;
        self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK as u32,
            0,
            Content::Other,
            &[one(&mask)],
            &[bytes_of(paths), bytes_of(segments)],
        )
    }

    /// Adds a chain of group effects; returns its resource.
    pub fn add_effect_chain(
        &mut self,
        effects: &[progpu_native_group_effect],
        revision: u32,
    ) -> Result<u32, SceneWriteError> {
        if effects.is_empty() || effects.len() > PROGPU_NATIVE_MAX_GROUP_EFFECTS as usize || revision == 0 {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.room_for_resource()?;
        let chain = progpu_native_scene_effect_chain {
            struct_size: size_of::<progpu_native_scene_effect_chain>() as u32,
            effect_count: effects.len() as u32,
            revision,
            reserved: 0,
        };
        let index = self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN as u32,
            0,
            Content::Other,
            &[one(&chain)],
            &[],
        )?;
        for source in effects {
            let mut effect = *source;
            effect.struct_size = size_of::<progpu_native_group_effect>() as u32;
            effect.flags = 0;
            effect.reserved = 0;
            effect.reserved2 = 0;
            self.resource_bytes.extend_from_slice(one(&effect));
        }
        let end = self.resource_bytes.len();
        self.resources[index as usize].auxiliary.end = end;
        Ok(index)
    }

    /// Draws analytic primitives.
    pub fn draw_analytic(
        &mut self,
        primitives: &[progpu_native_analytic_primitive],
        brush_indices: &[u32],
        bounds: progpu_native_image_rect,
        state: u32,
    ) -> Result<(), SceneWriteError> {
        if primitives.is_empty()
            || !self.valid_state(state)
            || (!brush_indices.is_empty() && brush_indices.len() != primitives.len())
            || !self.valid_brushes(brush_indices)
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.room_for_command()?;
        let resource = self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH as u32,
            0,
            Content::Other,
            &[bytes_of(primitives)],
            &[],
        )?;
        self.push_brushed_draw(PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC as u32, resource, brush_indices, bounds, state)
    }

    /// The resource of the path batch added last, when it is the last resource of the scene.
    pub fn last_path_resource(&self) -> Option<u32> {
        let last = self.resources.last()?;
        matches!(last.content, Content::Paths(_)).then(|| self.resources.len() as u32 - 1)
    }

    /// Draws a path batch of this scene again.
    pub fn draw_path_resource(
        &mut self,
        resource: u32,
        brush_indices: &[u32],
        bounds: progpu_native_image_rect,
        state: u32,
    ) -> Result<(), SceneWriteError> {
        let Some(Content::Paths(count)) = self.resources.get(resource as usize).map(|entry| entry.content) else {
            return Err(SceneWriteError::InvalidArgument);
        };
        if !self.valid_state(state)
            || (!brush_indices.is_empty() && brush_indices.len() != count as usize)
            || !self.valid_brushes(brush_indices)
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.push_brushed_draw(PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH as u32, resource, brush_indices, bounds, state)
    }

    /// Fills paths.
    pub fn draw_paths(
        &mut self,
        paths: &[progpu_native_scene_path_fill],
        segments: &[progpu_native_path_segment],
        brush_indices: &[u32],
        bounds: progpu_native_image_rect,
        state: u32,
    ) -> Result<(), SceneWriteError> {
        if !self.valid_state(state)
            || (!brush_indices.is_empty() && brush_indices.len() != paths.len())
            || !self.valid_brushes(brush_indices)
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.room_for_command()?;
        let resource = self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH as u32,
            0,
            Content::Paths(paths.len() as u32),
            &[bytes_of(paths)],
            &[bytes_of(segments)],
        )?;
        self.push_brushed_draw(PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH as u32, resource, brush_indices, bounds, state)
    }

    /// Draws strokes.
    #[allow(clippy::too_many_arguments)]
    pub fn draw_strokes(
        &mut self,
        strokes: &[progpu_native_scene_stroke],
        points: &[progpu_native_point],
        doubles: &[f64],
        brush_indices: &[u32],
        bounds: progpu_native_image_rect,
        state: u32,
    ) -> Result<(), SceneWriteError> {
        if strokes.is_empty()
            || points.is_empty()
            || !self.valid_state(state)
            || (!brush_indices.is_empty() && brush_indices.len() != strokes.len())
            || !self.valid_brushes(brush_indices)
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.room_for_command()?;
        let resource = self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH as u32,
            0,
            Content::Other,
            &[bytes_of(strokes)],
            &[bytes_of(points), bytes_of(doubles)],
        )?;
        self.push_brushed_draw(
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH as u32,
            resource,
            brush_indices,
            bounds,
            state,
        )
    }

    fn add_upload_image(
        &mut self,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
        flags: u32,
    ) -> Result<u32, SceneWriteError> {
        let minimum = u64::from(width) * 4;
        let required =
            if height == 0 { 0 } else { u64::from(row_bytes) * (u64::from(height) - 1) + minimum };
        if width == 0
            || height == 0
            || width > 16384
            || height > 16384
            || u64::from(row_bytes) < minimum
            || required != pixels.len() as u64
            || required > MAX_STREAM
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.push_resource(PROGPU_NATIVE_SCENE_RESOURCE_IMAGE as u32, flags, Content::Image, &[pixels], &[])
    }

    /// Adds an image of `RGBA8` pixels; returns its resource.
    pub fn add_rgba8_image(
        &mut self,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
    ) -> Result<u32, SceneWriteError> {
        self.add_upload_image(width, height, row_bytes, pixels, 0)
    }

    /// Adds an image of `BGRA8` pixels; returns its resource.
    pub fn add_bgra8_image(
        &mut self,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
    ) -> Result<u32, SceneWriteError> {
        self.add_upload_image(width, height, row_bytes, pixels, PROGPU_NATIVE_SCENE_IMAGE_BGRA8 as u32)
    }

    /// Adds an image the host binds as a texture; returns its resource, with its identifier and
    /// generation for the binding.
    pub fn add_external_image(&mut self, width: u32, height: u32) -> Result<(u32, u64, u64), SceneWriteError> {
        if width == 0 || height == 0 || width > 16384 || height > 16384 {
            return Err(SceneWriteError::InvalidArgument);
        }
        let index = self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_IMAGE as u32,
            PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE as u32,
            Content::Image,
            &[],
            &[],
        )?;
        let record = &self.resources[index as usize].record;
        Ok((index, record.resource_id, record.generation))
    }

    /// Draws an image. A draw that repeats the image, state and sampling of the draw before it
    /// joins it as a patch of one batch, as in the C++ builder.
    pub fn draw_image(
        &mut self,
        image_resource: u32,
        source: &progpu_native_scene_image_draw,
        bounds: progpu_native_image_rect,
        state: u32,
        sampling: Option<&progpu_native_scene_image_sampling_options>,
    ) -> Result<(), SceneWriteError> {
        let is_image = self
            .resources
            .get(image_resource as usize)
            .is_some_and(|resource| resource.content == Content::Image);
        let cubic = source.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC as u32;
        if !is_image
            || !self.valid_state(state)
            || source.flags & (PATCH_BATCH | PATCH_EXCLUDED) != 0
            || cubic != sampling.is_some()
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        let mut image = *source;
        image.struct_size = size_of::<progpu_native_scene_image_draw>() as u32;
        if self.try_merge_image(image_resource, &image, bounds, state, sampling) {
            return Ok(());
        }
        let mut record = self.command_record(PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE as u32, 0, state, image_resource);
        Self::set_bounds(&mut record, bounds);
        match sampling {
            Some(options) => self.push_command(record, &[one(&image), one(options)]),
            None => self.push_command(record, &[one(&image)]),
        }
    }

    fn try_merge_image(
        &mut self,
        image_resource: u32,
        image: &progpu_native_scene_image_draw,
        bounds: progpu_native_image_rect,
        state: u32,
        sampling: Option<&progpu_native_scene_image_sampling_options>,
    ) -> bool {
        let Some(previous) = self.commands.last() else { return false };
        if previous.record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE as u32
            || previous.record.resource_index != image_resource
            || previous.record.state_index != state
            || previous.payload.len() < size_of::<progpu_native_scene_image_draw>()
        {
            return false;
        }
        let range = previous.payload.clone();
        let mut previous_image: progpu_native_scene_image_draw = read(&self.command_bytes[range.clone()]);
        if !can_share_patch_batch(&previous_image, image) {
            return false;
        }
        let image_size = size_of::<progpu_native_scene_image_draw>();
        let sampling_size = size_of::<progpu_native_scene_image_sampling_options>();
        let cubic = image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC as u32;
        let mut previous_sampling = None;
        if cubic {
            if range.len() < image_size + sampling_size {
                return false;
            }
            let options: progpu_native_scene_image_sampling_options =
                read(&self.command_bytes[range.start + image_size..range.end]);
            previous_sampling = Some(options);
        }
        if !same_sampling(previous_sampling.as_ref(), sampling) {
            return false;
        }
        let batch_offset = image_size + if cubic { sampling_size } else { 0 };
        let batch_size = size_of::<progpu_native_scene_image_patch_batch>();
        let patch_size = size_of::<progpu_native_scene_image_patch>();
        let current = texture_patch(image);
        if previous_image.flags & PATCH_BATCH != 0 {
            if range.len() < batch_offset + batch_size {
                return false;
            }
            let mut batch: progpu_native_scene_image_patch_batch =
                read(&self.command_bytes[range.start + batch_offset..range.end]);
            if batch.patch_count == 0 || batch.patch_count >= MAX_PATCHES {
                return false;
            }
            if range.len() != batch_offset + batch_size + batch.patch_count as usize * patch_size {
                return false;
            }
            batch.patch_count += 1;
            let at = range.start + batch_offset;
            self.command_bytes[at..at + batch_size].copy_from_slice(one(&batch));
            self.command_bytes.extend_from_slice(one(&current));
        } else {
            let first = texture_patch(&previous_image);
            previous_image.flags |= PATCH_BATCH;
            let batch = progpu_native_scene_image_patch_batch {
                struct_size: batch_size as u32,
                flags: 0,
                patch_count: 2,
                reserved: 0,
            };
            // The last command's payload is the end of the buffer: it grows in place.
            self.command_bytes.truncate(range.start);
            self.command_bytes.extend_from_slice(one(&previous_image));
            if let Some(options) = &previous_sampling {
                self.command_bytes.extend_from_slice(one(options));
            }
            self.command_bytes.extend_from_slice(one(&batch));
            self.command_bytes.extend_from_slice(one(&first));
            self.command_bytes.extend_from_slice(one(&current));
        }
        let end = self.command_bytes.len();
        let command = self.commands.last_mut().expect("the command that was merged into");
        command.payload.end = end;
        let x = command.record.bounds_x.min(bounds.x);
        let y = command.record.bounds_y.min(bounds.y);
        let right = (command.record.bounds_x + command.record.bounds_width).max(bounds.x + bounds.width);
        let bottom = (command.record.bounds_y + command.record.bounds_height).max(bounds.y + bounds.height);
        Self::set_bounds(
            &mut command.record,
            progpu_native_image_rect { x, y, width: right - x, height: bottom - y },
        );
        true
    }

    /// Adds a text style, or finds the same one; returns its index in the style table.
    pub fn add_text_style(&mut self, source: &progpu_native_scene_text_style) -> Result<u32, SceneWriteError> {
        let mut style = *source;
        style.reserved1 = 0;
        style.reserved2 = 0;
        if let Some(index) = self.text_styles.iter().position(|existing| one(existing) == one(&style)) {
            return Ok(index as u32);
        }
        if self.text_styles.len() >= MAX_TEXT_STYLES {
            return Err(SceneWriteError::CapacityExceeded);
        }
        if self.text_style_table == NO_INDEX {
            self.text_style_table =
                self.push_table(PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE as u32, Table::TextStyles)?;
        }
        self.text_styles.push(style);
        Ok(self.text_styles.len() as u32 - 1)
    }

    /// Adds a glyph resource that carries its outlines and segments; returns its resource.
    pub fn add_glyph_outlines(
        &mut self,
        outlines: &[progpu_native_scene_glyph_outline],
        segments: &[progpu_native_path_segment],
    ) -> Result<u32, SceneWriteError> {
        if outlines.is_empty() || segments.is_empty() {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN as u32,
            0,
            Content::GlyphOutlines(outlines.len() as u32),
            &[bytes_of(outlines)],
            &[bytes_of(segments)],
        )
    }

    /// Adds a glyph resource that names outlines registered with the engine; returns its resource.
    pub fn add_glyph_keys(&mut self, keys: &[progpu_native_scene_glyph_key]) -> Result<u32, SceneWriteError> {
        if keys.is_empty() || keys.len() > 1 << 20 {
            return Err(SceneWriteError::InvalidArgument);
        }
        self.push_resource(
            PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN as u32,
            PROGPU_NATIVE_SCENE_GLYPH_OUTLINE_KEYS as u32,
            Content::GlyphKeys(keys.len() as u32),
            &[bytes_of(keys)],
            &[],
        )
    }

    /// Draws glyphs of a glyph resource, with a text style or `NO_INDEX`.
    pub fn draw_glyph_run(
        &mut self,
        glyph_resource: u32,
        glyphs: &[progpu_native_positioned_glyph],
        bounds: progpu_native_image_rect,
        state: u32,
        text_style: u32,
    ) -> Result<(), SceneWriteError> {
        let outlines = match self.resources.get(glyph_resource as usize).map(|resource| resource.content) {
            Some(Content::GlyphOutlines(count) | Content::GlyphKeys(count)) => count,
            _ => return Err(SceneWriteError::InvalidArgument),
        };
        let styled = text_style != NO_INDEX;
        if glyphs.is_empty()
            || !self.valid_state(state)
            || (styled && (text_style as usize) >= self.text_styles.len())
            || glyphs.iter().any(|glyph| glyph.outline_index >= outlines)
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        let flags = if styled { PROGPU_NATIVE_SCENE_GLYPH_STYLED as u32 } else { 0 };
        let mut record =
            self.command_record(PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN as u32, flags, state, glyph_resource);
        Self::set_bounds(&mut record, bounds);
        if styled {
            let draw = progpu_native_scene_glyph_draw {
                struct_size: size_of::<progpu_native_scene_glyph_draw>() as u32,
                style_resource_index: self.text_style_table,
                style_index: text_style,
                glyph_count: glyphs.len() as u32,
                reserved0: 0,
                reserved1: 0,
            };
            self.push_command(record, &[one(&draw), bytes_of(glyphs)])
        } else {
            self.push_command(record, &[bytes_of(glyphs)])
        }
    }

    /// Draws glyphs with a coverage per colour channel, from a resource of whole outlines.
    pub fn draw_rgb_glyph_run(
        &mut self,
        glyph_resource: u32,
        draw: &progpu_native_scene_rgb_glyph_draw,
        glyphs: &[progpu_native_scene_rgb_glyph_tile],
        bounds: progpu_native_image_rect,
        state: u32,
    ) -> Result<(), SceneWriteError> {
        let Some(Content::GlyphOutlines(outlines)) =
            self.resources.get(glyph_resource as usize).map(|resource| resource.content)
        else {
            return Err(SceneWriteError::InvalidArgument);
        };
        if glyphs.len() != draw.glyph_count as usize
            || !self.valid_state(state)
            || glyphs.iter().any(|glyph| glyph.outline_index >= outlines)
        {
            return Err(SceneWriteError::InvalidArgument);
        }
        let mut record =
            self.command_record(PROGPU_NATIVE_SCENE_COMMAND_DRAW_RGB_GLYPH_RUN as u32, 0, state, glyph_resource);
        Self::set_bounds(&mut record, bounds);
        self.push_command(record, &[one(draw), bytes_of(glyphs)])
    }

    /// Lays the stream out and returns it. The buffer is reused by the next build.
    pub fn build(&mut self) -> Result<&[u8], SceneWriteError> {
        if !self.stack.is_empty() {
            return Err(SceneWriteError::UnbalancedStack);
        }
        let command_offset = align8(size_of::<progpu_native_scene_header>() as u64);
        let resource_offset =
            align8(command_offset + self.commands.len() as u64 * size_of::<progpu_native_scene_command>() as u64);
        let arena_offset =
            align8(resource_offset + self.resources.len() as u64 * size_of::<progpu_native_scene_resource>() as u64);

        let mut total = arena_offset;
        let add = |cursor: &mut u64, size: usize| {
            if size != 0 {
                *cursor = align8(*cursor) + size as u64;
            }
        };
        for resource in &self.resources {
            let (payload, auxiliary) = self.sizes_of(resource);
            add(&mut total, payload);
            add(&mut total, auxiliary);
        }
        for command in &self.commands {
            add(&mut total, command.payload.len());
        }
        if total > MAX_STREAM || total > u64::from(u32::MAX) {
            return Err(SceneWriteError::CapacityExceeded);
        }

        let mut stream = std::mem::take(&mut self.stream);
        stream.clear();
        stream.resize(total as usize, 0);
        let mut cursor = arena_offset as usize;
        let append = |stream: &mut [u8], cursor: &mut usize, bytes: &[u8]| -> u32 {
            if bytes.is_empty() {
                return 0;
            }
            *cursor = (*cursor + 7) & !7;
            let at = *cursor;
            stream[at..at + bytes.len()].copy_from_slice(bytes);
            *cursor += bytes.len();
            at as u32
        };

        let resource_size = size_of::<progpu_native_scene_resource>();
        for (index, source) in self.resources.iter().enumerate() {
            let mut record = source.record;
            let (payload, auxiliary): (&[u8], &[u8]) = match source.table {
                Table::Brushes => (bytes_of(&self.brushes), bytes_of(&self.gradient_stops)),
                Table::TextStyles => (bytes_of(&self.text_styles), &[]),
                Table::None => {
                    (&self.resource_bytes[source.payload.clone()], &self.resource_bytes[source.auxiliary.clone()])
                }
            };
            record.payload_offset = append(&mut stream, &mut cursor, payload);
            record.auxiliary_offset = append(&mut stream, &mut cursor, auxiliary);
            record.payload_size = payload.len() as u32;
            record.auxiliary_size = auxiliary.len() as u32;
            let at = resource_offset as usize + index * resource_size;
            stream[at..at + resource_size].copy_from_slice(one(&record));
        }

        let command_size = size_of::<progpu_native_scene_command>();
        for (index, source) in self.commands.iter().enumerate() {
            let mut record = source.record;
            let payload = &self.command_bytes[source.payload.clone()];
            record.payload_offset = append(&mut stream, &mut cursor, payload);
            record.payload_size = payload.len() as u32;
            let at = command_offset as usize + index * command_size;
            stream[at..at + command_size].copy_from_slice(one(&record));
        }
        debug_assert_eq!(cursor as u64, total);

        let mut header: progpu_native_scene_header = zeroed();
        header.struct_size = size_of::<progpu_native_scene_header>() as u32;
        header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC as u32;
        header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION as u32;
        header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER as u32;
        header.total_size = total as u32;
        header.scene_id = self.scene_id;
        header.generation = self.generation;
        header.command_offset = command_offset as u32;
        header.command_count = self.commands.len() as u32;
        header.command_stride = command_size as u32;
        header.resource_offset = resource_offset as u32;
        header.resource_count = self.resources.len() as u32;
        header.resource_stride = resource_size as u32;
        header.arena_offset = arena_offset as u32;
        header.arena_size = (total - arena_offset) as u32;
        stream[..size_of::<progpu_native_scene_header>()].copy_from_slice(one(&header));
        self.stream = stream;
        Ok(&self.stream)
    }

    fn sizes_of(&self, resource: &Resource) -> (usize, usize) {
        match resource.table {
            Table::Brushes => {
                (std::mem::size_of_val(self.brushes.as_slice()), std::mem::size_of_val(self.gradient_stops.as_slice()))
            }
            Table::TextStyles => (std::mem::size_of_val(self.text_styles.as_slice()), 0),
            Table::None => (resource.payload.len(), resource.auxiliary.len()),
        }
    }
}

fn same_float(left: f32, right: f32) -> bool {
    left.to_bits() == right.to_bits()
}

fn can_share_patch_batch(left: &progpu_native_scene_image_draw, right: &progpu_native_scene_image_draw) -> bool {
    let same_transform = same_float(left.transform.m11, right.transform.m11)
        && same_float(left.transform.m12, right.transform.m12)
        && same_float(left.transform.m21, right.transform.m21)
        && same_float(left.transform.m22, right.transform.m22)
        && same_float(left.transform.m31, right.transform.m31)
        && same_float(left.transform.m32, right.transform.m32);
    left.flags & PATCH_EXCLUDED == 0
        && right.flags & (PATCH_EXCLUDED | PATCH_BATCH) == 0
        && left.image_width == right.image_width
        && left.image_height == right.image_height
        && left.row_bytes == right.row_bytes
        && left.sampling == right.sampling
        && left.max_anisotropy == right.max_anisotropy
        && left.flags & !PATCH_BATCH == right.flags
        && same_float(left.opacity, right.opacity)
        && same_transform
}

fn same_sampling(
    left: Option<&progpu_native_scene_image_sampling_options>,
    right: Option<&progpu_native_scene_image_sampling_options>,
) -> bool {
    match (left, right) {
        (None, None) => true,
        (Some(left), Some(right)) => {
            left.struct_size == right.struct_size
                && left.flags == right.flags
                && same_float(left.cubic_b, right.cubic_b)
                && same_float(left.cubic_c, right.cubic_c)
        }
        _ => false,
    }
}

fn texture_patch(image: &progpu_native_scene_image_draw) -> progpu_native_scene_image_patch {
    let mut patch: progpu_native_scene_image_patch = zeroed();
    patch.struct_size = size_of::<progpu_native_scene_image_patch>() as u32;
    patch.kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE as u32;
    patch.source_rect = image.source_rect;
    patch.destination_rect = image.destination_rect;
    patch.transform = progpu_native_affine_2d { m11: 1.0, m12: 0.0, m21: 0.0, m22: 1.0, m31: 0.0, m32: 0.0 };
    patch
}
