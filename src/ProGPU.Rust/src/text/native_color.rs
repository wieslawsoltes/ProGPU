//! Original managed native-scene color selection over native owned font queries.
//! C++ supplies query/retained-resource contracts, not an automatic raster codec.
//! Explicit decoding/preparation finishes before any Scene or GPU publication.
use crate::{
    Error,
    font::FontError,
    text::{
        native_design_geometry::{
            NativeDesignGeometry, NativeDesignGeometryError, NativeDesignGlyphSource,
            NativeDesignOutline,
        },
        native_font_provider::{NativeColorGlyphLayer, NativeColorPaletteOverride, NativeSvgGlyph},
    },
};
use std::{collections::HashMap, sync::Arc};
mod bitmap;
mod layers;
mod plan;
pub use bitmap::{NativeBitmapDecodeExecution, NativeDecodedColorBitmap};
pub use plan::{NativeColorReplay, NativeColorReplayStep, NativeColorVectorPath};
#[derive(Debug, thiserror::Error)]
pub enum NativeColorError {
    #[error(transparent)]
    Font(#[from] FontError),
    #[error(transparent)]
    Geometry(#[from] NativeDesignGeometryError),
    #[error(transparent)]
    Drawing(#[from] Error),
    /// Retained error value for callers with explicit codec capability admission.
    /// The default selected-strike decoder now returns an ordinary decode miss
    /// for unsupported TIFF, matching the original managed producer.
    #[error("selected color bitmap format {0:?} requires an explicit raster decoder")]
    ExternalBitmapDecoderRequired(crate::font::Tag),
}
type Result<T> = std::result::Result<T, NativeColorError>;
fn reserve<T>(count: usize) -> Result<Vec<T>> {
    let mut value = Vec::new();
    value
        .try_reserve_exact(count)
        .map_err(|_| Error::Capacity)?;
    Ok(value)
}
/// Explicit palette selection/overrides freeze into the resolver's generation.
/// Original native foreground entries remain white and keep their independent flag.
#[derive(Clone, Debug, Default)]
pub struct NativeColorPaletteSelection {
    pub palette_index: u16,
    pub overrides: Arc<[NativeColorPaletteOverride]>,
}
#[derive(Clone, Debug)]
pub struct NativeColrLayer {
    pub record: NativeColorGlyphLayer,
    pub outline: Arc<NativeDesignOutline>,
}
#[derive(Clone, Debug)]
pub enum NativeColorPresentation {
    /// Original nonempty COLR v0-prefix layer order, including no-ink layer slots.
    Colr(Arc<[NativeColrLayer]>),
    Svg(NativeSvgGlyph),
    Bitmap(NativeDecodedColorBitmap),
    /// No selected color presentation; actual outline/no-ink admission stays separate.
    Monochrome,
}
#[derive(Clone, Debug)]
pub struct NativeColorGlyph {
    source: Arc<NativeDesignGlyphSource>,
    target_pixels_per_em: f32,
    presentation: NativeColorPresentation,
}
impl NativeColorGlyph {
    pub fn source(&self) -> &Arc<NativeDesignGlyphSource> {
        &self.source
    }
    pub fn target_pixels_per_em(&self) -> f32 {
        self.target_pixels_per_em
    }
    pub fn presentation(&self) -> &NativeColorPresentation {
        &self.presentation
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
struct Key {
    bytes: usize,
    length: usize,
    face: u32,
    glyph: u16,
    coordinates: Arc<[i16]>,
    target: u32,
}
fn source_key(source: &NativeDesignGlyphSource, target: f32) -> Result<Key> {
    let font = source.font();
    let mut coordinates = reserve(source.normalized_coordinates().len())?;
    coordinates.extend_from_slice(source.normalized_coordinates());
    Ok(Key {
        bytes: font.data().as_ptr() as usize,
        length: font.data().len(),
        face: font.face_offset(),
        glyph: source.glyph(),
        coordinates: coordinates.into(),
        target: target.to_bits(),
    })
}
/// Bounded immutable-generation cache. Cache entries own selected source fonts,
/// decoded pixels and native outline generations, so pointer identity cannot be
/// recycled while an entry is live. Preparation never runs during GPU encoding.
pub struct NativeColorResolver {
    palette: NativeColorPaletteSelection,
    cache: HashMap<Key, Arc<NativeColorGlyph>>,
    paths: HashMap<layers::PathKey, (Arc<NativeColorGlyph>, crate::path::Path)>,
}
impl Default for NativeColorResolver {
    fn default() -> Self {
        Self::new(NativeColorPaletteSelection::default())
    }
}
impl NativeColorResolver {
    pub fn new(palette: NativeColorPaletteSelection) -> Self {
        Self {
            palette,
            cache: HashMap::new(),
            paths: HashMap::new(),
        }
    }
    pub fn palette(&self) -> &NativeColorPaletteSelection {
        &self.palette
    }
    pub fn cached_glyph_count(&self) -> usize {
        self.cache.len()
    }
    pub fn cached_path_count(&self) -> usize {
        self.paths.len()
    }
    pub fn clear(&mut self) {
        self.paths.clear();
        self.cache.clear();
    }
    /// Complete native lookup/decoded-image selection. Default PNG/JPEG results
    /// use their owned decoder routes; unsupported TIFF is a selected-source miss.
    /// Explicit external raster
    /// providers are not cached by a borrowed trait-object address: those results
    /// are owned only by the returned immutable generation/replay.
    pub fn resolve(
        &mut self,
        source: Arc<NativeDesignGlyphSource>,
        target_pixels_per_em: f32,
        decoder: Option<&dyn crate::image::EncodedRasterDecoder>,
    ) -> Result<Arc<NativeColorGlyph>> {
        if !target_pixels_per_em.is_finite() || target_pixels_per_em <= 0.0 {
            return Err(Error::InvalidInput.into());
        }
        let font = source.font();
        let key = source_key(&source, target_pixels_per_em)?;
        if decoder.is_none() {
            if let Some(value) = self.cache.get(&key) {
                return Ok(value.clone());
            }
        }
        let glyph = source.glyph();
        let colr = font.native_colr_layers(glyph).ok();
        let presentation = if let Some(records) = colr.filter(|v| !v.is_empty()) {
            let mut raw = reserve(usize::from(records.len()))?;
            raw.resize(usize::from(records.len()), NativeColorGlyphLayer::default());
            font.native_decode_colr_layers(
                glyph,
                self.palette.palette_index,
                &self.palette.overrides,
                &mut raw,
            )?;
            let mut output = reserve(raw.len())?;
            for record in raw {
                let outline = Arc::new(NativeDesignOutline::new(
                    font.clone(),
                    record.glyph_index,
                    source.normalized_coordinates(),
                )?);
                output.push(NativeColrLayer { record, outline });
            }
            NativeColorPresentation::Colr(output.into())
        } else if colr.is_none()
            && let Ok(svg) = font.native_svg_glyph_layers(glyph)
        {
            NativeColorPresentation::Svg(svg)
        } else if let Some(bitmap) = bitmap::decode(source.clone(), target_pixels_per_em, decoder)?
        {
            NativeColorPresentation::Bitmap(bitmap)
        } else {
            NativeColorPresentation::Monochrome
        };
        let result = Arc::new(NativeColorGlyph {
            source,
            target_pixels_per_em,
            presentation,
        });
        if decoder.is_none() {
            if self.cache.len() >= 4096 {
                self.cache.clear();
            }
            self.cache.try_reserve(1).map_err(|_| Error::Capacity)?;
            self.cache.insert(key, result.clone());
        }
        Ok(result)
    }
}
