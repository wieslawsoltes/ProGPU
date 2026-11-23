//! Native raw coverage/class searches, values, anchors and primitive lookups.
use super::*;
use crate::font::item_variation::ItemVariationStore;

impl SfntFace {
    /// Original native GDEF layout variation query. Empty coordinates avoid
    /// traversing GDEF; explicit zero coordinates still validate the store.
    /// Returns None for missing/unsupported stores, independently of zero delta.
    pub fn native_layout_variation(
        &self,
        outer: u16,
        inner: u16,
        coordinates: &[i16],
    ) -> Result<Option<f32>, FontError> {
        if coordinates.is_empty() {
            return Ok(None);
        }
        let Some(table) = self.table(Tag::new(*b"GDEF")) else {
            return Ok(None);
        };
        let data = table.bytes;
        require(fits(data, 0, 4))?;
        if u16_at(data, 0) != 1 || u16_at(data, 2) < 3 {
            return Ok(None);
        }
        require(fits(data, 0, 18))?;
        let offset = u32_at(data, 14) as usize;
        if offset == 0 {
            return Ok(None);
        }
        let store = ItemVariationStore::parse(data, offset, self.variation_axis_count()?)?;
        store.delta(coordinates, outer, inner).map(Some)
    }
}
#[derive(Clone, Copy)]
struct MarkHeader<'a> {
    index: usize,
    coverage: Coverage<'a>,
    classes: usize,
    mark: usize,
    target: usize,
}
fn value_size(format: u16) -> usize {
    format.count_ones() as usize * 2
}
impl<'t> Executor<'t, '_, '_, '_> {
    // Primitive native coverage/class readers validate extents but deliberately
    // do not impose the stricter full sorted-inventory policy of Context.
    fn raw_coverage(&self, at: usize, glyph: u16) -> Result<Option<usize>, FontError> {
        self.check(at, 4)?;
        let format = self.word(at);
        let count = usize::from(self.word(at + 2));
        let stride = match format {
            1 => 2,
            2 => 6,
            _ => return Err(FontError::InvalidFace),
        };
        self.check(at + 4, count * stride)?;
        let (mut low, mut high) = (0, count);
        while low < high {
            let middle = low + (high - low) / 2;
            let p = at + 4 + middle * stride;
            let start = self.word(p);
            let end = if format == 1 { start } else { self.word(p + 2) };
            if glyph < start {
                high = middle;
            } else if glyph > end {
                low = middle + 1;
            } else {
                return Ok(Some(if format == 1 {
                    middle
                } else {
                    usize::from(self.word(p + 4)) + usize::from(glyph - start)
                }));
            }
        }
        Ok(None)
    }
    fn raw_class(&self, at: usize, glyph: u16) -> Result<usize, FontError> {
        self.check(at, 4)?;
        match self.word(at) {
            1 => {
                self.check(at, 6)?;
                let start = self.word(at + 2);
                let count = usize::from(self.word(at + 4));
                self.check(at + 6, count * 2)?;
                let index = u32::from(glyph).wrapping_sub(u32::from(start));
                Ok(if (index as usize) < count {
                    usize::from(self.word(at + 6 + index as usize * 2))
                } else {
                    0
                })
            }
            2 => {
                let count = usize::from(self.word(at + 2));
                self.check(at + 4, count * 6)?;
                let (mut low, mut high) = (0, count);
                while low < high {
                    let mid = low + (high - low) / 2;
                    let p = at + 4 + mid * 6;
                    if glyph < self.word(p) {
                        high = mid;
                    } else if glyph > self.word(p + 2) {
                        low = mid + 1;
                    } else {
                        return Ok(usize::from(self.word(p + 4)));
                    }
                }
                Ok(0)
            }
            _ => Err(FontError::InvalidFace),
        }
    }
    fn device_delta(&self, parent: usize, offset: u16, ppem: u16) -> Result<(i32, i32), FontError> {
        if offset == 0 {
            return Ok((0, 0));
        }
        let at = relative(parent, u32::from(offset))?;
        self.check(at, 6)?;
        let first = self.word(at);
        let second = self.word(at + 2);
        let format = self.word(at + 4);
        if format == 0x8000 {
            let Some(font) = self.options.font else {
                return Ok((0, 0));
            };
            let Some(value) =
                font.native_layout_variation(first, second, self.options.normalized_coordinates)?
            else {
                return Ok((0, 0));
            };
            let rounded = f64::from(value).round();
            require(
                rounded.is_finite()
                    && rounded >= f64::from(i32::MIN)
                    && rounded <= f64::from(i32::MAX),
            )?;
            return Ok((rounded as i32, 0));
        }
        require((1..=3).contains(&format) && second >= first)?;
        let bits = 1u32 << format;
        let per_word = 16 / bits;
        let count = u32::from(second) - u32::from(first) + 1;
        self.check(at + 6, count.div_ceil(per_word) as usize * 2)?;
        let Some(font) = self.options.font else {
            return Ok((0, 0));
        };
        if ppem == 0 || ppem < first || ppem > second {
            return Ok((0, 0));
        }
        let index = u32::from(ppem - first);
        let packed = u32::from(self.word(at + 6 + (index / per_word) as usize * 2));
        let shift = 16 - bits * (index % per_word + 1);
        let mut pixels = ((packed >> shift) & ((1 << bits) - 1)) as i32;
        if pixels & (1 << (bits - 1)) != 0 {
            pixels -= 1 << bits;
        }
        if self.device.is_some() {
            return Ok((0, pixels * 64));
        }
        let units = font
            .header_metrics()
            .ok_or(FontError::InvalidFace)?
            .units_per_em;
        require(units != 0)?;
        Ok((
            (f64::from(pixels) * f64::from(units) / f64::from(ppem)).round() as i32,
            0,
        ))
    }
    fn value(&self, parent: usize, at: usize, format: u16) -> Result<[i32; 4], FontError> {
        require(format & 0xff00 == 0)?;
        self.check(at, value_size(format))?;
        let mut cursor = at;
        let mut design = [0i32; 4];
        let mut pixels = [0i32; 4];
        for (i, value) in design.iter_mut().enumerate() {
            if format & (1 << i) != 0 {
                *value = i32::from(self.word(cursor) as i16);
                cursor += 2;
            }
        }
        for i in 0..4 {
            if format & (16 << i) != 0 {
                let (delta, device) = self.device_delta(
                    parent,
                    self.word(cursor),
                    self.options.pixels_per_em[i % 2],
                )?;
                cursor += 2;
                design[i] = if self.device.is_some() {
                    checked(i64::from(design[i]) + i64::from(delta))?
                } else {
                    design[i].wrapping_add(delta)
                };
                pixels[i] = device;
            }
        }
        if let Some(frame) = self.device {
            let v = frame
                .source
                .project_design([[design[0], design[1]], [design[2], design[3]]])
                .map_err(|_| FontError::InvalidFace)?;
            return add_metric(
                [v[0][0], v[0][1], v[1][0], v[1][1]],
                pixels,
                frame.arithmetic,
            );
        }
        Ok(design)
    }
    fn adjusted(&self, glyph: ShapingGlyph, value: [i32; 4]) -> Result<ShapingGlyph, FontError> {
        let current = [
            glyph.offset_x,
            glyph.offset_y,
            glyph.advance_x,
            glyph.advance_y,
        ];
        let v = if let Some(frame) = self.device {
            add_metric(current, value, frame.arithmetic)?
        } else {
            std::array::from_fn(|i| current[i].wrapping_add(value[i]))
        };
        Ok(ShapingGlyph {
            offset_x: v[0],
            offset_y: v[1],
            advance_x: v[2],
            advance_y: v[3],
            ..glyph
        })
    }
    fn anchor(&self, at: usize, p: usize) -> Result<[i32; 2], FontError> {
        self.check(at, 6)?;
        let format = self.word(at);
        require((1..=3).contains(&format))?;
        if format == 2 {
            self.check(at, 8)?;
        }
        if format == 3 {
            self.check(at, 10)?;
        }
        let mut design = [
            i32::from(self.word(at + 2) as i16),
            i32::from(self.word(at + 4) as i16),
        ];
        if format == 2 {
            if let Some(frame) = self.device {
                return frame
                    .source
                    .contour_point(p, self.glyphs[p].glyph_id, self.word(at + 6))
                    .map_err(|_| FontError::InvalidFace);
            }
        }
        let mut pixels = [0; 2];
        if format == 3 {
            for axis in 0..2 {
                let (delta, device) = self.device_delta(
                    at,
                    self.word(at + 6 + axis * 2),
                    self.options.pixels_per_em[axis],
                )?;
                design[axis] = if self.device.is_some() {
                    checked(i64::from(design[axis]) + i64::from(delta))?
                } else {
                    design[axis].wrapping_add(delta)
                };
                pixels[axis] = device;
            }
        }
        if let Some(frame) = self.device {
            let v = frame
                .source
                .project_design([design, [0; 2]])
                .map_err(|_| FontError::InvalidFace)?;
            let v = add_metric(
                [v[0][0], v[0][1], 0, 0],
                [pixels[0], pixels[1], 0, 0],
                frame.arithmetic,
            )?;
            return Ok([v[0], v[1]]);
        }
        Ok(design)
    }
    pub(super) fn single(&mut self, s: usize, p: usize) -> Applied {
        self.check(s, 6)?;
        let format = self.word(s);
        let value_format = self.word(s + 4);
        let Some(index) = self.raw_coverage(
            relative(s, u32::from(self.word(s + 2)))?,
            self.glyphs[p].glyph_id as u16,
        )?
        else {
            return Ok(false);
        };
        let at = match format {
            1 => s + 6,
            2 => {
                self.check(s + 6, 2)?;
                let count = usize::from(self.word(s + 6));
                require(index < count)?;
                self.check(s + 8, count * value_size(value_format))?;
                s + 8 + index * value_size(value_format)
            }
            _ => return Err(FontError::InvalidFace),
        };
        self.glyphs[p] = self.adjusted(self.glyphs[p], self.value(s, at, value_format)?)?;
        self.positioned(p);
        Ok(true)
    }
    pub(super) fn pair(&mut self, s: usize, frame: Frame) -> Applied {
        self.check(s, 10)?;
        let format = self.word(s);
        let f1 = self.word(s + 4);
        let f2 = self.word(s + 6);
        require((f1 | f2) & 0xff00 == 0)?;
        let p = frame.position;
        let Some(index) = self.raw_coverage(
            relative(s, u32::from(self.word(s + 2)))?,
            self.glyphs[p].glyph_id as u16,
        )?
        else {
            return Ok(false);
        };
        let second = self.next(p + 1, frame);
        if second == self.glyphs.len() {
            return Ok(false);
        }
        let n1 = value_size(f1);
        let n2 = value_size(f2);
        let (mut parent, mut value_at) = (s, 0);
        match format {
            1 => {
                let count = usize::from(self.word(s + 8));
                require(index < count)?;
                self.check(s + 10, count * 2)?;
                parent = relative(s, u32::from(self.word(s + 10 + index * 2)))?;
                self.check(parent, 2)?;
                let count = usize::from(self.word(parent));
                let stride = 2 + n1 + n2;
                self.check(parent + 2, count * stride)?;
                let (mut low, mut high) = (0, count);
                while low < high {
                    let mid = low + (high - low) / 2;
                    let at = parent + 2 + mid * stride;
                    let glyph = u32::from(self.word(at));
                    if self.glyphs[second].glyph_id < glyph {
                        high = mid;
                    } else if self.glyphs[second].glyph_id > glyph {
                        low = mid + 1;
                    } else {
                        value_at = at + 2;
                        break;
                    }
                }
                if value_at == 0 {
                    return Ok(false);
                }
            }
            2 => {
                self.check(s, 16)?;
                let c1 = self.raw_class(
                    relative(s, u32::from(self.word(s + 8)))?,
                    self.glyphs[p].glyph_id as u16,
                )?;
                let c2 = self.raw_class(
                    relative(s, u32::from(self.word(s + 10)))?,
                    self.glyphs[second].glyph_id as u16,
                )?;
                let count1 = usize::from(self.word(s + 12));
                let count2 = usize::from(self.word(s + 14));
                if c1 >= count1 || c2 >= count2 {
                    return Ok(false);
                }
                value_at = c1
                    .checked_mul(count2)
                    .and_then(|v| v.checked_add(c2))
                    .and_then(|v| v.checked_mul(n1 + n2))
                    .and_then(|v| (s + 16).checked_add(v))
                    .ok_or(FontError::InvalidFace)?;
                self.check(value_at, n1 + n2)?;
            }
            _ => return Err(FontError::InvalidFace),
        }
        let a = self.value(parent, value_at, f1)?;
        let b = self.value(parent, value_at + n1, f2)?;
        let first = self.adjusted(self.glyphs[p], a)?;
        let second_value = self.adjusted(self.glyphs[second], b)?;
        self.glyphs[p] = first;
        self.glyphs[second] = second_value;
        self.positioned(p);
        self.positioned(second);
        self.dependency(p, second);
        Ok(true)
    }
    pub(super) fn cursive(&mut self, s: usize, frame: Frame) -> Applied {
        self.check(s, 6)?;
        require(self.word(s) == 1)?;
        let coverage = relative(s, u32::from(self.word(s + 2)))?;
        let p = frame.position;
        let Some(current) = self.raw_coverage(coverage, self.glyphs[p].glyph_id as u16)? else {
            return Ok(false);
        };
        let count = usize::from(self.word(s + 4));
        require(current < count)?;
        self.check(s + 6, count * 4)?;
        let entry = self.word(s + 6 + current * 4);
        if entry == 0 {
            return Ok(false);
        }
        let previous = self.previous(p, frame, false);
        if previous >= self.glyphs.len() {
            return Ok(false);
        }
        let Some(first_index) =
            self.raw_coverage(coverage, self.glyphs[previous].glyph_id as u16)?
        else {
            return Ok(false);
        };
        if first_index >= count {
            return Ok(false);
        }
        let exit = self.word(s + 8 + first_index * 4);
        require(exit != 0)?;
        let entry = self.anchor(relative(s, u32::from(entry))?, p)?;
        let exit = self.anchor(relative(s, u32::from(exit))?, previous)?;
        let (mut first, mut current) = (self.glyphs[previous], self.glyphs[p]);
        let calc = |value: i64| {
            if self.device.is_some() {
                checked(value)
            } else {
                Ok(value as i32)
            }
        };
        let horizontal = matches!(
            self.options.direction,
            GposDirection::LeftToRight | GposDirection::RightToLeft
        );
        match self.options.direction {
            GposDirection::LeftToRight => {
                let delta = i64::from(entry[0]) + i64::from(current.offset_x);
                first.advance_x = calc(i64::from(exit[0]) + i64::from(first.offset_x))?;
                current.advance_x = calc(i64::from(current.advance_x) - delta)?;
                current.offset_x = calc(i64::from(current.offset_x) - delta)?;
            }
            GposDirection::RightToLeft => {
                let delta = i64::from(exit[0]) + i64::from(first.offset_x);
                first.advance_x = calc(i64::from(first.advance_x) - delta)?;
                first.offset_x = calc(i64::from(first.offset_x) - delta)?;
                current.advance_x = calc(i64::from(entry[0]) + i64::from(current.offset_x))?;
            }
            GposDirection::TopToBottom => {
                let delta = i64::from(entry[1]) + i64::from(current.offset_y);
                first.advance_y = calc(i64::from(exit[1]) + i64::from(first.offset_y))?;
                current.advance_y = calc(i64::from(current.advance_y) - delta)?;
                current.offset_y = calc(i64::from(current.offset_y) - delta)?;
            }
            GposDirection::BottomToTop => {
                let delta = i64::from(exit[1]) + i64::from(first.offset_y);
                first.advance_y = calc(i64::from(first.advance_y) - delta)?;
                first.offset_y = calc(i64::from(first.offset_y) - delta)?;
                current.advance_y = entry[1];
            }
            GposDirection::Unspecified => return Err(FontError::InvalidFace),
        }
        let reverse = frame.flags & 1 != 0;
        let (child, parent) = if reverse {
            (previous, p)
        } else {
            (p, previous)
        };
        let axis = usize::from(horizontal);
        let cross = if reverse {
            i64::from(entry[axis]) - i64::from(exit[axis])
        } else {
            i64::from(exit[axis]) - i64::from(entry[axis])
        };
        let cross = calc(cross)?;
        let child_glyph = if reverse { &mut first } else { &mut current };
        if horizontal {
            child_glyph.offset_y = cross;
        } else {
            child_glyph.offset_x = cross;
        }
        self.glyphs[previous] = first;
        self.glyphs[p] = current;
        let kind = if horizontal {
            GposAttachmentKind::CursiveHorizontal
        } else {
            GposAttachmentKind::CursiveVertical
        };
        self.attachments[child].target = parent as i32;
        self.attachments[child].kind = kind;
        if self.attachments[parent].target == child as i32 && self.attachments[parent].kind == kind
        {
            self.attachments[parent].target = -1;
            self.attachments[parent].kind = GposAttachmentKind::None;
            if horizontal {
                self.glyphs[parent].offset_y = 0;
            } else {
                self.glyphs[parent].offset_x = 0;
            }
        }
        self.dependency(previous.min(p), previous.max(p));
        self.positioned(previous);
        self.positioned(p);
        Ok(true)
    }
    fn mark_header(&self, s: usize, p: usize) -> Result<Option<MarkHeader<'t>>, FontError> {
        self.check(s, 12)?;
        require(self.word(s) == 1)?;
        let data = self.table.bytes();
        let coverage = Coverage::parse(data, relative(s, u32::from(self.word(s + 2)))?)?;
        let target_coverage = Coverage::parse(data, relative(s, u32::from(self.word(s + 4)))?)?;
        let mark = relative(s, u32::from(self.word(s + 8)))?;
        let target = relative(s, u32::from(self.word(s + 10)))?;
        self.check(mark, 2)?;
        self.check(target, 2)?;
        let Some(index) = coverage.find(self.glyphs[p].glyph_id as u16) else {
            return Ok(None);
        };
        let classes = usize::from(self.word(s + 6));
        require(classes != 0)?;
        Ok(Some(MarkHeader {
            index: index as usize,
            coverage: target_coverage,
            classes,
            mark,
            target,
        }))
    }
    fn attach_mark(
        &mut self,
        mark: usize,
        target: usize,
        header: MarkHeader<'_>,
        coverage: usize,
        base: usize,
        records: usize,
    ) -> Applied {
        let count = usize::from(self.word(header.mark));
        require(header.index < count)?;
        self.check(header.mark + 2, count * 4)?;
        let record = header.mark + 2 + header.index * 4;
        let class = usize::from(self.word(record));
        let mark_anchor = self.word(record + 2);
        require(class < header.classes && mark_anchor != 0)?;
        let target_record = coverage
            .checked_mul(header.classes)
            .and_then(|v| v.checked_add(class))
            .and_then(|v| v.checked_mul(2))
            .and_then(|v| records.checked_add(v))
            .ok_or(FontError::InvalidFace)?;
        self.check(target_record, 2)?;
        let target_anchor = self.word(target_record);
        if target_anchor == 0 {
            return Ok(false);
        }
        let a = self.anchor(relative(header.mark, u32::from(mark_anchor))?, mark)?;
        let b = self.anchor(relative(base, u32::from(target_anchor))?, target)?;
        let x = i64::from(b[0]) - i64::from(a[0]);
        let y = i64::from(b[1]) - i64::from(a[1]);
        let offset = if self.device.is_some() {
            [checked(x)?, checked(y)?]
        } else {
            [x as i32, y as i32]
        };
        self.glyphs[mark].offset_x = offset[0];
        self.glyphs[mark].offset_y = offset[1];
        self.dependency(mark.min(target), mark.max(target));
        self.attachments[mark].target = target as i32;
        self.attachments[mark].kind = GposAttachmentKind::Mark;
        self.positioned(mark);
        Ok(true)
    }
    pub(super) fn mark(&mut self, s: usize, kind: u16, frame: Frame) -> Applied {
        let mark = frame.position;
        // The view's lifetime is the immutable table, independent of glyph writes.
        let Some(header) = self.mark_header(s, mark)? else {
            return Ok(false);
        };
        let target = self.previous(mark, frame, kind != 6);
        if target >= self.glyphs.len() {
            return Ok(false);
        }
        if let Some(gdef) = self.options.gdef {
            let class = gdef.glyph_class(self.glyphs[target].glyph_id as u16);
            if kind == 4 && !matches!(class, GlyphClass::Base | GlyphClass::Unclassified)
                || kind == 5 && class != GlyphClass::Ligature
                || kind == 6 && class != GlyphClass::Mark
            {
                return Ok(false);
            }
        }
        if kind == 6 {
            let first = self.attachments[mark].component;
            let second = self.attachments[target].component;
            if first != 255 && second != 255 && first != second {
                return Ok(false);
            }
        }
        let Some(coverage) = header.coverage.find(self.glyphs[target].glyph_id as u16) else {
            return Ok(false);
        };
        let coverage = coverage as usize;
        let count = usize::from(self.word(header.target));
        require(coverage < count)?;
        let (mut base, mut records, mut row) = (header.target, header.target + 2, coverage);
        if kind == 5 {
            self.check(header.target + 2, count * 2)?;
            base = relative(
                header.target,
                u32::from(self.word(header.target + 2 + coverage * 2)),
            )?;
            self.check(base, 2)?;
            let count = usize::from(self.word(base));
            require(count != 0)?;
            let mut component = count - 1;
            if self.attachments[mark].component != 255 {
                component = component.min(usize::from(self.attachments[mark].component));
            } else if self.attachments[target].component_count != 0 {
                component =
                    component.min(usize::from(self.attachments[target].component_count - 1));
            }
            records = component
                .checked_mul(header.classes)
                .and_then(|v| v.checked_mul(2))
                .and_then(|v| (base + 2).checked_add(v))
                .ok_or(FontError::InvalidFace)?;
            row = 0;
        }
        self.attach_mark(mark, target, header, row, base, records)
    }
}
