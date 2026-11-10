//! Original retained Mesh3D geometry, material and viewport compilation inputs.
//! Static owners and source texture leases remain independent of archive bytes.
use crate::{
    Error,
    vector::{Brush, Matrix4},
};
use std::sync::Arc;
mod camera;
pub use camera::{
    Camera, CameraControlUpdate, CameraMatrices, CameraNavigation, CameraNavigationKey,
    CameraProjectionError, OrthographicCamera, PerspectiveCamera, ProjectionCamera,
};
mod geometry;
mod lighting;
mod material;
mod producer;
pub use producer::{DiffuseMaterial3D, MeshMaterial3D, MeshModel3D, MeshVisual3D};
mod records;
pub use geometry::{MeshGeometry3D, MeshNormalExecution};
pub use lighting::{Light3D, LightKind3D};
pub use material::{
    MaterialBrushTarget3D, MeshMaterialState, MeshTextureEffect, MeshTexturePresentation,
    MeshTextureTilingMode,
};
pub use records::*;
#[cfg(feature = "gpu")]
mod texture;
#[cfg(feature = "gpu")]
pub use texture::{
    MeshTextureLease, MeshTextureLeaseOwner, MeshTextureMetadata, MeshTexturePlaneMetadata,
    MeshTextureRequest, MeshTextureSource,
};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::*;

/// Original primary raster mode; SolidWireframe selects its own wireframe shader.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum RenderMode3D {
    #[default]
    Solid = 0,
    Wireframe = 1,
    SolidWireframe = 2,
}
/// Original complete shader shading inventory.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum ShadingMode3D {
    #[default]
    Realistic = 0,
    Conceptual = 1,
    Flat = 2,
    HiddenLine = 3,
    ShadesOfGray = 4,
    XRay = 5,
    Normals = 6,
}
/// Original independent visible/occluded edge flags; unknown bits reject.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct MeshEdgeDisplay(pub u32);
impl MeshEdgeDisplay {
    pub const NONE: Self = Self(0);
    pub const BOUNDARY: Self = Self(1);
    pub const CREASE: Self = Self(2);
    pub const SILHOUETTE: Self = Self(4);
    pub const OCCLUDED: Self = Self(8);
    pub fn contains(self, flags: Self) -> bool {
        self.0 & flags.0 == flags.0
    }
}
impl std::ops::BitOr for MeshEdgeDisplay {
    type Output = Self;
    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}
