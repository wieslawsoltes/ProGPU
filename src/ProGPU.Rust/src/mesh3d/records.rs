//! Exact original shader records; all padding is explicit and initialized.
use super::*;
use bytemuck::{Pod, Zeroable};
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
#[repr(C)]
pub struct GpuVertex3D {
    pub position: [f32; 3],
    pub normal: [f32; 3],
    pub texture_coordinate: [f32; 2],
}
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
#[repr(C)]
pub struct GpuMesh3DRecord {
    pub model_transform: [[f32; 4]; 4],
    pub normal_transform: [[f32; 4]; 4],
    pub color: [f32; 4],
    pub light_direction: [f32; 4],
    pub ambient_color: [f32; 4],
    pub specular_color: [f32; 4],
    pub material_ambient: [f32; 4],
    pub opacity: f32,
    pub render_mode: f32,
    pub shading_mode: f32,
    pub texture_sampling_mode: f32,
    pub texture_effects0: [f32; 4],
    pub texture_effects1: [f32; 4],
    pub texture_info: [f32; 4],
    pub color_matrix_red: [f32; 4],
    pub color_matrix_green: [f32; 4],
    pub color_matrix_blue: [f32; 4],
    pub color_matrix_alpha: [f32; 4],
    pub color_matrix_offset: [f32; 4],
    pub texture_flags: [f32; 4],
    pub yuv_range: [f32; 4],
    pub yuv_red: [f32; 4],
    pub yuv_green: [f32; 4],
    pub yuv_blue: [f32; 4],
    pub texture_source_rect: [f32; 4],
    pub light_offset: u32,
    pub light_count: u32,
    pub light_padding: [u32; 2],
    pub material_gradient_points: [f32; 4],
    pub material_gradient_ellipse: [f32; 4],
    pub material_brush_transform0: [f32; 4],
    pub material_brush_transform1: [f32; 4],
    pub material_brush_metadata: [f32; 4],
    pub material_stop_metadata: [f32; 4],
}
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
#[repr(C)]
pub struct GpuLight3DRecord {
    pub metadata: [f32; 4],
    pub color: [f32; 4],
    pub position_range: [f32; 4],
    pub direction_inner_cos: [f32; 4],
    pub attenuation_outer_cos: [f32; 4],
}
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
#[repr(C)]
pub struct GpuMesh3DUniforms {
    pub projection: [[f32; 4]; 4],
    pub view: [[f32; 4]; 4],
    pub camera_position: [f32; 3],
    pub padding: f32,
    pub visible_edge_color: [f32; 4],
    pub occluded_edge_color: [f32; 4],
    pub edge_options0: [f32; 4],
    pub edge_options1: [f32; 4],
    pub edge_options2: [f32; 4],
}
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
#[repr(C)]
pub struct GpuMesh3DEdge {
    pub start: [f32; 4],
    pub end: [f32; 4],
    pub first_face_normal: [f32; 4],
    pub second_face_normal: [f32; 4],
    pub record_index: u32,
    pub topology: u32,
    pub reserved: [u32; 2],
}
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
#[repr(C)]
pub struct MeshGradientStop {
    pub color: [f32; 4],
    pub offset: f32,
    pub padding: [f32; 3],
}
/// Complete immutable CPU candidate for one actual viewport material generation.
#[derive(Clone, Debug, Default)]
pub struct Mesh3DRecords {
    pub records: Vec<GpuMesh3DRecord>,
    pub record_indices: Vec<u32>,
    pub lights: Vec<GpuLight3DRecord>,
    pub gradient_stops: Vec<MeshGradientStop>,
    pub edges: Vec<GpuMesh3DEdge>,
}
impl Mesh3DViewport {
    pub fn compile_records(
        &self,
        opacity: f32,
        materials: &[MeshMaterialState],
    ) -> Result<Mesh3DRecords, Error> {
        self.validate()?;
        if materials.len() != self.meshes.len() {
            return Err(Error::InvalidInput);
        }
        let mut result = Mesh3DRecords::default();
        result
            .records
            .try_reserve_exact(self.meshes.len())
            .map_err(|_| Error::Capacity)?;
        result
            .record_indices
            .try_reserve_exact(self.meshes.len())
            .map_err(|_| Error::Capacity)?;
        result
            .lights
            .try_reserve_exact(self.lights.len().max(1))
            .map_err(|_| Error::Capacity)?;
        for light in self.lights.iter() {
            result.lights.push(light.record()?);
        }
        if result.lights.is_empty() {
            result.lights.push(GpuLight3DRecord::default());
        }
        for (index, (mesh, state)) in self.meshes.iter().zip(materials).enumerate() {
            if state.size.contains(&0)
                || state.has_yuv && (!state.has_texture || mesh.yuv_conversion.is_none())
                || state.prepared_blur && (!state.has_texture || state.has_yuv)
            {
                return Err(Error::InvalidInput);
            }
            let mut record = material::record(self, mesh, *state, opacity);
            material::apply_brush(
                mesh.material_brush.as_deref(),
                mesh.material_brush_target,
                &mut record,
                &mut result.gradient_stops,
            )?;
            result.records.push(record);
            result.record_indices.push(index as u32);
            result
                .edges
                .try_reserve(mesh.edges.len())
                .map_err(|_| Error::Capacity)?;
            for edge in mesh.edges.iter() {
                result.edges.push(GpuMesh3DEdge {
                    start: extend(edge.start, 1.0),
                    end: extend(edge.end, 1.0),
                    first_face_normal: extend(edge.first_face_normal, 0.0),
                    second_face_normal: extend(edge.second_face_normal, 0.0),
                    record_index: index as u32,
                    topology: edge.topology as u32,
                    reserved: [0; 2],
                });
            }
        }
        if result.gradient_stops.is_empty() {
            result.gradient_stops.push(MeshGradientStop::default());
        }
        Ok(result)
    }
    /// Original independent perspective/view uniforms. Failed camera inversion
    /// retains source NaNs; normal matrices separately use identity on failure.
    pub fn uniforms(
        &self,
        projection: Matrix4,
        view: Matrix4,
        size: [u32; 2],
    ) -> Result<GpuMesh3DUniforms, Error> {
        self.edge_style.validate()?;
        let inverse = crate::image_filter::FilterTransform::new(view)
            .inverse_raw()
            .unwrap_or(Matrix4([[f32::NAN; 4]; 4]));
        let s = self.edge_style;
        Ok(GpuMesh3DUniforms {
            projection: projection.0,
            view: view.0,
            camera_position: [inverse.0[3][0], inverse.0[3][1], inverse.0[3][2]],
            padding: 0.0,
            visible_edge_color: s.visible_color,
            occluded_edge_color: s.occluded_color,
            edge_options0: [
                s.width,
                (s.crease_angle_degrees * std::f32::consts::PI / 180.0).cos(),
                s.occluded_dash_length,
                s.occluded_gap_length,
            ],
            edge_options1: [
                s.display.0 as f32,
                size[0] as f32,
                size[1] as f32,
                s.extension_length,
            ],
            edge_options2: [s.jitter_amount, 0.0, 0.0, 0.0],
        })
    }
}
pub(super) fn extend(v: [f32; 3], w: f32) -> [f32; 4] {
    [v[0], v[1], v[2], w]
}
