//! Original whole typed-draw preflight, before output geometry or GPU mutation.
use super::*;
use crate::native_scene::{
    engine_materials::{NativeBrushPage, NativeTextStylePage, native_glyph_payload},
    engine_resources::{self, NativeExternalImageInfo, NativeImageRole, NativeRgbGlyphPage},
    semantic::{self, glyph_scan, image_payload, rgb_glyph, text, values},
};
use budget::{MAX_COVERAGE_BYTES, NativeCompilationBudget};
mod glyph;
mod image;
mod masks;
mod path;
mod rgb;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeDrawFamily {
    Vector,
    Path,
    Glyph,
    Image,
    ThreeD,
    RgbGlyph,
    Clear,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeDrawCommandCost {
    pub command_index: u32,
    pub family: NativeDrawFamily,
    pub vertex_bytes: u64,
    pub index_bytes: u64,
    pub texture_bytes: u64,
    pub coverage_bytes: u64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeDrawPlanMetrics {
    pub draw_count: u32,
    pub vector_draw_count: u32,
    pub path_draw_count: u32,
    pub glyph_draw_count: u32,
    pub image_draw_count: u32,
    pub three_d_draw_count: u32,
    pub rgb_draw_count: u32,
    pub clear_count: u32,
    pub vector_vertex_bytes: u64,
    pub vector_index_bytes: u64,
    pub image_vertex_count: u64,
    pub path_count: u64,
    pub path_segment_count: u64,
    pub path_boolean_node_count: u64,
    pub glyph_outline_count: u64,
    pub glyph_segment_count: u64,
    pub glyph_count: u64,
    pub color_glyph_bitmap_count: u64,
    pub color_glyph_pixel_bytes: u64,
    pub glyph_coverage_bytes: u64,
    pub glyph_paint_count: u32,
    pub brush_count: u64,
    pub gradient_stop_count: u64,
    pub text_style_count: u64,
    pub material_bytes: u64,
    pub generated_mask_bytes: u64,
    pub has_styled_glyphs: bool,
    pub has_binary_glyphs: bool,
    pub has_vector_glyphs: bool,
    pub has_image_color_matrices: bool,
    pub has_state_masks: bool,
    pub has_vector_mask_chains: bool,
    pub has_text_mask_chains: bool,
    pub has_image_mask_chains: bool,
    pub has_masked_glyphs: bool,
    pub has_masked_images: bool,
}
/// Owns the complete source generation and actual frame at every command.
/// Family slices retain order; they never replace the source interleaving.
#[derive(Clone, Debug)]
pub struct NativeDrawExecutionPlan {
    scene: Arc<NativeScene>,
    frame: NativeSceneFrame,
    external: Arc<[NativeExternalImageInfo]>,
    materials: NativeBrushPage,
    styles: NativeTextStylePage,
    ordered: Arc<[NativeCommandFrame]>,
    vector: Arc<[NativeCommandFrame]>,
    path: Arc<[NativeCommandFrame]>,
    glyph: Arc<[NativeCommandFrame]>,
    image: Arc<[NativeCommandFrame]>,
    three_d: Arc<[NativeCommandFrame]>,
    rgb: Arc<[NativeCommandFrame]>,
    clear: Arc<[NativeCommandFrame]>,
    costs: Arc<[NativeDrawCommandCost]>,
    compilation: NativeCompilationBudget,
    metrics: NativeDrawPlanMetrics,
    engine_flags: u32,
    max_buffer_size: u64,
    antialias_padding: f32,
}
impl NativeDrawExecutionPlan {
    #[allow(clippy::too_many_arguments)]
    pub fn compile(
        scene: Arc<NativeScene>,
        frame: NativeSceneFrame,
        external: &[NativeExternalImageInfo],
        materials: &NativeBrushPage,
        styles: &NativeTextStylePage,
        engine_flags: u32,
        max_buffer_size: u64,
        antialias_padding: f32,
    ) -> Result<Self, Error> {
        validate_engine_flags(u64::from(engine_flags))?;
        if !Arc::ptr_eq(&scene, materials.scene())
            || !Arc::ptr_eq(&scene, styles.scene())
            || !engine_resources::external_info_valid(external)
        {
            return Err(Error::InvalidInput);
        }
        super::layer_plan::admit_frame(&scene, frame)?;
        let mut cursor = NativeExecutionCursor::new(scene.clone(), frame)?;
        let resource_count = scene.envelope().header().resource_count as usize;
        let mut masks = allocate(resource_count)?;
        masks.resize(resource_count, false);
        let mut glyph_resources = allocate(resource_count)?;
        glyph_resources.resize(resource_count, false);
        let (
            mut ordered,
            mut vector,
            mut paths,
            mut glyphs,
            mut images,
            mut three_d,
            mut rgb,
            mut clears,
            mut costs,
        ) = (
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        );
        let mut metrics = NativeDrawPlanMetrics::default();
        let mut compilation = NativeCompilationBudget::default();
        while let Some(current) = cursor.next_command()? {
            let command = current.command();
            let kind = command.record.kind;
            if kind == SCENE_COMMAND_CLEAR_TARGET {
                if current.source_state.flags & SCENE_STATE_MASK != 0 {
                    return Err(Error::UnsupportedExecution);
                }
                metrics.clear_count += 1;
                metrics.draw_count += 1;
                push(
                    &mut costs,
                    NativeDrawCommandCost {
                        command_index: current.command_index,
                        family: NativeDrawFamily::Clear,
                        vertex_bytes: 0,
                        index_bytes: 0,
                        texture_bytes: 0,
                        coverage_bytes: 0,
                    },
                )?;
                push(&mut clears, current.clone())?;
                push(&mut ordered, current)?;
                continue;
            }
            if !(SCENE_COMMAND_DRAW_ANALYTIC..=SCENE_COMMAND_DRAW_SOURCE_PATH).contains(&kind) {
                push(&mut ordered, current)?;
                continue;
            }
            let per_point = cursor
                .state_cursor()
                .has_per_point_guidelines(&current.state)?;
            if per_point && kind != SCENE_COMMAND_DRAW_PATH {
                return Err(Error::UnsupportedExecution);
            }
            masks::preflight(&current, &mut masks, &mut metrics)?;
            let mut cost = NativeDrawCommandCost {
                command_index: current.command_index,
                family: NativeDrawFamily::Vector,
                vertex_bytes: 0,
                index_bytes: 0,
                texture_bytes: 0,
                coverage_bytes: 0,
            };
            let destination = match kind {
                SCENE_COMMAND_DRAW_ANALYTIC
                | SCENE_COMMAND_DRAW_GEOMETRY
                | SCENE_COMMAND_DRAW_POINT_BATCH
                | SCENE_COMMAND_DRAW_VERTEX_MESH
                | SCENE_COMMAND_DRAW_SOURCE_COVERAGE
                | SCENE_COMMAND_DRAW_STROKE_BATCH => {
                    let (v, i) = engine_resources::vector::preflight_command(
                        &current,
                        materials,
                        antialias_padding,
                    )?;
                    cost.vertex_bytes = (v as u64).checked_mul(56).ok_or(Error::Capacity)?;
                    cost.index_bytes = (i as u64).checked_mul(4).ok_or(Error::Capacity)?;
                    metrics.vector_draw_count += 1;
                    add(&mut metrics.vector_vertex_bytes, cost.vertex_bytes)?;
                    add(&mut metrics.vector_index_bytes, cost.index_bytes)?;
                    &mut vector
                }
                SCENE_COMMAND_DRAW_PATH | SCENE_COMMAND_DRAW_SOURCE_PATH => {
                    cost.family = NativeDrawFamily::Path;
                    path::preflight(&current, materials, per_point, &mut cost, &mut metrics)?;
                    &mut paths
                }
                SCENE_COMMAND_DRAW_GLYPH_RUN | SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN => {
                    cost.family = NativeDrawFamily::Glyph;
                    glyph::preflight(
                        &current,
                        external,
                        &mut glyph_resources,
                        &mut cost,
                        &mut metrics,
                    )?;
                    &mut glyphs
                }
                SCENE_COMMAND_DRAW_IMAGE => {
                    cost.family = NativeDrawFamily::Image;
                    image::preflight(&current, external, &mut cost, &mut metrics)?;
                    &mut images
                }
                SCENE_COMMAND_DRAW_RGB_GLYPH_RUN => {
                    cost.family = NativeDrawFamily::RgbGlyph;
                    rgb::preflight(&current, engine_flags, max_buffer_size, &mut cost)?;
                    metrics.rgb_draw_count += 1;
                    &mut rgb
                }
                SCENE_COMMAND_DRAW_LINE_3D_BATCH | SCENE_COMMAND_DRAW_MESH_3D_BATCH => {
                    cost.family = NativeDrawFamily::ThreeD;
                    three_d_cost(&current, &mut cost)?;
                    metrics.three_d_draw_count += 1;
                    &mut three_d
                }
                _ => return Err(Error::InvalidInput),
            };
            if !compilation.add(
                cost.vertex_bytes,
                cost.index_bytes,
                cost.texture_bytes,
                cost.coverage_bytes,
            ) {
                return Err(Error::Capacity);
            }
            metrics.draw_count += 1;
            push(destination, current.clone())?;
            push(&mut costs, cost)?;
            push(&mut ordered, current)?;
        }
        metrics.brush_count = materials.brushes().len() as u64;
        metrics.gradient_stop_count = materials.gradient_stops().len() as u64;
        metrics.text_style_count = styles.styles().len() as u64;
        metrics.material_bytes = metrics.brush_count * wire::Brush::WIRE_SIZE as u64
            + metrics.gradient_stop_count * wire::GradientStop::WIRE_SIZE as u64
            + metrics.text_style_count * wire::TextStyle::WIRE_SIZE as u64;
        let mut external_owned = allocate(external.len())?;
        external_owned.extend_from_slice(external);
        Ok(Self {
            scene,
            frame,
            external: external_owned.into(),
            materials: materials.clone(),
            styles: styles.clone(),
            ordered: ordered.into(),
            vector: vector.into(),
            path: paths.into(),
            glyph: glyphs.into(),
            image: images.into(),
            three_d: three_d.into(),
            rgb: rgb.into(),
            clear: clears.into(),
            costs: costs.into(),
            compilation,
            metrics,
            engine_flags,
            max_buffer_size,
            antialias_padding,
        })
    }
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn frame(&self) -> NativeSceneFrame {
        self.frame
    }
    pub fn ordered_frames(&self) -> &[NativeCommandFrame] {
        &self.ordered
    }
    pub fn vector_frames(&self) -> &[NativeCommandFrame] {
        &self.vector
    }
    pub fn path_frames(&self) -> &[NativeCommandFrame] {
        &self.path
    }
    pub fn glyph_frames(&self) -> &[NativeCommandFrame] {
        &self.glyph
    }
    pub fn image_frames(&self) -> &[NativeCommandFrame] {
        &self.image
    }
    pub fn three_d_frames(&self) -> &[NativeCommandFrame] {
        &self.three_d
    }
    pub fn rgb_frames(&self) -> &[NativeCommandFrame] {
        &self.rgb
    }
    pub fn clear_frames(&self) -> &[NativeCommandFrame] {
        &self.clear
    }
    pub fn costs(&self) -> &[NativeDrawCommandCost] {
        &self.costs
    }
    pub fn compilation_budget(&self) -> NativeCompilationBudget {
        self.compilation
    }
    pub fn metrics(&self) -> NativeDrawPlanMetrics {
        self.metrics
    }
    pub fn materials(&self) -> &NativeBrushPage {
        &self.materials
    }
    pub fn text_styles(&self) -> &NativeTextStylePage {
        &self.styles
    }
    pub fn external_images(&self) -> &[NativeExternalImageInfo] {
        &self.external
    }
    pub fn engine_flags(&self) -> u32 {
        self.engine_flags
    }
    pub fn max_buffer_size(&self) -> u64 {
        self.max_buffer_size
    }
    pub fn antialias_padding(&self) -> f32 {
        self.antialias_padding
    }
    /// The final layer/material budget adds the complete binary snapshot once.
    pub fn budget_input(&self) -> NativeSceneBudgetInput {
        NativeSceneBudgetInput {
            compilation: self.compilation,
            material_bytes: self.metrics.material_bytes,
            generated_mask_bytes: self.metrics.generated_mask_bytes,
            has_state_masks: self.metrics.has_state_masks,
            three_d_draw_count: self.metrics.three_d_draw_count,
            has_binary_glyphs: self.metrics.has_binary_glyphs,
            glyph_coverage_bytes: self.metrics.glyph_coverage_bytes,
        }
    }
    /// Source aggregate inventory bounds follow the combined layer/material
    /// budget. The owning resource plan invokes this before allocating pages.
    pub fn validate_page_limits(&self) -> Result<(), Error> {
        let m = self.metrics;
        if m.path_count > 1 << 20
            || m.path_segment_count > 1 << 24
            || m.path_boolean_node_count > 1 << 22
            || m.path_count > u64::from(u32::MAX) / 6
            || m.glyph_outline_count > 1 << 20
            || m.glyph_segment_count > 1 << 24
            || m.glyph_count > 1 << 24
            || m.color_glyph_bitmap_count > 1 << 20
            || m.color_glyph_pixel_bytes > u64::from(SCENE_MAX_STREAM_BYTES)
        {
            return Err(Error::Capacity);
        }
        Ok(())
    }
}
fn three_d_cost(frame: &NativeCommandFrame, cost: &mut NativeDrawCommandCost) -> Result<(), Error> {
    let c = frame.command();
    let r = frame
        .scene
        .envelope()
        .resource(c.record.resource_index)
        .ok_or(Error::InvalidInput)?;
    if c.record.kind == SCENE_COMMAND_DRAW_LINE_3D_BATCH {
        if r.record.kind != SCENE_RESOURCE_LINE_3D_BATCH
            || r.payload.is_empty()
            || r.payload.len() % wire::Line3d::WIRE_SIZE != 0
            || c.payload.len() != wire::Camera3d::WIRE_SIZE
        {
            return Err(Error::InvalidInput);
        }
        cost.vertex_bytes = r.payload.len() as u64 + 176;
    } else {
        let count = r.payload.len() / wire::Mesh3d::WIRE_SIZE;
        let material_size =
            wire::Camera3d::WIRE_SIZE + wire::Mesh3dMaterials::WIRE_SIZE + count * 4;
        if r.record.kind != SCENE_RESOURCE_MESH_3D_BATCH
            || r.payload.is_empty()
            || r.auxiliary.is_empty()
            || r.payload.len() % wire::Mesh3d::WIRE_SIZE != 0
            || (c.payload.len() != wire::Camera3d::WIRE_SIZE && c.payload.len() != material_size)
        {
            return Err(Error::InvalidInput);
        }
        cost.vertex_bytes = r.payload.len() as u64
            + r.auxiliary.len() as u64
            + 176
            + if c.payload.len() == material_size {
                (material_size - wire::Camera3d::WIRE_SIZE) as u64
            } else {
                0
            };
    }
    Ok(())
}
fn allocate<T>(capacity: usize) -> Result<Vec<T>, Error> {
    let mut v = Vec::new();
    v.try_reserve_exact(capacity).map_err(|_| Error::Capacity)?;
    Ok(v)
}
fn push<T>(v: &mut Vec<T>, value: T) -> Result<(), Error> {
    v.try_reserve(1).map_err(|_| Error::Capacity)?;
    v.push(value);
    Ok(())
}
fn add(value: &mut u64, increment: u64) -> Result<(), Error> {
    *value = value.checked_add(increment).ok_or(Error::Capacity)?;
    Ok(())
}
fn read<T: WireRecord>(bytes: &[u8], index: usize) -> Result<T, Error> {
    index
        .checked_mul(T::WIRE_SIZE)
        .and_then(|offset| bytes.get(offset..))
        .and_then(T::read)
        .ok_or(Error::InvalidInput)
}
fn input(scene: &NativeScene) -> semantic::Input<'_> {
    semantic::Input {
        bytes: scene.envelope().bytes(),
        header: scene.envelope().header(),
    }
}