/// Exact source edge style and independent physical-pixel modifiers.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MeshEdgeStyle {
    pub display: MeshEdgeDisplay,
    pub visible_color: [f32; 4],
    pub occluded_color: [f32; 4],
    pub width: f32,
    pub crease_angle_degrees: f32,
    pub occluded_dash_length: f32,
    pub occluded_gap_length: f32,
    pub extension_length: f32,
    pub jitter_amount: f32,
}
impl Default for MeshEdgeStyle {
    fn default() -> Self {
        Self {
            display: MeshEdgeDisplay::NONE,
            visible_color: [0.85, 0.85, 0.9, 1.0],
            occluded_color: [0.45, 0.45, 0.5, 0.7],
            width: 1.0,
            crease_angle_degrees: 30.0,
            occluded_dash_length: 6.0,
            occluded_gap_length: 4.0,
            extension_length: 0.0,
            jitter_amount: 0.0,
        }
    }
}
impl MeshEdgeStyle {
    pub fn validate(self) -> Result<(), Error> {
        let bounded = |v: f32, lo: f32, hi: f32| v.is_finite() && v >= lo && v <= hi;
        if self.display.0 & !15 != 0
            || self
                .visible_color
                .iter()
                .chain(&self.occluded_color)
                .any(|v| !bounded(*v, 0.0, 1.0))
            || !bounded(self.width, 0.0, 64.0)
            || self.width == 0.0
            || !bounded(self.crease_angle_degrees, 0.0, 180.0)
            || !self.occluded_dash_length.is_finite()
            || self.occluded_dash_length <= 0.0
            || !self.occluded_gap_length.is_finite()
            || self.occluded_gap_length < 0.0
            || !bounded(self.extension_length, 0.0, 64.0)
            || !bounded(self.jitter_amount, 0.0, 16.0)
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum MeshEdgeTopology {
    #[default]
    Manifold = 0,
    Boundary = 1,
    NonManifold = 2,
}
/// Actual original mesh edge inventory, never inferred from a bounding box.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct MeshEdge3D {
    pub start: [f32; 3],
    pub end: [f32; 3],
    pub first_face_normal: [f32; 3],
    pub second_face_normal: [f32; 3],
    pub topology: MeshEdgeTopology,
}
impl MeshEdge3D {
    pub fn validate(self) -> Result<(), Error> {
        if self
            .start
            .iter()
            .chain(&self.end)
            .chain(&self.first_face_normal)
            .chain(&self.second_face_normal)
            .any(|v| !v.is_finite())
        {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
}
/// Complete frozen original mesh record inputs. Null geometry still owns its
/// material/edge slots but does not emit a triangle draw.
#[derive(Clone, Debug)]
pub struct Mesh3DEntry {
    pub geometry: Option<MeshGeometry3D>,
    pub edges: Arc<[MeshEdge3D]>,
    #[cfg(feature = "gpu")]
    pub texture_source: Option<Arc<dyn MeshTextureSource>>,
    pub material_brush: Option<Arc<Brush>>,
    pub material_brush_target: MaterialBrushTarget3D,
    pub texture_effect: MeshTextureEffect,
    pub texture_sampling: crate::image::ImageSampling,
    pub texture_tiling: MeshTextureTilingMode,
    pub yuv_conversion: Option<crate::image_effect::YuvConversion>,
    pub texture_presentation: MeshTexturePresentation,
    pub model_transform: Matrix4,
    pub color: [f32; 4],
    pub specular_color: [f32; 3],
    pub shininess: f32,
    pub ambient_color: [f32; 3],
    pub self_illumination: f32,
    pub opacity: f32,
    pub is_back_face: bool,
    pub shading_mode_override: Option<ShadingMode3D>,
}
impl Default for Mesh3DEntry {
    fn default() -> Self {
        Self {
            geometry: None,
            edges: Arc::from([]),
            #[cfg(feature = "gpu")]
            texture_source: None,
            material_brush: None,
            material_brush_target: Default::default(),
            texture_effect: Default::default(),
            texture_sampling: crate::image::ImageSampling::Linear,
            texture_tiling: Default::default(),
            yuv_conversion: None,
            texture_presentation: Default::default(),
            model_transform: Matrix4::IDENTITY,
            color: [1.0; 4],
            specular_color: [0.2; 3],
            shininess: 32.0,
            ambient_color: [0.2; 3],
            self_illumination: 0.0,
            opacity: 1.0,
            is_back_face: false,
            shading_mode_override: None,
        }
    }
}
impl Mesh3DEntry {
    /// Original mesh producer retains computed normals and the actual edge list.
    pub fn new(geometry: MeshGeometry3D) -> Self {
        Self {
            edges: geometry.edges().clone(),
            geometry: Some(geometry),
            ..Self::default()
        }
    }
}
/// Immutable-at-publication original viewport generation. Generation zero retains
/// dynamic upload policy; nonzero generations never stand in for a source owner.
#[derive(Clone, Debug)]
pub struct Mesh3DViewport {
    pub meshes: Arc<[Mesh3DEntry]>,
    pub lights: Arc<[Light3D]>,
    pub scene_generation: u64,
    pub record_generation: u64,
    pub scene_reused: bool,
    pub scene_compilation_count: u32,
    pub model_visual_visit_count: u32,
    pub viewport_size: [f32; 2],
    pub light_direction: [f32; 3],
    pub light_intensity: f32,
    pub ambient_color: [f32; 3],
    pub ambient_intensity: f32,
    pub render_mode: RenderMode3D,
    pub shading_mode: ShadingMode3D,
    pub edge_style: MeshEdgeStyle,
}
impl Default for Mesh3DViewport {
    fn default() -> Self {
        Self {
            meshes: Arc::from([]),
            lights: Arc::from([]),
            scene_generation: 0,
            record_generation: 0,
            scene_reused: false,
            scene_compilation_count: 0,
            model_visual_visit_count: 0,
            viewport_size: [400.0, 300.0],
            light_direction: [0.5, 1.0, -0.5],
            light_intensity: 1.0,
            ambient_color: [1.0; 3],
            ambient_intensity: 0.2,
            render_mode: Default::default(),
            shading_mode: Default::default(),
            edge_style: Default::default(),
        }
    }
}
impl Mesh3DViewport {
    /// Whole original CPU admission before any output or source texture acquisition.
    pub fn validate(&self) -> Result<(), Error> {
        self.edge_style.validate()?;
        if self.meshes.len() > i32::MAX as usize || self.lights.len() > 16 {
            return Err(Error::Capacity);
        }
        for light in self.lights.iter() {
            light.record()?;
        }
        let mut count = 0usize;
        let mut stops = 0usize;
        for mesh in self.meshes.iter() {
            count = count.checked_add(mesh.edges.len()).ok_or(Error::Capacity)?;
            if count > i32::MAX as usize {
                return Err(Error::Capacity);
            }
            for edge in mesh.edges.iter() {
                edge.validate()?;
            }
            stops = stops
                .checked_add(material::validate_brush(
                    mesh.material_brush.as_deref(),
                    mesh.material_brush_target,
                )?)
                .ok_or(Error::Capacity)?;
            if stops > 65536 {
                return Err(Error::Capacity);
            }
            #[cfg(feature = "gpu")]
            if mesh.material_brush.is_some() && mesh.texture_source.is_some() {
                return Err(Error::UnsupportedExecution);
            }
        }
        Ok(())
    }
}
