//! Complete original picture brush inventory and versioned raw pen properties.
use super::{
    ArchiveError, MAX_ARRAY_ELEMENTS, Result,
    geometry::cap,
    io::{Reader, Writer},
};
use crate::{
    backdrop::{BackdropMaterialBrush, BackdropMaterialKind, BackdropMaterialSource},
    vector::*,
};
use std::sync::Arc;

/// Actual original constructors, including unresolved theme and backdrop source
/// policy. Archive reads do not resolve a theme key or capture a host backdrop.
#[derive(Clone, Debug, PartialEq)]
pub enum ArchiveBrush {
    Vector(Brush),
    ThemeResource { opacity: f32, key: String },
    BackdropMaterial(BackdropMaterialBrush),
}
impl ArchiveBrush {
    pub fn opacity(&self) -> f32 {
        match self {
            Self::Vector(b) => b.opacity,
            Self::ThemeResource { opacity, .. } => *opacity,
            Self::BackdropMaterial(b) => b.opacity,
        }
    }
    pub fn to_vector(&self) -> Result<Brush> {
        match self {
            Self::Vector(b) => Ok(b.clone()),
            _ => Err(ArchiveError::Unsupported),
        }
    }
}
impl From<Brush> for ArchiveBrush {
    fn from(value: Brush) -> Self {
        Self::Vector(value)
    }
}
impl From<BackdropMaterialBrush> for ArchiveBrush {
    fn from(value: BackdropMaterialBrush) -> Self {
        Self::BackdropMaterial(value)
    }
}
/// Property assignment semantics retain nonfinite raw miter/offset values and
/// distinguish null from present-empty dash inventories, as in the archive.
#[derive(Clone, Debug, PartialEq)]
pub struct ArchivePen {
    pub brush: Arc<ArchiveBrush>,
    pub options: PenOptions,
    pub dashes: Option<Arc<[f64]>>,
}
impl ArchivePen {
    pub fn from_pen(pen: &Pen) -> Self {
        Self {
            brush: Arc::new(ArchiveBrush::Vector((*pen.brush).clone())),
            options: pen.options,
            dashes: pen.dash_array().map(Arc::from),
        }
    }
    pub fn to_pen(&self) -> Result<Pen> {
        let mut pen = Pen::new(Arc::new(self.brush.to_vector()?));
        pen.options = self.options;
        pen.set_dash_array(self.dashes.as_deref())
            .map_err(ArchiveError::Drawing)?;
        Ok(pen)
    }
}
pub(super) fn write_brush(writer: &mut Writer, brush: Option<&ArchiveBrush>) -> Result<()> {
    let Some(brush) = brush else {
        return writer.write(0u8);
    };
    let tag = match brush {
        ArchiveBrush::ThemeResource { .. } => 9,
        ArchiveBrush::BackdropMaterial(_) => 10,
        ArchiveBrush::Vector(b) => match &b.kind {
            BrushKind::Solid(_) => 1,
            BrushKind::Linear(_) => 2,
            BrushKind::Radial(_) => 3,
            BrushKind::TwoPointConical(_) => 4,
            BrushKind::Sweep(s) if s.start_angle == 0.0 && s.end_angle == 360.0 => 5,
            BrushKind::Sweep(_) => 11,
            BrushKind::PerlinNoise(_) => 6,
            BrushKind::Hatch(_) => 7,
            BrushKind::CrossHatch(_) => 8,
            BrushKind::HatchSet(_) if writer.version >= 5 => 12,
            BrushKind::Tile(_) if writer.version >= 6 => 13,
            BrushKind::Path(_) if writer.version >= 6 => 14,
            _ => return Err(ArchiveError::Unsupported),
        },
    };
    writer.write(tag as u8)?;
    writer.write(brush.opacity())?;
    match brush {
        ArchiveBrush::ThemeResource { key, .. } => writer.string(Some(key)),
        ArchiveBrush::BackdropMaterial(b) => write_backdrop(writer, b),
        ArchiveBrush::Vector(b) => {
            match &b.kind {
                BrushKind::Solid(v) => writer.write(*v)?,
                BrushKind::Linear(g) => {
                    writer.write(g.start)?;
                    writer.write(g.end)?;
                    write_options(writer, g.options)?;
                    write_stops(writer, &g.stops)?;
                }
                BrushKind::Radial(g) => {
                    writer.write(g.center)?;
                    writer.write(g.origin)?;
                    writer.write(g.radii)?;
                    write_options(writer, g.options)?;
                    write_stops(writer, &g.stops)?;
                }
                BrushKind::TwoPointConical(g) => {
                    writer.write(g.start_center)?;
                    writer.write(g.start_radius)?;
                    writer.write(g.end_center)?;
                    writer.write(g.end_radius)?;
                    write_options(writer, g.options)?;
                    write_stops(writer, &g.stops)?;
                    writer.write(g.outside_color.is_some())?;
                    if let Some(color) = g.outside_color {
                        writer.write(color)?;
                    }
                }
                BrushKind::Sweep(g) => {
                    writer.write(g.center)?;
                    write_options(writer, g.options)?;
                    if tag == 11 {
                        writer.write(g.start_angle)?;
                        writer.write(g.end_angle)?;
                    }
                    write_stops(writer, &g.stops)?;
                }
                BrushKind::Path(g) => {
                    writer.array(g.boundary())?;
                    writer.array(g.surround_colors())?;
                    writer.write(g.center)?;
                    writer.write(g.center_color)?;
                    writer.write(g.focus_scales)?;
                    write_options(writer, g.options)?;
                    writer.write(g.uses_preset_colors())?;
                    if g.uses_preset_colors() {
                        write_stops(writer, g.preset_stops())?;
                    } else {
                        writer.count(g.blend_stops().len(), MAX_ARRAY_ELEMENTS)?;
                        for s in g.blend_stops() {
                            writer.write(s.factor)?;
                            writer.write(s.offset)?;
                        }
                    }
                }
                BrushKind::PerlinNoise(g) => {
                    writer.write(g.turbulence)?;
                    writer.write(g.base_frequency)?;
                    writer.write(g.octaves)?;
                    writer.write(g.seed)?;
                    writer.write(g.tile_size)?;
                    writer.write(g.transform)?;
                }
                BrushKind::Hatch(g) | BrushKind::CrossHatch(g) => {
                    writer.write(g.angle)?;
                    writer.write(g.spacing)?;
                    writer.write(g.thickness)?;
                    writer.write(g.color)?;
                    if writer.version >= 4 {
                        writer.write(g.transform)?;
                    }
                }
                BrushKind::HatchSet(g) => {
                    writer.write(g.thickness)?;
                    writer.write(g.color)?;
                    writer.write(g.transform)?;
                    writer.count(g.families().len(), MAX_ARRAY_ELEMENTS)?;
                    for f in g.families() {
                        writer.write(f.base)?;
                        writer.write(f.direction)?;
                        writer.write(f.tangent_shift)?;
                        writer.write(f.spacing)?;
                        writer.write(f.dash_offset)?;
                        writer.write(f.dash_count)?;
                        writer.write(f.dash_period)?;
                    }
                    writer.array(g.dashes())?;
                }
                BrushKind::Tile(g) => {
                    writer.write(g.pattern())?;
                    writer.write(g.foreground())?;
                    writer.write(g.background())?;
                }
            }
            Ok(())
        }
    }
}
pub(super) fn read_brush(reader: &mut Reader<'_>) -> Result<Option<ArchiveBrush>> {
    let tag = reader.read::<u8>()?;
    if tag == 0 {
        return Ok(None);
    }
    let opacity = reader.read()?;
    let kind = match tag {
        1 => BrushKind::Solid(reader.read()?),
        2 => {
            let start = reader.read()?;
            let end = reader.read()?;
            let options = read_options(reader)?;
            BrushKind::Linear(LinearGradient {
                start,
                end,
                options,
                stops: read_stops(reader)?,
            })
        }
        3 => {
            let center = reader.read()?;
            let origin = reader.read()?;
            let radii = reader.read()?;
            let options = read_options(reader)?;
            BrushKind::Radial(RadialGradient {
                center,
                origin,
                radii,
                options,
                stops: read_stops(reader)?,
            })
        }
        4 => {
            let start_center = reader.read()?;
            let start_radius = reader.read()?;
            let end_center = reader.read()?;
            let end_radius = reader.read()?;
            let options = read_options(reader)?;
            let stops = read_stops(reader)?;
            let outside_color = if reader.read::<bool>()? {
                Some(reader.read()?)
            } else {
                None
            };
            BrushKind::TwoPointConical(TwoPointConicalGradient {
                start_center,
                start_radius,
                end_center,
                end_radius,
                options,
                stops,
                outside_color,
            })
        }
        5 | 11 => {
            let center = reader.read()?;
            let options = read_options(reader)?;
            let (start_angle, end_angle) = if tag == 11 {
                (reader.read()?, reader.read()?)
            } else {
                (0.0, 360.0)
            };
            BrushKind::Sweep(SweepGradient {
                center,
                options,
                start_angle,
                end_angle,
                stops: read_stops(reader)?,
            })
        }
        6 => BrushKind::PerlinNoise(PerlinNoise {
            turbulence: reader.read()?,
            base_frequency: reader.read()?,
            octaves: reader.read()?,
            seed: reader.read()?,
            tile_size: reader.read()?,
            transform: reader.read()?,
        }),
        7 | 8 => {
            let mut hatch = HatchPattern::new(
                reader.read()?,
                reader.read()?,
                reader.read()?,
                reader.read()?,
            );
            if reader.version >= 4 {
                hatch.transform = reader.read()?;
            }
            if tag == 7 {
                BrushKind::Hatch(hatch)
            } else {
                BrushKind::CrossHatch(hatch)
            }
        }
        9 => {
            return Ok(Some(ArchiveBrush::ThemeResource {
                opacity,
                key: reader.string()?.ok_or(ArchiveError::InvalidData)?,
            }));
        }
        10 => {
            return Ok(Some(ArchiveBrush::BackdropMaterial(read_backdrop(
                reader, opacity,
            )?)));
        }
        12 if reader.version >= 5 => {
            let thickness = reader.read()?;
            let color = reader.read()?;
            let transform = reader.read()?;
            let count = reader.count(MAX_ARRAY_ELEMENTS)?;
            let mut families = reserve(count)?;
            for _ in 0..count {
                families.push(HatchLineFamily {
                    base: reader.read()?,
                    direction: reader.read()?,
                    tangent_shift: reader.read()?,
                    spacing: reader.read()?,
                    dash_offset: reader.read()?,
                    dash_count: reader.read()?,
                    dash_period: reader.read()?,
                });
            }
            let dashes: Vec<f32> = reader.array()?;
            let mut hatch = HatchPatternSet::new(&families, &dashes, thickness, color)
                .map_err(ArchiveError::Drawing)?;
            hatch.transform = transform;
            BrushKind::HatchSet(hatch)
        }
        13 if reader.version >= 6 => BrushKind::Tile(TilePattern::new(
            reader.read()?,
            reader.read()?,
            reader.read()?,
            [0.0; 2],
        )),
        14 if reader.version >= 6 => {
            let boundary: Vec<[f32; 2]> = reader.array()?;
            let surround: Vec<[f32; 4]> = reader.array()?;
            let center = reader.read()?;
            let center_color = reader.read()?;
            let focus_scales = reader.read()?;
            let options = read_options(reader)?;
            let (blends, presets) = if reader.read::<bool>()? {
                (Vec::new(), read_stops(reader)?)
            } else {
                let count = reader.count(MAX_ARRAY_ELEMENTS)?;
                let mut values = reserve(count)?;
                for _ in 0..count {
                    values.push(PathGradientBlendStop {
                        factor: reader.read()?,
                        offset: reader.read()?,
                    });
                }
                (values, Vec::new())
            };
            let mut path = PathGradient::new(
                &boundary,
                &surround,
                center,
                center_color,
                &blends,
                &presets,
            )
            .map_err(ArchiveError::Drawing)?;
            path.focus_scales = focus_scales;
            path.options = options;
            BrushKind::Path(path)
        }
        _ => return Err(ArchiveError::InvalidData),
    };
    Ok(Some(ArchiveBrush::Vector(Brush { opacity, kind })))
}
fn write_options(writer: &mut Writer, options: GradientOptions) -> Result<()> {
    writer.write(options.transform)?;
    writer.write(options.spread as i32)?;
    writer.write(options.interpolation as i32)
}
fn read_options(reader: &mut Reader<'_>) -> Result<GradientOptions> {
    Ok(GradientOptions {
        transform: reader.read()?,
        spread: match reader.enumeration(3)? {
            0 => GradientSpread::Pad,
            1 => GradientSpread::Reflect,
            2 => GradientSpread::Repeat,
            _ => GradientSpread::Decal,
        },
        interpolation: match reader.enumeration(1)? {
            0 => GradientInterpolation::SRgbLinear,
            _ => GradientInterpolation::ScRgbLinear,
        },
    })
}
fn write_stops(writer: &mut Writer, stops: &[GradientStop]) -> Result<()> {
    writer.count(stops.len(), MAX_ARRAY_ELEMENTS)?;
    for stop in stops {
        writer.write(stop.color)?;
        writer.write(stop.offset)?;
    }
    Ok(())
}
fn read_stops(reader: &mut Reader<'_>) -> Result<Vec<GradientStop>> {
    let count = reader.count(MAX_ARRAY_ELEMENTS)?;
    let mut values = reserve(count)?;
    for _ in 0..count {
        values.push(GradientStop {
            color: reader.read()?,
            offset: reader.read()?,
        });
    }
    Ok(values)
}
fn write_backdrop(writer: &mut Writer, b: &BackdropMaterialBrush) -> Result<()> {
    writer.write(b.kind as i32)?;
    writer.write(b.source as i32)?;
    writer.write(b.tint_color)?;
    writer.write(b.luminosity_color)?;
    writer.write(b.fallback_color)?;
    writer.write(b.noise_color)?;
    writer.write(b.tint_opacity)?;
    writer.write(b.luminosity_opacity)?;
    writer.write(b.material_opacity)?;
    writer.write(b.noise_opacity)?;
    writer.write(b.blur_radius)?;
    writer.write(b.saturation)?;
    writer.write(b.use_fallback)
}
fn read_backdrop(reader: &mut Reader<'_>, opacity: f32) -> Result<BackdropMaterialBrush> {
    Ok(BackdropMaterialBrush {
        kind: match reader.enumeration(4)? {
            0 => BackdropMaterialKind::Acrylic,
            1 => BackdropMaterialKind::Mica,
            2 => BackdropMaterialKind::Blur,
            3 => BackdropMaterialKind::Tint,
            _ => BackdropMaterialKind::Fallback,
        },
        source: match reader.enumeration(2)? {
            0 => BackdropMaterialSource::None,
            1 => BackdropMaterialSource::Texture,
            _ => BackdropMaterialSource::HostBackdrop,
        },
        tint_color: reader.read()?,
        luminosity_color: reader.read()?,
        fallback_color: reader.read()?,
        noise_color: reader.read()?,
        tint_opacity: reader.read()?,
        luminosity_opacity: reader.read()?,
        material_opacity: reader.read()?,
        noise_opacity: reader.read()?,
        blur_radius: reader.read()?,
        saturation: reader.read()?,
        use_fallback: reader.read()?,
        opacity,
    })
}
pub(super) fn write_pen(writer: &mut Writer, pen: Option<&ArchivePen>) -> Result<()> {
    writer.write(pen.is_some())?;
    let Some(pen) = pen else {
        return Ok(());
    };
    let p = pen.options;
    if writer.version < 7 && p.clip_miter_at_limit || writer.version < 8 && p.use_wpf_join_semantics
    {
        return Err(ArchiveError::Unsupported);
    }
    write_brush(writer, Some(&pen.brush))?;
    writer.write(p.thickness)?;
    writer.write(p.join as i32)?;
    writer.write(p.miter_limit)?;
    writer.write(p.start_cap as i32)?;
    writer.write(p.end_cap as i32)?;
    writer.write(p.dash_cap as i32)?;
    writer.nullable_array(pen.dashes.as_deref())?;
    writer.write(p.dash_offset)?;
    if writer.version >= 3 {
        writer.write(p.transform as i32)?;
    }
    if writer.version >= 7 {
        writer.write(p.clip_miter_at_limit)?;
    }
    if writer.version >= 8 {
        writer.write(p.use_wpf_join_semantics)?;
    }
    Ok(())
}
pub(super) fn read_pen(reader: &mut Reader<'_>) -> Result<Option<ArchivePen>> {
    if !reader.read::<bool>()? {
        return Ok(None);
    }
    let brush = Arc::new(read_brush(reader)?.ok_or(ArchiveError::InvalidData)?);
    let mut options = PenOptions {
        thickness: reader.read()?,
        join: match reader.enumeration(3)? {
            0 => PenLineJoin::Miter,
            1 => PenLineJoin::Bevel,
            2 => PenLineJoin::Round,
            _ => PenLineJoin::MiterOrBevel,
        },
        miter_limit: reader.read()?,
        start_cap: cap(reader)?,
        end_cap: cap(reader)?,
        dash_cap: cap(reader)?,
        ..Default::default()
    };
    let dashes = reader.nullable_array::<f64>()?.map(Arc::from);
    options.dash_offset = reader.read()?;
    if reader.version >= 3 {
        options.transform = match reader.enumeration(1)? {
            0 => PenStrokeTransform::Normal,
            _ => PenStrokeTransform::Fixed,
        };
    }
    if reader.version >= 7 {
        options.clip_miter_at_limit = reader.read()?;
    }
    if reader.version >= 8 {
        options.use_wpf_join_semantics = reader.read()?;
    }
    Ok(Some(ArchivePen {
        brush,
        options,
        dashes,
    }))
}
fn reserve<T>(count: usize) -> Result<Vec<T>> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| ArchiveError::Capacity)?;
    Ok(values)
}
