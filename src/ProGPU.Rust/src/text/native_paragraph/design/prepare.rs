//! Original paragraph_layout_core admission and full-paragraph native run execution.
use super::super::prepare::{
    map_breaks, substitute_by, valid_context, valid_features, valid_number, valid_tag,
};
use super::*;

pub(super) fn options(
    o: NativeDesignOptions,
    input_count: usize,
    inline: bool,
) -> Result<(), NativeParagraphError> {
    let l = o.layout;
    if !l.scale.is_finite()
        || l.scale <= 0.0
        || !l.maximum_width.is_finite()
        || l.maximum_width < 0.0
        || !l.line_height.is_finite()
        || l.line_height < 0.0
        || !l.ellipsis_advance.is_finite()
        || l.ellipsis_advance < 0.0
        || !l.collapse_width.is_finite()
        || l.collapse_width < -1.0
        || (l.collapse_width >= 0.0
            && (l.maximum_lines == 0 || l.trimming == NativeTextTrimming::None))
        || !matches!(
            l.direction,
            GposDirection::Unspecified | GposDirection::LeftToRight | GposDirection::RightToLeft
        )
        || !o.tabs.interval.is_finite()
        || o.tabs.interval < 0.0
        || !o.tabs.origin.is_finite()
        || (inline && l.trimming != NativeTextTrimming::None)
        || (o.measure_intrinsic_widths
            && (l.maximum_lines != 0 || l.trimming != NativeTextTrimming::None))
        || o.continuation_start.is_some_and(|i| {
            i < 0
                || o.measure_intrinsic_widths
                || ((l.maximum_lines != 0 || l.trimming != NativeTextTrimming::None)
                    && l.collapse_width < 0.0)
                || (input_count == 0 && i != 0)
        })
    {
        return Err(invalid(NativeParagraphStage::Shaping));
    }
    Ok(())
}
pub(super) fn validate(
    fonts: &[SfntFace],
    input: &[UnicodeScalar],
    styles: &[NativeDesignStyle],
    s: NativeShapeRequest<'_>,
    o: NativeDesignOptions,
    inline: Option<&NativeInlineFlow>,
) -> Result<(), NativeParagraphError> {
    let stage = NativeParagraphStage::Shaping;
    options(o, input.len(), inline.is_some())?;
    if fonts.is_empty()
        || fonts.len() > u32::MAX as usize
        || input.len() > i32::MAX as usize
        || styles.len() > input.len()
        || !valid_context(input)
        || !valid_context(s.pre_context)
        || !valid_context(s.post_context)
        || !valid_features(s.features)
        || !valid_tag(s.unicode_script)
        || !valid_tag(s.language)
        || s.flags.0 & 12 == 12
        || s.normalized_coordinates.len() > u32::MAX as usize
        || !matches!(
            s.direction,
            GposDirection::Unspecified | GposDirection::LeftToRight | GposDirection::RightToLeft
        )
    {
        return Err(invalid(stage));
    }
    let mut previous = 0u64;
    for scalar in input {
        let end = u64::from(scalar.input_index) + u64::from(scalar.input_length);
        if u64::from(scalar.input_index) < previous || end > i32::MAX as u64 {
            return Err(invalid(stage));
        }
        previous = end;
    }
    let mut expected = 0;
    for style in styles {
        let n = style.scalar_count as usize;
        if style.scalar_start as usize != expected
            || n == 0
            || n > input.len() - expected
            || style.font_index as usize >= fonts.len()
            || !style.scale.is_finite()
            || style.scale <= 0.0
            || !valid_number(style.number_substitution)
            || !valid_features(&style.features)
            || !valid_tag(style.language)
        {
            return Err(invalid(stage));
        }
        expected += n;
    }
    if !styles.is_empty() && expected != input.len() {
        return Err(invalid(stage));
    }
    if let Some(flow) = inline {
        if (!input.is_empty() && styles.is_empty())
            || flow.style_metrics.len() != styles.len()
            || flow.objects.len() > input.len()
            || flow
                .style_metrics
                .iter()
                .any(|m| !valid_values(0.0, m.ascent, m.descent))
        {
            return Err(invalid(stage));
        }
        let mut previous = None;
        for object in &flow.objects {
            if object.scalar_index as usize >= input.len()
                || previous.is_some_and(|i| object.scalar_index <= i)
                || input[object.scalar_index as usize].code_point != 0xfffc
                || !valid_values(object.width, object.ascent, object.descent)
            {
                return Err(invalid(stage));
            }
            previous = Some(object.scalar_index);
        }
        let mut objects = flow.objects.iter();
        for (i, s) in input.iter().enumerate() {
            if s.code_point == 0xfffc && objects.next().is_none_or(|o| o.scalar_index as usize != i)
            {
                return Err(invalid(stage));
            }
        }
        if objects.next().is_some() {
            return Err(invalid(stage));
        }
    }
    Ok(())
}
fn valid_values(width: f32, ascent: f32, descent: f32) -> bool {
    [width, ascent, descent]
        .into_iter()
        .all(|v| v.is_finite() && v >= 0.0)
}

