//! Native SVG layer records into the existing retained path/material route.
//! No XML parsing, font loading, source callback or CPU pixel work occurs in replay.
use super::*;
use crate::{
    Error,
    path::Path,
    scene::{MaterialPaint, PathDraw, PathSampling, Scene},
    vector::{
        Brush, BrushKind, GradientOptions, GradientSpread, GradientStop, LinearGradient,
        RadialGradient,
    },
};
/// Explicit placement of the original decoded native layer generation. This is
/// a glyph-coordinate draw; text shaping/baseline/italic policy remains with the
/// actual text producer rather than being inferred from this XML document.
#[derive(Clone, Debug)]
pub struct NativeSvgGlyphDraw {
    pub glyph: NativeSvgGlyph,
    pub transform: Affine,
    pub sampling: PathSampling,
    pub opacity: f32,
}
impl NativeSvgGlyphDraw {
    pub fn new(glyph: NativeSvgGlyph) -> Self {
        Self {
            glyph,
            transform: Affine::IDENTITY,
            sampling: PathSampling::default(),
            opacity: 1.0,
        }
    }
    /// Build the complete retained candidate before publishing any caller Scene
    /// command. Existing path/frame/material admission remains independent.
    pub fn scene(&self) -> std::result::Result<Scene, Error> {
        if !self.opacity.is_finite() {
            return Err(Error::InvalidInput);
        }
        let mut scene = Scene::default();
        let mut brushes = Vec::new();
        brushes
            .try_reserve_exact(self.glyph.brushes().len())
            .map_err(|_| Error::Capacity)?;
        for record in self.glyph.brushes() {
            brushes.push(Arc::new(material(*record, self.glyph.gradient_stops())?));
        }
        for layer in self.glyph.layers() {
            let end = layer
                .segment_offset
                .checked_add(layer.segment_count)
                .ok_or(Error::Capacity)?;
            let source = self
                .glyph
                .segments()
                .get(layer.segment_offset..end)
                .ok_or(Error::InvalidInput)?;
            let mut segments = Vec::new();
            segments
                .try_reserve_exact(source.len())
                .map_err(|_| Error::Capacity)?;
            segments.extend_from_slice(source);
            let mut path = Path::new(segments, layer.bounds, layer.fill_rule)?;
            path.retain_source_owner(self.glyph.inner.clone());
            let paint = MaterialPaint {
                brush: brushes
                    .get(layer.brush_index as usize)
                    .ok_or(Error::InvalidInput)?
                    .clone(),
                semantic_opacity: self.opacity,
            };
            scene.draw_material_path(
                PathDraw {
                    path,
                    transform: values::multiply(layer.transform, self.transform),
                    color: [1.0; 4],
                    sampling: self.sampling,
                },
                paint,
            )?;
        }
        Ok(scene)
    }
    /// Atomically append all source layers in their original painter order.
    pub fn record(&self, scene: &mut Scene) -> std::result::Result<(), Error> {
        scene.append(&self.scene()?)
    }
}
fn material(record: GpuBrush, records: &[GpuGradientStop]) -> std::result::Result<Brush, Error> {
    // Source SVG emits only these three kinds and an identity brush-coordinate
    // transform. Preserve its already-transformed points without applying the
    // layer transform to paint a second time.
    let kind = if record.kind == 0 {
        BrushKind::Solid(record.colors[0])
    } else {
        let start = record.stop_offset as usize;
        let end = start
            .checked_add(record.stop_count as usize)
            .ok_or(Error::Capacity)?;
        let records = records.get(start..end).ok_or(Error::InvalidInput)?;
        let mut stops = Vec::new();
        stops
            .try_reserve_exact(records.len())
            .map_err(|_| Error::Capacity)?;
        stops.extend(records.iter().map(|v| GradientStop {
            color: v.color,
            offset: v.offset,
        }));
        let options = GradientOptions {
            spread: match record.spread {
                0 => GradientSpread::Pad,
                1 => GradientSpread::Reflect,
                2 => GradientSpread::Repeat,
                _ => return Err(Error::InvalidInput),
            },
            ..Default::default()
        };
        match record.kind {
            1 => BrushKind::Linear(LinearGradient {
                start: record.start,
                end: record.end,
                stops,
                options,
            }),
            2 => BrushKind::Radial(RadialGradient {
                center: record.center,
                origin: record.start,
                radii: [record.radius, record.radius_y],
                stops,
                options,
            }),
            _ => return Err(Error::InvalidInput),
        }
    };
    Ok(Brush {
        opacity: record.opacity,
        kind,
    })
}
