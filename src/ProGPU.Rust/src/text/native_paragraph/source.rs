//! Original source-double paragraph generation, ideal-unit fitting and reflow.
//! Float records are only raster shadows of this generation's double authority.
use super::*;
mod fitting;
mod interaction;
mod policy;
mod transport;
mod writer;
pub use fitting::{NativeSourceFittedSlice, NativeSourceFitting};
pub use interaction::*;
pub use policy::*;
pub use transport::*;
#[derive(Debug, thiserror::Error)]
pub enum NativeSourceError {
    #[error("invalid original source paragraph metadata")]
    InvalidArgument,
    #[error("unsupported original source boundary or mapping")]
    Unsupported,
    #[error(transparent)]
    Font(#[from] FontError),
    #[error(transparent)]
    Geometry(#[from] NativeParagraphGeometryError),
    #[error(transparent)]
    Paragraph(#[from] NativeParagraphError),
}
fn require(value: bool) -> Result<(), NativeSourceError> {
    if value {
        Ok(())
    } else {
        Err(NativeSourceError::InvalidArgument)
    }
}
fn copy<T: Clone>(v: &[T]) -> Result<Vec<T>, NativeSourceError> {
    Ok(copied(v)?)
}
fn alloc<T: Clone>(n: usize, v: T) -> Result<Vec<T>, NativeSourceError> {
    Ok(filled(n, v)?)
}
fn reserve<T>(n: usize) -> Result<Vec<T>, NativeSourceError> {
    Ok(reserved(n)?)
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceGlyphMetrics {
    pub advance_x: f64,
    pub advance_y: f64,
    pub offset_x: f64,
    pub offset_y: f64,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceItemMetrics {
    pub ascent: f64,
    pub descent: f64,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceGlyphPosition {
    pub x: f64,
    pub y: f64,
    pub advance_x: f64,
    pub advance_y: f64,
    pub cluster: i32,
    pub reserved: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceLineMetrics {
    pub width: f64,
    pub top: f64,
    pub height: f64,
    pub baseline_offset: f64,
    pub baseline_y: f64,
    pub origin_x: f64,
    pub glyph_start: u32,
    pub glyph_count: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceFittedLine {
    pub glyph_start: u32,
    pub glyph_count: u32,
    pub width: f64,
    pub clipped: u32,
    pub reserved: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceStyle {
    pub em_size: f64,
    pub ascent: f64,
    pub descent: f64,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceIntrinsicWidths {
    pub minimum: f64,
    pub maximum: f64,
}
/// Explicit original source options; no source-host policy is inferred from DPI.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeSourceOptions {
    pub em_size: f64,
    pub pixels_per_dip: f64,
    pub maximum_width: f64,
    pub line_height: f64,
    pub tab_origin: f64,
    pub em_policy: NativeSourceEmPolicy,
    pub advance_policy: NativeSourceAdvancePolicy,
    pub offset_policy: NativeSourceOffsetPolicy,
    pub allow_emergency_break: bool,
    pub measure_intrinsic_widths: bool,
    pub preserve_whole_run_placement: bool,
    pub preserve_soft_wrap_space_geometry: bool,
}
impl NativeSourceOptions {
    pub fn policy(self, em_size: f64) -> NativeSourcePolicy {
        NativeSourcePolicy {
            em_size,
            pixels_per_dip: self.pixels_per_dip,
            em_policy: self.em_policy,
            advance_policy: self.advance_policy,
            offset_policy: self.offset_policy,
        }
    }
    fn validate(self) -> Result<(), NativeSourceError> {
        require(
            self.em_size.is_finite()
                && self.em_size > 0.0
                && self.pixels_per_dip.is_finite()
                && self.pixels_per_dip > 0.0
                && self.maximum_width.is_finite()
                && self.maximum_width >= 0.0
                && self.maximum_width <= f64::from(f32::MAX)
                && self.line_height.is_finite()
                && self.line_height >= 0.0
                && self.line_height <= f64::from(f32::MAX)
                && self.tab_origin.is_finite(),
        )
    }
}
/// Actual source generation and its independently owned width-specific placement.
#[derive(Debug)]
pub struct NativeSourceParagraph {
    raster: Arc<NativeHintedParagraph>,
    options: NativeSourceOptions,
    styles: Arc<[NativeSourceStyle]>,
    fitting: Arc<NativeSourceFitting>,
    item_metrics: Arc<[NativeSourceItemMetrics]>,
    glyphs: Vec<NativeSourceGlyphPosition>,
    lines: Vec<NativeSourceLineMetrics>,
    intrinsic: NativeSourceIntrinsicWidths,
    boxes: Vec<NativeSourceClusterBox>,
    carets: Vec<NativeSourceCaretStop>,
}
impl NativeSourceParagraph {
    /// Full native font-to-source paragraph route. Explicit old raster shadows
    /// must match the selected original source em/DPI/metric policies exactly.
    pub fn shape(
        fonts: &[SfntFace],
        input: &[UnicodeScalar],
        styles: &[NativeParagraphStyle],
        source_styles: &[NativeSourceStyle],
        shaping: NativeShapeRequest<'_>,
        layout: NativeTextLayoutOptions,
        options: NativeSourceOptions,
        projection: NativeHintedProjection,
        provider: &mut dyn NativeHintedProvider,
    ) -> Result<Arc<Self>, NativeSourceError> {
        options.validate()?;
        require(
            source_styles.len() == styles.len()
                && options.maximum_width as f32 == layout.maximum_width
                && options.line_height as f32 == layout.line_height
                && layout.alignment != NativeTextAlignment::Justify,
        )?;
        let mut policies = reserve(styles.len())?;
        for (style, source) in styles.iter().zip(source_styles) {
            let policy = options.policy(source.em_size);
            let selected = policy.resolve_device()?;
            let font = fonts
                .get(style.font_index as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
            let header = font
                .header_metrics()
                .ok_or(NativeSourceError::InvalidArgument)?;
            require(
                header.units_per_em != 0
                    && source.em_size as f32 / f32::from(header.units_per_em) == style.source_scale
                    && selected.pixels_per_em_26_6 == style.hinting.pixels_per_em_26_6[0]
                    && selected.pixels_per_em_26_6 == style.hinting.pixels_per_em_26_6[1]
                    && selected.logical_units_per_physical_pixel
                        == style.logical_units_per_physical_pixel
                    && source.ascent.is_finite()
                    && source.ascent >= 0.0
                    && source.descent.is_finite()
                    && source.descent >= 0.0
                    && source.ascent as f32 == style.metrics.ascent
                    && source.descent as f32 == style.metrics.descent,
            )?;
            policies.push(policy);
        }
        prepare::validate(fonts, input, styles, shaping, layout)?;
        let mut source =
            prepare::build(fonts, input, styles, shaping, layout, projection, provider)?;
        let mut items = reserve(source.logical.len())?;
        for i in 0..source.logical.len() {
            let owner = source.owners[i];
            let run = &source.runs[owner.run_index as usize];
            source.logical[i] = policies[run.style_index as usize]
                .wire(run.generation.glyphs()[owner.run_glyph_index as usize])?;
            let metric = source_styles[run.style_index as usize];
            items.push(NativeSourceItemMetrics {
                ascent: metric.ascent,
                descent: metric.descent,
            });
        }
        source.source_policies = Some(policies);
        let source = Arc::new(source);
        let styles: Arc<[NativeSourceStyle]> = copy(source_styles)?.into();
        let items: Arc<[NativeSourceItemMetrics]> = items.into();
        let intrinsic = if options.measure_intrinsic_widths {
            fitting::intrinsic(&source, options)?
        } else {
            NativeSourceIntrinsicWidths::default()
        };
        Self::place(source, styles, items, 0, layout, options, intrinsic)
    }
    fn place(
        source: Arc<NativeParagraphSource>,
        styles: Arc<[NativeSourceStyle]>,
        items: Arc<[NativeSourceItemMetrics]>,
        start: usize,
        mut layout: NativeTextLayoutOptions,
        options: NativeSourceOptions,
        intrinsic: NativeSourceIntrinsicWidths,
    ) -> Result<Arc<Self>, NativeSourceError> {
        layout.direction = if source.paragraph_level == 0 {
            GposDirection::LeftToRight
        } else {
            GposDirection::RightToLeft
        };
        let fitted = Arc::new(fitting::fit(&source, start, layout.maximum_lines, options)?);
        let written = writer::write(
            &source.logical[start..],
            &source.breaks[start..],
            &source.levels[start..],
            &source.scales[start..],
            source.paragraph_level,
            layout,
            options,
            &fitted.metrics[start..],
            &items[start..],
            &fitted.lines,
            fitted.width_policy,
        )?;
        let mut raster = NativeHintedParagraph {
            source,
            options: layout,
            logical_start: start,
            glyphs: written.raster_glyphs,
            lines: written.raster_lines,
            owners: Vec::new(),
            levels: written.levels,
            ends: Vec::new(),
            origins: written.origins,
            frames: written.frames,
            metrics: NativeTextLayoutMetrics::default(),
        };
        raster.owners = reserve(raster.glyphs.len())?;
        raster.ends = reserve(raster.glyphs.len())?;
        for g in &mut raster.glyphs {
            g.glyph_index += start as u32;
            let index = g.glyph_index as usize;
            raster.owners.push(raster.source.owners[index]);
            raster.ends.push(raster.source.ends[index]);
        }
        raster.metrics = native_measure_text_lines(&raster.lines, layout.maximum_width, true)?;
        let (boxes, carets) = interaction::build(
            &written.glyphs,
            &written.lines,
            &raster.ends,
            &raster.levels,
        )?;
        Ok(Arc::new(Self {
            raster: Arc::new(raster),
            options,
            styles,
            fitting: fitted,
            item_metrics: items,
            glyphs: written.glyphs,
            lines: written.lines,
            intrinsic,
            boxes,
            carets,
        }))
    }
    /// Retain the complete original source and actual capture; only source fitting
    /// and its bounded original placement recipes execute for the new measure.
    pub fn reflow(
        &self,
        input_start: i32,
        maximum_width: f64,
    ) -> Result<Arc<Self>, NativeSourceError> {
        require(
            input_start >= 0
                && self
                    .raster
                    .lines
                    .first()
                    .is_some_and(|l| input_start >= l.input_start),
        )?;
        let source = &self.raster.source;
        let start = source.logical.partition_point(|g| g.cluster < input_start);
        require(
            source
                .logical
                .get(start)
                .is_some_and(|g| g.cluster == input_start),
        )?;
        let mut options = self.options;
        options.maximum_width = maximum_width;
        options.validate()?;
        let mut layout = self.raster.options;
        layout.maximum_width = maximum_width as f32;
        let result = Self::place(
            source.clone(),
            self.styles.clone(),
            self.item_metrics.clone(),
            start,
            layout,
            options,
            self.intrinsic,
        )?;
        require(
            result
                .raster
                .lines
                .first()
                .is_some_and(|l| l.input_start == input_start),
        )?;
        Ok(result)
    }
    pub fn raster(&self) -> &Arc<NativeHintedParagraph> {
        &self.raster
    }
    pub fn options(&self) -> NativeSourceOptions {
        self.options
    }
    pub fn styles(&self) -> &[NativeSourceStyle] {
        &self.styles
    }
    pub fn fitting(&self) -> &Arc<NativeSourceFitting> {
        &self.fitting
    }
    pub fn item_metrics(&self) -> &[NativeSourceItemMetrics] {
        &self.item_metrics
    }
    pub fn glyphs(&self) -> &[NativeSourceGlyphPosition] {
        &self.glyphs
    }
    pub fn lines(&self) -> &[NativeSourceLineMetrics] {
        &self.lines
    }
    pub fn interaction(&self) -> NativeSourceInteraction<'_> {
        NativeSourceInteraction {
            boxes: &self.boxes,
            carets: &self.carets,
            lines: &self.lines,
        }
    }
    /// Missing measurement is distinct from an explicitly measured empty width.
    pub fn intrinsic_widths(&self) -> Option<NativeSourceIntrinsicWidths> {
        self.options
            .measure_intrinsic_widths
            .then_some(self.intrinsic)
    }
}
