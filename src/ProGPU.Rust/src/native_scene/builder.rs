//! Owned typed producers for the original native scene stream.
//!
//! Recording retains source input metadata separately from serialized raster
//! commands. Original provider/engine/target admission remains independent.
//! Related record publication follows each original producer's allocation
//! preflight; retained capacities may grow after a failed operation. Multi-step
//! 3D material appends retain the native producer's separate failure contract.
use super::{wire, MAX_COMMANDS, MAX_RESOURCES, MAX_STACK_DEPTH, MAX_STREAM_BYTES, NO_INDEX};
use wire::{constants::*, WireRecord};
use NativeSceneBuildError as B;

mod brush;
mod geometry;
mod glyph;
mod hit;
mod hit_capture;
mod image;
mod layer;
mod path;
mod text;
mod three_d;
pub use image::{NativeSceneFullImageCopy, NativeSceneImageOptions};
pub use layer::NativeSceneCompositeMask;
pub use text::NativeShapedTextSceneOptions;
mod scopes;
mod serialization;
mod state;

pub type BuildResult<T> = Result<T, NativeSceneBuildError>;

/// Original builder failure categories, returned directly instead of a mutable
/// C++ last-error slot. A successful Rust result replaces the original `none`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[repr(u32)]
pub enum NativeSceneBuildError {
    #[error("invalid native scene builder argument")]
    InvalidArgument = 1,
    #[error("invalid native scene builder state")]
    InvalidState = 2,
    #[error("unbalanced native scene builder stack")]
    UnbalancedStack = 3,
    #[error("native scene builder capacity exceeded")]
    CapacityExceeded = 4,
    #[error("native scene builder allocation failed")]
    OutOfMemory = 5,
    #[error("native scene input coverage is unsupported")]
    UnsupportedHitTest = 6,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeSceneLayerHitTestMode {
    #[default]
    Unspecified = 0,
    SourceOpacity = 1,
    SourceIdentityEffect = 2,
    SourceLocalCache = 3,
    SourceOpacityMask = 4,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeSceneHitTestOpacityMode {
    #[default]
    RenderedVisibility = 0,
    SourceGeometry = 1,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSceneBuildMetrics {
    pub command_count: u32,
    pub resource_count: u32,
    pub brush_count: u32,
    pub text_style_count: u32,
    pub maximum_stack_depth: u32,
    pub arena_bytes: u64,
    pub stream_bytes: u64,
}

/// Explicit source scope options. A hit rectangle is retained only while an
/// actual hit owner is selected; source input never uses culling bounds.
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeSceneSaveOptions {
    pub local_hit_rectangle: Option<wire::ImageRect>,
    pub point_only_rectangle: bool,
    pub input_only: bool,
    pub empty_point_region: bool,
    pub render_only: bool,
}

#[derive(Debug, Default)]
struct ResourceEntry {
    record: wire::Resource,
    payload: Vec<u8>,
    auxiliary: Vec<u8>,
    brush_table: bool,
    text_style_table: bool,
    rgba8_image: bool,
    bgra8_image: bool,
    r8_image: bool,
    picture_image: bool,
    source_geometry_clip: bool,
    image_width: u32,
    image_height: u32,
    image_row_bytes: u32,
    glyph_outline_count: u32,
}

#[derive(Debug, Default)]
struct CommandEntry {
    record: wire::Command,
    payload: Vec<u8>,
}

#[derive(Clone, Copy, Debug)]
struct HitTestOwnerBoundary {
    first_command: usize,
    owner: Option<i32>,
}
#[derive(Clone, Copy, Debug)]
struct GlyphHitBoundsEntry {
    command_index: usize,
    local_bounds: wire::ImageRect,
}
#[derive(Clone, Copy, Debug)]
struct HitRectangleScope {
    first_command: usize,
    last_command: usize,
    local_bounds: wire::ImageRect,
    point_only: bool,
    empty_point_region: bool,
}
#[derive(Clone, Copy, Debug)]
struct SourceHitLayer {
    command_index: usize,
    changes_frame: bool,
    content_to_parent: wire::Affine2d,
    geometry_clip: u32,
}
#[derive(Clone, Copy, Debug)]
struct InputOnlyRange {
    first_command: usize,
    last_command: usize,
}

/// Mutable, exclusively borrowed recorder. `build` produces an independent
/// owned generation; reset reuses allocations without changing prior outputs.
#[derive(Debug)]
pub struct NativeSceneBuilder {
    scene_id: u64,
    generation: u64,
    resources: Vec<ResourceEntry>,
    commands: Vec<CommandEntry>,
    brushes: Vec<wire::Brush>,
    gradient_stops: Vec<wire::GradientStop>,
    text_styles: Vec<wire::TextStyle>,
    brush_resource_index: u32,
    text_style_resource_index: u32,
    stack_depth: u32,
    materialized_layer_depth: u32,
    maximum_stack_depth: u32,
    stack_kinds: [u8; MAX_STACK_DEPTH as usize],
    layer_command_indices: [usize; MAX_STACK_DEPTH as usize],
    stack_state_indices: [u32; MAX_STACK_DEPTH as usize],
    arena_reserve: u64,
    hit_test_owners: Vec<HitTestOwnerBoundary>,
    glyph_hit_bounds: Vec<GlyphHitBoundsEntry>,
    hit_rectangle_scopes: Vec<HitRectangleScope>,
    source_geometry_hit_layers: Vec<SourceHitLayer>,
    input_only_ranges: Vec<InputOnlyRange>,
    input_only_stack: [usize; MAX_STACK_DEPTH as usize],
    render_only_ranges: Vec<InputOnlyRange>,
    render_only_stack: [usize; MAX_STACK_DEPTH as usize],
    hit_rectangle_stack: [usize; MAX_STACK_DEPTH as usize],
}

impl NativeSceneBuilder {
    pub fn new(scene_id: u64, generation: u64) -> BuildResult<Self> {
        if scene_id == 0 || generation == 0 {
            return Err(B::InvalidArgument);
        }
        Ok(Self {
            scene_id,
            generation,
            resources: Vec::new(),
            commands: Vec::new(),
            brushes: Vec::new(),
            gradient_stops: Vec::new(),
            text_styles: Vec::new(),
            brush_resource_index: NO_INDEX,
            text_style_resource_index: NO_INDEX,
            stack_depth: 0,
            materialized_layer_depth: 0,
            maximum_stack_depth: 0,
            stack_kinds: [0; MAX_STACK_DEPTH as usize],
            layer_command_indices: [0; MAX_STACK_DEPTH as usize],
            stack_state_indices: [NO_INDEX; MAX_STACK_DEPTH as usize],
            arena_reserve: 0,
            hit_test_owners: Vec::new(),
            glyph_hit_bounds: Vec::new(),
            hit_rectangle_scopes: Vec::new(),
            source_geometry_hit_layers: Vec::new(),
            input_only_ranges: Vec::new(),
            input_only_stack: [0; MAX_STACK_DEPTH as usize],
            render_only_ranges: Vec::new(),
            render_only_stack: [0; MAX_STACK_DEPTH as usize],
            hit_rectangle_stack: [0; MAX_STACK_DEPTH as usize],
        })
    }

    pub fn scene_id(&self) -> u64 {
        self.scene_id
    }
    pub fn generation(&self) -> u64 {
        self.generation
    }

    pub fn reserve(
        &mut self,
        command_count: u32,
        resource_count: u32,
        arena_bytes: u64,
    ) -> BuildResult<()> {
        if command_count > MAX_COMMANDS
            || resource_count > MAX_RESOURCES
            || arena_bytes > u64::from(MAX_STREAM_BYTES)
        {
            return Err(B::CapacityExceeded);
        }
        reserve_total(&mut self.commands, command_count as usize)?;
        reserve_total(&mut self.resources, resource_count as usize)?;
        reserve_total(
            &mut self.brushes,
            resource_count.min(SCENE_MAX_BRUSHES) as usize,
        )?;
        self.arena_reserve = arena_bytes;
        Ok(())
    }

    pub fn reset(&mut self, scene_id: u64, generation: u64) -> BuildResult<()> {
        if scene_id == 0 || generation == 0 {
            return Err(B::InvalidArgument);
        }
        self.scene_id = scene_id;
        self.generation = generation;
        self.resources.clear();
        self.commands.clear();
        self.hit_test_owners.clear();
        self.glyph_hit_bounds.clear();
        self.hit_rectangle_scopes.clear();
        self.source_geometry_hit_layers.clear();
        self.input_only_ranges.clear();
        self.input_only_stack.fill(0);
        self.render_only_ranges.clear();
        self.render_only_stack.fill(0);
        self.hit_rectangle_stack.fill(0);
        self.brushes.clear();
        self.gradient_stops.clear();
        self.text_styles.clear();
        self.brush_resource_index = NO_INDEX;
        self.text_style_resource_index = NO_INDEX;
        self.stack_depth = 0;
        self.materialized_layer_depth = 0;
        self.maximum_stack_depth = 0;
        self.stack_kinds.fill(0);
        self.layer_command_indices.fill(0);
        self.stack_state_indices.fill(NO_INDEX);
        self.arena_reserve = 0;
        Ok(())
    }

    pub fn advance_generation(&mut self, generation: u64) -> BuildResult<()> {
        if generation <= self.generation {
            return Err(B::InvalidArgument);
        }
        self.generation = generation;
        Ok(())
    }

    pub fn set_resource_identity(
        &mut self,
        index: u32,
        id: u64,
        generation: u64,
    ) -> BuildResult<()> {
        if id == 0 || generation == 0 {
            return Err(B::InvalidArgument);
        }
        let resource = self
            .resources
            .get_mut(index as usize)
            .ok_or(B::InvalidArgument)?;
        resource.record.resource_id = id;
        resource.record.generation = generation;
        Ok(())
    }

    pub fn identity_transform() -> wire::Affine2d {
        wire::Affine2d {
            m11: 1.0,
            m22: 1.0,
            ..Default::default()
        }
    }
    pub fn identity_state() -> wire::State {
        wire::State {
            struct_size: wire::State::WIRE_SIZE as u32,
            transform: Self::identity_transform(),
            opacity: 1.0,
            ..Default::default()
        }
    }

    fn new_resource(&self, kind: u32) -> ResourceEntry {
        ResourceEntry {
            record: wire::Resource {
                struct_size: wire::Resource::WIRE_SIZE as u32,
                kind,
                flags: SCENE_RECORD_REQUIRED,
                resource_id: self.resources.len() as u64 + 1,
                generation: self.generation,
                ..Default::default()
            },
            ..Default::default()
        }
    }
    fn new_command(
        &self,
        kind: u32,
        bounds: wire::ImageRect,
        state_index: u32,
        resource_index: u32,
    ) -> CommandEntry {
        CommandEntry {
            record: wire::Command {
                struct_size: wire::Command::WIRE_SIZE as u32,
                kind,
                flags: SCENE_RECORD_REQUIRED,
                command_id: self.commands.len() as u64 + 1,
                state_index,
                resource_index,
                bounds_x: bounds.x,
                bounds_y: bounds.y,
                bounds_width: bounds.width,
                bounds_height: bounds.height,
                ..Default::default()
            },
            ..Default::default()
        }
    }
    fn valid_state_index(&self, index: u32, allow_per_point: bool) -> bool {
        if index == NO_INDEX {
            return true;
        }
        let Some(resource) = self.resources.get(index as usize) else {
            return false;
        };
        if resource.record.kind != SCENE_RESOURCE_STATE
            || resource.payload.len() != wire::State::WIRE_SIZE
        {
            return false;
        }
        let Some(state) = wire::State::read(&resource.payload) else {
            return false;
        };
        if state.flags & SCENE_STATE_GUIDELINE_SET == 0 {
            return true;
        }
        let Some(guidelines) = self.resources.get(state.guideline_resource_index as usize) else {
            return false;
        };
        if guidelines.record.kind != SCENE_RESOURCE_GUIDELINE_SET {
            return false;
        }
        let Some(header) = wire::GuidelineSet::read(&guidelines.payload) else {
            return false;
        };
        header.flags & SCENE_GUIDELINE_COMPOSITE_ONLY == 0
            && (allow_per_point || header.flags & SCENE_GUIDELINE_PER_POINT == 0)
    }

    fn brush_payload(&self, indices: &[u32], explicit: bool) -> BuildResult<Vec<u8>> {
        if indices.is_empty() && !explicit {
            return Ok(Vec::new());
        }
        let mut bytes = encode(&[wire::DrawBrushes {
            struct_size: wire::DrawBrushes::WIRE_SIZE as u32,
            brush_resource_index: if indices.is_empty() {
                NO_INDEX
            } else {
                self.brush_resource_index
            },
            brush_count: u32::try_from(indices.len()).map_err(|_| B::CapacityExceeded)?,
            reserved: 0,
        }])?;
        append_encoded(&mut bytes, indices)?;
        Ok(bytes)
    }

    fn render_commands(&self) -> impl Iterator<Item = &CommandEntry> {
        let mut index = 0;
        let mut range_index = 0;
        std::iter::from_fn(move || {
            while index < self.commands.len() {
                while range_index < self.input_only_ranges.len()
                    && self.input_only_ranges[range_index].first_command < index
                {
                    range_index += 1;
                }
                if range_index < self.input_only_ranges.len()
                    && self.input_only_ranges[range_index].first_command == index
                {
                    index = self.input_only_ranges[range_index].last_command + 1;
                    range_index += 1;
                    continue;
                }
                let command = &self.commands[index];
                index += 1;
                return Some(command);
            }
            None
        })
    }
}

fn finite_color(color: wire::Color) -> bool {
    [color.r, color.g, color.b, color.a]
        .into_iter()
        .all(f32::is_finite)
}
fn finite_transform(t: wire::Affine2d) -> bool {
    [t.m11, t.m12, t.m21, t.m22, t.m31, t.m32]
        .into_iter()
        .all(f32::is_finite)
}
fn finite_rect(r: wire::ImageRect) -> bool {
    [r.x, r.y, r.width, r.height]
        .into_iter()
        .all(f32::is_finite)
        && r.width >= 0.0
        && r.height >= 0.0
}

/// Explicit geometric reserve before related record publication. Vec length and
/// all owned identities remain untouched if any later allocation fails.
fn reserve_append<T>(values: &mut Vec<T>, additional: usize) -> BuildResult<()> {
    let maximum = (isize::MAX as usize) / std::mem::size_of::<T>().max(1);
    let required = values
        .len()
        .checked_add(additional)
        .filter(|&n| n <= maximum)
        .ok_or(B::InvalidState)?;
    if required <= values.capacity() {
        return Ok(());
    }
    let doubled = values
        .capacity()
        .checked_mul(2)
        .unwrap_or(maximum)
        .min(maximum);
    let requested = required.max(doubled).max(8.min(maximum));
    values
        .try_reserve_exact(requested - values.len())
        .map_err(|_| B::OutOfMemory)
}
fn reserve_total<T>(values: &mut Vec<T>, count: usize) -> BuildResult<()> {
    if count > values.capacity() {
        values
            .try_reserve_exact(count.saturating_sub(values.len()))
            .map_err(|_| B::OutOfMemory)?;
    }
    Ok(())
}
fn zeroed(size: usize) -> BuildResult<Vec<u8>> {
    let mut bytes = Vec::new();
    bytes.try_reserve_exact(size).map_err(|_| B::OutOfMemory)?;
    bytes.resize(size, 0);
    Ok(bytes)
}
fn copy_bytes(bytes: &[u8]) -> BuildResult<Vec<u8>> {
    let mut copied = zeroed(bytes.len())?;
    copied.copy_from_slice(bytes);
    Ok(copied)
}
fn encode<T: WireRecord>(values: &[T]) -> BuildResult<Vec<u8>> {
    let size = values
        .len()
        .checked_mul(T::WIRE_SIZE)
        .ok_or(B::CapacityExceeded)?;
    let mut bytes = zeroed(size)?;
    write_records(values, &mut bytes)?;
    Ok(bytes)
}
fn write_records<T: WireRecord>(values: &[T], bytes: &mut [u8]) -> BuildResult<()> {
    let size = values
        .len()
        .checked_mul(T::WIRE_SIZE)
        .ok_or(B::CapacityExceeded)?;
    if bytes.len() < size {
        return Err(B::CapacityExceeded);
    }
    for (value, destination) in values
        .iter()
        .zip(bytes[..size].chunks_exact_mut(T::WIRE_SIZE))
    {
        value.write_into(destination).map_err(|_| B::InvalidState)?;
    }
    Ok(())
}
fn append_encoded<T: WireRecord>(bytes: &mut Vec<u8>, values: &[T]) -> BuildResult<()> {
    let additional = values
        .len()
        .checked_mul(T::WIRE_SIZE)
        .ok_or(B::CapacityExceeded)?;
    let old = bytes.len();
    let end = old.checked_add(additional).ok_or(B::CapacityExceeded)?;
    reserve_append(bytes, additional)?;
    bytes.resize(end, 0);
    write_records(values, &mut bytes[old..])
}
