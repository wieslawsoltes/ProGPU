//! Original mixed static-DXF vector producer and immutable execution generation.
use super::{
    ArchiveCommand, ArchiveCommandKind as K, ArchiveMaterialEnvironment, ArchivePicture,
    ArchiveReplayOptions,
};
use crate::{
    Error,
    geometry::Mesh,
    image::ImageSamplingPath,
    scene::{CompiledScene, DrawRange},
    vector::{MaterialTable, Matrix4},
};
use std::{ops::Range, sync::Arc};
mod clip;
mod compact;
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{
    PreparedStaticDxf, StaticDxfEngine, StaticDxfMetrics, StaticDxfTarget, UploadedStaticDxf,
};

/// Original compilation-time policy, independent of later static viewport
/// zoom/placement. Ordinary path coverage uses source DPI; static_zoom belongs
/// to text-derived path gamma and does not multiply ordinary path raster scale.
#[derive(Clone, Debug)]
pub struct StaticDxfCompileOptions {
    pub dpi_scale: f32,
    pub static_zoom: f32,
    pub offscreen: bool,
    pub materials: Option<Arc<ArchiveMaterialEnvironment>>,
}
impl Default for StaticDxfCompileOptions {
    fn default() -> Self {
        Self {
            dpi_scale: 1.0,
            static_zoom: 1.0,
            offscreen: false,
            materials: None,
        }
    }
}

/// Exact execution family after static compilation. Spline extension4 executes
/// its compiled vectors in DrawStaticDxfBuffer, unlike the ordinary no-op route.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StaticDxfBatchKind {
    Vector,
    Hatch,
}
#[derive(Clone, Debug)]
pub struct StaticDxfBatch {
    pub kind: StaticDxfBatchKind,
    pub indices: Range<u32>,
}

#[derive(Debug)]
struct Data {
    source: Arc<ArchivePicture>,
    options: StaticDxfCompileOptions,
    compiled: CompiledScene,
    batches: Vec<StaticDxfBatch>,
}

