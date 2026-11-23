//! Original native camera-framed lines and packed mesh/material producers.
use super::*;
use crate::native_scene::semantic::{stored_stop_count, valid_brush_records, values};
impl NativeSceneBuilder {
    pub fn draw_lines_3d(
        &mut self,
        lines: &[wire::Line3d],
        camera: &wire::Camera3d,
        bounds: wire::ImageRect,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        if lines.is_empty()
            || !finite_rect(bounds)
            || !self.valid_state_index(state_resource_index, false)
            || !values::camera_3d(camera)
            || self.resources.len() >= MAX_RESOURCES as usize
            || self.commands.len() >= MAX_COMMANDS as usize
            || !lines.iter().all(values::line_3d)
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        reserve_append(&mut self.commands, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_LINE_3D_BATCH);
        resource.payload = encode(lines)?;
        let mut command = self.new_command(
            SCENE_COMMAND_DRAW_LINE_3D_BATCH,
            bounds,
            state_resource_index,
            self.resources.len() as u32,
        );
        command.payload = encode(&[*camera])?;
        self.resources.push(resource);
        self.commands.push(command);
        Ok(())
    }
    pub fn draw_meshes_3d(
        &mut self,
        meshes: &[wire::Mesh3d],
        vertices: &[wire::Mesh3dVertex],
        indices: &[u32],
        camera: &wire::Camera3d,
        bounds: wire::ImageRect,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        self.draw_meshes_3d_with_materials(
            meshes,
            vertices,
            indices,
            &[],
            &[],
            &[],
            camera,
            bounds,
            state_resource_index,
        )
    }
    pub fn draw_meshes_3d_with_lights(
        &mut self,
        meshes: &[wire::Mesh3d],
        vertices: &[wire::Mesh3dVertex],
        indices: &[u32],
        lights: &[wire::Light3d],
        camera: &wire::Camera3d,
        bounds: wire::ImageRect,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        self.draw_meshes_3d_with_materials(
            meshes,
            vertices,
            indices,
            lights,
            &[],
            &[],
            camera,
            bounds,
            state_resource_index,
        )
    }
    /// Snapshot complete packed ranges and original material pages in one batch.
    /// Unreferenced trailing inputs are omitted because native section offsets
    /// derive from the maximum referenced ends, with no separate length words.
    pub fn draw_meshes_3d_with_materials(
        &mut self,
        meshes: &[wire::Mesh3d],
        vertices: &[wire::Mesh3dVertex],
        indices: &[u32],
        lights: &[wire::Light3d],
        materials: &[wire::Brush],
        gradient_stops: &[wire::GradientStop],
        camera: &wire::Camera3d,
        bounds: wire::ImageRect,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        let (mut used_vertices, mut used_indices, mut used_lights) = (0u64, 0u64, 0u64);
        for mesh in meshes {
            used_vertices =
                used_vertices.max(u64::from(mesh.vertex_offset) + u64::from(mesh.vertex_count));
            used_indices =
                used_indices.max(u64::from(mesh.index_offset) + u64::from(mesh.index_count));
            used_lights =
                used_lights.max(u64::from(mesh.light_offset) + u64::from(mesh.light_count));
        }
        if used_vertices > vertices.len() as u64
            || used_indices > indices.len() as u64
            || used_lights > lights.len() as u64
        {
            return Err(B::InvalidArgument);
        }
        let vertices = &vertices[..used_vertices as usize];
        let indices = &indices[..used_indices as usize];
        let lights = &lights[..used_lights as usize];
        let auxiliary_bytes = used_vertices * wire::Mesh3dVertex::WIRE_SIZE as u64
            + used_indices * 4
            + used_lights * wire::Light3d::WIRE_SIZE as u64;
        let create_brush_table = !materials.is_empty() && self.brush_resource_index == NO_INDEX;
        let needed_resources = 1 + usize::from(create_brush_table);
        if meshes.is_empty()
            || vertices.is_empty()
            || (!materials.is_empty() && materials.len() != meshes.len())
            || (materials.is_empty() && !gradient_stops.is_empty())
            || auxiliary_bytes > u64::from(MAX_STREAM_BYTES)
            || !finite_rect(bounds)
            || !self.valid_state_index(state_resource_index, false)
            || !values::camera_3d(camera)
            || needed_resources > MAX_RESOURCES as usize - self.resources.len()
            || self.commands.len() >= MAX_COMMANDS as usize
            || !vertices.iter().all(values::mesh_3d_vertex)
            || !lights.iter().all(values::light_3d)
        {
            return Err(B::InvalidArgument);
        }
        for mesh in meshes {
            if !values::mesh_3d(mesh, vertices.len(), indices.len(), lights.len()) {
                return Err(B::InvalidArgument);
            }
            if mesh.topology == MESH_3D_EDGE_LIST {
                for pair in vertices[mesh.vertex_offset as usize
                    ..mesh.vertex_offset as usize + mesh.vertex_count as usize]
                    .chunks_exact(2)
                {
                    if !values::mesh_3d_edge_pair(&pair[0], &pair[1]) {
                        return Err(B::InvalidArgument);
                    }
                }
            }
            if mesh.flags & MESH_3D_MATERIAL_IMAGE != 0 {
                let image = self
                    .resources
                    .get(mesh.material_image_resource_index as usize)
                    .ok_or(B::InvalidArgument)?;
                if image.record.kind != SCENE_RESOURCE_IMAGE
                    || image.record.flags & SCENE_EXTERNAL_IMAGE == 0
                {
                    return Err(B::InvalidArgument);
                }
            }
            if indices
                [mesh.index_offset as usize..mesh.index_offset as usize + mesh.index_count as usize]
                .iter()
                .any(|&i| i >= mesh.vertex_count)
            {
                return Err(B::InvalidArgument);
            }
        }
        for material in materials {
            if !valid_brush_records(material, gradient_stops)
                || !matches!(
                    material.kind,
                    SCENE_BRUSH_SOLID | SCENE_BRUSH_LINEAR_GRADIENT | SCENE_BRUSH_RADIAL_GRADIENT
                )
            {
                return Err(B::InvalidArgument);
            }
        }
        // Original observable failure policy: each successful add_brush stays
        // retained if a later material or final batch allocation fails. Only
        // the related mesh resource/command pair publishes together.
        let mut material_indices = Vec::new();
        reserve_total(&mut material_indices, materials.len())?;
        for material in materials {
            let count = stored_stop_count(material) as usize;
            let start = material.stop_offset as usize;
            let stops = gradient_stops
                .get(start..start + count)
                .ok_or(B::InvalidArgument)?;
            material_indices.push(self.add_brush(*material, stops)?);
        }
        reserve_append(&mut self.resources, 1)?;
        reserve_append(&mut self.commands, 1)?;
        let mut auxiliary = zeroed(auxiliary_bytes as usize)?;
        let vertex_bytes = vertices.len() * wire::Mesh3dVertex::WIRE_SIZE;
        let index_bytes = indices.len() * 4;
        write_records(vertices, &mut auxiliary[..vertex_bytes])?;
        write_records(
            indices,
            &mut auxiliary[vertex_bytes..vertex_bytes + index_bytes],
        )?;
        write_records(lights, &mut auxiliary[vertex_bytes + index_bytes..])?;
        let mut resource = self.new_resource(SCENE_RESOURCE_MESH_3D_BATCH);
        resource.payload = encode(meshes)?;
        resource.auxiliary = auxiliary;
        let mut command = self.new_command(
            SCENE_COMMAND_DRAW_MESH_3D_BATCH,
            bounds,
            state_resource_index,
            self.resources.len() as u32,
        );
        command.payload = encode(&[*camera])?;
        if !material_indices.is_empty() {
            append_encoded(
                &mut command.payload,
                &[wire::Mesh3dMaterials {
                    struct_size: wire::Mesh3dMaterials::WIRE_SIZE as u32,
                    brush_resource_index: self.brush_resource_index,
                    brush_count: material_indices.len() as u32,
                    reserved0: 0,
                }],
            )?;
            append_encoded(&mut command.payload, &material_indices)?;
        }
        self.resources.push(resource);
        self.commands.push(command);
        Ok(())
    }
}
