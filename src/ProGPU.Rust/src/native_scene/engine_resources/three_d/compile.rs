//! Whole-inventory native admission followed by atomic CPU page publication.
use super::records::*;
use super::*;
use crate::native_scene::{execution::NativeStateCursor, semantic::brush::stored_stop_count};
#[derive(Default, Clone, Copy)]
struct Counts {
    cameras: usize,
    lines: usize,
    meshes: usize,
    vertices: usize,
    indices: usize,
    lights: usize,
    materials: usize,
    stops: usize,
    edges: usize,
    gpu_draws: usize,
    native_bytes: u64,
}
impl Counts {
    fn include(&mut self, b: Self) -> Result<(), Error> {
        macro_rules! sum { ($($n:ident),*)=>{$(self.$n=add(self.$n,b.$n)?;)*}; }
        sum!(
            cameras, lines, meshes, vertices, indices, lights, materials, stops, edges, gpu_draws
        );
        self.native_bytes = self
            .native_bytes
            .checked_add(b.native_bytes)
            .ok_or(Error::Capacity)?;
        for n in self.lengths() {
            count(n)?;
        }
        Ok(())
    }
    fn lengths(self) -> [usize; 9] {
        [
            self.cameras,
            self.lines,
            self.meshes,
            self.vertices,
            self.indices,
            self.lights,
            self.materials,
            self.stops,
            self.edges,
        ]
    }
    fn metrics(self) -> Result<NativeThreeDMetrics, Error> {
        let mut storage = 0u64;
        let mut uploads = 0u64;
        for (n, stride) in self.lengths().into_iter().zip(MINIMUM_SIZES) {
            let size = (n as u64).checked_mul(stride).ok_or(Error::Capacity)?;
            uploads = uploads.checked_add(size).ok_or(Error::Capacity)?;
            if self.cameras != 0 {
                storage = storage
                    .checked_add(size.max(stride))
                    .ok_or(Error::Capacity)?;
            }
        }
        Ok(NativeThreeDMetrics {
            command_draws: self.cameras as u64,
            gpu_draws: self.gpu_draws as u64,
            storage_bytes: storage,
            upload_bytes: uploads,
            buffer_allocations: if self.cameras == 0 { 0 } else { 9 },
            bind_groups: if self.cameras == 0 {
                0
            } else {
                1 + self.meshes as u64
            },
            native_admission_bytes: self.native_bytes,
            ..Default::default()
        })
    }
}
#[derive(Clone, Copy)]
struct SourceLayout {
    vertices: usize,
    lights: usize,
    index_start: usize,
    light_start: usize,
}
fn layout(resource: &super::super::super::ResourceView<'_>) -> Result<SourceLayout, Error> {
    let mut v = 0usize;
    let mut i = 0usize;
    let mut l = 0usize;
    for raw in resource.payload.chunks_exact(wire::Mesh3d::WIRE_SIZE) {
        let m = read::<wire::Mesh3d>(raw, 0)?;
        v = v.max(add(m.vertex_offset as usize, m.vertex_count as usize)?);
        i = i.max(add(m.index_offset as usize, m.index_count as usize)?);
        l = l.max(add(m.light_offset as usize, m.light_count as usize)?);
    }
    let index_start = bytes(v, wire::Mesh3dVertex::WIRE_SIZE)?;
    let light_start = add(index_start, bytes(i, 4)?)?;
    if add(light_start, bytes(l, wire::Light3d::WIRE_SIZE)?)? != resource.auxiliary.len() {
        return Err(Error::InvalidInput);
    }
    Ok(SourceLayout {
        vertices: v,
        lights: l,
        index_start,
        light_start,
    })
}
fn material_map<'a>(
    owner: &'a NativeScene,
    payload: &[u8],
    meshes: usize,
) -> Result<Option<(super::super::super::ResourceView<'a>, usize)>, Error> {
    if payload.len() == wire::Camera3d::WIRE_SIZE {
        return Ok(None);
    }
    let base = wire::Camera3d::WIRE_SIZE + wire::Mesh3dMaterials::WIRE_SIZE;
    if payload.len() != add(base, bytes(meshes, 4)?)? {
        return Err(Error::InvalidInput);
    }
    let map = read::<wire::Mesh3dMaterials>(payload, wire::Camera3d::WIRE_SIZE)?;
    let resource = owner
        .envelope()
        .resource(map.brush_resource_index)
        .ok_or(Error::InvalidInput)?;
    Ok(Some((resource, base)))
}
fn source_material<'a>(
    owner: &'a NativeScene,
    payload: &[u8],
    mesh_count: usize,
    index: usize,
) -> Result<(wire::Brush, Option<super::super::super::ResourceView<'a>>), Error> {
    if let Some((resource, base)) = material_map(owner, payload, mesh_count)? {
        let index = read::<u32>(payload, base + index * 4)? as usize;
        Ok((
            read(resource.payload, bytes(index, wire::Brush::WIRE_SIZE)?)?,
            Some(resource),
        ))
    } else {
        let mut brush = wire::Brush {
            kind: SCENE_BRUSH_SOLID,
            opacity: 1.0,
            ..Default::default()
        };
        brush.colors[0] = wire::Color {
            r: 1.0,
            g: 1.0,
            b: 1.0,
            a: 1.0,
        };
        brush.coordinate_transform0[0] = 1.0;
        brush.coordinate_transform1[1] = 1.0;
        Ok((brush, None))
    }
}
fn image_slot(
    owner: &NativeScene,
    mesh: wire::Mesh3d,
    external: &[NativeExternalImageInfo],
) -> Result<Option<usize>, Error> {
    if mesh.flags & MESH_3D_MATERIAL_IMAGE == 0 {
        return Ok(None);
    }
    let r = owner
        .envelope()
        .resource(mesh.material_image_resource_index)
        .ok_or(Error::InvalidInput)?
        .record;
    external_slot(
        external,
        r.resource_id,
        r.generation,
        NativeImageRole::Primary,
    )
    .map(Some)
    .ok_or(Error::ResourceDomain)
}
fn inspect(
    owner: &NativeScene,
    frame: &NativeCommandFrame,
    external: &[NativeExternalImageInfo],
) -> Result<Counts, Error> {
    let c = frame.command();
    let r = owner
        .envelope()
        .resource(c.record.resource_index)
        .ok_or(Error::InvalidInput)?;
    let mut result = Counts {
        cameras: 1,
        ..Default::default()
    };
    if c.record.kind == SCENE_COMMAND_DRAW_LINE_3D_BATCH {
        if r.record.kind != SCENE_RESOURCE_LINE_3D_BATCH
            || r.payload.is_empty()
            || r.payload.len() % wire::Line3d::WIRE_SIZE != 0
            || c.payload.len() != wire::Camera3d::WIRE_SIZE
        {
            return Err(Error::InvalidInput);
        }
        result.lines = r.payload.len() / wire::Line3d::WIRE_SIZE;
        result.gpu_draws = 1;
        result.native_bytes = r.payload.len() as u64 + 176;
        return Ok(result);
    }
    if r.record.kind != SCENE_RESOURCE_MESH_3D_BATCH
        || r.payload.is_empty()
        || r.payload.len() % wire::Mesh3d::WIRE_SIZE != 0
        || r.auxiliary.is_empty()
    {
        return Err(Error::InvalidInput);
    }
    let source = layout(&r)?;
    result.meshes = r.payload.len() / wire::Mesh3d::WIRE_SIZE;
    result.vertices = source.vertices;
    result.lights = source.lights;
    result.materials = result.meshes;
    let mapped = material_map(owner, c.payload, result.meshes)?.is_some();
    result.native_bytes = r.payload.len() as u64
        + r.auxiliary.len() as u64
        + 176
        + if mapped {
            (c.payload.len() - wire::Camera3d::WIRE_SIZE) as u64
        } else {
            0
        };
    for (index, raw) in r.payload.chunks_exact(wire::Mesh3d::WIRE_SIZE).enumerate() {
        let m = read::<wire::Mesh3d>(raw, 0)?;
        image_slot(owner, m, external)?;
        if m.topology == MESH_3D_EDGE_LIST {
            result.edges = add(result.edges, m.vertex_count as usize / 2)?;
            result.gpu_draws = add(
                result.gpu_draws,
                if m.flags & MESH_3D_EDGE_DISPLAY_OCCLUDED != 0 {
                    2
                } else {
                    1
                },
            )?;
        } else {
            result.gpu_draws = add(result.gpu_draws, 1)?;
            let n = if m.topology == MESH_3D_TRIANGLES {
                m.index_count as usize
            } else {
                let mut n = 0;
                for j in 2..m.index_count as usize {
                    let at = source.index_start + (m.index_offset as usize + j) * 4;
                    let a = read::<u32>(r.auxiliary, at - 8)?;
                    let b = read::<u32>(r.auxiliary, at - 4)?;
                    let c = read::<u32>(r.auxiliary, at)?;
                    if a != b && b != c && a != c {
                        n = add(n, 3)?;
                    }
                }
                n
            };
            result.indices = add(result.indices, n)?;
        }
        let (material, resource) = source_material(owner, c.payload, result.meshes, index)?;
        let stops = stored_stop_count(&material) as usize;
        if let Some(r) = resource {
            let end = bytes(
                add(material.stop_offset as usize, stops)?,
                wire::GradientStop::WIRE_SIZE,
            )?;
            if end > r.auxiliary.len() {
                return Err(Error::InvalidInput);
            }
        }
        result.stops = add(result.stops, stops)?;
    }
    Ok(result)
}
impl NativeThreeDPage {
    /// `frames` must carry every original 3D command once, in source order.
    /// Source state is intentionally not target-localized: camera viewport mapping
    /// owns crop translation. Mapped presentation and per-draw masks retain the
    /// original independent execution gates.
    pub fn compile_frames(
        owner: Arc<NativeScene>,
        frame: NativeSceneFrame,
        frames: &[NativeCommandFrame],
        external: &[NativeExternalImageInfo],
        maximum_storage_bytes: u64,
    ) -> Result<Self, Error> {
        Self::compile_frames_with_preference(
            owner,
            frame,
            frames,
            external,
            maximum_storage_bytes,
            crate::path::ExecutionPreference::Automatic,
        )
    }
    pub fn compile_frames_with_preference(
        owner: Arc<NativeScene>,
        frame: NativeSceneFrame,
        frames: &[NativeCommandFrame],
        external: &[NativeExternalImageInfo],
        maximum_storage_bytes: u64,
        preference: crate::path::ExecutionPreference,
    ) -> Result<Self, Error> {
        let lowering = NativeThreeDLoweringPath::select(preference)?;
        let p = frame.resolve_presentation().ok_or(Error::InvalidInput)?;
        if !external_info_valid(external) {
            return Err(Error::InvalidInput);
        }
        let state_cursor = NativeStateCursor::new(owner.clone(), [frame.dpi_scale; 2]);
        let mut totals = Counts::default();
        let mut selected = allocate(frames.len())?;
        let mut last = None;
        for f in frames {
            if !Arc::ptr_eq(f.scene(), &owner) {
                return Err(Error::ResourceDomain);
            }
            let kind = f.command().record.kind;
            if !matches!(
                kind,
                SCENE_COMMAND_DRAW_LINE_3D_BATCH | SCENE_COMMAND_DRAW_MESH_3D_BATCH
            ) {
                continue;
            }
            if f.dpi_scale() != frame.dpi_scale || last.is_some_and(|i| i >= f.command_index()) {
                return Err(Error::InvalidInput);
            }
            if f.source_state().flags & SCENE_STATE_MASK != 0
                || state_cursor.has_per_point_guidelines(&f.source_state())?
            {
                return Err(Error::UnsupportedExecution);
            }
            last = Some(f.command_index());
            totals.include(inspect(&owner, f, external)?)?;
            selected.push(f.clone());
        }
        let expected = owner
            .envelope()
            .commands()
            .filter(|c| {
                matches!(
                    c.record.kind,
                    SCENE_COMMAND_DRAW_LINE_3D_BATCH | SCENE_COMMAND_DRAW_MESH_3D_BATCH
                )
            })
            .count();
        if selected.len() != expected {
            return Err(Error::InvalidInput);
        }
        if expected != 0
            && (p.viewport_x != 0
                || p.viewport_y != 0
                || p.viewport_width != frame.width
                || p.viewport_height != frame.height
                || p.dpi_scale_x != frame.dpi_scale
                || p.dpi_scale_y != frame.dpi_scale)
        {
            return Err(Error::UnsupportedExecution);
        }
        let metrics = totals.metrics()?;
        if metrics.storage_bytes > maximum_storage_bytes {
            return Err(Error::Capacity);
        }
        let mut cameras = allocate(totals.cameras)?;
        let mut lines = allocate(totals.lines)?;
        let mut meshes = allocate(totals.meshes)?;
        let mut vertices = allocate(totals.vertices)?;
        let mut indices = allocate(totals.indices)?;
        let mut lights = allocate(totals.lights)?;
        let mut materials = allocate(totals.materials)?;
        let mut stops = allocate(totals.stops)?;
        let mut edges = allocate(totals.edges)?;
        let mut draws = allocate(expected)?;
        let mut mesh_draws = allocate(totals.meshes)?;
        for f in selected {
            let command = f.command();
            let c = command.record;
            let resource = owner
                .envelope()
                .resource(c.resource_index)
                .ok_or(Error::InvalidInput)?;
            let camera = read::<wire::Camera3d>(command.payload, 0)?;
            let target = f.target();
            let state = f.source_state();
            let transform = affine(state.transform);
            let camera_index = count(cameras.len())?;
            cameras.push(NativeThreeDCamera {
                projection: matrix(camera.projection),
                view: matrix(camera.view),
                camera_position: point(camera.camera_position, 1.0),
                viewport: [
                    target.width.max(1) as f32,
                    target.height.max(1) as f32,
                    frame.dpi_scale,
                    0.0,
                ],
                viewport_rect: [
                    c.bounds_x * frame.dpi_scale - target.x as f32,
                    c.bounds_y * frame.dpi_scale - target.y as f32,
                    c.bounds_width * frame.dpi_scale,
                    c.bounds_height * frame.dpi_scale,
                ],
            });
            if c.kind == SCENE_COMMAND_DRAW_LINE_3D_BATCH {
                let first_record = count(lines.len())?;
                for raw in resource.payload.chunks_exact(wire::Line3d::WIRE_SIZE) {
                    let line = read::<wire::Line3d>(raw, 0)?;
                    lines.push(NativeThreeDLine {
                        start: point(line.start, 1.0),
                        end: point(line.end, 1.0),
                        color: color(line.color),
                        thickness: line.thickness,
                        opacity: line.opacity * state.opacity,
                        camera_index,
                        flags: 0,
                        transform: multiply(matrix(line.transform), transform, lowering),
                    });
                }
                let record_count = count(lines.len())? - first_record;
                draws.push(NativeThreeDDraw {
                    frame: f,
                    first_record,
                    record_count,
                });
                continue;
            }
            let source = layout(&resource)?;
            let vertex_base = count(vertices.len())?;
            let light_base = count(lights.len())?;
            for i in 0..source.vertices {
                vertices.push(read(resource.auxiliary, i * wire::Mesh3dVertex::WIRE_SIZE)?);
            }
            for i in 0..source.lights {
                lights.push(read(
                    resource.auxiliary,
                    source.light_start + i * wire::Light3d::WIRE_SIZE,
                )?);
            }
            let first_record = count(meshes.len())?;
            let mesh_count = resource.payload.len() / wire::Mesh3d::WIRE_SIZE;
            for (index, raw) in resource
                .payload
                .chunks_exact(wire::Mesh3d::WIRE_SIZE)
                .enumerate()
            {
                let m = read::<wire::Mesh3d>(raw, 0)?;
                let is_edge = m.topology == MESH_3D_EDGE_LIST;
                let index_offset = count(indices.len())?;
                let edge_offset = count(edges.len())?;
                if is_edge {
                    for j in (0..m.vertex_count).step_by(2) {
                        let a =
                            vertices[vertex_base as usize + m.vertex_offset as usize + j as usize];
                        let b = vertices
                            [vertex_base as usize + m.vertex_offset as usize + j as usize + 1];
                        edges.push(NativeThreeDEdge {
                            start: point(a.position, 1.0),
                            end: point(b.position, 1.0),
                            first_normal: point(a.normal, 0.0),
                            second_normal: point(b.normal, 0.0),
                            mesh_index: count(meshes.len())?,
                            topology: a.texture_coordinate.x as u32,
                            reserved0: 0,
                            reserved1: 0,
                        });
                    }
                } else if m.topology == MESH_3D_TRIANGLES {
                    for j in 0..m.index_count as usize {
                        indices.push(read(
                            resource.auxiliary,
                            source.index_start + (m.index_offset as usize + j) * 4,
                        )?);
                    }
                } else {
                    for j in 2..m.index_count as usize {
                        let at = source.index_start + (m.index_offset as usize + j) * 4;
                        let a = read::<u32>(resource.auxiliary, at - 8)?;
                        let b = read::<u32>(resource.auxiliary, at - 4)?;
                        let c = read::<u32>(resource.auxiliary, at)?;
                        if a == b || b == c || a == c {
                            continue;
                        }
                        indices.extend_from_slice(&if j & 1 == 0 { [a, b, c] } else { [b, a, c] });
                    }
                }
                let topology = if is_edge {
                    MESH_3D_EDGE_LIST
                } else {
                    MESH_3D_TRIANGLES
                };
                let index_count = count(indices.len())? - index_offset;
                meshes.push(NativeThreeDMesh {
                    flags: m.flags,
                    topology,
                    render_mode: m.render_mode,
                    camera_index,
                    vertex_offset: vertex_base + m.vertex_offset,
                    vertex_count: m.vertex_count,
                    index_offset,
                    index_count,
                    model_transform: multiply(matrix(m.model_transform), transform, lowering),
                    normal_transform: matrix(m.normal_transform),
                    color: color(m.color),
                    light_direction: float4(m.light_direction),
                    ambient_color: float4(m.ambient_color),
                    specular_color: float4(m.specular_color),
                    material_ambient: float4(m.material_ambient),
                    opacity: m.opacity * state.opacity,
                    shading_mode: m.shading_mode,
                    material_image_resource_index: m.material_image_resource_index,
                    material_factors: m.material_factors,
                    light_offset: light_base + m.light_offset,
                    light_count: m.light_count,
                    reserved0: 0,
                    reserved1: 0,
                });
                let material_image = image_slot(&owner, m, external)?;
                let (mut material, source_material) =
                    source_material(&owner, command.payload, mesh_count, index)?;
                if let Some(source) = source_material {
                    let offset = material.stop_offset as usize;
                    let n = stored_stop_count(&material) as usize;
                    material.stop_offset = count(stops.len())?;
                    for j in 0..n {
                        stops.push(read(
                            source.auxiliary,
                            (offset + j) * wire::GradientStop::WIRE_SIZE,
                        )?);
                    }
                }
                materials.push(material);
                mesh_draws.push(NativeThreeDMeshDraw {
                    topology,
                    flags: m.flags,
                    index_count,
                    edge_offset,
                    edge_count: count(edges.len())? - edge_offset,
                    edge_vertex_count: if is_edge && m.specular_color.y > 0.0 {
                        18
                    } else {
                        6
                    },
                    material_image,
                });
            }
            draws.push(NativeThreeDDraw {
                frame: f,
                first_record,
                record_count: count(mesh_count)?,
            });
        }
        let mut external_copy = allocate(external.len())?;
        external_copy.extend_from_slice(external);
        Ok(Self {
            owner,
            frame,
            draws: draws.into(),
            cameras: cameras.into(),
            lines: lines.into(),
            meshes: meshes.into(),
            vertices: vertices.into(),
            indices: indices.into(),
            lights: lights.into(),
            materials: materials.into(),
            stops: stops.into(),
            edges: edges.into(),
            mesh_draws: mesh_draws.into(),
            external: external_copy.into(),
            metrics,
            lowering,
        })
    }
}
