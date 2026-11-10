//! Actual retained paragraph/run to outline occurrence adaptation. Nonfont source
//! items stay explicit slots; a synthetic sign has no fabricated native run.
use super::*;
struct Item {
    record: NativeParagraphPositionedRecord,
    owner: NativeDesignPlacementOwner,
    font: Option<SfntFace>,
    coordinates: Arc<[i16]>,
    scale: f32,
    style: Option<u32>,
}
fn copy_coords(v: &[i16]) -> Result<Arc<[i16]>> {
    let mut out = reserved(v.len())?;
    out.extend_from_slice(v);
    Ok(out.into())
}
fn paragraph_items(
    source: &NativeDesignSource,
    records: &[NativeParagraphPositionedRecord],
    owners: impl Iterator<Item = Option<NativeDesignOccurrence>>,
    options: NativeDesignOptions,
) -> Result<Vec<Item>> {
    let mut output = reserved(records.len())?;
    let mut run_coordinates: HashMap<u32, Arc<[i16]>> = HashMap::new();
    for (&record, owner) in records.iter().zip(owners) {
        let (owner, font, coordinates, scale, style) = match owner {
            Some(NativeDesignOccurrence::Glyph {
                run_index,
                run_glyph_index,
            }) => {
                let run = source
                    .runs()
                    .get(run_index as usize)
                    .ok_or(FontError::InvalidArgument)?;
                let glyph = run
                    .generation
                    .glyphs()
                    .get(run_glyph_index as usize)
                    .ok_or(FontError::InvalidArgument)?;
                if glyph.glyph_id != record.glyph_id || run.font_index != record.font_index {
                    return Err(FontError::InvalidArgument.into());
                }
                let scale = if source.styles().is_empty() {
                    options.layout.scale
                } else {
                    source.glyph_scales()[record.glyph_index as usize]
                };
                let coordinates = if let Some(value) = run_coordinates.get(&run_index) {
                    value.clone()
                } else {
                    let value = copy_coords(run.generation.normalized_coordinates())?;
                    run_coordinates
                        .try_reserve(1)
                        .map_err(|_| FontError::Capacity)?;
                    run_coordinates.insert(run_index, value.clone());
                    value
                };
                (
                    NativeDesignPlacementOwner::Paragraph(owner.unwrap()),
                    Some(run.generation.font().clone()),
                    coordinates,
                    scale,
                    run.style_index,
                )
            }
            Some(
                item @ (NativeDesignOccurrence::Tab { .. }
                | NativeDesignOccurrence::InlineObject { .. }),
            ) => (
                NativeDesignPlacementOwner::Paragraph(item),
                None,
                Arc::from([]),
                0.0,
                None,
            ),
            None => {
                if record.glyph_index != u32::MAX || record.font_index != 0 {
                    return Err(FontError::InvalidArgument.into());
                }
                (
                    NativeDesignPlacementOwner::Ellipsis,
                    Some(
                        source
                            .fonts()
                            .first()
                            .ok_or(FontError::InvalidFace)?
                            .clone(),
                    ),
                    copy_coords(&source.shaping().normalized_coordinates)?,
                    options.layout.scale,
                    None,
                )
            }
        };
        output.push(Item {
            record,
            owner,
            font,
            coordinates,
            scale,
            style,
        });
    }
    if output.len() != records.len() {
        return Err(FontError::InvalidArgument.into());
    }
    Ok(output)
}
pub(super) fn build(
    source: NativeDesignGeometrySource,
    color_sources: bool,
) -> Result<Arc<NativeDesignGeometry>> {
    let items = match &source {
        NativeDesignGeometrySource::Paragraph(p) => paragraph_items(
            p.source(),
            p.glyphs(),
            p.owners().iter().copied(),
            p.options(),
        )?,
        NativeDesignGeometrySource::Fragments(p) => paragraph_items(
            p.source(),
            p.glyphs(),
            p.owners().iter().copied().map(Some),
            p.options(),
        )?,
        NativeDesignGeometrySource::Vertical(v) => {
            let mut items = reserved(v.glyphs().len())?;
            let coordinates = copy_coords(v.run().normalized_coordinates())?;
            for &g in v.glyphs() {
                let original = v
                    .run()
                    .glyphs()
                    .get(g.glyph_index as usize)
                    .ok_or(FontError::InvalidArgument)?;
                if original.glyph_id != g.glyph_id {
                    return Err(FontError::InvalidArgument.into());
                }
                items.push(Item {
                    record: NativeParagraphPositionedRecord {
                        glyph_index: g.glyph_index,
                        glyph_id: g.glyph_id,
                        font_index: 0,
                        cluster: g.cluster,
                        x: g.position[0],
                        y: g.position[1],
                        advance_x: g.advance[0],
                        advance_y: g.advance[1],
                    },
                    owner: NativeDesignPlacementOwner::Vertical {
                        run_glyph_index: g.glyph_index,
                    },
                    font: Some(v.run().font().clone()),
                    coordinates: coordinates.clone(),
                    scale: v.options().scale,
                    style: None,
                });
            }
            items
        }
    };
    if items.len() > u32::MAX as usize
        || items.iter().any(|i| {
            ![
                i.record.x,
                i.record.y,
                i.record.advance_x,
                i.record.advance_y,
            ]
            .into_iter()
            .all(f32::is_finite)
        })
    {
        return Err(FontError::InvalidArgument.into());
    }
    let mut result = NativeDesignGeometry {
        source,
        styles: Vec::new(),
        outlines: Vec::new(),
        slots: reserved(items.len())?,
        glyph_sources: reserved(items.len())?,
    };
    let mut cache = HashMap::new();
    let mut glyph_sources: HashMap<(u32, u16, Arc<[i16]>), Arc<NativeDesignGlyphSource>> =
        HashMap::new();
    let mut styles = HashMap::new();
    for (index, item) in items.into_iter().enumerate() {
        let record = item.record;
        let (style_index, outline_index) = if let Some(font) = item.font {
            if !item.scale.is_finite() || item.scale <= 0.0 {
                return Err(FontError::InvalidArgument.into());
            }
            let glyph = u16::try_from(record.glyph_id).map_err(|_| FontError::InvalidGlyph)?;
            let units = font
                .header_metrics()
                .filter(|h| h.units_per_em != 0)
                .ok_or(FontError::InvalidFace)?
                .units_per_em;
            let style_key = (record.font_index, item.style, item.scale.to_bits());
            let style = if let Some(&i) = styles.get(&style_key) {
                i
            } else {
                let i = result.styles.len() as u32;
                result
                    .styles
                    .try_reserve(1)
                    .map_err(|_| FontError::Capacity)?;
                styles.try_reserve(1).map_err(|_| FontError::Capacity)?;
                result.styles.push(NativeDesignGeometryStyle {
                    font_index: record.font_index,
                    source_style_index: item.style,
                    scale: item.scale,
                    units_per_em: units,
                });
                styles.insert(style_key, i);
                i
            };
            if glyph >= font.glyph_count().ok_or(FontError::InvalidFace)? {
                return Err(FontError::InvalidGlyph.into());
            }
            let key = (record.font_index, glyph, item.coordinates);
            let glyph_source = if let Some(source) = glyph_sources.get(&key) {
                source.clone()
            } else {
                let source = Arc::new(NativeDesignGlyphSource {
                    font: font.clone(),
                    glyph,
                    coordinates: key.2.clone(),
                });
                glyph_sources
                    .try_reserve(1)
                    .map_err(|_| FontError::Capacity)?;
                glyph_sources.insert(key.clone(), source.clone());
                source
            };
            result.glyph_sources.push(Some(glyph_source));
            let outline = if let Some(&i) = cache.get(&key) {
                Some(i)
            } else if color_sources
                && !((font.table(Tag::new(*b"glyf")).is_some()
                    && font.table(Tag::new(*b"loca")).is_some())
                    || font.table(Tag::new(*b"CFF ")).is_some()
                    || font.table(Tag::new(*b"CFF2")).is_some())
            {
                None
            } else {
                let outline = Arc::new(NativeDesignOutline::new(font, glyph, &key.2)?);
                let i = result.outlines.len() as u32;
                result
                    .outlines
                    .try_reserve(1)
                    .map_err(|_| FontError::Capacity)?;
                cache.try_reserve(1).map_err(|_| FontError::Capacity)?;
                result.outlines.push(outline);
                cache.insert(key, i);
                Some(i)
            };
            (Some(style), outline)
        } else {
            result.glyph_sources.push(None);
            (None, None)
        };
        result.slots.push(NativeDesignGeometrySlot {
            positioned_index: index as u32,
            source_glyph_index: record.glyph_index,
            glyph_id: record.glyph_id,
            font_index: record.font_index,
            style_index,
            outline_index,
            cluster: record.cluster,
            position: [record.x, record.y],
            advance: [record.advance_x, record.advance_y],
            owner: item.owner,
        });
    }
    Ok(Arc::new(result))
}