/// A complete static source generation with original geometry, path raster
/// requests, material arena and painter-ordered Vector/Hatch ranges.
#[derive(Clone, Debug)]
pub struct StaticDxfBuffer(Arc<Data>);
impl StaticDxfBuffer {
    /// Original DrawingContext/provider path. All accepted command fields and
    /// inactive side buffers remain owned independently from execution lowering.
    pub fn compile(
        source: Arc<ArchivePicture>,
        options: StaticDxfCompileOptions,
    ) -> Result<Self, Error> {
        Self::compile_source(source, options, true)
    }
    fn compile_source(
        source: Arc<ArchivePicture>,
        options: StaticDxfCompileOptions,
        provider: bool,
    ) -> Result<Self, Error> {
        if !options.dpi_scale.is_finite() || options.dpi_scale <= 0.0 {
            return Err(Error::InvalidInput);
        }
        u32::try_from(source.commands.len()).map_err(|_| Error::Capacity)?;
        // Whole-family admission precedes private geometry/atlas work.
        for c in source.commands.iter() {
            match c.kind {
                K::DrawRect
                | K::DrawRoundedRect
                | K::DrawEllipse
                | K::DrawCircle
                | K::FillTriangle
                | K::FillQuad
                | K::DrawPointBatch
                | K::DrawDotGrid
                | K::DrawDeviceDotGrid
                | K::DrawVertexMesh
                | K::DrawHatch
                | K::DrawLine
                | K::DrawBezier
                | K::DrawCubicBezier
                | K::DrawPolyline
                | K::DrawPath
                | K::DrawSpline
                | K::PushClip
                | K::PopClip
                | K::PushOpacity
                | K::PopOpacity
                | K::PushGeometryClip
                | K::PopGeometryClip
                | K::PushOpacityMask
                | K::PopOpacityMask
                | K::PushBlendMode
                | K::PopBlendMode => {}
                K::DrawExtension if matches!(c.extension_id, 3 | 4) => {}
                _ => return Err(Error::UnsupportedExecution),
            }
        }
        let mut scene = crate::scene::Scene::default();
        let mut clips = clip::Clips::default();
        let mut contexts = Vec::new();
        contexts
            .try_reserve_exact(source.commands.len())
            .map_err(|_| Error::Capacity)?;
        let mut group = 0u64;
        let mut replay = ArchiveReplayOptions::default();
        replay.materials = options.materials.clone();
        replay.text_context.dpi_scale = options.dpi_scale;
        replay.text_context.static_zoom = options.static_zoom;
        for source_command in source.commands.iter() {
            let mut command = source_command.clone();
            let transform = if command.transform == Matrix4([[0.0; 4]; 4]) {
                Matrix4::IDENTITY
            } else {
                command.transform
            };
            match command.kind {
                K::PushClip => {
                    clips.push(command.rect, transform)?;
                    continue;
                }
                K::PopClip => {
                    clips.pop();
                    continue;
                }
                K::PushOpacity
                | K::PopOpacity
                | K::PushGeometryClip
                | K::PopGeometryClip
                | K::PushOpacityMask
                | K::PopOpacityMask
                | K::PushBlendMode
                | K::PopBlendMode => continue,
                _ => {}
            }
            if command.kind == K::DrawVertexMesh && command.brush.is_none() {
                continue;
            }
            let extension = matches!(
                command.kind,
                K::DrawHatch | K::DrawSpline | K::DrawExtension
            );
            let gpu_active = command.use_gpu_transforms;
            // Original static compilation sets the active flag, but never
            // loads the command camera or performs ordinary brush rebasing.
            command.use_gpu_transforms = false;
            match command.kind {
                K::DrawHatch => {
                    command.kind = K::DrawExtension;
                    command.extension_id = 3;
                }
                K::DrawExtension if command.extension_id == 4 => command.kind = K::DrawSpline,
                _ => {}
            }
            if !provider {
                command.point_buffer_count = 0;
                command.double_buffer_count = 0;
                command.weight_buffer_count = 0;
            }
            let execution = source.with_commands(Arc::from([command]));
            let chunk = execution
                .to_scene_with(replay.clone())
                .map_err(super::drawing_error)?;
            let (chunk, marked) = chunk.resolve_static_dxf_chunk(
                options.dpi_scale,
                options.offscreen,
                clips.active(),
                gpu_active,
            )?;
            if extension {
                group = group.checked_add(1).ok_or(Error::Capacity)?;
            }
            // Private compiler barriers provide exact occurrence/range identity;
            // final batches merge ordinary vectors within the source flush group.
            scene.push_blend(crate::scene::LayerBlend::SrcOver)?;
            scene.append(&chunk)?;
            scene.pop_blend()?;
            contexts.push(compact::Context { marked, group });
            if extension {
                group = group.checked_add(1).ok_or(Error::Capacity)?;
            }
        }
        let mut compiled = scene.compile_for_target(
            options.dpi_scale,
            &mut MaterialTable::default(),
            ImageSamplingPath::NativeSampler,
            options.offscreen,
        )?;
        let batches = compact::finish(&mut compiled, &contexts)?;
        Ok(Self(Arc::new(Data {
            source,
            options,
            compiled,
            batches,
        })))
    }
    /// No-provider source overload. The original helper consumes inline point,
    /// knot and weight arrays; it does not invent a side-buffer provider.
    pub fn compile_commands(
        commands: Arc<[ArchiveCommand]>,
        options: StaticDxfCompileOptions,
    ) -> Result<Self, Error> {
        Self::compile_source(
            Arc::new(ArchivePicture {
                commands,
                ..Default::default()
            }),
            options,
            false,
        )
    }
    pub fn source(&self) -> &Arc<ArchivePicture> {
        &self.0.source
    }
    pub fn options(&self) -> &StaticDxfCompileOptions {
        &self.0.options
    }
    /// Original DxfStaticBuffer normalizes its retained bookkeeping zoom only
    /// after compilation; source gamma receives the original compile argument.
    pub fn static_zoom(&self) -> f32 {
        let zoom = self.0.options.static_zoom;
        if zoom.is_finite() && zoom > 0.0 {
            zoom
        } else {
            1.0
        }
    }
    pub fn mesh(&self) -> &Mesh {
        &self.0.compiled.mesh
    }
    pub fn materials(&self) -> &crate::vector::MaterialPage {
        self.0.compiled.materials()
    }
    pub fn batches(&self) -> &[StaticDxfBatch] {
        &self.0.batches
    }
    pub fn path_raster_count(&self) -> usize {
        self.0.compiled.path_raster_count()
    }
    pub fn atlas_size(&self) -> u32 {
        self.0.compiled.atlas_size()
    }
    pub fn is_empty(&self) -> bool {
        self.mesh().indices().is_empty()
    }
    pub fn same_generation(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}
