//! Original native ordinary glyph atlas policy, distinct from hinted captures
//! and explicit vector paths. Source positions and native font owners survive.
use crate::{
    Error,
    path::Segment,
    text::{
        glyph::GpuGlyphRecord,
        native_design_geometry::{
            NativeDesignGeometry, NativeDesignOutline, NativeDesignOutlineKind,
        },
        paint::{GlyphCoverageTransfer, GpuTextStyle, TextRenderingMode},
    },
};
use std::{collections::HashMap, sync::Arc};
mod bitmap;
mod placement;
mod plan;
#[cfg(feature = "gpu")]
mod resolve;
pub use bitmap::{NativeColorGlyphBatch, NativeColorGlyphBitmap, NativeColorGlyphPlacement};
pub use placement::{NativeAtlasFrame, NativeAtlasHinting};
pub use plan::{NativeAtlasOccurrence, NativeAtlasPlan, NativeAtlasRaster, NativeAtlasReference};
#[cfg(feature = "gpu")]
pub use resolve::ResolvedNativeAtlas;

/// Original independent em, solid text style and synthetic simulation policy.
#[derive(Clone, Copy, Debug)]
pub struct NativeAtlasPaint {
    pub font_size: f32,
    pub color: [f32; 4],
    pub bold: bool,
    pub italic: bool,
    pub rendering_mode: TextRenderingMode,
    pub coverage_transfer: GlyphCoverageTransfer,
}
impl NativeAtlasPaint {
    pub fn new(font_size: f32, color: [f32; 4]) -> Self {
        Self {
            font_size,
            color,
            bold: false,
            italic: false,
            rendering_mode: TextRenderingMode::Grayscale,
            coverage_transfer: GlyphCoverageTransfer::default(),
        }
    }
}

/// Immutable selected native paragraph/fragment/vertical occurrences. Color
/// records, when supplied, belong to this exact source generation and slots.
#[derive(Clone, Debug)]
pub struct NativeDesignAtlasDraw {
    geometry: Arc<NativeDesignGeometry>,
    selected: Arc<[u32]>,
    paints: Arc<[NativeAtlasPaint]>,
    colors: Option<Arc<NativeColorGlyphBatch>>,
    position: [f32; 2],
    frame: NativeAtlasFrame,
}
impl NativeDesignAtlasDraw {
    pub fn selected(
        geometry: Arc<NativeDesignGeometry>,
        selected: Arc<[u32]>,
        paints: Arc<[NativeAtlasPaint]>,
        colors: Option<Arc<NativeColorGlyphBatch>>,
        position: [f32; 2],
        frame: NativeAtlasFrame,
    ) -> Result<Self, Error> {
        let draw = Self {
            geometry,
            selected,
            paints,
            colors,
            position,
            frame,
        };
        draw.validate()?;
        Ok(draw)
    }
    pub fn all(
        geometry: Arc<NativeDesignGeometry>,
        paints: Arc<[NativeAtlasPaint]>,
        colors: Option<Arc<NativeColorGlyphBatch>>,
        position: [f32; 2],
        frame: NativeAtlasFrame,
    ) -> Result<Self, Error> {
        if geometry.slots().len() > u32::MAX as usize {
            return Err(Error::Capacity);
        }
        let mut selected = reserve(geometry.slots().len())?;
        selected.extend(0..geometry.slots().len() as u32);
        Self::selected(geometry, selected.into(), paints, colors, position, frame)
    }
    pub fn geometry(&self) -> &Arc<NativeDesignGeometry> {
        &self.geometry
    }
    pub fn selected_slots(&self) -> &[u32] {
        &self.selected
    }
    pub fn paints(&self) -> &[NativeAtlasPaint] {
        &self.paints
    }
    pub fn colors(&self) -> Option<&Arc<NativeColorGlyphBatch>> {
        self.colors.as_ref()
    }
    pub fn position(&self) -> [f32; 2] {
        self.position
    }
    pub fn frame(&self) -> NativeAtlasFrame {
        self.frame
    }
    pub fn with_frame(&self, frame: NativeAtlasFrame) -> Result<Self, Error> {
        let mut result = self.clone();
        result.frame = frame;
        result.validate()?;
        Ok(result)
    }
    /// Admission covers all selected slots, including no-ink records, and every
    /// original style. Static native MIL outline admission stays independent of
    /// the broader native CFF/gvar explicit vector route.
    pub fn validate(&self) -> Result<(), Error> {
        self.frame.validate()?;
        if !self.position.into_iter().all(f32::is_finite)
            || self.selected.len() > u32::MAX as usize / 2
            || self.paints.len() != self.geometry.styles().len()
            || self.paints.len() >= 1 << 24
            || self
                .colors
                .as_ref()
                .is_some_and(|c| !Arc::ptr_eq(c.source(), &self.geometry))
        {
            return Err(Error::InvalidInput);
        }
        for (p, s) in self.paints.iter().zip(self.geometry.styles()) {
            if !p.font_size.is_finite()
                || p.font_size <= 0.0
                || !p.color.into_iter().all(f32::is_finite)
                || (p.font_size / f32::from(s.units_per_em)).to_bits() != s.scale.to_bits()
                || p.coverage_transfer.encoding() != 0
                    && p.rendering_mode != TextRenderingMode::Grayscale
            {
                return Err(Error::InvalidInput);
            }
        }
        for &index in self.selected.iter() {
            let slot = self
                .geometry
                .slots()
                .get(index as usize)
                .ok_or(Error::InvalidInput)?;
            if !slot
                .position
                .into_iter()
                .chain(slot.advance)
                .all(f32::is_finite)
            {
                return Err(Error::InvalidInput);
            }
            if let Some(style) = slot.style_index {
                if style as usize >= self.paints.len() {
                    return Err(Error::InvalidInput);
                }
            }
            if self
                .colors
                .as_ref()
                .and_then(|c| c.bitmap_for_slot(index))
                .is_none()
                && let Some(outline) = slot.outline_index
            {
                let outline = self
                    .geometry
                    .outlines()
                    .get(outline as usize)
                    .ok_or(Error::InvalidInput)?;
                if outline.kind() != NativeDesignOutlineKind::TrueType
                    || outline.normalized_coordinates().iter().any(|&v| v != 0)
                {
                    return Err(Error::UnsupportedExecution);
                }
            }
        }
        Ok(())
    }
    /// Resolve original target em, native quarter phase and complete immutable
    /// descriptors before acquiring any atlas storage or changing renderer state.
    pub fn prepare(&self, dpi_scale: f32) -> Result<Arc<NativeAtlasPlan>, Error> {
        plan::prepare(self, dpi_scale)
    }
}
fn reserve<T>(count: usize) -> Result<Vec<T>, Error> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| Error::Capacity)?;
    Ok(values)
}
