//! Original Viewport3D recursive model and front/back material producer.
use super::*;
use crate::picture_archive::{ArchiveBrush, ArchiveMaterialEnvironment};

/// Frozen original DiffuseMaterial properties, including independent source
/// brush opacity and scalar material color. The source owner is borrowed only
/// while snapshotting; entries retain all actual GPU/material dependencies.
#[derive(Clone, Debug)]
pub struct DiffuseMaterial3D {
    pub brush: Option<Arc<ArchiveBrush>>,
    pub brush_target: MaterialBrushTarget3D,
    pub color: [f32; 4],
    pub specular_color: [f32; 3],
    pub shininess: f32,
    pub ambient_color: [f32; 3],
    pub self_illumination: f32,
    #[cfg(feature = "gpu")]
    pub texture_source: Option<Arc<dyn MeshTextureSource>>,
    pub texture_effect: MeshTextureEffect,
    pub texture_sampling: crate::image::ImageSampling,
    pub texture_tiling: MeshTextureTilingMode,
    pub yuv_conversion: Option<crate::image_effect::YuvConversion>,
    pub texture_presentation: MeshTexturePresentation,
}
impl Default for DiffuseMaterial3D {
    fn default() -> Self {
        Self {
            brush: Some(Arc::new(ArchiveBrush::Vector(Brush::new(
                crate::vector::BrushKind::Solid([1.0; 4]),
            )))),
            brush_target: Default::default(),
            color: [1.0; 4],
            specular_color: [0.2; 3],
            shininess: 32.0,
            ambient_color: [0.2; 3],
            self_illumination: 0.0,
            #[cfg(feature = "gpu")]
            texture_source: None,
            texture_effect: Default::default(),
            texture_sampling: crate::image::ImageSampling::Linear,
            texture_tiling: Default::default(),
            yuv_conversion: None,
            texture_presentation: Default::default(),
        }
    }
}
/// Original unrecognized Material subtype remains distinct from absent material.
#[derive(Clone, Debug)]
pub enum MeshMaterial3D {
    Diffuse(Arc<DiffuseMaterial3D>),
    Other,
}
/// Original Model3D transform is inherited by children even without a drawable
/// GeometryModel3D. An absent mesh never becomes a fabricated triangle source.
#[derive(Clone, Debug, Default)]
pub struct MeshModel3D {
    pub transform: Matrix4,
    pub geometry: Option<MeshGeometry3D>,
    pub material: Option<MeshMaterial3D>,
    pub back_material: Option<MeshMaterial3D>,
}
/// Ordered source visual inventory. Other Visual3D subclasses do not traverse a
/// synthetic ModelVisual child list, matching the original typed branch.
#[derive(Clone, Debug)]
pub enum MeshVisual3D {
    Model {
        content: Option<MeshModel3D>,
        children: Arc<[Arc<MeshVisual3D>]>,
    },
    Other,
}
impl MeshVisual3D {
    pub fn model(content: MeshModel3D) -> Self {
        Self::Model {
            content: Some(content),
            children: Arc::from([]),
        }
    }
}
impl Mesh3DViewport {
    /// Compile actual immutable original scene sources in order, with explicit
    /// theme inputs. No texture producer is acquired, uploaded or encoded here.
    /// A complete private candidate is returned only after all source branches.
    pub fn compile_source(
        &self,
        roots: &[Arc<MeshVisual3D>],
        environment: Option<&ArchiveMaterialEnvironment>,
    ) -> Result<Self, Error> {
        let mut result = self.clone();
        let mut entries = Vec::new();
        let mut visited = 0u32;
        for visual in roots {
            compile(
                visual,
                Matrix4::IDENTITY,
                environment,
                &mut entries,
                &mut visited,
                0,
            )?;
        }
        result.meshes = entries.into();
        result.model_visual_visit_count = visited;
        result.scene_compilation_count = 1;
        result.scene_reused = false;
        result.validate()?;
        Ok(result)
    }
}
fn compile(
    visual: &MeshVisual3D,
    parent: Matrix4,
    environment: Option<&ArchiveMaterialEnvironment>,
    entries: &mut Vec<Mesh3DEntry>,
    visited: &mut u32,
    depth: u32,
) -> Result<(), Error> {
    if depth > 256 || *visited >= 1_000_000 {
        return Err(Error::Capacity);
    }
    *visited += 1;
    let MeshVisual3D::Model { content, children } = visual else {
        return Ok(());
    };
    let mut transform = parent;
    if let Some(content) = content {
        transform = content.transform * parent;
        if let Some(mesh) = &content.geometry
            && !mesh.positions().is_empty()
            && !mesh.indices().is_empty()
        {
            if content.material.is_some() || content.back_material.is_none() {
                let mut entry = material_entry(content.material.as_ref(), environment)?;
                entry.geometry = Some(mesh.clone());
                entry.edges = mesh.edges().clone();
                entry.model_transform = transform;
                entries.try_reserve(1).map_err(|_| Error::Capacity)?;
                entries.push(entry);
            }
            if let Some(MeshMaterial3D::Diffuse(back)) = &content.back_material
                && back.brush.is_some()
            {
                let mut entry = material_entry(content.back_material.as_ref(), environment)?;
                entry.geometry = Some(mesh.clone());
                entry.edges = Arc::from([]);
                entry.model_transform = transform;
                entry.is_back_face = true;
                entries.try_reserve(1).map_err(|_| Error::Capacity)?;
                entries.push(entry);
            }
        }
    }
    for child in children.iter() {
        compile(child, transform, environment, entries, visited, depth + 1)?;
    }
    Ok(())
}
fn material_entry(
    material: Option<&MeshMaterial3D>,
    environment: Option<&ArchiveMaterialEnvironment>,
) -> Result<Mesh3DEntry, Error> {
    let mut result = Mesh3DEntry::default();
    let Some(MeshMaterial3D::Diffuse(material)) = material else {
        return Ok(result);
    };
    let Some(brush) = &material.brush else {
        return Ok(result);
    };
    result.opacity = brush.opacity();
    result.specular_color = material.specular_color;
    result.shininess = material.shininess;
    result.ambient_color = material.ambient_color;
    result.self_illumination = material.self_illumination;
    result.material_brush_target = material.brush_target;
    let active = if matches!(brush.as_ref(), ArchiveBrush::ThemeResource { .. }) {
        environment
            .ok_or(Error::UnsupportedExecution)?
            .resolve(brush)
            .map_err(crate::picture_archive::drawing_error)?
    } else {
        Some(brush.clone())
    };
    if let Some(active) = active
        && let ArchiveBrush::Vector(active) = active.as_ref()
    {
        match &active.kind {
            crate::vector::BrushKind::Solid(color) => result.color = *color,
            crate::vector::BrushKind::Linear(_) | crate::vector::BrushKind::Radial(_) => {
                result.material_brush = Some(Arc::new(active.clone()));
                result.opacity = 1.0;
            }
            _ => {}
        }
    }
    result.color = (wide::f32x4::from(result.color) * wide::f32x4::from(material.color)).to_array();
    result.opacity *= result.color[3];
    result.color[3] = 1.0;
    #[cfg(feature = "gpu")]
    {
        result.texture_source = material.texture_source.clone();
    }
    result.texture_effect = material.texture_effect;
    result.texture_sampling = material.texture_sampling;
    result.texture_tiling = material.texture_tiling;
    result.yuv_conversion = material.yuv_conversion;
    result.texture_presentation = material.texture_presentation;
    Ok(result)
}
