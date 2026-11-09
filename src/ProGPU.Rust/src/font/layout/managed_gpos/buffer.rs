use super::*;
use crate::text::unicode::ManagedMarkProfile;
/// Original managed positioning direction; independent of lookup RTL flags.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum ManagedPositionDirection {
    /// Original unspecified direction; no cursive major-axis adjustment.
    Unspecified = 0,
    /// Logical left-to-right horizontal placement.
    #[default]
    LeftToRight = 1,
    /// Logical right-to-left horizontal placement.
    RightToLeft = 2,
    /// Logical top-to-bottom vertical placement.
    TopToBottom = 3,
    /// Logical bottom-to-top vertical placement.
    BottomToTop = 4,
}
impl TryFrom<u8> for ManagedPositionDirection {
    type Error = FontError;
    fn try_from(value: u8) -> Result<Self, FontError> {
        match value {
            0 => Ok(Self::Unspecified),
            1 => Ok(Self::LeftToRight),
            2 => Ok(Self::RightToLeft),
            3 => Ok(Self::TopToBottom),
            4 => Ok(Self::BottomToTop),
            _ => Err(FontError::InvalidArgument),
        }
    }
}
/// Owned already-prepared managed position records and actual font generation.
/// Source metric initialization, fallback and output ordering are separate stages.
/// GPOS keeps all 31 fields, original per-add saturation and independent filtering.
#[derive(Clone, Debug)]
pub struct ManagedPositionBuffer {
    pub(super) glyphs: Vec<ManagedShapingGlyph>,
    font: SfntFace,
    use_gdef: bool,
    profile: ManagedMarkProfile,
    direction: ManagedPositionDirection,
    pub(super) mark_coverage: i32,
    states: Vec<u8>,
    path: Vec<usize>,
    reversals: Vec<(usize, usize, i16)>,
}
fn clamp(value: i32) -> i16 {
    value.clamp(i16::MIN as i32, i16::MAX as i32) as i16
}
fn add(left: i16, right: i32) -> i16 {
    clamp(i32::from(left).wrapping_add(right))
}
impl ManagedPositionBuffer {
    /// Retain already-initialized design-unit metrics; no source preparation is inferred.
    pub fn new(
        font: SfntFace,
        glyphs: Vec<ManagedShapingGlyph>,
        direction: ManagedPositionDirection,
    ) -> Result<Self, FontError> {
        Self::with_mark_profile(font, glyphs, direction, ManagedMarkProfile::default())
    }
    /// Select an independently witnessed original Rune category repertoire.
    pub fn with_mark_profile(
        font: SfntFace,
        glyphs: Vec<ManagedShapingGlyph>,
        direction: ManagedPositionDirection,
        profile: ManagedMarkProfile,
    ) -> Result<Self, FontError> {
        if glyphs.len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        let len = |tag| font.table(Tag::new(tag)).map_or(0, |t| t.bytes.len());
        let use_gdef = !is_gdef_blocklisted(len(*b"GDEF"), len(*b"GSUB"), len(*b"GPOS"));
        Ok(Self {
            glyphs,
            font,
            use_gdef,
            profile,
            direction,
            mark_coverage: -1,
            states: Vec::new(),
            path: Vec::new(),
            reversals: Vec::new(),
        })
    }
    /// Exact retained selected source generation.
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    /// Current complete original record inventory.
    pub fn glyphs(&self) -> &[ManagedShapingGlyph] {
        &self.glyphs
    }
    /// Modify values without changing the established live count.
    pub fn glyphs_mut(&mut self) -> &mut [ManagedShapingGlyph] {
        &mut self.glyphs
    }
    /// Consume this buffer, retaining every original record field.
    pub fn into_glyphs(self) -> Vec<ManagedShapingGlyph> {
        self.glyphs
    }
    /// Original direction, separate from per-lookup RightToLeft.
    pub fn direction(&self) -> ManagedPositionDirection {
        self.direction
    }
    /// Current original GDEF filtering coverage, or -1.
    pub fn mark_filtering_coverage(&self) -> i32 {
        self.mark_coverage
    }
    fn gdef(&self) -> &[u8] {
        if self.use_gdef {
            self.font.table(Tag::new(*b"GDEF")).map_or(&[], |t| t.bytes)
        } else {
            &[]
        }
    }
    pub(super) fn glyph(&self, i: usize) -> u16 {
        self.glyphs[i].glyph_index
    }
    /// Original byte GDEF classification and non-ignorable Rune fallback.
    pub fn glyph_class(&self, i: usize) -> Result<u16, FontError> {
        let g = self.glyphs.get(i).ok_or(FontError::InvalidArgument)?;
        let d = self.gdef();
        let class = if fits(d, 4, 2) && u16_at(d, 4) != 0 {
            class_at(d, i64::from(u16_at(d, 4)), g.glyph_index) as u8
        } else {
            0
        };
        if class != 0 {
            return Ok(u16::from(class));
        }
        Ok(
            if !default_ignorable(g.code_point)
                && self
                    .profile
                    .is_mark(g.code_point)
                    .map_err(|_| FontError::InvalidGlyph)?
            {
                3
            } else {
                1
            },
        )
    }
    fn ignored(&self, i: usize, flags: u16) -> Result<bool, FontError> {
        let g = self.glyphs[i];
        if default_ignorable(g.code_point) {
            return Ok(true);
        }
        if flags & 0xff1e == 0 {
            return Ok(false);
        }
        let class = self.glyph_class(i)?;
        let d = self.gdef();
        if class == 3 {
            if flags & 16 != 0
                && (self.mark_coverage < 0
                    || coverage_at(d, i64::from(self.mark_coverage), g.glyph_index)?.is_none())
            {
                return Ok(true);
            }
            if flags >> 8 != 0 {
                let actual = if fits(d, 10, 2) && u16_at(d, 10) != 0 {
                    class_at(d, i64::from(u16_at(d, 10)), g.glyph_index)
                } else {
                    0
                };
                if actual != flags >> 8 {
                    return Ok(true);
                }
            }
        }
        Ok(match class {
            1 => flags & 2 != 0,
            2 => flags & 4 != 0,
            3 => flags & 8 != 0,
            _ => false,
        })
    }
    pub(super) fn eligible(&self, i: usize, flags: u16) -> Result<bool, FontError> {
        Ok(i < self.glyphs.len() && !self.ignored(i, flags)?)
    }
    pub(super) fn next(&self, start: usize, flags: u16) -> Result<Option<usize>, FontError> {
        for i in start..self.glyphs.len() {
            if !self.ignored(i, flags)? {
                return Ok(Some(i));
            }
        }
        Ok(None)
    }
    pub(super) fn previous(&self, before: usize, flags: u16) -> Result<Option<usize>, FontError> {
        for i in (0..before).rev() {
            if !self.ignored(i, flags)? {
                return Ok(Some(i));
            }
        }
        Ok(None)
    }
    pub(super) fn previous_covered(
        &self,
        mut before: usize,
        flags: u16,
        data: &[u8],
        coverage: i64,
        skip_marks: bool,
    ) -> Result<Option<usize>, FontError> {
        while before > 0 {
            before -= 1;
            if self.ignored(before, flags)? || skip_marks && self.glyph_class(before)? == 3 {
                continue;
            }
            return Ok(coverage_at(data, coverage, self.glyph(before))?.map(|_| before));
        }
        Ok(None)
    }
    pub(super) fn select_mark_set(&mut self, index: u16) {
        self.mark_coverage = -1;
        if index == u16::MAX {
            return;
        }
        let d = self.gdef();
        if !fits(d, 12, 2) {
            return;
        }
        let sets = usize::from(u16_at(d, 12));
        if sets == 0 || !fits(d, sets, 4) || u16_at(d, sets) != 1 {
            return;
        }
        let pointer = sets + 4 + usize::from(index) * 4;
        if index >= u16_at(d, sets + 2) || !fits(d, pointer, 4) {
            return;
        }
        let rel = u32_at(d, pointer);
        if rel <= i32::MAX as u32 && fits_at(d, sets as i64 + i64::from(rel), 4) {
            self.mark_coverage = (sets + rel as usize) as i32;
        }
    }
    pub(super) fn dependency(&mut self, start: usize, end: usize) {
        let start = start.min(self.glyphs.len());
        let end = end.max(start).min(self.glyphs.len());
        if end - start < 2 {
            return;
        }
        let min = self.glyphs[start..end]
            .iter()
            .map(|g| g.cluster)
            .min()
            .unwrap();
        for g in &mut self.glyphs[start..end] {
            if g.cluster != min {
                g.flags |= 3;
            }
        }
    }
    pub(super) fn values(&mut self, i: usize, values: [i16; 4]) {
        let g = &mut self.glyphs[i];
        g.offset_x = add(g.offset_x, i32::from(values[0]));
        g.offset_y = add(g.offset_y, i32::from(values[1]));
        g.advance_x = add(g.advance_x, i32::from(values[2]));
        g.advance_y = add(g.advance_y, i32::from(values[3]));
        g.positioned = 1;
    }
    pub(super) fn component(&self, mark: usize, ligature: usize) -> usize {
        let g = self.glyphs[mark];
        if g.ligature_component != 255 {
            usize::from(g.ligature_component)
        } else {
            let count = self.glyphs[ligature].ligature_component_count;
            if count > 0 {
                usize::from(count - 1)
            } else {
                i32::MAX as usize
            }
        }
    }
    pub(super) fn can_attach(&self, a: usize, b: usize) -> bool {
        let a = self.glyphs[a].ligature_component;
        let b = self.glyphs[b].ligature_component;
        a == 255 || b == 255 || a == b
    }
    pub(super) fn attach(&mut self, mark: usize, target: usize, x: i32, y: i32) {
        self.dependency(mark.min(target), mark.max(target) + 1);
        let g = &mut self.glyphs[mark];
        g.offset_x = clamp(x);
        g.offset_y = clamp(y);
        g.attachment_target = target as i32;
        g.attachment_kind = 1;
        g.positioned = 1;
    }
    pub(super) fn cursive(
        &mut self,
        previous: usize,
        current: usize,
        entry: [i32; 2],
        exit: [i32; 2],
        flags: u16,
    ) -> Result<(), FontError> {
        // Reserve before mutation; original recursive unwind uses this bounded
        // reusable explicit path, preserving even existing attachment cycles.
        self.reversals.clear();
        self.reversals
            .try_reserve(self.glyphs.len())
            .map_err(|_| FontError::Capacity)?;
        self.dependency(previous.min(current), previous.max(current) + 1);
        let mut p = self.glyphs[previous];
        let mut c = self.glyphs[current];
        use ManagedPositionDirection::*;
        match self.direction {
            Unspecified => {}
            LeftToRight => {
                p.advance_x = clamp(exit[0] + i32::from(p.offset_x));
                let d = entry[0] + i32::from(c.offset_x);
                c.advance_x = add(c.advance_x, -d);
                c.offset_x = add(c.offset_x, -d);
            }
            RightToLeft => {
                let d = exit[0] + i32::from(p.offset_x);
                p.advance_x = add(p.advance_x, -d);
                p.offset_x = add(p.offset_x, -d);
                c.advance_x = clamp(entry[0] + i32::from(c.offset_x));
            }
            TopToBottom => {
                p.advance_y = clamp(exit[1] + i32::from(p.offset_y));
                let d = entry[1] + i32::from(c.offset_y);
                c.advance_y = add(c.advance_y, -d);
                c.offset_y = add(c.offset_y, -d);
            }
            BottomToTop => {
                let d = exit[1] + i32::from(p.offset_y);
                p.advance_y = add(p.advance_y, -d);
                p.offset_y = add(p.offset_y, -d);
                c.advance_y = clamp(entry[1]);
            }
        }
        let horizontal = matches!(self.direction, LeftToRight | RightToLeft);
        let rtl = flags & 1 != 0;
        let (child, parent) = if rtl {
            (previous, current)
        } else {
            (current, previous)
        };
        self.glyphs[previous] = p;
        self.glyphs[current] = c;
        let mut i = child;
        loop {
            let g = self.glyphs[i];
            if !matches!(g.attachment_kind, 2 | 3) {
                break;
            }
            self.glyphs[i].attachment_target = -1;
            self.glyphs[i].attachment_kind = 0;
            let old = g.attachment_target as usize;
            if old >= self.glyphs.len() || old == parent {
                break;
            }
            self.reversals
                .push((i, old, if horizontal { g.offset_y } else { g.offset_x }));
            i = old;
        }
        while let Some((i, old, offset)) = self.reversals.pop() {
            let g = &mut self.glyphs[old];
            if horizontal {
                g.offset_y = clamp(-i32::from(offset));
            } else {
                g.offset_x = clamp(-i32::from(offset));
            }
            g.attachment_target = i as i32;
            g.attachment_kind = if horizontal { 2 } else { 3 };
        }
        let g = &mut self.glyphs[child];
        g.attachment_target = parent as i32;
        g.attachment_kind = if horizontal { 2 } else { 3 };
        if horizontal {
            g.offset_y = clamp(if rtl {
                entry[1] - exit[1]
            } else {
                exit[1] - entry[1]
            });
        } else {
            g.offset_x = clamp(if rtl {
                entry[0] - exit[0]
            } else {
                exit[0] - entry[0]
            });
        }
        let kind = g.attachment_kind;
        let p = &mut self.glyphs[parent];
        if p.attachment_target == child as i32 && p.attachment_kind == kind {
            p.attachment_target = -1;
            p.attachment_kind = 0;
            if horizontal {
                p.offset_y = 0;
            } else {
                p.offset_x = 0;
            }
        }
        Ok(())
    }
    /// Resolve all original mark/cursive attachments in source order. Cycles keep
    /// the original visiting-state rule. Explicit paths avoid stack overflow;
    /// scratch allocations precede mutation and are reused on subsequent calls.
    pub fn resolve_attachments(&mut self) -> Result<(), FontError> {
        if !self.glyphs.iter().any(|g| g.attachment_target >= 0) {
            return Ok(());
        }
        self.states.clear();
        self.path.clear();
        let count = self.glyphs.len();
        self.states
            .try_reserve(count)
            .map_err(|_| FontError::Capacity)?;
        self.path
            .try_reserve(count)
            .map_err(|_| FontError::Capacity)?;
        self.states.resize(count, 0);
        for start in 0..count {
            let mut i = start;
            while self.states[i] == 0 {
                self.states[i] = 1;
                self.path.push(i);
                let target = self.glyphs[i].attachment_target as usize;
                if target >= count || self.states[target] != 0 {
                    break;
                }
                i = target;
            }
            while let Some(i) = self.path.pop() {
                let mut g = self.glyphs[i];
                let target = g.attachment_target as usize;
                if target < count && self.states[target] != 1 {
                    let p = self.glyphs[target];
                    if g.attachment_kind == 1 {
                        g.offset_x = add(g.offset_x, i32::from(p.offset_x));
                        g.offset_y = add(g.offset_y, i32::from(p.offset_y));
                        let forward = matches!(
                            self.direction,
                            ManagedPositionDirection::LeftToRight
                                | ManagedPositionDirection::TopToBottom
                        );
                        if target != i {
                            let offset = usize::from(!forward);
                            let a = i.min(target) + offset;
                            let b = i.max(target) + offset;
                            let sign = if (target < i) == forward { -1 } else { 1 };
                            for p in &self.glyphs[a..b] {
                                g.offset_x = add(g.offset_x, sign * i32::from(p.advance_x));
                                g.offset_y = add(g.offset_y, sign * i32::from(p.advance_y));
                            }
                        }
                    } else if g.attachment_kind == 3 {
                        g.offset_x = add(g.offset_x, i32::from(p.offset_x));
                    } else if g.attachment_kind == 2 {
                        g.offset_y = add(g.offset_y, i32::from(p.offset_y));
                    }
                    self.glyphs[i] = g;
                }
                self.states[i] = 2;
            }
        }
        Ok(())
    }
    /// Original late mark advance clearing, with optional per-axis offset repair.
    pub fn zero_mark_advances(&mut self, adjust_offsets: bool) -> Result<(), FontError> {
        for i in 0..self.glyphs.len() {
            if self.glyph_class(i)? == 3 {
                let g = &mut self.glyphs[i];
                if adjust_offsets {
                    g.offset_x = add(g.offset_x, -i32::from(g.advance_x));
                    g.offset_y = add(g.offset_y, -i32::from(g.advance_y));
                }
                g.advance_x = 0;
                g.advance_y = 0;
            }
        }
        Ok(())
    }
}
