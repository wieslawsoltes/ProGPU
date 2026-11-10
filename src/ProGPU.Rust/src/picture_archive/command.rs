//! Complete original RenderCommand transport, retaining every original field.
use super::{
    ArchiveBrush, ArchiveError, ArchivePath, ArchivePen, ArchivePicture, ArchiveTexturePatch,
    ArchiveVertexMesh, Result,
    geometry::{
        color_blend, read_path, read_texture_patches, read_vertex_mesh, write_path,
        write_texture_patches, write_vertex_mesh,
    },
    io::{Reader, Writer},
    material::{read_brush, read_pen, write_brush, write_pen},
};
use crate::{
    font::ManagedFont,
    text::paint::{GlyphCoverageTransfer, TextHintingMode, TextRenderingMode},
    vector::{Matrix4, VertexColorBlend},
};
use std::sync::Arc;

/// Complete original RenderCommandType inventory, including source-only records.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(i32)]
pub enum ArchiveCommandKind {
    #[default]
    DrawRect = 0,
    DrawPath = 1,
    DrawText = 2,
    DrawTexture = 3,
    PushClip = 4,
    PopClip = 5,
    PushOpacity = 6,
    PopOpacity = 7,
    DrawLine = 8,
    DrawEllipse = 9,
    DrawCircle = 10,
    DrawRoundedRect = 11,
    DrawBezier = 12,
    DrawCubicBezier = 13,
    DrawPolyline = 14,
    DrawSpline = 15,
    FillTriangle = 16,
    FillQuad = 17,
    DrawLine3D = 18,
    DrawHatch = 19,
    DrawAcisSolid = 20,
    DrawStaticDxf = 21,
    DrawGpuLineSeries = 22,
    DrawGpuScatterSeries = 23,
    DrawPicture = 24,
    DrawVisual = 25,
    DrawExtension = 26,
    PushGeometryClip = 27,
    PopGeometryClip = 28,
    PushOpacityMask = 29,
    PopOpacityMask = 30,
    PushBlendMode = 31,
    PopBlendMode = 32,
    DrawGlyphRun = 33,
    DrawVertexMesh = 34,
    DrawPointBatch = 35,
    DrawDotGrid = 36,
    DrawDeviceDotGrid = 37,
    DrawHintedGlyphs = 38,
}
impl TryFrom<i32> for ArchiveCommandKind {
    type Error = ArchiveError;
    fn try_from(value: i32) -> Result<Self> {
        match value {
            0 => Ok(Self::DrawRect),
            1 => Ok(Self::DrawPath),
            2 => Ok(Self::DrawText),
            3 => Ok(Self::DrawTexture),
            4 => Ok(Self::PushClip),
            5 => Ok(Self::PopClip),
            6 => Ok(Self::PushOpacity),
            7 => Ok(Self::PopOpacity),
            8 => Ok(Self::DrawLine),
            9 => Ok(Self::DrawEllipse),
            10 => Ok(Self::DrawCircle),
            11 => Ok(Self::DrawRoundedRect),
            12 => Ok(Self::DrawBezier),
            13 => Ok(Self::DrawCubicBezier),
            14 => Ok(Self::DrawPolyline),
            15 => Ok(Self::DrawSpline),
            16 => Ok(Self::FillTriangle),
            17 => Ok(Self::FillQuad),
            18 => Ok(Self::DrawLine3D),
            19 => Ok(Self::DrawHatch),
            20 => Ok(Self::DrawAcisSolid),
            21 => Ok(Self::DrawStaticDxf),
            22 => Ok(Self::DrawGpuLineSeries),
            23 => Ok(Self::DrawGpuScatterSeries),
            24 => Ok(Self::DrawPicture),
            25 => Ok(Self::DrawVisual),
            26 => Ok(Self::DrawExtension),
            27 => Ok(Self::PushGeometryClip),
            28 => Ok(Self::PopGeometryClip),
            29 => Ok(Self::PushOpacityMask),
            30 => Ok(Self::PopOpacityMask),
            31 => Ok(Self::PushBlendMode),
            32 => Ok(Self::PopBlendMode),
            33 => Ok(Self::DrawGlyphRun),
            34 => Ok(Self::DrawVertexMesh),
            35 => Ok(Self::DrawPointBatch),
            36 => Ok(Self::DrawDotGrid),
            37 => Ok(Self::DrawDeviceDotGrid),
            38 => Ok(Self::DrawHintedGlyphs),
            _ => Err(ArchiveError::InvalidData),
        }
    }
}

