//! Original font runs, safe wrapping, styled line metrics and tab/justification arithmetic.
use super::*;
use crate::text::{
    managed_pipeline::shape_scaled,
    preparation::{ManagedBufferFlags, ManagedTextContext},
    unicode::bidi::managed_visual_order,
};
use std::ops::Range;

enum CandidateKind {
    Glyph { font: ManagedFont, glyph: u16 },
    Box(usize),
}
struct Candidate {
    kind: CandidateKind,
    style: usize,
    cluster: i32,
    code_point: u32,
    level: i8,
    advance: f32,
    offset_x: f32,
    offset_y: f32,
    ascent: f32,
    descent: f32,
    flags: u32,
}
impl Candidate {
    fn is_tab(&self) -> bool {
        matches!(self.kind, CandidateKind::Glyph { .. }) && self.code_point == 9
    }
    fn whitespace(&self) -> bool {
        matches!(self.kind, CandidateKind::Glyph { .. }) && matches!(self.code_point, 32 | 9 | 0xa0)
    }
}
/// Original logical/visual access, retaining indices rather than copying font owners.
#[derive(Clone, Copy)]
struct Candidates<'a> {
    values: &'a [Candidate],
    order: Option<&'a [usize]>,
}
impl<'a> Candidates<'a> {
    fn len(self) -> usize {
        self.order.map_or(self.values.len(), <[usize]>::len)
    }
    fn get(self, index: usize) -> &'a Candidate {
        &self.values[self.order.map_or(index, |o| o[index])]
    }
}
struct Segment {
    range: Range<usize>,
    paragraph: Range<usize>,
    font: ManagedFont,
    level: i8,
    style: usize,
}
struct LineRequest<'a> {
    range: Range<usize>,
    paragraph_level: i8,
    paragraph_start: usize,
    paragraph_style: &'a StyledTextParagraphStyle,
    first: bool,
    final_line: bool,
}
#[derive(Default)]
struct Placement {
    y: f32,
    maximum_width: f32,
    maximum_bottom: f32,
}
impl StyledTextLayout {
    pub(super) fn generate<F: ManagedFontFallback + ?Sized>(
        &mut self,
        style_map: &[usize],
        box_map: &[i32],
        fallback: &F,
    ) -> Result<(), TextLayoutError> {
        reserve(&mut self.glyphs, self.text.len().max(1))?;
        reserve(&mut self.boxes, self.inline_boxes.len())?;
        reserve(
            &mut self.lines,
            self.text.iter().filter(|&&c| c == 10).count() + 1,
        )?;
        let mut placement = Placement::default();
        let mut start = 0;
        while start <= self.text.len() {
            let newline = self.text[start..]
                .iter()
                .position(|&c| c == 10)
                .map(|i| start + i);
            let end = newline.unwrap_or(self.text.len());
            let empty_style = if style_map.is_empty() {
                0
            } else {
                style_map[start.min(self.text.len() - 1)]
            };
            let style = self.paragraph_style(start, &self.ranges[empty_style].style);
            let paragraph =
                ManagedBidiParagraph::resolve(&self.text[start..end], self.options.base_direction)?;
            let candidates =
                self.shape_paragraph(start..end, &paragraph, style_map, box_map, fallback)?;
            if candidates.is_empty() {
                let text_style = &self.ranges[empty_style].style;
                let ascent = ascent(text_style, &text_style.font);
                let descent = descent(text_style, &text_style.font);
                let height =
                    style.space_before + line_height(ascent + descent, &style) + style.space_after;
                let baseline = placement.y + style.space_before + ascent;
                reserve(&mut self.lines, 1)?;
                self.lines.push(StyledTextLine {
                    glyph_offset: self.glyphs.len(),
                    glyph_count: 0,
                    box_offset: self.boxes.len(),
                    box_count: 0,
                    top: placement.y,
                    baseline,
                    width: 0.0,
                    height,
                    is_paragraph_final: true,
                    paragraph_start: start as i32,
                });
                placement.maximum_bottom = max(placement.maximum_bottom, baseline + descent);
                placement.y += height;
            } else {
                let mut candidate_start = 0;
                let mut first = true;
                while candidate_start < candidates.len() {
                    let candidate_end = find_line_end(
                        &candidates,
                        candidate_start,
                        self.options.max_width,
                        &style,
                        first,
                    )?;
                    self.place_line(
                        &candidates,
                        LineRequest {
                            range: candidate_start..candidate_end,
                            paragraph_level: paragraph.paragraph_level(),
                            paragraph_start: start,
                            paragraph_style: &style,
                            first,
                            final_line: candidate_end == candidates.len(),
                        },
                        &mut placement,
                    )?;
                    candidate_start = candidate_end;
                    first = false;
                }
                if style.space_after > 0.0 {
                    self.lines
                        .last_mut()
                        .expect("a nonempty paragraph emits a line")
                        .height += style.space_after;
                    placement.y += style.space_after;
                }
            }
            let Some(newline) = newline else {
                break;
            };
            start = newline + 1;
        }
        self.content_size = [
            if self.options.max_width.is_infinite() {
                placement.maximum_width
            } else {
                self.options.max_width
            },
            max(placement.y, placement.maximum_bottom),
        ];
        Ok(())
    }
    fn paragraph_style(
        &self,
        start: usize,
        text_style: &StyledTextStyle,
    ) -> StyledTextParagraphStyle {
        if let Ok(index) = self
            .options
            .paragraph_styles
            .binary_search_by_key(&(start as i32), |s| s.text_start)
        {
            return self.options.paragraph_styles[index].clone();
        }
        StyledTextParagraphStyle {
            alignment: text_style.alignment,
            minimum_line_spacing: self.options.minimum_line_spacing,
            exact_line_spacing: self.options.exact_line_spacing,
            ..StyledTextParagraphStyle::new(start as i32, max(text_style.font_size * 4.0, 0.01))
        }
    }
    fn shape_paragraph<F: ManagedFontFallback + ?Sized>(
        &self,
        range: Range<usize>,
        paragraph: &ManagedBidiParagraph,
        styles: &[usize],
        boxes: &[i32],
        fallback: &F,
    ) -> Result<Vec<Candidate>, TextLayoutError> {
        let mut candidates = Vec::new();
        reserve(&mut candidates, range.len().max(1))?;
        for bidi in paragraph.runs() {
            let mut start = range.start + bidi.start;
            let end = start + bidi.length;
            while start < end {
                if boxes[start] >= 0 {
                    let index = boxes[start] as usize;
                    let b = self.inline_boxes[index];
                    reserve(&mut candidates, 1)?;
                    candidates.push(Candidate {
                        kind: CandidateKind::Box(index),
                        style: styles[start],
                        cluster: start as i32,
                        code_point: 0xfffc,
                        level: bidi.level,
                        advance: b.width,
                        offset_x: 0.0,
                        offset_y: 0.0,
                        ascent: b.ascent,
                        descent: b.descent,
                        flags: 0,
                    });
                    start += 1;
                    continue;
                }
                let style = styles[start];
                let requested = &self.ranges[style].style.font;
                let (font, scalar_length) =
                    self.resolve_font(start, end, requested, None, fallback)?;
                let font_start = start;
                start += scalar_length;
                while start < end && boxes[start] < 0 && styles[start] == style {
                    let (next, length) =
                        self.resolve_font(start, end, requested, Some(&font), fallback)?;
                    if !next.same_instance(&font) {
                        break;
                    }
                    start += length;
                }
                self.append_segment(
                    &mut candidates,
                    Segment {
                        range: font_start..start,
                        paragraph: range.clone(),
                        font,
                        level: bidi.level,
                        style,
                    },
                )?;
            }
        }
        Ok(candidates)
    }
    fn resolve_font<F: ManagedFontFallback + ?Sized>(
        &self,
        start: usize,
        end: usize,
        requested: &ManagedFont,
        previous: Option<&ManagedFont>,
        fallback: &F,
    ) -> Result<(ManagedFont, usize), TextLayoutError> {
        let first = self.text[start];
        let (cp, length) = if (0xd800..=0xdbff).contains(&first)
            && start + 1 < end
            && (0xdc00..=0xdfff).contains(&self.text[start + 1])
        {
            (
                0x10000 + ((u32::from(first) - 0xd800) << 10) + u32::from(self.text[start + 1])
                    - 0xdc00,
                2,
            )
        } else {
            (u32::from(first), 1)
        };
        if requested.glyph_index(cp)? != 0 || matches!(cp, 32 | 9) {
            return Ok((requested.clone(), length));
        }
        if let Some(previous) = previous.filter(|_| crate::font::layout::default_ignorable(cp)) {
            return Ok((previous.clone(), length));
        }
        Ok((
            fallback
                .resolve(requested, cp)?
                .unwrap_or_else(|| requested.clone()),
            length,
        ))
    }
    fn append_segment(
        &self,
        candidates: &mut Vec<Candidate>,
        segment: Segment,
    ) -> Result<(), TextLayoutError> {
        let style = &self.ranges[segment.style].style;
        let mut options = self.options.shaping.clone();
        options.direction = Some(if segment.level & 1 == 0 {
            Direction::LeftToRight
        } else {
            Direction::RightToLeft
        });
        let mut prep = self.preparation.clone();
        if segment.range.start == segment.paragraph.start {
            prep.buffer_flags = prep.buffer_flags | ManagedBufferFlags::BEGINNING_OF_TEXT;
        }
        if segment.range.end == segment.paragraph.end {
            prep.buffer_flags = prep.buffer_flags | ManagedBufferFlags::END_OF_TEXT;
        }
        prep.pre_context = ManagedTextContext::view(
            self.text.clone(),
            segment.paragraph.start..segment.range.start,
        )?;
        prep.post_context =
            ManagedTextContext::view(self.text.clone(), segment.range.end..segment.paragraph.end)?;
        let shaped = shape_scaled(
            &self.text[segment.range.clone()],
            segment.font.clone(),
            style.font_size,
            options,
            prep,
        )?;
        let advance_scale = style.width_scale * style.tracking_factor;
        let ascent = ascent(style, &segment.font);
        let descent = descent(style, &segment.font);
        reserve(candidates, shaped.len())?;
        let mut append = |range: Range<usize>| {
            for g in &shaped[range] {
                candidates.push(Candidate {
                    kind: CandidateKind::Glyph {
                        font: segment.font.clone(),
                        glyph: g.glyph_index,
                    },
                    style: segment.style,
                    cluster: (segment.range.start as i32).wrapping_add(g.cluster),
                    code_point: g.code_point,
                    level: segment.level,
                    advance: g.advance_x * advance_scale,
                    offset_x: g.offset_x * style.width_scale,
                    offset_y: g.offset_y,
                    ascent,
                    descent,
                    flags: g.flags,
                });
            }
        };
        if segment.level & 1 == 0 || shaped.len() < 2 {
            append(0..shaped.len());
        } else {
            let mut end = shaped.len();
            while end > 0 {
                let mut start = end - 1;
                while start > 0 && shaped[start - 1].cluster == shaped[end - 1].cluster {
                    start -= 1;
                }
                append(start..end);
                end = start;
            }
        }
        Ok(())
    }
    fn place_line(
        &mut self,
        logical: &[Candidate],
        request: LineRequest<'_>,
        placement: &mut Placement,
    ) -> Result<(), TextLayoutError> {
        let order = visual_order(logical, request.range, request.paragraph_level)?;
        let visual = Candidates {
            values: logical,
            order: Some(&order),
        };
        let style = request.paragraph_style;
        let start = line_start(style, request.first);
        let mut advances = Vec::new();
        reserve(&mut advances, visual.len())?;
        let mut natural_cursor = start;
        let mut ascent = 0.0;
        let mut descent = 0.0;
        for index in 0..visual.len() {
            let c = visual.get(index);
            let advance = if c.is_tab() {
                tab_advance(visual, index, natural_cursor, style)
            } else {
                c.advance
            };
            advances.push(advance);
            natural_cursor += advance;
            ascent = max(ascent, c.ascent);
            descent = max(descent, c.descent);
        }
        let natural_width = natural_cursor - start;
        let space_before = if request.first {
            style.space_before
        } else {
            0.0
        };
        let height = space_before + line_height(ascent + descent, style);
        let baseline = placement.y + space_before + ascent;
        let available = if self.options.max_width.is_infinite() {
            natural_width
        } else {
            self.options.max_width - style.right_indent - start
        };
        if available.partial_cmp(&0.0) != Some(std::cmp::Ordering::Greater) && visual.len() > 0 {
            return Err(TextLayoutError::InvalidArgument);
        }
        let remaining = max(0.0, available - natural_width);
        let mut x = start
            + match style.alignment {
                TextAlignment::Center => remaining * 0.5,
                TextAlignment::Right => remaining,
                _ => 0.0,
            };
        let gaps = if style.alignment == TextAlignment::Justify && !request.final_line {
            whitespace_groups(visual)
        } else {
            0
        };
        let extra = if gaps > 0 {
            remaining / gaps as f32
        } else {
            0.0
        };
        let mut inside_whitespace = false;
        let glyph_offset = self.glyphs.len();
        let box_offset = self.boxes.len();
        // Each candidate emits at most one of these two disjoint inventories.
        let box_count = order
            .iter()
            .filter(|&&i| matches!(logical[i].kind, CandidateKind::Box(_)))
            .count();
        reserve(&mut self.boxes, box_count)?;
        reserve(&mut self.glyphs, visual.len() - box_count)?;
        for (index, &advance) in advances.iter().enumerate() {
            let c = visual.get(index);
            let text_style = &self.ranges[c.style].style;
            if (!c.whitespace() || c.is_tab()) && inside_whitespace {
                x += extra;
                inside_whitespace = false;
            }
            if c.is_tab() {
                x += advance;
                continue;
            }
            match &c.kind {
                CandidateKind::Box(index) => {
                    let b = self.inline_boxes[*index];
                    self.boxes.push(StyledTextPositionedBox {
                        position: [x, baseline - b.ascent],
                        width: b.width,
                        height: b.ascent + b.descent,
                        baseline: b.ascent,
                        text_position: b.text_position,
                        style_index: c.style,
                        tag: b.tag,
                    });
                }
                CandidateKind::Glyph { font, glyph } => {
                    self.glyphs.push(StyledTextGlyph {
                        glyph_index: *glyph,
                        position: [
                            x + c.offset_x,
                            baseline + c.offset_y - text_style.baseline_shift,
                        ],
                        advance,
                        font: font.clone(),
                        style_index: c.style,
                        cluster: c.cluster,
                        bidi_level: c.level,
                        code_point: c.code_point,
                        shaping_flags: c.flags,
                    });
                }
            }
            x += advance;
            if c.whitespace() && !c.is_tab() {
                inside_whitespace = true;
            }
        }
        if inside_whitespace {
            x += extra;
        }
        let width = if style.alignment == TextAlignment::Justify
            && !request.final_line
            && !self.options.max_width.is_infinite()
        {
            start + available
        } else {
            max(start + natural_width, x)
        };
        placement.maximum_width = max(placement.maximum_width, width);
        placement.maximum_bottom = max(placement.maximum_bottom, baseline + descent);
        reserve(&mut self.lines, 1)?;
        self.lines.push(StyledTextLine {
            glyph_offset,
            glyph_count: self.glyphs.len() - glyph_offset,
            box_offset,
            box_count: self.boxes.len() - box_offset,
            top: placement.y,
            baseline,
            width,
            height,
            is_paragraph_final: request.final_line,
            paragraph_start: request.paragraph_start as i32,
        });
        placement.y += height;
        Ok(())
    }
}
fn ascent(style: &StyledTextStyle, font: &ManagedFont) -> f32 {
    max(
        0.0,
        f32::from(font.metadata().ascender) * style.font_size
            / f32::from(font.metadata().units_per_em)
            + style.baseline_shift,
    )
}
fn descent(style: &StyledTextStyle, font: &ManagedFont) -> f32 {
    max(
        0.0,
        -f32::from(font.metadata().descender) * style.font_size
            / f32::from(font.metadata().units_per_em)
            - style.baseline_shift,
    )
}
fn line_height(natural: f32, style: &StyledTextParagraphStyle) -> f32 {
    if style.minimum_line_spacing <= 0.0 {
        natural
    } else if style.exact_line_spacing {
        style.minimum_line_spacing
    } else {
        max(natural, style.minimum_line_spacing)
    }
}
fn line_start(style: &StyledTextParagraphStyle, first: bool) -> f32 {
    style.left_indent + if first { style.first_line_indent } else { 0.0 }
}
fn safe_break(candidates: &[Candidate], index: usize) -> bool {
    index == 0
        || index >= candidates.len()
        || (candidates[index - 1].cluster != candidates[index].cluster
            && candidates[index].flags & 1 == 0)
}
fn find_line_end(
    candidates: &[Candidate],
    start: usize,
    max_width: f32,
    style: &StyledTextParagraphStyle,
    first: bool,
) -> Result<usize, TextLayoutError> {
    if max_width.is_infinite() {
        return Ok(candidates.len());
    }
    let mut cursor = line_start(style, first);
    let limit = max_width - style.right_indent;
    if limit.partial_cmp(&cursor) != Some(std::cmp::Ordering::Greater) {
        return Err(TextLayoutError::InvalidArgument);
    }
    let mut last_whitespace = None;
    let mut last_safe = None;
    let view = Candidates {
        values: candidates,
        order: None,
    };
    for index in start..candidates.len() {
        if index > start && safe_break(candidates, index) {
            last_safe = Some(index);
        }
        let c = &candidates[index];
        if c.whitespace() && safe_break(candidates, index + 1) {
            last_whitespace = Some(index + 1);
        }
        let advance = if c.is_tab() {
            tab_advance(view, index, cursor, style)
        } else {
            c.advance
        };
        if index > start && cursor + advance > limit {
            if let Some(at) = last_whitespace.filter(|&at| at > start) {
                return Ok(at);
            }
            if let Some(at) = last_safe.filter(|&at| at > start) {
                return Ok(at);
            }
            let mut safe = index + 1;
            while safe < candidates.len() && !safe_break(candidates, safe) {
                safe += 1;
            }
            return Ok(safe);
        }
        cursor += advance;
    }
    Ok(candidates.len())
}
fn visual_order(
    logical: &[Candidate],
    range: Range<usize>,
    paragraph_level: i8,
) -> Result<Vec<usize>, TextLayoutError> {
    let mut starts = Vec::new();
    let mut levels = Vec::new();
    reserve(&mut starts, range.len() + 1)?;
    reserve(&mut levels, range.len())?;
    let mut at = range.start;
    while at < range.end {
        starts.push(at);
        levels.push(logical[at].level);
        let cluster = logical[at].cluster;
        at += 1;
        while at < range.end && logical[at].cluster == cluster {
            at += 1;
        }
    }
    starts.push(range.end);
    for group in (0..levels.len()).rev() {
        if !logical[starts[group]..starts[group + 1]]
            .iter()
            .all(Candidate::whitespace)
        {
            break;
        }
        levels[group] = paragraph_level;
    }
    let order = managed_visual_order(&levels)?;
    let mut visual = Vec::new();
    reserve(&mut visual, range.len())?;
    for group in order {
        visual.extend(starts[group]..starts[group + 1]);
    }
    Ok(visual)
}
fn whitespace_groups(candidates: Candidates<'_>) -> usize {
    let mut count = 0;
    let mut active = false;
    for index in 0..candidates.len() {
        let c = candidates.get(index);
        if c.whitespace() && !c.is_tab() {
            if !active {
                count += 1;
            }
            active = true;
        } else {
            active = false;
        }
    }
    count
}
fn tab_advance(
    candidates: Candidates<'_>,
    index: usize,
    cursor: f32,
    style: &StyledTextParagraphStyle,
) -> f32 {
    for stop in style.tab_stops.iter() {
        if stop.position.partial_cmp(&cursor) != Some(std::cmp::Ordering::Greater) {
            continue;
        }
        let offset = tab_field_offset(candidates, index + 1, stop.alignment);
        let advance = stop.position - cursor - offset;
        if advance >= 0.0 {
            return advance;
        }
    }
    let interval = style.default_tab_interval;
    let mut target = ((cursor / interval).floor() + 1.0) * interval;
    if let Some(last) = style.tab_stops.last() {
        target = max(target, last.position + interval);
    }
    max(0.0, target - cursor)
}
fn tab_field_offset(
    candidates: Candidates<'_>,
    start: usize,
    alignment: StyledTextTabAlignment,
) -> f32 {
    if alignment == StyledTextTabAlignment::Left {
        return 0.0;
    }
    let mut width = 0.0;
    let mut decimal_width = f32::NAN;
    for index in start..candidates.len() {
        let c = candidates.get(index);
        if c.is_tab() {
            break;
        }
        if decimal_width.is_nan() && matches!(c.code_point, 46 | 44 | 32) {
            decimal_width = width;
        }
        width += c.advance;
    }
    match alignment {
        StyledTextTabAlignment::Left => 0.0,
        StyledTextTabAlignment::Center => width * 0.5,
        StyledTextTabAlignment::Right => width,
        StyledTextTabAlignment::Decimal => {
            if decimal_width.is_nan() {
                width
            } else {
                decimal_width
            }
        }
    }
}