pub(super) fn build(
    fonts: &[SfntFace],
    input: &[UnicodeScalar],
    styles: &[NativeDesignStyle],
    shaping: NativeShapeRequest<'_>,
    options: NativeDesignOptions,
    inline: Option<&NativeInlineFlow>,
) -> Result<NativeDesignSource, NativeParagraphError> {
    let stage = NativeParagraphStage::Shaping;
    let mut v = NativeDesignSource {
        source: copied(input).map_err(|e| error(stage, e))?,
        input: copied(input).map_err(|e| error(stage, e))?,
        fonts: copied(fonts).map_err(|e| error(stage, e))?,
        styles: copied(styles).map_err(|e| error(stage, e))?,
        shaping: NativeParagraphShaping::capture(shaping).map_err(|e| error(stage, e))?,
        inline: inline.cloned(),
        tab_flow: options.tabs.interval > 0.0,
        paragraph_level: if shaping.direction == GposDirection::RightToLeft {
            1
        } else {
            0
        },
        source_digit_bidi: styles
            .iter()
            .any(|s| s.number_substitution.digits & 0x4000_0000 != 0),
        scalar_levels: filled(input.len(), BidiLevel::default()).map_err(|e| error(stage, e))?,
        scripts: Vec::new(),
        graphemes: Vec::new(),
        fallback: Vec::new(),
        line_classes: filled(input.len(), LineBreakClass::default())
            .map_err(|e| error(stage, e))?,
        scalar_breaks: filled(input.len(), LineBreakKind::default())
            .map_err(|e| error(stage, e))?,
        runs: Vec::new(),
        logical: Vec::new(),
        levels: Vec::new(),
        font_indices: Vec::new(),
        scales: Vec::new(),
        owners: Vec::new(),
        breaks: Vec::new(),
        metrics: Vec::new(),
    };
    if input.is_empty() {
        return Ok(v);
    }
    let mut selected = 0;
    substitute_by(&mut v.input, v.shaping.direction, |i| {
        while selected + 1 < v.styles.len()
            && i >= (v.styles[selected].scalar_start + v.styles[selected].scalar_count) as usize
        {
            selected += 1;
        }
        v.styles.get(selected).map(|s| s.number_substitution)
    });
    let requested = match shaping.direction {
        GposDirection::LeftToRight => 0,
        GposDirection::RightToLeft => 1,
        _ => -1,
    };
    v.paragraph_level = BidiWorkspace::new(input.len())
        .map_err(|e| error(NativeParagraphStage::Bidi, e))?
        .resolve_into(
            if v.source_digit_bidi {
                &v.source
            } else {
                &v.input
            },
            requested,
            &mut v.scalar_levels,
        )
        .map_err(|e| error(NativeParagraphStage::Bidi, e))?
        .paragraph_level;
    v.scripts = filled(input.len(), ScriptRun::default()).map_err(|e| error(stage, e))?;
    if matches!(shaping.unicode_script, Tag(0)) || shaping.unicode_script == Tag::new(*b"DFLT") {
        let count =
            unicode::itemize_scripts_into(&v.input, &mut v.scripts).map_err(|e| error(stage, e))?;
        v.scripts.truncate(count as usize);
    } else {
        let first = input.first().unwrap();
        let last = input.last().unwrap();
        v.scripts[0] = ScriptRun {
            scalar_start: 0,
            scalar_count: input.len() as u32,
            input_start: first.input_index,
            input_length: last.input_index + u32::from(last.input_length) - first.input_index,
            script: shaping.unicode_script,
        };
        v.scripts.truncate(1);
    }
    v.graphemes = filled(input.len(), GraphemeCluster::default()).map_err(|e| error(stage, e))?;
    let count =
        unicode::grapheme::segment_into(&v.input, GraphemePolicy::Unicode17, &mut v.graphemes)
            .map_err(|e| error(stage, e))?;
    v.graphemes.truncate(count as usize);
    if styles.is_empty() {
        let mut candidates = reserved(fonts.len()).map_err(|e| error(stage, e))?;
        for (i, font) in v.fonts.iter().enumerate() {
            candidates.push(NativeFallbackCandidate {
                font: Some(font.clone()),
                identity: i as u64,
            });
        }
        v.fallback = native_itemize_font_fallback(&v.input, &v.graphemes, &candidates, 0)
            .map_err(|e| error(stage, e))?;
    } else {
        v.fallback = reserved(styles.len()).map_err(|e| error(stage, e))?;
        for s in &v.styles {
            let first = &v.input[s.scalar_start as usize];
            let last = &v.input[(s.scalar_start + s.scalar_count - 1) as usize];
            v.fallback.push(NativeFallbackRun {
                scalar_index: s.scalar_start,
                scalar_count: s.scalar_count,
                input_index: first.input_index,
                input_length: last.input_index + u32::from(last.input_length) - first.input_index,
                font_index: s.font_index,
                missing: false,
            });
        }
    }
    unicode::line_break::resolve_into(
        &v.input,
        LineBreakPolicy::Unicode17,
        &mut v.line_classes,
        &mut v.scalar_breaks,
    )
    .map_err(|e| error(NativeParagraphStage::LineBreak, e))?;
    v.runs = reserved(input.len()).map_err(|e| error(stage, e))?;
    let (mut start, mut script, mut fallback, mut object) = (0usize, 0usize, 0usize, 0usize);
    while start < v.input.len() {
        while start >= (v.scripts[script].scalar_start + v.scripts[script].scalar_count) as usize {
            script += 1;
        }
        while start
            >= (v.fallback[fallback].scalar_index + v.fallback[fallback].scalar_count) as usize
        {
            fallback += 1;
        }
        let font_index = v.fallback[fallback].font_index;
        let level = v.scalar_levels[start].level;
        if let Some(item) = v
            .inline
            .as_ref()
            .and_then(|f| f.objects.get(object))
            .copied()
            .filter(|o| o.scalar_index as usize == start)
        {
            append_item(
                &mut v,
                ShapingGlyph {
                    glyph_id: u32::MAX - 1,
                    code_point: 0xfffc,
                    cluster: input[start].input_index as i32,
                    advance_x: if item.width == 0.0 { 0 } else { 1 },
                    ..ShapingGlyph::default()
                },
                level,
                u32::MAX,
                if item.width == 0.0 { 1.0 } else { item.width },
                NativeTextItemMetrics {
                    ascent: item.ascent,
                    descent: item.descent,
                },
                NativeDesignOccurrence::InlineObject {
                    object_index: object as u32,
                },
            )?;
            object += 1;
            start += 1;
            continue;
        }
        let metric = v
            .inline
            .as_ref()
            .map_or(NativeTextItemMetrics::default(), |f| {
                f.style_metrics[fallback]
            });
        if v.tab_flow && v.input[start].code_point == 9 {
            let paragraph_level = v.paragraph_level;
            append_item(
                &mut v,
                ShapingGlyph {
                    glyph_id: u32::MAX,
                    code_point: 9,
                    cluster: input[start].input_index as i32,
                    ..ShapingGlyph::default()
                },
                paragraph_level,
                font_index,
                options.layout.scale,
                metric,
                NativeDesignOccurrence::Tab {
                    scalar_index: start as u32,
                    font_index,
                },
            )?;
            start += 1;
            continue;
        }
        let limit = ((v.scripts[script].scalar_start + v.scripts[script].scalar_count) as usize)
            .min((v.fallback[fallback].scalar_index + v.fallback[fallback].scalar_count) as usize);
        let mut end = start + 1;
        while end < limit
            && v.scalar_levels[end].level == level
            && (!v.tab_flow || v.input[end].code_point != 9)
            && (v.inline.is_none() || v.input[end].code_point != 0xfffc)
        {
            end += 1;
        }
        let mut request = v.shaping.request();
        let mut scale = options.layout.scale;
        if let Some(style) = v.styles.get(fallback) {
            request.features = &style.features;
            request.language = style.language;
            scale = style.scale;
        }
        request.direction = if level & 1 == 0 {
            GposDirection::LeftToRight
        } else {
            GposDirection::RightToLeft
        };
        request.unicode_script = v.scripts[script].script;
        if start != 0 {
            request.pre_context = &v.input[..start];
        }
        if end != v.input.len() {
            request.post_context = &v.input[end..];
        }
        let run = Arc::new(
            NativeShaper::new(v.fonts[font_index as usize].clone(), request)
                .map_err(|e| error(stage, e))?
                .shape(&v.input[start..end])
                .map_err(|e| error(stage, e))?,
        );
        let first = v.logical.len();
        let n = run.glyphs().len();
        reserve_items(&mut v, n)?;
        let append = |v: &mut NativeDesignSource, range: Range<usize>| {
            for i in range {
                let mut g = run.glyphs()[i];
                // Original generic ABI protects INT_MIN and otherwise negates Y.
                // Device paragraphs have their separate checked-negation admission.
                g.advance_y = g.advance_y.wrapping_neg();
                g.offset_y = g.offset_y.wrapping_neg();
                v.logical.push(g);
                v.levels.push(level);
                v.font_indices.push(font_index);
                v.scales.push(scale);
                v.metrics.push(metric);
                v.owners.push(NativeDesignOccurrence::Glyph {
                    run_index: v.runs.len() as u32,
                    run_glyph_index: i as u32,
                });
            }
        };
        if level & 1 == 0 {
            append(&mut v, 0..n);
        } else {
            let mut end = n;
            while end != 0 {
                let mut begin = end - 1;
                while begin != 0 && run.glyphs()[begin - 1].cluster == run.glyphs()[end - 1].cluster
                {
                    begin -= 1;
                }
                append(&mut v, begin..end);
                end = begin;
            }
        }
        v.runs.push(NativeDesignRun {
            generation: run,
            scalar_range: start as u32..end as u32,
            logical_range: first as u32..v.logical.len() as u32,
            font_index,
            style_index: (!styles.is_empty()).then_some(fallback as u32),
            bidi_level: level,
            scale,
        });
        start = end;
    }
    v.breaks = filled(v.logical.len(), LineBreakKind::Prohibited).map_err(|e| error(stage, e))?;
    map_breaks(&v.input, &v.scalar_breaks, &v.logical, &mut v.breaks)?;
    Ok(v)
}
fn reserve_items(v: &mut NativeDesignSource, count: usize) -> Result<(), NativeParagraphError> {
    let stage = NativeParagraphStage::Shaping;
    if v.logical
        .len()
        .checked_add(count)
        .is_none_or(|n| n > u32::MAX as usize)
    {
        return Err(error(stage, FontError::Capacity));
    }
    macro_rules! reserve{($($field:ident),*)=>{$(v.$field.try_reserve(count).map_err(|_|error(stage,FontError::Capacity))?;)*};}
    reserve!(logical, levels, font_indices, scales, owners, metrics);
    Ok(())
}
fn append_item(
    v: &mut NativeDesignSource,
    glyph: ShapingGlyph,
    level: i8,
    font: u32,
    scale: f32,
    metrics: NativeTextItemMetrics,
    owner: NativeDesignOccurrence,
) -> Result<(), NativeParagraphError> {
    reserve_items(v, 1)?;
    v.logical.push(glyph);
    v.levels.push(level);
    v.font_indices.push(font);
    v.scales.push(scale);
    v.metrics.push(metrics);
    v.owners.push(owner);
    Ok(())
}