/// Exact original ten texture sampling modes; mip policy remains independent.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(i32)]
pub enum ArchiveTextureSampling {
    #[default]
    Linear = 0,
    Nearest = 1,
    Cubic = 2,
    LinearMipmap = 3,
    MagLinearMinLinearMipNearest = 4,
    MagLinearMinNearestMipLinear = 5,
    MagLinearMinNearestMipNearest = 6,
    MagNearestMinLinearMipLinear = 7,
    MagNearestMinLinearMipNearest = 8,
    MagNearestMinNearestMipLinear = 9,
}
impl TryFrom<i32> for ArchiveTextureSampling {
    type Error = ArchiveError;
    fn try_from(value: i32) -> Result<Self> {
        match value {
            0 => Ok(Self::Linear),
            1 => Ok(Self::Nearest),
            2 => Ok(Self::Cubic),
            3 => Ok(Self::LinearMipmap),
            4 => Ok(Self::MagLinearMinLinearMipNearest),
            5 => Ok(Self::MagLinearMinNearestMipLinear),
            6 => Ok(Self::MagLinearMinNearestMipNearest),
            7 => Ok(Self::MagNearestMinLinearMipLinear),
            8 => Ok(Self::MagNearestMinLinearMipNearest),
            9 => Ok(Self::MagNearestMinNearestMipLinear),
            _ => Err(ArchiveError::InvalidData),
        }
    }
}

