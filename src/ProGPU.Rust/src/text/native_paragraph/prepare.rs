//! Native paragraph admission, number substitution, run partitioning and source maps.
use super::*;
const SCALAR_MASK: u32 = 0x001f_ffff;
const CONTEXTUAL: u32 = 0x8000_0000;
const SOURCE_BIDI: u32 = 0x4000_0000;
pub(super) fn valid_tag(tag: Tag) -> bool {
    tag.0 == 0
        || tag
            .0
            .to_be_bytes()
            .into_iter()
            .all(|c| (0x20..=0x7e).contains(&c))
}
pub(super) fn valid_features(features: &[NativeFeature]) -> bool {
    features.len() <= u32::MAX as usize
        && features
            .iter()
            .all(|f| f.tag.0 != 0 && valid_tag(f.tag) && f.start <= f.end)
}
pub(super) fn valid_context(input: &[UnicodeScalar]) -> bool {
    input.len() <= u32::MAX as usize
        && input.iter().all(|s| {
            char::from_u32(s.code_point).is_some() && s.input_length != 0 && s.reserved == 0
        })
}
pub(super) fn valid_number(n: NativeNumberSubstitution) -> bool {
    let p = n.digits;
    let zero = p & SCALAR_MASK;
    (p == 0
        || p & !(SCALAR_MASK | CONTEXTUAL | SOURCE_BIDI) == 0
            && zero != 0
            && zero <= 0x10fff6
            && !(zero <= 0xdfff && zero + 9 >= 0xd800)
            && (0..10).all(|i| unicode::decimal_digit_value(zero + i) == Some(i as u8)))
        && [n.percent, n.group_separator, n.decimal_separator]
            .into_iter()
            .all(|c| c == 0 || char::from_u32(c).is_some())
}
pub(super) fn validate(
    fonts: &[SfntFace],
    input: &[UnicodeScalar],
    styles: &[NativeParagraphStyle],
    shaping: NativeShapeRequest<'_>,
    options: NativeTextLayoutOptions,
) -> Result<(), NativeParagraphError> {
    let stage = NativeParagraphStage::Shaping;
    if !valid_context(input)
        || !valid_context(shaping.pre_context)
        || !valid_context(shaping.post_context)
        || !valid_features(shaping.features)
        || !valid_tag(shaping.unicode_script)
        || !valid_tag(shaping.language)
        || shaping.flags.0 & 12 == 12
        || shaping.normalized_coordinates.len() > u32::MAX as usize
        || !options.ellipsis_advance.is_finite()
        || options.ellipsis_advance < 0.0
        || input.len() > i32::MAX as usize
        || fonts.len() > u32::MAX as usize
        || styles.len() > input.len()
        || input.is_empty() != styles.is_empty()
        || !matches!(
            shaping.direction,
            GposDirection::Unspecified | GposDirection::LeftToRight | GposDirection::RightToLeft
        )
        || !matches!(
            options.direction,
            GposDirection::Unspecified | GposDirection::LeftToRight | GposDirection::RightToLeft
        )
        || options.trimming != NativeTextTrimming::None
        || options.collapse_width != -1.0
        || !options.scale.is_finite()
        || options.scale <= 0.0
        || !options.maximum_width.is_finite()
        || options.maximum_width < 0.0
        || !options.line_height.is_finite()
        || options.line_height < 0.0
    {
        return Err(invalid(stage));
    }
    let mut previous = 0u64;
    for s in input {
        let end = u64::from(s.input_index) + u64::from(s.input_length);
        if s.input_length == 0
            || u64::from(s.input_index) < previous
            || end > i32::MAX as u64
            || char::from_u32(s.code_point).is_none()
            || matches!(s.code_point, 9 | 0xfffc)
        {
            return Err(invalid(stage));
        }
        previous = end;
    }
    let mut expected = 0usize;
    for s in styles {
        let n = s.scalar_count as usize;
        let scale = s.logical_units_per_physical_pixel / 64.0;
        if s.scalar_start as usize != expected
            || n == 0
            || n > input.len() - expected
            || s.font_index as usize >= fonts.len()
            || !s.source_scale.is_finite()
            || s.source_scale <= 0.0
            || !valid_number(s.number_substitution)
            || !valid_features(&s.features)
            || !valid_tag(s.language)
            || !s.logical_units_per_physical_pixel.is_finite()
            || s.logical_units_per_physical_pixel <= 0.0
            || !scale.is_finite()
            || scale <= 0.0
            || !s.metrics.ascent.is_finite()
            || s.metrics.ascent < 0.0
            || !s.metrics.descent.is_finite()
            || s.metrics.descent < 0.0
            || s.hinting
                .pixels_per_em_26_6
                .iter()
                .any(|&v| v == 0 || v > i32::MAX as u32)
            || s.hinting.phase_26_6.iter().any(|&v| v >= 64)
            || s.hinting.variation_coordinates_16_16.len() > 65535
            || s.hinting.outline_policy != NativeOutlineLoadPolicy::Normal
        {
            return Err(invalid(stage));
        }
        expected += n;
    }
    if expected != input.len() {
        return Err(invalid(stage));
    }
    Ok(())
}
fn hard(k: LineBreakClass) -> bool {
    matches!(
        k,
        LineBreakClass::Mandatory
            | LineBreakClass::CarriageReturn
            | LineBreakClass::LineFeed
            | LineBreakClass::NextLine
    )
}
fn substitute(
    input: &mut [UnicodeScalar],
    styles: &[NativeParagraphStyle],
    direction: GposDirection,
) {
    let mut style = 0;
    substitute_by(input, direction, |i| {
        while style + 1 < styles.len()
            && i >= styles[style].scalar_start as usize + styles[style].scalar_count as usize
        {
            style += 1;
        }
        styles.get(style).map(|s| s.number_substitution)
    });
}
pub(super) fn substitute_by(
    input: &mut [UnicodeScalar],
    direction: GposDirection,
    mut at: impl FnMut(usize) -> Option<NativeNumberSubstitution>,
) {
    let initial = direction == GposDirection::RightToLeft;
    let mut arabic = initial;
    for (i, s) in input.iter_mut().enumerate() {
        let original = s.code_point;
        if let Some(n) = at(i) {
            let zero = n.digits & SCALAR_MASK;
            let active = n.digits & CONTEXTUAL == 0 || arabic;
            let replacement = if !active {
                0
            } else if (0x30..=0x39).contains(&original) && zero != 0 {
                zero + original - 0x30
            } else {
                match original {
                    0x25 => n.percent,
                    0x2c => n.group_separator,
                    0x2e => n.decimal_separator,
                    _ => 0,
                }
            };
            if replacement != 0 {
                s.code_point = replacement;
                s.canonical_combining_class = unicode::canonical_combining_class(replacement);
                s.script = unicode::script(replacement);
            }
        }
        if hard(unicode::line_break::break_class(original)) {
            arabic = initial;
        } else {
            match unicode::bidi_class(original) {
                unicode::BidiClass::ArabicLetter => arabic = true,
                unicode::BidiClass::LeftToRight | unicode::BidiClass::RightToLeft => arabic = false,
                _ => {}
            }
        }
    }
}
pub(super) fn build(
    fonts: &[SfntFace],
    input: &[UnicodeScalar],
    styles: &[NativeParagraphStyle],
    shaping: NativeShapeRequest<'_>,
    options: NativeTextLayoutOptions,
    projection: NativeHintedProjection,
    provider: &mut dyn NativeHintedProvider,
) -> Result<NativeParagraphSource, NativeParagraphError> {
    let stage = NativeParagraphStage::Shaping;
    // All source/feature/style arrays are owned before the first provider callback.
    let mut v = NativeParagraphSource {
        source_policies: None,
        source: copied(input).map_err(|e| error(stage, e))?,
        input: copied(input).map_err(|e| error(stage, e))?,
        fonts: copied(fonts).map_err(|e| error(stage, e))?,
        styles: copied(styles).map_err(|e| error(stage, e))?,
        shaping: NativeParagraphShaping::capture(shaping).map_err(|e| error(stage, e))?,
        paragraph_level: if shaping.direction == GposDirection::RightToLeft {
            1
        } else {
            0
        },
        source_digit_bidi: styles
            .iter()
            .any(|s| s.number_substitution.digits & SOURCE_BIDI != 0),
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
        source_scales: Vec::new(),
        scales: Vec::new(),
        owners: Vec::new(),
        breaks: Vec::new(),
        metrics: Vec::new(),
        ends: Vec::new(),
        justification: Vec::new(),
    };
    if input.is_empty() {
        return Ok(v);
    }
    substitute(&mut v.input, &v.styles, v.shaping.direction);
    if v.input.iter().any(|s| matches!(s.code_point, 9 | 0xfffc)) {
        return Err(invalid(stage));
    }
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
    if shaping.unicode_script == Tag::new([0; 4]) || shaping.unicode_script == Tag::new(*b"DFLT") {
        let n =
            unicode::itemize_scripts_into(&v.input, &mut v.scripts).map_err(|e| error(stage, e))?;
        v.scripts.truncate(n as usize);
    } else {
        v.scripts[0] = ScriptRun {
            scalar_start: 0,
            scalar_count: input.len() as u32,
            input_start: input[0].input_index,
            input_length: input.last().unwrap().input_index
                + u32::from(input.last().unwrap().input_length)
                - input[0].input_index,
            script: shaping.unicode_script,
        };
        v.scripts.truncate(1);
    }
    v.graphemes = filled(input.len(), GraphemeCluster::default()).map_err(|e| error(stage, e))?;
    let ng = unicode::grapheme::segment_into(&v.input, GraphemePolicy::Unicode17, &mut v.graphemes)
        .map_err(|e| error(stage, e))?;
    v.graphemes.truncate(ng as usize);
    v.fallback = reserved(v.styles.len()).map_err(|e| error(stage, e))?;
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
    unicode::line_break::resolve_into(
        &v.input,
        LineBreakPolicy::Unicode17,
        &mut v.line_classes,
        &mut v.scalar_breaks,
    )
    .map_err(|e| error(NativeParagraphStage::LineBreak, e))?;
    v.runs = reserved(input.len()).map_err(|e| error(stage, e))?;
    let mut start = 0;
    let mut script = 0;
    let mut style = 0;
    while start < v.input.len() {
        while start >= (v.scripts[script].scalar_start + v.scripts[script].scalar_count) as usize {
            script += 1;
        }
        while start >= (v.styles[style].scalar_start + v.styles[style].scalar_count) as usize {
            style += 1;
        }
        let s = &v.styles[style];
        let level = v.scalar_levels[start].level;
        let limit = ((v.scripts[script].scalar_start + v.scripts[script].scalar_count) as usize)
            .min((s.scalar_start + s.scalar_count) as usize);
        let mut end = start + 1;
        while end < limit && v.scalar_levels[end].level == level {
            end += 1;
        }
        let mut request = v.shaping.request();
        request.features = &s.features;
        request.language = s.language;
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
        let shaper = NativeShaper::new(v.fonts[s.font_index as usize].clone(), request)
            .map_err(|e| error(stage, e))?;
        let run = shaper
            .shape_device(&v.input[start..end], &s.hinting, projection, provider)
            .map_err(|e| error(stage, e))?;
        let first = v.logical.len();
        let n = run.glyphs().len();
        if first.checked_add(n).is_none_or(|n| n > u32::MAX as usize)
            || run.descriptor_indices().len() != n
        {
            return Err(invalid(stage));
        }
        macro_rules! reserve {($($field:ident),*)=>{$(v.$field.try_reserve(n).map_err(|_|error(stage,FontError::Capacity))?;)*};}
        reserve!(
            logical,
            levels,
            font_indices,
            source_scales,
            scales,
            owners,
            metrics
        );
        let append = |v: &mut NativeParagraphSource,
                      range: Range<usize>|
         -> Result<(), NativeParagraphError> {
            for i in range {
                let mut g = run.glyphs()[i];
                g.advance_y = g.advance_y.checked_neg().ok_or_else(|| invalid(stage))?;
                g.offset_y = g.offset_y.checked_neg().ok_or_else(|| invalid(stage))?;
                let descriptor = run.descriptor_indices()[i];
                if descriptor as usize >= run.source_descriptor_count()
                    || run
                        .batch()
                        .glyphs()
                        .get(descriptor as usize)
                        .is_none_or(|d| d.glyph_id != g.glyph_id)
                {
                    return Err(invalid(stage));
                }
                v.logical.push(g);
                v.levels.push(level);
                v.font_indices.push(v.styles[style].font_index);
                v.source_scales.push(v.styles[style].source_scale);
                v.scales
                    .push(v.styles[style].logical_units_per_physical_pixel / 64.0);
                v.owners.push(NativeParagraphGlyphOwner {
                    run_index: v.runs.len() as u32,
                    run_glyph_index: i as u32,
                    descriptor_index: descriptor,
                });
                v.metrics.push(v.styles[style].metrics);
            }
            Ok(())
        };
        if level & 1 == 0 {
            append(&mut v, 0..n)?;
        } else {
            let mut e = n;
            while e != 0 {
                let mut b = e - 1;
                while b != 0 && run.glyphs()[b - 1].cluster == run.glyphs()[e - 1].cluster {
                    b -= 1;
                }
                append(&mut v, b..e)?;
                e = b;
            }
        }
        let s = &v.styles[style];
        v.runs.push(NativeParagraphRun {
            generation: run,
            scalar_range: start as u32..end as u32,
            logical_range: first as u32..v.logical.len() as u32,
            font_index: s.font_index,
            style_index: style as u32,
            bidi_level: level,
            source_scale: s.source_scale,
            logical_units_per_physical_pixel: s.logical_units_per_physical_pixel,
        });
        start = end;
    }
    v.breaks = filled(v.logical.len(), LineBreakKind::Prohibited).map_err(|e| error(stage, e))?;
    map_breaks(&v.input, &v.scalar_breaks, &v.logical, &mut v.breaks)?;
    v.ends = coverage(&v.source, &v.input, &v.line_classes, &v.logical)?;
    if options.alignment == NativeTextAlignment::Justify {
        v.justification = filled(v.logical.len(), NativeTextJustification::Content)
            .map_err(|e| error(stage, e))?;
        native_classify_justification_into(&v.input, &v.logical, &mut v.justification)
            .map_err(|e| error(NativeParagraphStage::ClusterMap, e))?;
    }
    Ok(v)
}
pub(super) fn map_breaks(
    input: &[UnicodeScalar],
    breaks: &[LineBreakKind],
    glyphs: &[ShapingGlyph],
    output: &mut [LineBreakKind],
) -> Result<(), NativeParagraphError> {
    let stage = NativeParagraphStage::ClusterMap;
    let mut scalar = 0;
    let mut start = 0;
    let mut previous = -1;
    while start < glyphs.len() {
        let cluster = glyphs[start].cluster;
        if cluster < 0 || cluster <= previous {
            return Err(invalid(stage));
        }
        let mut end = start + 1;
        while end < glyphs.len() && glyphs[end].cluster == cluster {
            end += 1;
        }
        let next = if end < glyphs.len() {
            if glyphs[end].cluster <= cluster {
                return Err(invalid(stage));
            }
            glyphs[end].cluster as u32
        } else {
            u32::MAX
        };
        while scalar < input.len() && input[scalar].input_index < next {
            scalar += 1;
        }
        if scalar == 0 {
            return Err(invalid(stage));
        }
        output[start..end].fill(LineBreakKind::Prohibited);
        output[end - 1] = breaks[scalar - 1];
        previous = cluster;
        start = end;
    }
    Ok(())
}
fn coverage(
    source: &[UnicodeScalar],
    input: &[UnicodeScalar],
    classes: &[LineBreakClass],
    glyphs: &[ShapingGlyph],
) -> Result<Vec<i32>, NativeParagraphError> {
    let stage = NativeParagraphStage::ClusterMap;
    let mut ends = filled(glyphs.len(), 0).map_err(|e| error(stage, e))?;
    let mut scalar = 0;
    let mut glyph = 0;
    while glyph < glyphs.len() {
        let cluster = glyphs[glyph].cluster;
        if cluster < 0 {
            return Err(invalid(stage));
        }
        while scalar < source.len() && source[scalar].input_index < (cluster as u32) {
            scalar += 1;
        }
        if scalar == source.len() || source[scalar].input_index != cluster as u32 {
            return Err(invalid(stage));
        }
        let mut next = glyph + 1;
        while next < glyphs.len() && glyphs[next].cluster == cluster {
            next += 1;
        }
        let limit = if next == glyphs.len() {
            i32::MAX
        } else {
            glyphs[next].cluster
        };
        if limit <= cluster {
            return Err(invalid(stage));
        }
        let first = input[scalar].code_point;
        let starts_break = hard(classes[scalar]);
        let mut end = cluster;
        for i in scalar..source.len() {
            if source[i].input_index >= limit as u32 {
                break;
            }
            if !starts_break && hard(classes[i]) {
                break;
            }
            if starts_break
                && i != scalar
                && !(first == 13 && i == scalar + 1 && input[i].code_point == 10)
            {
                break;
            }
            let far = u64::from(source[i].input_index) + u64::from(source[i].input_length);
            if far > limit as u64 || far > i32::MAX as u64 {
                return Err(invalid(stage));
            }
            end = far as i32;
        }
        if end <= cluster {
            return Err(invalid(stage));
        }
        ends[glyph..next].fill(end);
        glyph = next;
    }
    Ok(ends)
}
