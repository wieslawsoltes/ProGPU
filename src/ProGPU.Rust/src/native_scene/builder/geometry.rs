//! Native batched geometry, mesh/source coverage and raw stroke producers.
use super::*;
use crate::native_scene::semantic::stroke;
use crate::native_scene::semantic::{source_draw, values, vertex_mesh as mesh};
impl NativeSceneBuilder {
    pub fn draw_geometry(
        &mut self,
        primitives: &[wire::GeometryPrimitive],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        if primitives.is_empty()
            || !finite_rect(bounds)
            || !self.valid_state_index(state_resource_index, false)
            || (!brush_indices.is_empty() && brush_indices.len() != primitives.len())
            || self.resources.len() >= MAX_RESOURCES as usize
            || self.commands.len() >= MAX_COMMANDS as usize
            || !primitives.iter().all(values::geometry)
            || brush_indices
                .iter()
                .any(|&i| i as usize >= self.brushes.len())
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        reserve_append(&mut self.commands, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_GEOMETRY_BATCH);
        resource.payload = encode(primitives)?;
        let mut command = self.new_command(
            SCENE_COMMAND_DRAW_GEOMETRY,
            bounds,
            state_resource_index,
            self.resources.len() as u32,
        );
        command.payload = self.brush_payload(brush_indices, false)?;
        self.resources.push(resource);
        self.commands.push(command);
        Ok(())
    }
    pub fn draw_vertex_meshes(
        &mut self,
        meshes: &[wire::VertexMesh],
        vertices: &[wire::MeshVertex],
        indices: &[u16],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        self.draw_vertex_meshes_core(
            meshes,
            vertices,
            indices,
            brush_indices,
            bounds,
            state_resource_index,
            None,
        )
    }
    pub fn draw_source_coverage(
        &mut self,
        meshes: &[wire::VertexMesh],
        vertices: &[wire::MeshVertex],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        frame: &wire::SourceCoverageFrame,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        self.draw_vertex_meshes_core(
            meshes,
            vertices,
            &[],
            brush_indices,
            bounds,
            state_resource_index,
            Some(frame),
        )
    }
    fn draw_vertex_meshes_core(
        &mut self,
        meshes: &[wire::VertexMesh],
        vertices: &[wire::MeshVertex],
        indices: &[u16],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        state_resource_index: u32,
        frame: Option<&wire::SourceCoverageFrame>,
    ) -> BuildResult<()> {
        let auxiliary_size = vertices
            .len()
            .checked_mul(wire::MeshVertex::WIRE_SIZE)
            .and_then(|n| indices.len().checked_mul(2).and_then(|i| n.checked_add(i)))
            .ok_or(B::InvalidArgument)?;
        if frame.is_some_and(|f| {
            !source_draw::valid_source_coverage_frame(f)
                || brush_indices.len() != meshes.len()
                || !indices.is_empty()
                || vertices.len() > 1048576
        }) || meshes.is_empty()
            || !finite_rect(bounds)
            || !self.valid_state_index(state_resource_index, false)
            || (!brush_indices.is_empty() && brush_indices.len() != meshes.len())
            || meshes.len() > MAX_STREAM_BYTES as usize / wire::VertexMesh::WIRE_SIZE
            || auxiliary_size > MAX_STREAM_BYTES as usize
            || mesh::layout(meshes, auxiliary_size) != Some((vertices.len(), indices.len()))
            || self.resources.len() >= MAX_RESOURCES as usize
            || self.commands.len() >= MAX_COMMANDS as usize
        {
            return Err(B::InvalidArgument);
        }
        for mesh in meshes {
            if !mesh::valid(mesh, vertices, indices.len()) {
                return Err(B::InvalidArgument);
            }
            if let Some(frame) = frame {
                if !source_draw::valid_source_coverage_mesh_shape(mesh, vertices.len() as u32)
                    || !vertices[mesh.vertex_offset as usize
                        ..mesh.vertex_offset as usize + mesh.vertex_count as usize]
                        .iter()
                        .all(|v| source_draw::valid_source_coverage_vertex(v, frame))
                {
                    return Err(B::InvalidArgument);
                }
            }
            if indices
                [mesh.index_offset as usize..mesh.index_offset as usize + mesh.index_count as usize]
                .iter()
                .any(|&i| u32::from(i) >= mesh.vertex_count)
            {
                return Err(B::InvalidArgument);
            }
        }
        if brush_indices
            .iter()
            .any(|&i| i as usize >= self.brushes.len())
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        reserve_append(&mut self.commands, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_VERTEX_MESH);
        resource.payload = encode(meshes)?;
        resource.auxiliary = encode(vertices)?;
        append_encoded(&mut resource.auxiliary, indices)?;
        let mut command = self.new_command(
            if frame.is_some() {
                SCENE_COMMAND_DRAW_SOURCE_COVERAGE
            } else {
                SCENE_COMMAND_DRAW_VERTEX_MESH
            },
            bounds,
            state_resource_index,
            self.resources.len() as u32,
        );
        command.payload = self.brush_payload(brush_indices, false)?;
        if let Some(frame) = frame {
            append_encoded(&mut command.payload, &[*frame])?;
        }
        self.resources.push(resource);
        self.commands.push(command);
        Ok(())
    }
    pub fn draw_strokes(
        &mut self,
        strokes: &[wire::Stroke],
        points: &[wire::Point],
        doubles: &[f64],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        let auxiliary_size = points
            .len()
            .checked_mul(wire::Point::WIRE_SIZE)
            .and_then(|n| doubles.len().checked_mul(8).and_then(|d| n.checked_add(d)))
            .ok_or(B::InvalidArgument)?;
        if strokes.is_empty()
            || points.is_empty()
            || !finite_rect(bounds)
            || !self.valid_state_index(state_resource_index, false)
            || (!brush_indices.is_empty() && brush_indices.len() != strokes.len())
            || auxiliary_size > MAX_STREAM_BYTES as usize
            || stroke::layout(strokes, auxiliary_size) != Some((points.len(), doubles.len()))
            || self.resources.len() >= MAX_RESOURCES as usize
            || self.commands.len() >= MAX_COMMANDS as usize
            || !points.iter().all(|p| p.x.is_finite() && p.y.is_finite())
            || !doubles.iter().all(|v| v.is_finite())
            || brush_indices
                .iter()
                .any(|&i| i as usize >= self.brushes.len())
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        reserve_append(&mut self.commands, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_STROKE_BATCH);
        resource.payload = encode(strokes)?;
        resource.auxiliary = encode(points)?;
        append_encoded(&mut resource.auxiliary, doubles)?;
        let mut command = self.new_command(
            SCENE_COMMAND_DRAW_STROKE_BATCH,
            bounds,
            state_resource_index,
            self.resources.len() as u32,
        );
        command.payload = self.brush_payload(brush_indices, false)?;
        self.resources.push(resource);
        self.commands.push(command);
        Ok(())
    }
}
