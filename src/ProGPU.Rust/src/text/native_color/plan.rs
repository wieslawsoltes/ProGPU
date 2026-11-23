use super::*;
use crate::{
    geometry::Affine,
    path::Path,
    scene::{PathSampling, Scene},
    text::{
        native_atlas::{
            NativeAtlasFrame, NativeAtlasPaint, NativeColorGlyphBatch, NativeColorGlyphBitmap,
            NativeColorGlyphPlacement, NativeDesignAtlasDraw,
        },
        native_design_geometry::{
            NativeDesignOutlineKind, NativeDesignPaint, NativeDesignVectorDraw,
        },
        native_font_provider::NativeSvgGlyphDraw,
        paint::{TextPaintContext, TextPathPolicy, TextRenderingMode},
    },
    vector::{Brush, BrushKind, Matrix4},
};
#[derive(Clone, Debug)]
pub struct NativeColorVectorPath {
    pub path: Path,
    pub transform: Matrix4,
    pub brush: Arc<Brush>,
    pub semantic_opacity: f32,
    pub policy: TextPathPolicy,
    pub source_slot: u32,
    pub source: Arc<NativeColorGlyph>,
}
#[derive(Clone, Debug)]
pub enum NativeColorReplayStep {
    Atlas(NativeDesignAtlasDraw),
    Colr(NativeColorVectorPath),
    /// Native SVG layer contract keeps local geometry and the actual full affine
    /// layer placement; it is not rewritten through the managed DOM path parser.
    Svg {
        source: Arc<NativeColorGlyph>,
        draw: NativeSvgGlyphDraw,
    },
    Vector(NativeDesignVectorDraw),
}
#[derive(Debug)]
pub struct NativeColorReplay {
    geometry: Arc<NativeDesignGeometry>,
    dpi: f32,
    steps: Vec<NativeColorReplayStep>,
    sources: Vec<Arc<NativeColorGlyph>>,
    colors: Arc<NativeColorGlyphBatch>,
    owner_draw: NativeDesignAtlasDraw,
}
impl NativeColorReplay {
    pub fn geometry(&self) -> &Arc<NativeDesignGeometry> {
        &self.geometry
    }
    pub fn dpi_scale(&self) -> f32 {
        self.dpi
    }
    pub fn steps(&self) -> &[NativeColorReplayStep] {
        &self.steps
    }
    pub fn glyph_sources(&self) -> &[Arc<NativeColorGlyph>] {
        &self.sources
    }
    pub fn bitmap_batch(&self) -> &Arc<NativeColorGlyphBatch> {
        &self.colors
    }
    /// Original selection runs in source occurrence order. A selected COLR/SVG
    /// layer group interrupts atlas batching; bitmap and monochrome atlas slots
    /// preserve their common order, full source slots and actual frame identity.
    #[allow(clippy::too_many_arguments)]
    pub fn prepare(
        resolver: &mut NativeColorResolver,
        geometry: Arc<NativeDesignGeometry>,
        selected: &[u32],
        paints: Arc<[NativeAtlasPaint]>,
        position: [f32; 2],
        frame: NativeAtlasFrame,
        dpi: f32,
        decoder: Option<&dyn crate::image::EncodedRasterDecoder>,
    ) -> Result<Arc<Self>> {
        frame.validate()?;
        if !dpi.is_finite()
            || dpi <= 0.0
            || !position.into_iter().all(f32::is_finite)
            || paints.len() != geometry.styles().len()
            || selected.len() > 1 << 23
        {
            return Err(Error::InvalidInput.into());
        }
        let scale = frame.scale()?;
        for (paint, style) in paints.iter().zip(geometry.styles()) {
            if !paint.font_size.is_finite()
                || paint.font_size <= 0.0
                || !paint.color.into_iter().all(f32::is_finite)
                || (paint.font_size / f32::from(style.units_per_em)).to_bits()
                    != style.scale.to_bits()
            {
                return Err(Error::InvalidInput.into());
            }
            if paint.coverage_transfer.encoding() != 0
                && paint.rendering_mode != TextRenderingMode::Grayscale
            {
                return Err(Error::InvalidInput.into());
            }
        }
        for &slot in selected {
            let slot = geometry
                .slots()
                .get(slot as usize)
                .ok_or(Error::InvalidInput)?;
            if !slot
                .position
                .into_iter()
                .chain(slot.advance)
                .all(f32::is_finite)
            {
                return Err(Error::InvalidInput.into());
            }
            if slot.style_index.is_some_and(|v| v as usize >= paints.len()) {
                return Err(Error::InvalidInput.into());
            }
        }
        let affine = Affine(frame.transform.map(|v| v as f32));
        let context = TextPaintContext {
            transform: Matrix4::from_affine(affine),
            dpi_scale: dpi,
            semantic_opacity: frame.opacity,
            suppress_cached_cleartype: frame.suppress_cleartype,
            ..Default::default()
        };
        let mut steps = reserve(selected.len())?;
        let mut sources = reserve(selected.len())?;
        let mut pending: Vec<u32> = reserve(selected.len())?;
        let mut raw_steps: Vec<(usize, Vec<u32>)> = Vec::new();
        let mut records = Vec::new();
        let mut pixels = Vec::new();
        let mut placements = reserve(geometry.slots().len())?;
        placements.resize(geometry.slots().len(), None);
        let mut bitmap_indices = HashMap::new();
        // One source/provider invocation owns this complete preparation. Keep
        // repeated external-decoder results here without caching its borrowed
        // identity across calls or trusting a recyclable trait-object address.
        let mut prepared: HashMap<Key, Arc<NativeColorGlyph>> = HashMap::new();
        for &slot_index in selected {
            let slot = geometry.slots()[slot_index as usize];
            let Some(style) = slot.style_index else {
                continue;
            };
            let source = geometry
                .glyph_source(slot_index)
                .ok_or(Error::InvalidInput)?
                .clone();
            let paint = paints[style as usize];
            let target = (paint.font_size * dpi * scale).clamp(4.0, 128.0);
            let key = source_key(&source, target)?;
            let glyph = if let Some(glyph) = prepared.get(&key) {
                glyph.clone()
            } else {
                let glyph = resolver.resolve(source, target, decoder)?;
                prepared.try_reserve(1).map_err(|_| Error::Capacity)?;
                prepared.insert(key, glyph.clone());
                glyph
            };
            let origin = [
                slot.position[0] + position[0],
                slot.position[1] + position[1],
            ];
            if !origin.into_iter().all(f32::is_finite) {
                return Err(Error::InvalidInput.into());
            }
            let is_atlas = matches!(glyph.presentation(), NativeColorPresentation::Bitmap(_))
                || matches!(glyph.presentation(), NativeColorPresentation::Monochrome)
                    && slot.outline_index.is_some_and(|i| {
                        let o = &geometry.outlines()[i as usize];
                        o.kind() == NativeDesignOutlineKind::TrueType
                            && o.normalized_coordinates().iter().all(|&v| v == 0)
                    });
            if !is_atlas && !pending.is_empty() {
                raw_steps.try_reserve(1).map_err(|_| Error::Capacity)?;
                raw_steps.push((steps.len(), std::mem::take(&mut pending)));
            }
            match glyph.presentation() {
                NativeColorPresentation::Colr(_) => {
                    for path in layers::colr(
                        resolver,
                        &glyph,
                        slot_index,
                        paint,
                        geometry.styles()[style as usize].scale,
                        origin,
                        context,
                    )? {
                        steps.try_reserve(1).map_err(|_| Error::Capacity)?;
                        steps.push(NativeColorReplayStep::Colr(path));
                    }
                }
                NativeColorPresentation::Svg(svg) => {
                    let em = geometry.styles()[style as usize].scale;
                    let mut draw = NativeSvgGlyphDraw::new(svg.clone());
                    let italic = if paint.italic { 0.22 } else { 0.0 };
                    let local = Affine([em, 0.0, -italic * em, em, origin[0], origin[1]]);
                    let [a, b, c, d, x, y] = local.0;
                    let [e, f, g, h, u, v] = affine.0;
                    draw.transform = Affine([
                        a * e + b * g,
                        a * f + b * h,
                        c * e + d * g,
                        c * f + d * h,
                        x * e + y * g + u,
                        x * f + y * h + v,
                    ]);
                    draw.opacity = frame.opacity;
                    draw.sampling =
                        PathSampling::new(if paint.rendering_mode == TextRenderingMode::Aliased {
                            1
                        } else {
                            8
                        })?;
                    if paint.coverage_transfer.encoding() != 0 {
                        draw.sampling = draw.sampling.with_grayscale_contrast(
                            (paint.coverage_transfer.encoding() - 1) as u8,
                        )?;
                    }
                    steps.try_reserve(1).map_err(|_| Error::Capacity)?;
                    steps.push(NativeColorReplayStep::Svg {
                        source: glyph.clone(),
                        draw,
                    });
                }
                NativeColorPresentation::Bitmap(bitmap) => {
                    let key = Arc::as_ptr(&glyph) as usize;
                    let index = if let Some(&index) = bitmap_indices.get(&key) {
                        index
                    } else {
                        let index = u32::try_from(records.len()).map_err(|_| Error::Capacity)?;
                        let image = bitmap.image();
                        let size = image.size();
                        let record = NativeColorGlyphBitmap {
                            pixel_offset: pixels.len() as u64,
                            width: size[0],
                            height: size[1],
                            row_bytes: image.stride(),
                            bearing: bitmap.bearing,
                            render_size: bitmap.render_size,
                            ..Default::default()
                        };
                        pixels
                            .try_reserve(image.pixels().len())
                            .map_err(|_| Error::Capacity)?;
                        pixels.extend_from_slice(image.pixels());
                        records.try_reserve(1).map_err(|_| Error::Capacity)?;
                        records.push(record);
                        bitmap_indices.try_reserve(1).map_err(|_| Error::Capacity)?;
                        bitmap_indices.insert(key, index);
                        index
                    };
                    placements[slot_index as usize] = Some(NativeColorGlyphPlacement {
                        bitmap_index: index,
                        atlas_to_logical_scale: (paint.font_size * dpi / target)
                            * bitmap.raster_scale,
                    });
                    pending.try_reserve(1).map_err(|_| Error::Capacity)?;
                    pending.push(slot_index);
                }
                NativeColorPresentation::Monochrome if is_atlas => {
                    pending.try_reserve(1).map_err(|_| Error::Capacity)?;
                    pending.push(slot_index);
                }
                NativeColorPresentation::Monochrome => {
                    if slot.outline_index.is_none() {
                        return Err(Error::UnsupportedExecution.into());
                    }
                    let mut vector_paints = reserve(paints.len())?;
                    for paint in paints.iter() {
                        let mut value = NativeDesignPaint::new(
                            paint.font_size,
                            Some(Arc::new(Brush::new(BrushKind::Solid(paint.color)))),
                        );
                        value.bold = paint.bold;
                        value.italic = paint.italic;
                        value.rendering_mode = paint.rendering_mode;
                        value.coverage_transfer = paint.coverage_transfer;
                        vector_paints.push(value);
                    }
                    let draw = NativeDesignVectorDraw::selected(
                        geometry.clone(),
                        &[slot_index],
                        &vector_paints,
                        position,
                        0.0,
                        context,
                    )?;
                    steps.try_reserve(1).map_err(|_| Error::Capacity)?;
                    steps.push(NativeColorReplayStep::Vector(draw));
                }
            }
            sources.push(glyph);
        }
        if !pending.is_empty() {
            raw_steps.try_reserve(1).map_err(|_| Error::Capacity)?;
            raw_steps.push((steps.len(), pending));
        }
        let colors = NativeColorGlyphBatch::from_source_positions(
            geometry.clone(),
            dpi,
            records.into(),
            pixels.into(),
            placements.into(),
        )?;
        let mut ordered = reserve(
            steps
                .len()
                .checked_add(raw_steps.len())
                .ok_or(Error::Capacity)?,
        )?;
        let mut raw = raw_steps.into_iter().peekable();
        for (index, step) in steps
            .into_iter()
            .map(Some)
            .chain(std::iter::once(None))
            .enumerate()
        {
            while raw.peek().is_some_and(|v| v.0 == index) {
                let (_, selected) = raw.next().expect("peeked atlas run");
                ordered.push(NativeColorReplayStep::Atlas(
                    NativeDesignAtlasDraw::selected(
                        geometry.clone(),
                        selected.into(),
                        paints.clone(),
                        Some(colors.clone()),
                        position,
                        frame,
                    )?,
                ));
            }
            if let Some(step) = step {
                ordered.push(step);
            }
        }
        // An empty selected view retains the complete actual source generation
        // even when every color layer has no ink. It emits no atlas instances.
        let owner_draw = NativeDesignAtlasDraw::selected(
            geometry.clone(),
            Arc::from([]),
            paints.clone(),
            Some(colors.clone()),
            position,
            frame,
        )?;
        Ok(Arc::new(Self {
            geometry,
            dpi,
            steps: ordered,
            sources,
            colors,
            owner_draw,
        }))
    }
    /// Build/append a complete retained candidate. The frozen physical bitmap
    /// selection requires this exact DPI; another target needs a new prepare.
    pub fn record(&self, scene: &mut Scene, dpi: f32) -> Result<()> {
        if dpi.to_bits() != self.dpi.to_bits() {
            return Err(Error::InvalidInput.into());
        }
        let mut candidate = Scene::default();
        candidate.draw_native_design_atlas(self.owner_draw.clone())?;
        for step in &self.steps {
            match step {
                NativeColorReplayStep::Atlas(draw) => {
                    candidate.draw_native_design_atlas(draw.clone())?
                }
                NativeColorReplayStep::Colr(path) => {
                    candidate.draw_native_color_path(path.clone())?
                }
                NativeColorReplayStep::Svg { draw, .. } => draw.record(&mut candidate)?,
                NativeColorReplayStep::Vector(draw) => {
                    candidate.draw_native_design_vector(draw.clone())?
                }
            }
        }
        scene.append(&candidate)?;
        Ok(())
    }
}
