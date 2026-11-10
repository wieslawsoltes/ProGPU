//! Original TextLayout shaping, safe wrapping, L1/L2 and sequential placement.
use super::*;
use crate::text::{
    ShapingGlyph,
    managed_pipeline::{shape_design_units, shape_scaled},
    preparation::ManagedBufferFlags,
    unicode::bidi::managed_visual_order,
};

#[derive(Clone, Debug)]
struct Candidate {
    font: ManagedFont,
    glyph_index: u16,
    cluster: i32,
    code_point: u32,
    level: i8,
    advance_x: f32,
    advance_y: f32,
    offset_x: f32,
    offset_y: f32,
    flags: u32,
}
impl Candidate {
    fn whitespace(&self) -> bool {
        matches!(self.code_point, 0x20 | 0x09)
    }
}
impl ManagedTextLayout {
    pub(super) fn generate<F: ManagedFontFallback + ?Sized>(
        &mut self,
        fallback: &F,
    ) -> Result<(), TextLayoutError> {
        if self.text.is_empty() {
            if let Some(metrics) = &self.options.formatting.source_device_line_metrics {
                reserve(&mut self.lines, 1)?;
                self.lines.push(LineRange {
                    start: 0,
                    count: 0,
                    left: 0.0,
                    top: 0.0,
                    height: metrics.metrics.height() as f32,
                    source_start: 0,
                    source_end: 0,
                    paragraph_level: 0,
                });
            }
            return Ok(());
        }
        let mut shaped = Vec::new();
        if self.vertical() {
            return self.generate_vertical(fallback, &mut shaped);
        }
        if self.try_ascii(&mut shaped)? {
            return Ok(());
        }
        let (height, ascent) = self.line_metrics();
        let mut widths = Vec::new();
        let mut y = 0.0;
        let mut source_start = 0;
        while source_start <= self.text.len() {
            let newline = self.text[source_start..]
                .iter()
                .position(|&c| matches!(c, 13 | 10))
                .map(|i| source_start + i);
            let source_end = newline.unwrap_or(self.text.len());
            let paragraph = ManagedBidiParagraph::resolve(
                &self.text[source_start..source_end],
                self.options.shaping.direction,
            )?;
            let candidates =
                self.shape_range(source_start, source_end, &paragraph, fallback, &mut shaped)?;
            if candidates.is_empty() {
                reserve(&mut self.lines, 1)?;
                reserve(&mut widths, 1)?;
                self.lines.push(LineRange {
                    start: self.glyphs.len(),
                    count: 0,
                    left: 0.0,
                    top: y,
                    height,
                    source_start: source_start as i32,
                    source_end: source_end as i32,
                    paragraph_level: paragraph.paragraph_level(),
                });
                widths.push(0.0);
                y += height;
            } else {
                let mut start = 0;
                while start < candidates.len() {
                    let mut width = 0.0;
                    let mut last_break = None;
                    let mut last_safe = None;
                    let mut end = start;
                    while end < candidates.len() {
                        let candidate = &candidates[end];
                        if end > start && safe_break(&candidates, end) {
                            last_safe = Some(end);
                        }
                        if candidate.whitespace() && safe_break(&candidates, end + 1) {
                            last_break = Some(end + 1);
                        }
                        let advance = self.advance(candidate, width);
                        if !self.options.max_width.is_infinite()
                            && width + advance > self.options.max_width
                            && end > start
                        {
                            end = if let Some(at) = last_break.filter(|&at| at > start) {
                                at
                            } else if let Some(at) = last_safe.filter(|&at| at > start) {
                                at
                            } else {
                                next_safe(&candidates, end + 1)
                            };
                            break;
                        }
                        width += advance;
                        end += 1;
                    }
                    if end == start {
                        end += 1;
                    }
                    let glyph_start = self.glyphs.len();
                    let visual = visual_line(&candidates, start, end, paragraph.paragraph_level())?;
                    reserve(&mut self.glyphs, visual.len())?;
                    let mut x = 0.0;
                    for index in visual {
                        let c = &candidates[index];
                        let advance = self.advance(c, x);
                        self.glyphs.push(self.place(
                            c,
                            [x + c.offset_x, y + ascent + c.offset_y],
                            advance,
                            height,
                            advance,
                        ));
                        x += advance;
                    }
                    reserve(&mut self.lines, 1)?;
                    reserve(&mut widths, 1)?;
                    self.lines.push(LineRange {
                        start: glyph_start,
                        count: self.glyphs.len() - glyph_start,
                        left: 0.0,
                        top: y,
                        height,
                        source_start: if start == 0 {
                            source_start as i32
                        } else {
                            candidates[start].cluster
                        },
                        source_end: source_end as i32,
                        paragraph_level: paragraph.paragraph_level(),
                    });
                    widths.push(self.measured_width(&candidates[start..end]));
                    y += height;
                    start = end;
                }
            }
            let Some(newline) = newline else {
                break;
            };
            source_start = newline
                + if self.text[newline] == 13 && self.text.get(newline + 1) == Some(&10) {
                    2
                } else {
                    1
                };
        }
        let mut max_width = 0.0;
        for (index, width) in widths.into_iter().enumerate() {
            max_width = max(max_width, width);
            let shift = self.alignment_shift(width);
            if shift <= 0.0 || shift.is_infinite() {
                continue;
            }
            let line = &mut self.lines[index];
            line.left = shift;
            for glyph in &mut self.glyphs[line.start..line.start + line.count] {
                glyph.position[0] += shift;
            }
        }
        self.content_size = [max_width, y];
        self.measured_size = [
            if self.options.max_width.is_infinite() {
                max_width
            } else {
                self.options.max_width
            },
            y,
        ];
        Ok(())
    }
    fn place(
        &self,
        c: &Candidate,
        position: [f32; 2],
        width: f32,
        height: f32,
        advance: f32,
    ) -> TextRunGlyph {
        TextRunGlyph {
            character: self.text[c.cluster.clamp(0, self.text.len() as i32 - 1) as usize],
            code_point: c.code_point,
            glyph_index: c.glyph_index,
            position,
            width: max(0.0, width.ceil()) as u32,
            height: max(0.0, height.ceil()) as u32,
            advance,
            font: c.font.clone(),
            cluster: c.cluster,
            bidi_level: c.level,
            shaping_flags: c.flags,
        }
    }
    fn try_ascii(&mut self, shaped: &mut Vec<ShapingGlyph>) -> Result<bool, TextLayoutError> {
        if self.options.alignment != TextAlignment::Left
            || (!self.options.formatting.measure_trailing_whitespace
                && self.text.last() == Some(&32))
            || !matches!(
                self.options.shaping.direction,
                None | Some(Direction::LeftToRight)
            )
            || self.text.iter().any(|&c| !(32..=126).contains(&c))
        {
            return Ok(false);
        }
        shape_design_units(
            self.text.clone(),
            self.font.clone(),
            self.options.shaping.clone(),
            self.preparation.clone(),
            shaped,
        )?;
        let scale = self.font_size / f32::from(self.font.metadata().units_per_em);
        let mut width = 0.0;
        for glyph in shaped.iter() {
            width += glyph.advance_x as f32 * scale;
        }
        if !self.options.max_width.is_infinite() && width > self.options.max_width {
            return Ok(false);
        }
        let (height, ascent) = self.line_metrics();
        reserve(&mut self.glyphs, shaped.len())?;
        let mut x = 0.0;
        for g in shaped.iter() {
            if g.glyph_id == 0 {
                self.glyphs.clear();
                return Ok(false);
            }
            let advance = g.advance_x as f32 * scale;
            self.glyphs.push(TextRunGlyph {
                character: self.text[g.cluster.clamp(0, self.text.len() as i32 - 1) as usize],
                code_point: g.code_point,
                glyph_index: u16::try_from(g.glyph_id).map_err(|_| TextLayoutError::Capacity)?,
                position: [
                    x + g.offset_x as f32 * scale,
                    ascent + g.offset_y as f32 * scale,
                ],
                width: max(0.0, advance.ceil()) as u32,
                height: max(0.0, height.ceil()) as u32,
                advance,
                font: self.font.clone(),
                cluster: g.cluster,
                bidi_level: 0,
                shaping_flags: g.flags,
            });
            x += advance;
        }
        reserve(&mut self.lines, 1)?;
        self.lines.push(LineRange {
            start: 0,
            count: self.glyphs.len(),
            left: 0.0,
            top: 0.0,
            height,
            source_start: 0,
            source_end: self.text.len() as i32,
            paragraph_level: 0,
        });
        self.content_size = [width, height];
        self.measured_size = [
            if self.options.max_width.is_infinite() {
                width
            } else {
                self.options.max_width
            },
            height,
        ];
        Ok(true)
    }
    fn generate_vertical<F: ManagedFontFallback + ?Sized>(
        &mut self,
        fallback: &F,
        shaped: &mut Vec<ShapingGlyph>,
    ) -> Result<(), TextLayoutError> {
        let (spacing, _) = self.line_metrics();
        let mut x = 0.0;
        let mut max_height = 0.0;
        let mut start = 0;
        while start <= self.text.len() {
            // Original vertical writer splits LF only; CR keeps its original route.
            let newline = self.text[start..]
                .iter()
                .position(|&c| c == 10)
                .map(|i| start + i);
            let end = newline.unwrap_or(self.text.len());
            let paragraph = ManagedBidiParagraph::resolve(
                &self.text[start..end],
                self.options.shaping.direction,
            )?;
            let candidates = self.shape_range(start, end, &paragraph, fallback, shaped)?;
            reserve(&mut self.glyphs, candidates.len())?;
            let mut y = 0.0;
            for c in candidates {
                self.glyphs.push(self.place(
                    &c,
                    [x + spacing * 0.5 + c.offset_x, y + c.offset_y],
                    spacing,
                    c.advance_y.abs(),
                    c.advance_y,
                ));
                y += c.advance_y;
            }
            max_height = max(max_height, y.abs());
            x += spacing;
            let Some(newline) = newline else {
                break;
            };
            start = newline + 1;
        }
        let shift = self.alignment_shift(x);
        if shift > 0.0 && !shift.is_infinite() {
            for glyph in &mut self.glyphs {
                glyph.position[0] += shift;
            }
        }
        self.content_size = [x, max_height];
        self.measured_size = [
            if self.options.max_width.is_infinite() {
                x
            } else {
                self.options.max_width
            },
            max_height,
        ];
        Ok(())
    }
    fn shape_range<F: ManagedFontFallback + ?Sized>(
        &self,
        start: usize,
        end: usize,
        paragraph: &ManagedBidiParagraph,
        fallback: &F,
        shaped: &mut Vec<ShapingGlyph>,
    ) -> Result<Vec<Candidate>, TextLayoutError> {
        let mut candidates = Vec::new();
        reserve(&mut candidates, (end - start).max(1))?;
        for bidi in paragraph.runs() {
            let run_start = start + bidi.start;
            let run_end = run_start + bidi.length;
            let mut font_start = run_start;
            let mut run_font: Option<ManagedFont> = None;
            let mut at = run_start;
            while at < run_end {
                let scalar_start = at;
                let first = self.text[at];
                at += 1;
                let cp = if (0xd800..=0xdbff).contains(&first)
                    && at < run_end
                    && (0xdc00..=0xdfff).contains(&self.text[at])
                {
                    let low = self.text[at];
                    at += 1;
                    0x10000 + ((u32::from(first) - 0xd800) << 10) + u32::from(low) - 0xdc00
                } else {
                    u32::from(first)
                };
                let mut resolved = self.font.clone();
                let glyph = self.font.glyph_index(cp)?;
                if self.options.formatting.enable_font_fallback
                    && glyph == 0
                    && !matches!(cp, 32 | 9)
                {
                    if let Some(previous) = run_font
                        .as_ref()
                        .filter(|_| crate::font::layout::default_ignorable(cp))
                    {
                        resolved = previous.clone();
                    } else if let Some(selected) = fallback.resolve(&self.font, cp)? {
                        resolved = selected;
                    }
                }
                if let Some(previous) = &run_font {
                    if !previous.same_instance(&resolved) {
                        self.append_run(
                            &mut candidates,
                            font_start..scalar_start,
                            previous,
                            bidi.level,
                            start..end,
                            shaped,
                        )?;
                        font_start = scalar_start;
                        run_font = Some(resolved);
                    }
                } else {
                    run_font = Some(resolved);
                    font_start = scalar_start;
                }
            }
            if let Some(font) = run_font.filter(|_| font_start < run_end) {
                self.append_run(
                    &mut candidates,
                    font_start..run_end,
                    &font,
                    bidi.level,
                    start..end,
                    shaped,
                )?;
            }
        }
        Ok(candidates)
    }
    fn append_run(
        &self,
        candidates: &mut Vec<Candidate>,
        range: std::ops::Range<usize>,
        font: &ManagedFont,
        level: i8,
        paragraph: std::ops::Range<usize>,
        shaped: &mut Vec<ShapingGlyph>,
    ) -> Result<(), TextLayoutError> {
        let text = if range.start == 0 && range.end == self.text.len() {
            self.text.clone()
        } else {
            Arc::from(&self.text[range.clone()])
        };
        let mut prep = self.preparation.clone();
        if range.start == paragraph.start {
            prep.buffer_flags = prep.buffer_flags | ManagedBufferFlags::BEGINNING_OF_TEXT;
        }
        if range.end == paragraph.end {
            prep.buffer_flags = prep.buffer_flags | ManagedBufferFlags::END_OF_TEXT;
        }
        prep.pre_context = crate::text::preparation::ManagedTextContext::view(
            self.text.clone(),
            paragraph.start..range.start,
        )?;
        prep.post_context = crate::text::preparation::ManagedTextContext::view(
            self.text.clone(),
            range.end..paragraph.end,
        )?;
        let mut options = self.options.shaping.clone();
        if self.vertical() {
            let glyphs = shape_scaled(text, font.clone(), self.font_size, options, prep)?;
            reserve(candidates, glyphs.len())?;
            for g in glyphs {
                candidates.push(Candidate {
                    font: font.clone(),
                    glyph_index: g.glyph_index,
                    cluster: (range.start as i32).wrapping_add(g.cluster),
                    code_point: g.code_point,
                    level,
                    advance_x: g.advance_x,
                    advance_y: g.advance_y,
                    offset_x: g.offset_x,
                    offset_y: g.offset_y,
                    flags: g.flags,
                });
            }
            return Ok(());
        }
        options.direction = Some(if level & 1 == 0 {
            Direction::LeftToRight
        } else {
            Direction::RightToLeft
        });
        shape_design_units(text, font.clone(), options, prep, shaped)?;
        let scale = self.font_size / f32::from(font.metadata().units_per_em);
        reserve(candidates, shaped.len())?;
        let mut append = |glyphs: &[ShapingGlyph]| -> Result<(), TextLayoutError> {
            for g in glyphs {
                candidates.push(Candidate {
                    font: font.clone(),
                    glyph_index: u16::try_from(g.glyph_id)
                        .map_err(|_| TextLayoutError::Capacity)?,
                    cluster: (range.start as i32).wrapping_add(g.cluster),
                    code_point: g.code_point,
                    level,
                    advance_x: g.advance_x as f32 * scale,
                    advance_y: g.advance_y as f32 * scale,
                    offset_x: g.offset_x as f32 * scale,
                    offset_y: g.offset_y as f32 * scale,
                    flags: g.flags,
                });
            }
            Ok(())
        };
        if level & 1 == 0 || shaped.len() < 2 {
            append(shaped)?;
        } else {
            let mut end = shaped.len();
            while end > 0 {
                let mut start = end - 1;
                while start > 0 && shaped[start - 1].cluster == shaped[end - 1].cluster {
                    start -= 1;
                }
                append(&shaped[start..end])?;
                end = start;
            }
        }
        Ok(())
    }
    fn advance(&self, c: &Candidate, current: f32) -> f32 {
        let format = &self.options.formatting;
        if c.code_point != 9 || format.tab_stops.is_empty() {
            return c.advance_x;
        }
        let mut stop = format.first_tab_offset;
        let mut repeat = 0.0;
        for &interval in format.tab_stops.iter() {
            if !interval.is_finite() || interval <= 0.0 {
                continue;
            }
            stop += interval;
            repeat = interval;
            if stop > current + 0.001 {
                return stop - current;
            }
        }
        if repeat > 0.0 {
            let repeats = ((current - stop) / repeat).floor() + 1.0;
            return stop + max(1.0, repeats) * repeat - current;
        }
        c.advance_x
    }
    fn measured_width(&self, candidates: &[Candidate]) -> f32 {
        let mut width = 0.0;
        let mut measured = 0.0;
        for c in candidates {
            width += self.advance(c, width);
            if self.options.formatting.measure_trailing_whitespace || !c.whitespace() {
                measured = width;
            }
        }
        measured
    }
}
fn safe_break(candidates: &[Candidate], at: usize) -> bool {
    at == 0
        || at >= candidates.len()
        || (candidates[at - 1].cluster != candidates[at].cluster && candidates[at].flags & 1 == 0)
}
fn next_safe(candidates: &[Candidate], at: usize) -> usize {
    let mut at = at.min(candidates.len());
    while at < candidates.len() && !safe_break(candidates, at) {
        at += 1;
    }
    at
}
fn visual_line(
    candidates: &[Candidate],
    start: usize,
    end: usize,
    paragraph_level: i8,
) -> Result<Vec<usize>, TextLayoutError> {
    let mut starts = Vec::new();
    let mut levels = Vec::new();
    reserve(&mut starts, end - start + 1)?;
    reserve(&mut levels, end - start)?;
    let mut at = start;
    while at < end {
        starts.push(at);
        levels.push(candidates[at].level);
        let cluster = candidates[at].cluster;
        at += 1;
        while at < end && candidates[at].cluster == cluster {
            at += 1;
        }
    }
    starts.push(end);
    for group in (0..levels.len()).rev() {
        if !candidates[starts[group]..starts[group + 1]]
            .iter()
            .all(Candidate::whitespace)
        {
            break;
        }
        levels[group] = paragraph_level;
    }
    let visual = managed_visual_order(&levels)?;
    let mut result = Vec::new();
    reserve(&mut result, end - start)?;
    for group in visual {
        result.extend(starts[group]..starts[group + 1]);
    }
    Ok(result)
}
