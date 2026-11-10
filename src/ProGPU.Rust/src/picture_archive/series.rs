//! Original archive series side-buffer precedence and explicit source capture.
use super::*;
use crate::{
    series::{SeriesData, SeriesDraw, SeriesKind, SeriesLayout, SeriesShader},
    vector::{Brush, BrushKind, Matrix4},
};

pub(super) fn series_draw(
    picture: &ArchivePicture,
    command: &ArchiveCommand,
    transform: Matrix4,
    opacity: f32,
) -> Result<SeriesDraw> {
    let (kind, shader) = match command.kind {
        ArchiveCommandKind::DrawGpuLineSeries => (SeriesKind::Line, SeriesShader::BuiltIn),
        ArchiveCommandKind::DrawGpuScatterSeries => (SeriesKind::Scatter, SeriesShader::BuiltIn),
        ArchiveCommandKind::DrawExtension if command.extension_id == 5 => {
            (SeriesKind::Line, SeriesShader::Extension)
        }
        ArchiveCommandKind::DrawExtension if command.extension_id == 6 => {
            (SeriesKind::Scatter, SeriesShader::Extension)
        }
        _ => return Err(ArchiveError::Unsupported),
    };
    // Original provider always precedes the inline GpuPoints field, even when
    // this actual side-buffer slice is empty. No fabricated inline fallback.
    let selected = picture.floats(command.float_buffer_offset, command.float_buffer_count)?;
    let count = u32::try_from(command.gpu_points_count).map_err(|_| ArchiveError::InvalidData)?;
    let xy = (count as usize)
        .checked_mul(2)
        .ok_or(ArchiveError::Capacity)?;
    let layout = if kind == SeriesKind::Line || selected.len() == xy {
        SeriesLayout::Xy
    } else {
        SeriesLayout::XyRadius
    };
    if (count as usize)
        .checked_mul(layout.stride())
        .ok_or(ArchiveError::Capacity)?
        > selected.len()
    {
        return Err(ArchiveError::InvalidData);
    }
    let source = SeriesData::slice(
        picture.floats.clone(),
        command.float_buffer_offset as usize,
        count,
        layout,
    )?;
    let mut draw = SeriesDraw::new(kind, source);
    draw.shader = shader;
    draw.transform = transform;
    draw.scale = command.scale;
    draw.translate = command.translate;
    draw.radius_or_width = command.radius_x;
    draw.semantic_opacity = opacity;
    draw.brush = command.brush.as_ref().map(|brush| {
        Arc::new(match brush.as_ref() {
            ArchiveBrush::Vector(value) => value.clone(),
            other => {
                let mut value = Brush::new(BrushKind::Solid([1.0; 4]));
                value.opacity = other.opacity();
                value
            }
        })
    });
    draw.validate()?;
    Ok(draw)
}
pub(super) fn capture_series(draw: &SeriesDraw) -> Result<Arc<ArchivePicture>> {
    draw.validate()?;
    let data = draw.source.as_data().ok_or(ArchiveError::Unsupported)?;
    // Packing is a non-wire execution preference. Preserve only source defaults.
    if draw.packing != crate::path::ExecutionPreference::Automatic {
        return Err(ArchiveError::Unsupported);
    }
    let mut commands = super::reserve(3)?;
    if draw.semantic_opacity != 1.0 {
        commands.push(ArchiveCommand {
            kind: ArchiveCommandKind::PushOpacity,
            font_size: draw.semantic_opacity,
            ..Default::default()
        });
    }
    let (kind, extension_id) = match (draw.kind, draw.shader) {
        (SeriesKind::Line, SeriesShader::BuiltIn) => (ArchiveCommandKind::DrawGpuLineSeries, 0),
        (SeriesKind::Scatter, SeriesShader::BuiltIn) => {
            (ArchiveCommandKind::DrawGpuScatterSeries, 0)
        }
        (SeriesKind::Line, SeriesShader::Extension) => (ArchiveCommandKind::DrawExtension, 5),
        (SeriesKind::Scatter, SeriesShader::Extension) => (ArchiveCommandKind::DrawExtension, 6),
    };
    commands.push(ArchiveCommand {
        kind,
        extension_id,
        brush: draw
            .brush
            .as_ref()
            .map(|b| Arc::new(ArchiveBrush::Vector((**b).clone()))),
        transform: draw.transform,
        scale: draw.scale,
        translate: draw.translate,
        radius_x: draw.radius_or_width,
        gpu_points_count: i32::try_from(data.point_count()).map_err(|_| ArchiveError::Capacity)?,
        float_buffer_offset: i32::try_from(data.offset()).map_err(|_| ArchiveError::Capacity)?,
        float_buffer_count: i32::try_from(data.values().len())
            .map_err(|_| ArchiveError::Capacity)?,
        ..Default::default()
    });
    if draw.semantic_opacity != 1.0 {
        commands.push(ArchiveCommand {
            kind: ArchiveCommandKind::PopOpacity,
            ..Default::default()
        });
    }
    Ok(Arc::new(ArchivePicture {
        floats: data.owner().clone(),
        commands: commands.into(),
        ..Default::default()
    }))
}
