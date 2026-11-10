//! Atomic native packed vector page, with a complete preflight before emission.
use super::*;
use crate::native_scene::execution::budget::NativeCompilationBudget;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeVectorPageMetrics {
    pub draw_count: u64,
    pub vertex_count: u64,
    pub index_count: u64,
    pub vertex_bytes: u64,
    pub index_bytes: u64,
    pub preflight_vertex_bytes: u64,
    pub preflight_index_bytes: u64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeVectorDrawRange {
    pub command_index: u32,
    pub draw: NativeAnalyticDraw,
}
/// Immutable original source, material generation and actual ordered target frames.
/// No provider callbacks, GPU uploads or mutable renderer caches occur here.
#[derive(Clone, Debug)]
pub struct NativeVectorPage {
    scene: Arc<NativeScene>,
    frames: Arc<[NativeCommandFrame]>,
    materials: NativeBrushPage,
    vertices: Arc<[VectorVertex]>,
    indices: Arc<[u32]>,
    draws: Arc<[NativeVectorDrawRange]>,
    budget: NativeCompilationBudget,
    metrics: NativeVectorPageMetrics,
}
impl NativeVectorPage {
    pub fn compile(
        scene: Arc<NativeScene>,
        frames: &[NativeCommandFrame],
        materials: &NativeBrushPage,
        antialias_padding_pixels: f32,
    ) -> Result<Self, Error> {
        if !Arc::ptr_eq(&scene, materials.scene())
            || !antialias_padding_pixels.is_finite()
            || antialias_padding_pixels <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        let mut budget = NativeCompilationBudget::default();
        let mut previous = None;
        for frame in frames {
            if !Arc::ptr_eq(&scene, &frame.scene)
                || previous.is_some_and(|v| frame.command_index <= v)
            {
                return Err(Error::InvalidInput);
            }
            previous = Some(frame.command_index);
            let (v, i) = compile_command(frame, materials, antialias_padding_pixels, None)?;
            let v = (v as u64).checked_mul(56).ok_or(Error::Capacity)?;
            let i = (i as u64).checked_mul(4).ok_or(Error::Capacity)?;
            if !budget.add(v, i, 0, 0) {
                return Err(Error::Capacity);
            }
        }
        let mut output = Output::default();
        output.reserve(
            usize::try_from(budget.vertex_bytes / 56).map_err(|_| Error::Capacity)?,
            usize::try_from(budget.index_bytes / 4).map_err(|_| Error::Capacity)?,
        )?;
        let mut draws = allocate(frames.len())?;
        let mut retained = allocate(frames.len())?;
        for frame in frames {
            let (v, i) = (output.vertices.len(), output.indices.len());
            compile_command(
                frame,
                materials,
                antialias_padding_pixels,
                Some(&mut output),
            )?;
            let draw = NativeAnalyticDraw {
                vertex_offset_bytes: v as u64 * 56,
                index_offset_bytes: i as u64 * 4,
                vertex_count: u32::try_from(output.vertices.len() - v)
                    .map_err(|_| Error::Capacity)?,
                index_count: u32::try_from(output.indices.len() - i)
                    .map_err(|_| Error::Capacity)?,
            };
            draws.push(NativeVectorDrawRange {
                command_index: frame.command_index,
                draw,
            });
            retained.push(frame.clone());
        }
        let metrics = NativeVectorPageMetrics {
            draw_count: draws.len() as u64,
            vertex_count: output.vertices.len() as u64,
            index_count: output.indices.len() as u64,
            vertex_bytes: output.vertices.len() as u64 * 56,
            index_bytes: output.indices.len() as u64 * 4,
            preflight_vertex_bytes: budget.vertex_bytes,
            preflight_index_bytes: budget.index_bytes,
        };
        if metrics.vertex_bytes > budget.vertex_bytes || metrics.index_bytes > budget.index_bytes {
            return Err(Error::Capacity);
        }
        Ok(Self {
            scene,
            frames: retained.into(),
            materials: materials.clone(),
            vertices: output.vertices.into(),
            indices: output.indices.into(),
            draws: draws.into(),
            budget,
            metrics,
        })
    }
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn frames(&self) -> &[NativeCommandFrame] {
        &self.frames
    }
    pub fn materials(&self) -> &NativeBrushPage {
        &self.materials
    }
    pub fn vertices(&self) -> &[VectorVertex] {
        &self.vertices
    }
    pub fn indices(&self) -> &[u32] {
        &self.indices
    }
    pub fn draws(&self) -> &[NativeVectorDrawRange] {
        &self.draws
    }
    pub fn budget(&self) -> NativeCompilationBudget {
        self.budget
    }
    pub fn metrics(&self) -> NativeVectorPageMetrics {
        self.metrics
    }
}
fn read<T: WireRecord>(bytes: &[u8], index: usize) -> Result<T, Error> {
    let start = index.checked_mul(T::WIRE_SIZE).ok_or(Error::Capacity)?;
    T::read(bytes.get(start..).ok_or(Error::InvalidInput)?).ok_or(Error::InvalidInput)
}
fn records<T: WireRecord>(bytes: &[u8], empty: bool) -> Result<Vec<T>, Error> {
    if (!empty && bytes.is_empty()) || bytes.len() % T::WIRE_SIZE != 0 {
        return Err(Error::InvalidInput);
    }
    let mut values = allocate(bytes.len() / T::WIRE_SIZE)?;
    for chunk in bytes.chunks_exact(T::WIRE_SIZE) {
        values.push(T::read(chunk).ok_or(Error::InvalidInput)?);
    }
    Ok(values)
}
fn count<T: WireRecord>(bytes: &[u8]) -> Result<usize, Error> {
    if bytes.is_empty() || bytes.len() % T::WIRE_SIZE != 0 {
        return Err(Error::InvalidInput);
    }
    Ok(bytes.len() / T::WIRE_SIZE)
}
fn add(total: &mut (usize, usize), value: (usize, usize)) -> Result<(), Error> {
    total.0 = total.0.checked_add(value.0).ok_or(Error::Capacity)?;
    total.1 = total.1.checked_add(value.1).ok_or(Error::Capacity)?;
    Ok(())
}
fn compile_command(
    frame: &NativeCommandFrame,
    materials: &NativeBrushPage,
    padding: f32,
    mut output: Option<&mut Output>,
) -> Result<(usize, usize), Error> {
    let command = frame.command();
    let expected = match command.record.kind {
        SCENE_COMMAND_DRAW_ANALYTIC => SCENE_RESOURCE_ANALYTIC_BATCH,
        SCENE_COMMAND_DRAW_GEOMETRY => SCENE_RESOURCE_GEOMETRY_BATCH,
        SCENE_COMMAND_DRAW_POINT_BATCH => SCENE_RESOURCE_POINT_BATCH,
        SCENE_COMMAND_DRAW_VERTEX_MESH | SCENE_COMMAND_DRAW_SOURCE_COVERAGE => {
            SCENE_RESOURCE_VERTEX_MESH
        }
        SCENE_COMMAND_DRAW_STROKE_BATCH => SCENE_RESOURCE_STROKE_BATCH,
        _ => return Err(Error::InvalidInput),
    };
    let resource = frame
        .scene
        .envelope()
        .resource(command.record.resource_index)
        .ok_or(Error::InvalidInput)?;
    if resource.record.kind != expected {
        return Err(Error::InvalidInput);
    }
    let mut total = (0usize, 0usize);
    let state = &frame.state;
    let brush = |index: usize| {
        materials.draw_brush(
            frame.command_index,
            u32::try_from(index).map_err(|_| Error::Capacity)?,
        )
    };
    match command.record.kind {
        SCENE_COMMAND_DRAW_ANALYTIC => {
            let count = count::<wire::AnalyticPrimitive>(resource.payload)?;
            if !resource.auxiliary.is_empty() || count > u32::MAX as usize / 6 {
                return Err(Error::InvalidInput);
            }
            for index in 0..count {
                let mut p: wire::AnalyticPrimitive = read(resource.payload, index)?;
                let b = brush(index)?;
                if let Some((_, b)) = b {
                    apply_analytic_transform(&mut p, state);
                    p.color = if b.kind == SCENE_BRUSH_SOLID {
                        b.colors[0]
                    } else {
                        wire::Color {
                            r: p.x + p.width * 0.5,
                            g: p.y + p.height * 0.5,
                            b: 0.0,
                            a: 1.0,
                        }
                    };
                } else {
                    apply_analytic_state(&mut p, state);
                }
                if !values::analytic(&p) {
                    return Err(Error::InvalidInput);
                }
                add(&mut total, (4, 6))?;
                if let Some(out) = output.as_deref_mut() {
                    let primitive = AnalyticPrimitive {
                        shape: match p.kind {
                            PRIMITIVE_RECTANGLE => Shape::Rectangle,
                            PRIMITIVE_ELLIPSE => Shape::Ellipse,
                            _ => Shape::RoundedRectangle,
                        },
                        bounds: [p.x, p.y, p.width, p.height],
                        color: color(p.color),
                        transform: affine(p.transform),
                        corner_radius: p.corner_radius,
                        stroke_thickness: p.stroke_thickness,
                        aliased: p.flags & PRIMITIVE_FLAG_EDGE_ALIASED != 0,
                    };
                    let mut vertices =
                        primitive.vertices(padding / primitive.transform.scales()?[1])?;
                    for v in &mut vertices {
                        v.brush_index = b.map_or(0, |v| v.0) as f32;
                    }
                    out.quad(vertices)?;
                }
            }
        }
        SCENE_COMMAND_DRAW_GEOMETRY => {
            let count = count::<wire::GeometryPrimitive>(resource.payload)?;
            if !resource.auxiliary.is_empty() || count > u32::MAX as usize {
                return Err(Error::InvalidInput);
            }
            for index in 0..count {
                let mut p: wire::GeometryPrimitive = read(resource.payload, index)?;
                let b = brush(index)?;
                if let Some((_, b)) = b {
                    apply_geometry_transform(&mut p, state);
                    if b.kind == SCENE_BRUSH_SOLID {
                        p.color = b.colors[0];
                    }
                } else {
                    apply_geometry_state(&mut p, state);
                }
                add(&mut total, geometry::capacity(&p)?)?;
                if let Some(out) = output.as_deref_mut() {
                    geometry::append(&p, b.map_or(0, |v| v.0) as f32, out)?;
                }
            }
        }
        SCENE_COMMAND_DRAW_POINT_BATCH => {
            let count = count::<wire::PointBatch>(resource.payload)?;
            let points = records::<wire::Point>(resource.auxiliary, false)?;
            if count > SCENE_MAX_DRAW_BRUSH_INDICES as usize || points.len() > u32::MAX as usize {
                return Err(Error::InvalidInput);
            }
            for index in 0..count {
                let mut p: wire::PointBatch = read(resource.payload, index)?;
                let b = brush(index)?;
                if let Some((_, b)) = b {
                    apply_point_batch_transform(&mut p, state);
                    if b.kind == SCENE_BRUSH_SOLID {
                        p.color = b.colors[0];
                    }
                } else {
                    apply_point_batch_state(&mut p, state);
                }
                add(&mut total, points::capacity(&p, &points)?)?;
                if let Some(out) = output.as_deref_mut() {
                    points::append(
                        &p,
                        &points,
                        b.map_or(0, |v| v.0) as f32,
                        b.is_some_and(|(_, b)| b.kind != SCENE_BRUSH_SOLID),
                        out,
                    )?;
                }
            }
        }
        SCENE_COMMAND_DRAW_VERTEX_MESH | SCENE_COMMAND_DRAW_SOURCE_COVERAGE => {
            let meshes = records::<wire::VertexMesh>(resource.payload, false)?;
            if meshes.len() > SCENE_MAX_DRAW_BRUSH_INDICES as usize {
                return Err(Error::InvalidInput);
            }
            let (vc, ic) = crate::native_scene::semantic::vertex_mesh::layout(
                &meshes,
                resource.auxiliary.len(),
            )
            .ok_or(Error::InvalidInput)?;
            let split = vc
                .checked_mul(wire::MeshVertex::WIRE_SIZE)
                .ok_or(Error::Capacity)?;
            let vertices = records::<wire::MeshVertex>(&resource.auxiliary[..split], true)?;
            let indices = records::<u16>(&resource.auxiliary[split..], true)?;
            let source = command.record.kind == SCENE_COMMAND_DRAW_SOURCE_COVERAGE;
            let offset = if source {
                let start = command
                    .payload
                    .len()
                    .checked_sub(wire::SourceCoverageFrame::WIRE_SIZE)
                    .ok_or(Error::InvalidInput)?;
                let captured = wire::SourceCoverageFrame::read(&command.payload[start..])
                    .ok_or(Error::InvalidInput)?;
                let offset = super::super::path::source_coverage_placement(
                    &captured,
                    &frame.source_state,
                    frame.target,
                    frame.presentation,
                )?;
                for vertex in &vertices {
                    let p = vertex.position;
                    if p.x + offset[0] < 0.0
                        || p.y + offset[1] < 0.0
                        || p.x + offset[0] > frame.target.width as f32
                        || p.y + offset[1] > frame.target.height as f32
                    {
                        return Err(Error::UnsupportedExecution);
                    }
                }
                offset
            } else {
                [0.0; 2]
            };
            for (index, mut p) in meshes.into_iter().enumerate() {
                let b = brush(index)?;
                if !source {
                    apply_vertex_mesh_transform(&mut p, state);
                }
                add(&mut total, mesh::capacity(&p, &vertices, ic)?)?;
                if let Some(out) = output.as_deref_mut() {
                    if source {
                        mesh::append_source(
                            &p,
                            &vertices,
                            offset,
                            state.opacity,
                            b.map_or(0, |v| v.0) as f32,
                            out,
                        )?;
                    } else {
                        mesh::append(
                            &p,
                            &vertices,
                            &indices,
                            state.opacity,
                            b.map_or(0, |v| v.0) as f32,
                            out,
                        )?;
                    }
                }
            }
        }
        SCENE_COMMAND_DRAW_STROKE_BATCH => {
            let strokes = records::<wire::Stroke>(resource.payload, false)?;
            if strokes.len() > SCENE_MAX_DRAW_BRUSH_INDICES as usize {
                return Err(Error::InvalidInput);
            }
            let (pc, _) =
                crate::native_scene::semantic::stroke::layout(&strokes, resource.auxiliary.len())
                    .ok_or(Error::InvalidInput)?;
            let split = pc
                .checked_mul(wire::Point::WIRE_SIZE)
                .ok_or(Error::Capacity)?;
            let points = records::<wire::Point>(&resource.auxiliary[..split], true)?;
            let doubles = records::<f64>(&resource.auxiliary[split..], true)?;
            if doubles.iter().any(|v| !v.is_finite()) {
                return Err(Error::InvalidInput);
            }
            for (index, mut p) in strokes.into_iter().enumerate() {
                let b = brush(index)?;
                apply_stroke_transform(&mut p, state);
                if let Some((_, b)) = b {
                    if b.kind == SCENE_BRUSH_SOLID {
                        p.color = b.colors[0];
                    }
                } else {
                    p.color.a *= state.opacity;
                }
                add(&mut total, stroke::capacity(&p, &points, &doubles)?)?;
                if let Some(out) = output.as_deref_mut() {
                    stroke::append(&p, &points, &doubles, b.map_or(0, |v| v.0) as f32, out)?;
                }
            }
        }
        _ => unreachable!(),
    }
    Ok(total)
}