/// Original raw retained command. Rectangles are x/y/width/height; the archive's
/// separate cull bounds are left/top/right/bottom. Public fields allow complete
/// source transport without silently dropping inactive metadata. Default values
/// match the zero-initialized original struct, including both zero matrices.
#[derive(Clone, Debug)]
pub struct ArchiveCommand {
    pub kind: ArchiveCommandKind,
    pub hit_test_id: i32,
    pub rect: [f32; 4],
    pub brush: Option<Arc<ArchiveBrush>>,
    pub pen: Option<Arc<ArchivePen>>,
    pub path: Option<Arc<ArchivePath>>,
    pub text: Option<String>,
    pub font: Option<ManagedFont>,
    pub font_size: f32,
    pub position: [f32; 2],
    pub is_bold: bool,
    pub is_italic: bool,
    pub font_transform: [f32; 2],
    pub has_font_transform: bool,
    pub rotation: f32,
    pub text_rendering_mode: TextRenderingMode,
    pub text_hinting_mode: TextHintingMode,
    pub use_vector_glyph_rendering: bool,
    pub src_rect: [f32; 4],
    pub texture_patches: Option<Arc<[ArchiveTexturePatch]>>,
    pub texture_sampling_mode: ArchiveTextureSampling,
    pub texture_max_anisotropy: u8,
    pub texture_cubic_coefficients: [f32; 2],
    pub has_texture_cubic_coefficients: bool,
    pub is_edge_aliased: bool,
    pub is_pen_thickness_local: bool,
    pub path_sample_grid: u32,
    pub path_coverage_gamma: f32,
    pub position2: [f32; 2],
    pub position3: [f32; 2],
    pub position4: [f32; 2],
    pub radius_x: f32,
    pub radius_y: f32,
    pub corner_radius: f32,
    pub polyline_points: Option<Arc<[[f32; 2]]>>,
    pub is_closed: bool,
    pub spline_knots: Option<Arc<[f64]>>,
    pub spline_weights: Option<Arc<[f64]>>,
    pub spline_degree: i32,
    pub position3d1: [f32; 3],
    pub position3d2: [f32; 3],
    pub edges3d: Option<Arc<[[[f32; 3]; 2]]>>,
    pub transform: Matrix4,
    pub gpu_points: Option<Arc<[f32]>>,
    pub gpu_points_count: i32,
    pub use_gpu_transforms: bool,
    pub camera_view: Matrix4,
    pub scale: [f32; 2],
    pub translate: [f32; 2],
    pub point_buffer_offset: i32,
    pub point_buffer_count: i32,
    pub double_buffer_offset: i32,
    pub double_buffer_count: i32,
    pub line3d_buffer_offset: i32,
    pub line3d_buffer_count: i32,
    pub weight_buffer_offset: i32,
    pub weight_buffer_count: i32,
    pub float_buffer_offset: i32,
    pub float_buffer_count: i32,
    pub picture: Option<Arc<ArchivePicture>>,
    pub glyph_indices: Option<Arc<[u16]>>,
    pub glyph_positions: Option<Arc<[[f32; 2]]>>,
    pub vertex_mesh: Option<Arc<ArchiveVertexMesh>>,
    pub vertex_color_blend_mode: VertexColorBlend,
    pub extension_id: i32,
    /// Actual original source-only DataParam owner. The source archive wire rejects it.
    #[cfg(feature = "gpu")]
    pub backdrop_material: Option<Arc<crate::backdrop::BackdropMaterialParameters>>,
    pub int_param: i32,
    pub float_param: f32,
    pub prefer_glyph_atlas: bool,
    pub use_logical_glyph_atlas_resolution: bool,
    pub glyph_coverage_transfer: GlyphCoverageTransfer,
}
impl Default for ArchiveCommand {
    fn default() -> Self {
        Self {
            kind: Default::default(),
            hit_test_id: Default::default(),
            rect: Default::default(),
            brush: Default::default(),
            pen: Default::default(),
            path: Default::default(),
            text: Default::default(),
            font: Default::default(),
            font_size: Default::default(),
            position: Default::default(),
            is_bold: Default::default(),
            is_italic: Default::default(),
            font_transform: Default::default(),
            has_font_transform: Default::default(),
            rotation: Default::default(),
            text_rendering_mode: Default::default(),
            text_hinting_mode: Default::default(),
            use_vector_glyph_rendering: Default::default(),
            src_rect: Default::default(),
            texture_patches: Default::default(),
            texture_sampling_mode: Default::default(),
            texture_max_anisotropy: Default::default(),
            texture_cubic_coefficients: Default::default(),
            has_texture_cubic_coefficients: Default::default(),
            is_edge_aliased: Default::default(),
            is_pen_thickness_local: Default::default(),
            path_sample_grid: Default::default(),
            path_coverage_gamma: Default::default(),
            position2: Default::default(),
            position3: Default::default(),
            position4: Default::default(),
            radius_x: Default::default(),
            radius_y: Default::default(),
            corner_radius: Default::default(),
            polyline_points: Default::default(),
            is_closed: Default::default(),
            spline_knots: Default::default(),
            spline_weights: Default::default(),
            spline_degree: Default::default(),
            position3d1: Default::default(),
            position3d2: Default::default(),
            edges3d: Default::default(),
            transform: Matrix4([[0.0; 4]; 4]),
            gpu_points: Default::default(),
            gpu_points_count: Default::default(),
            use_gpu_transforms: Default::default(),
            camera_view: Matrix4([[0.0; 4]; 4]),
            scale: Default::default(),
            translate: Default::default(),
            point_buffer_offset: Default::default(),
            point_buffer_count: Default::default(),
            double_buffer_offset: Default::default(),
            double_buffer_count: Default::default(),
            line3d_buffer_offset: Default::default(),
            line3d_buffer_count: Default::default(),
            weight_buffer_offset: Default::default(),
            weight_buffer_count: Default::default(),
            float_buffer_offset: Default::default(),
            float_buffer_count: Default::default(),
            picture: Default::default(),
            glyph_indices: Default::default(),
            glyph_positions: Default::default(),
            vertex_mesh: Default::default(),
            vertex_color_blend_mode: VertexColorBlend::Clear,
            extension_id: Default::default(),
            #[cfg(feature = "gpu")]
            backdrop_material: None,
            int_param: Default::default(),
            float_param: Default::default(),
            prefer_glyph_atlas: Default::default(),
            use_logical_glyph_atlas_resolution: Default::default(),
            glyph_coverage_transfer: Default::default(),
        }
    }
}
impl ArchiveCommand {
    fn validate_version(&self, version: i32) -> Result<()> {
        if version < 9 && self.text_hinting_mode == TextHintingMode::IntegerPixel {
            return Err(ArchiveError::Unsupported);
        }
        let transfer = self.glyph_coverage_transfer.encoding();
        if transfer != 0
            && (self.kind != ArchiveCommandKind::DrawGlyphRun
                || self.text_rendering_mode != TextRenderingMode::Grayscale)
        {
            return Err(ArchiveError::InvalidData);
        }
        if version < 10 && transfer != 0 {
            return Err(ArchiveError::Unsupported);
        }
        Ok(())
    }
    pub(super) fn read(reader: &mut Reader<'_>, level: u32) -> Result<Self> {
        let command = Self {
            kind: ArchiveCommandKind::try_from(reader.read::<i32>()?)?,
            hit_test_id: reader.read()?,
            rect: reader.read()?,
            brush: read_brush(reader)?.map(Arc::new),
            pen: read_pen(reader)?.map(Arc::new),
            path: read_path(reader, level)?.map(Arc::new),
            text: reader.string()?,
            font: read_font(reader)?,
            font_size: reader.read()?,
            position: reader.read()?,
            is_bold: reader.read()?,
            is_italic: reader.read()?,
            font_transform: reader.read()?,
            has_font_transform: reader.read()?,
            rotation: reader.read()?,
            text_rendering_mode: match reader.enumeration(2)? {
                0 => TextRenderingMode::Grayscale,
                1 => TextRenderingMode::Aliased,
                _ => TextRenderingMode::ClearType,
            },
            text_hinting_mode: match reader.enumeration(3)? {
                0 => TextHintingMode::Auto,
                1 => TextHintingMode::Fixed,
                2 => TextHintingMode::Animated,
                _ => TextHintingMode::IntegerPixel,
            },
            use_vector_glyph_rendering: reader.read()?,
            src_rect: reader.read()?,
            texture_patches: read_texture_patches(reader)?.map(Arc::from),
            texture_sampling_mode: ArchiveTextureSampling::try_from(reader.read::<i32>()?)?,
            texture_max_anisotropy: reader.read()?,
            texture_cubic_coefficients: reader.read()?,
            has_texture_cubic_coefficients: reader.read()?,
            is_edge_aliased: reader.read()?,
            is_pen_thickness_local: reader.read()?,
            path_sample_grid: reader.read()?,
            path_coverage_gamma: reader.read()?,
            position2: reader.read()?,
            position3: reader.read()?,
            position4: reader.read()?,
            radius_x: reader.read()?,
            radius_y: reader.read()?,
            corner_radius: reader.read()?,
            polyline_points: reader.nullable_array()?.map(Arc::from),
            is_closed: reader.read()?,
            spline_knots: reader.nullable_array()?.map(Arc::from),
            spline_weights: reader.nullable_array()?.map(Arc::from),
            spline_degree: reader.read()?,
            position3d1: reader.read()?,
            position3d2: reader.read()?,
            edges3d: reader.nullable_array()?.map(Arc::from),
            transform: reader.read()?,
            gpu_points: reader.nullable_array()?.map(Arc::from),
            gpu_points_count: reader.read()?,
            use_gpu_transforms: reader.read()?,
            camera_view: reader.read()?,
            scale: reader.read()?,
            translate: reader.read()?,
            point_buffer_offset: reader.read()?,
            point_buffer_count: reader.read()?,
            double_buffer_offset: reader.read()?,
            double_buffer_count: reader.read()?,
            line3d_buffer_offset: reader.read()?,
            line3d_buffer_count: reader.read()?,
            weight_buffer_offset: reader.read()?,
            weight_buffer_count: reader.read()?,
            float_buffer_offset: reader.read()?,
            float_buffer_count: reader.read()?,
            picture: if reader.read::<bool>()? {
                Some(Arc::new(ArchivePicture::read(reader, level + 1)?))
            } else {
                None
            },
            glyph_indices: reader.nullable_array()?.map(Arc::from),
            glyph_positions: reader.nullable_array()?.map(Arc::from),
            vertex_mesh: read_vertex_mesh(reader)?.map(Arc::new),
            vertex_color_blend_mode: color_blend(reader)?,
            extension_id: reader.read()?,
            #[cfg(feature = "gpu")]
            backdrop_material: None,
            int_param: reader.read()?,
            float_param: reader.read()?,
            prefer_glyph_atlas: if reader.version >= 9 {
                reader.read()?
            } else {
                false
            },
            use_logical_glyph_atlas_resolution: if reader.version >= 9 {
                reader.read()?
            } else {
                false
            },
            glyph_coverage_transfer: if reader.version >= 10 {
                read_transfer(reader)?
            } else {
                GlyphCoverageTransfer::default()
            },
        };
        command.validate_version(reader.version)?;
        Ok(command)
    }
    pub(super) fn write(&self, writer: &mut Writer, level: u32) -> Result<()> {
        self.validate_version(writer.version)?;
        #[cfg(feature = "gpu")]
        if self.backdrop_material.is_some() {
            return Err(ArchiveError::Unsupported);
        }
        writer.write(self.kind as i32)?;
        writer.write(self.hit_test_id)?;
        writer.write(self.rect)?;
        write_brush(writer, self.brush.as_deref())?;
        write_pen(writer, self.pen.as_deref())?;
        write_path(writer, self.path.as_deref(), level)?;
        writer.string(self.text.as_deref())?;
        write_font(writer, self.font.as_ref())?;
        writer.write(self.font_size)?;
        writer.write(self.position)?;
        writer.write(self.is_bold)?;
        writer.write(self.is_italic)?;
        writer.write(self.font_transform)?;
        writer.write(self.has_font_transform)?;
        writer.write(self.rotation)?;
        writer.write(self.text_rendering_mode as i32)?;
        writer.write(self.text_hinting_mode as i32)?;
        writer.write(self.use_vector_glyph_rendering)?;
        writer.write(self.src_rect)?;
        write_texture_patches(writer, self.texture_patches.as_deref())?;
        writer.write(self.texture_sampling_mode as i32)?;
        writer.write(self.texture_max_anisotropy)?;
        writer.write(self.texture_cubic_coefficients)?;
        writer.write(self.has_texture_cubic_coefficients)?;
        writer.write(self.is_edge_aliased)?;
        writer.write(self.is_pen_thickness_local)?;
        writer.write(self.path_sample_grid)?;
        writer.write(self.path_coverage_gamma)?;
        writer.write(self.position2)?;
        writer.write(self.position3)?;
        writer.write(self.position4)?;
        writer.write(self.radius_x)?;
        writer.write(self.radius_y)?;
        writer.write(self.corner_radius)?;
        writer.nullable_array(self.polyline_points.as_deref())?;
        writer.write(self.is_closed)?;
        writer.nullable_array(self.spline_knots.as_deref())?;
        writer.nullable_array(self.spline_weights.as_deref())?;
        writer.write(self.spline_degree)?;
        writer.write(self.position3d1)?;
        writer.write(self.position3d2)?;
        writer.nullable_array(self.edges3d.as_deref())?;
        writer.write(self.transform)?;
        writer.nullable_array(self.gpu_points.as_deref())?;
        writer.write(self.gpu_points_count)?;
        writer.write(self.use_gpu_transforms)?;
        writer.write(self.camera_view)?;
        writer.write(self.scale)?;
        writer.write(self.translate)?;
        writer.write(self.point_buffer_offset)?;
        writer.write(self.point_buffer_count)?;
        writer.write(self.double_buffer_offset)?;
        writer.write(self.double_buffer_count)?;
        writer.write(self.line3d_buffer_offset)?;
        writer.write(self.line3d_buffer_count)?;
        writer.write(self.weight_buffer_offset)?;
        writer.write(self.weight_buffer_count)?;
        writer.write(self.float_buffer_offset)?;
        writer.write(self.float_buffer_count)?;
        writer.write(self.picture.is_some())?;
        if let Some(picture) = &self.picture {
            picture.write(writer, level + 1)?;
        }
        writer.nullable_array(self.glyph_indices.as_deref())?;
        writer.nullable_array(self.glyph_positions.as_deref())?;
        write_vertex_mesh(writer, self.vertex_mesh.as_deref())?;
        writer.write(self.vertex_color_blend_mode as i32)?;
        writer.write(self.extension_id)?;
        writer.write(self.int_param)?;
        writer.write(self.float_param)?;
        if writer.version >= 9 {
            writer.write(self.prefer_glyph_atlas)?;
        }
        if writer.version >= 9 {
            writer.write(self.use_logical_glyph_atlas_resolution)?;
        }
        if writer.version >= 10 {
            writer.write(self.glyph_coverage_transfer.encoding())?;
        }
        Ok(())
    }
}
fn read_transfer(reader: &mut Reader<'_>) -> Result<GlyphCoverageTransfer> {
    let value = reader.read::<u32>()?;
    match value {
        0 => Ok(GlyphCoverageTransfer::default()),
        1..=13 => Ok(GlyphCoverageTransfer::grayscale_contrast(
            (value - 1) as u8,
        )?),
        _ => Err(ArchiveError::InvalidData),
    }
}
fn read_font(reader: &mut Reader<'_>) -> Result<Option<ManagedFont>> {
    if !reader.read::<bool>()? {
        return Ok(None);
    }
    let bytes = reader.array::<u8>()?;
    // Original ReadFont creates a new TtfFont from bytes with default face zero;
    // selected TTC indices, variation settings and palettes are not wire fields.
    Ok(Some(ManagedFont::new(Arc::<[u8]>::from(bytes), 0)?))
}
fn write_font(writer: &mut Writer, font: Option<&ManagedFont>) -> Result<()> {
    writer.write(font.is_some())?;
    if let Some(font) = font {
        writer.array(font.source().data())?;
    }
    Ok(())
}
