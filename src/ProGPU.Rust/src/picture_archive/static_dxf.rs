//! Original mixed static-DXF vector/text producer and immutable generation.
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
mod text;
pub use text::{StaticRetainedGlyphGeometry, StaticRetainedGlyphInstance};
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
    /// Actual Unicode/fallback generation for original DrawText layout.
    pub text: Option<Arc<super::ArchiveTextEnvironment>>,
}
impl Default for StaticDxfCompileOptions {
    fn default() -> Self {
        Self {
            dpi_scale: 1.0,
            static_zoom: 1.0,
            offscreen: false,
            materials: None,
            text: None,
        }
    }
}

/// Exact execution family after static compilation. Spline extension4 executes
/// its compiled vectors in DrawStaticDxfBuffer, unlike the ordinary no-op route.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StaticDxfBatchKind {
    Vector,
    Hatch,
    Text,
}
#[derive(Clone, Debug)]
pub struct StaticDxfBatch {
    pub kind: StaticDxfBatchKind,
    /// Vector/Hatch index range or Text instance range, in its original stream.
    pub indices: Range<u32>,
}

#[derive(Debug)]
struct Data {
    source: Arc<ArchivePicture>,
    options: StaticDxfCompileOptions,
    compiled: CompiledScene,
    batches: Vec<StaticDxfBatch>,
    retained_glyphs: StaticRetainedGlyphGeometry,
    #[cfg(feature = "gpu")]
    glyph_atlas: Option<Arc<text::FrozenAtlas>>,
}

/// A complete static source generation with original geometry, path raster
/// requests, materials, original Vector/Text/Hatch flush ranges and final retained
/// analytic glyph pass. Actual source owners survive independent target replay.
#[derive(Clone, Debug)]
pub struct StaticDxfBuffer(Arc<Data>);
impl StaticDxfBuffer {
    /// Original DrawingContext/provider path. All accepted command fields and
    /// inactive side buffers remain owned independently from execution lowering.
    /// Retained outline/color/vector text is synchronous. Commands requiring an
    /// actual glyph atlas use `compile_with_glyph_atlas` under the GPU feature.
    pub fn compile(
        source: Arc<ArchivePicture>,
        options: StaticDxfCompileOptions,
    ) -> Result<Self, Error> {
        Self::compile_source(source, options, true, None)
    }
    fn compile_source(
        source: Arc<ArchivePicture>,
        options: StaticDxfCompileOptions,
        provider: bool,
        prepared_text: Option<text::Compilation>,
    ) -> Result<Self, Error> {
        admit_source(&source, &options)?;
        let mut text = match prepared_text {
            Some(value) => value,
            None => text::compile(&source, &options)?,
        };
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
        for (command_index, source_command) in source.commands.iter().enumerate() {
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
            let chunk = if matches!(command.kind, K::DrawText | K::DrawGlyphRun) {
                text.chunks.remove(&command_index).unwrap_or_default()
            } else {
                let execution = source.with_commands(Arc::from([command]));
                execution
                    .to_scene_with(replay.clone())
                    .map_err(super::drawing_error)?
            };
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
            retained_glyphs: text.retained,
            #[cfg(feature = "gpu")]
            glyph_atlas: text.atlas,
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
            None,
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
            && self.0.retained_glyphs.is_empty()
            && self.text_instance_count() == 0
    }
    pub fn same_generation(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    pub(crate) fn identity(&self) -> usize {
        Arc::as_ptr(&self.0) as usize
    }
    pub fn retained_glyphs(&self) -> &StaticRetainedGlyphGeometry {
        &self.0.retained_glyphs
    }
    pub fn text_instance_count(&self) -> usize {
        #[cfg(feature = "gpu")]
        {
            self.0.compiled.glyphs.len()
        }
        #[cfg(not(feature = "gpu"))]
        {
            0
        }
    }
    /// Exact frozen legacy atlas instances after the source's final flag pass.
    #[cfg(feature = "gpu")]
    pub fn text_instances(&self) -> &[crate::text::paint::GlyphInstance] {
        &self.0.compiled.glyphs
    }
    #[cfg(feature = "gpu")]
    pub fn glyph_atlas_statistics(&self) -> Option<crate::text::glyph::GlyphAtlasStatistics> {
        self.0.glyph_atlas.as_ref().map(|v| v.statistics)
    }
    /// Actual glyph producer work already completed during explicit compilation.
    #[cfg(feature = "gpu")]
    pub fn glyph_producer_metrics(&self) -> Option<crate::text::glyph::GlyphGpuMetrics> {
        self.0.glyph_atlas.as_ref().map(|v| v.producer_metrics)
    }
    /// Exact live frame-uniform allocation for this source at one target.
    pub fn target_uniform_bytes(&self, masked: bool) -> u64 {
        224 + if masked
            && self
                .batches()
                .iter()
                .any(|b| b.kind == StaticDxfBatchKind::Vector && !b.indices.is_empty())
        {
            96
        } else {
            0
        } + if self.text_instance_count() != 0 || !self.retained_glyphs().is_empty() {
            96
        } else {
            0
        }
    }
    /// Complete static source compilation with an exclusively transferred actual
    /// glyph atlas. The buffer freezes its slots/textures for its whole lifetime;
    /// cancellation/error drops the private atlas instead of exposing half a batch.
    #[cfg(feature = "gpu")]
    pub async fn compile_with_glyph_atlas(
        source: Arc<ArchivePicture>,
        options: StaticDxfCompileOptions,
        atlas: crate::text::glyph::GlyphAtlas,
    ) -> Result<Self, Error> {
        admit_source(&source, &options)?;
        let text = text::compile_with_atlas(&source, &options, atlas).await?;
        Self::compile_source(source, options, true, Some(text))
    }
    /// The original inline-array overload with the same exclusive atlas contract.
    #[cfg(feature = "gpu")]
    pub async fn compile_commands_with_glyph_atlas(
        commands: Arc<[ArchiveCommand]>,
        options: StaticDxfCompileOptions,
        atlas: crate::text::glyph::GlyphAtlas,
    ) -> Result<Self, Error> {
        let source = Arc::new(ArchivePicture {
            commands,
            ..Default::default()
        });
        admit_source(&source, &options)?;
        let text = text::compile_with_atlas(&source, &options, atlas).await?;
        Self::compile_source(source, options, false, Some(text))
    }
}

fn admit_source(source: &ArchivePicture, options: &StaticDxfCompileOptions) -> Result<(), Error> {
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
            K::DrawText | K::DrawGlyphRun => {}
            K::DrawExtension if matches!(c.extension_id, 3 | 4) => {}
            _ => return Err(Error::UnsupportedExecution),
        }
    }
    Ok(())
}
