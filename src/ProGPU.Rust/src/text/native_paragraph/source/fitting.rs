//! Original bounded native source candidate fitting and placement witnesses.
use super::*;
const MAX_VISITS: usize = 1 << 24;
#[derive(Clone, Debug)]
pub struct NativeSourceFittedSlice {
    pub run_index: u32,
    pub placement: Arc<NativeDevicePositionedSlice>,
}
#[derive(Debug)]
pub struct NativeSourceFitting {
    pub(super) first: usize,
    pub(super) width_policy: NativeSourceWidthPolicy,
    pub(super) glyphs: Vec<ShapingGlyph>,
    pub(super) metrics: Vec<NativeSourceGlyphMetrics>,
    pub(super) lines: Vec<NativeSourceFittedLine>,
    pub(super) slices: Vec<NativeSourceFittedSlice>,
    pub(super) slice_indices: Vec<u32>,
    pub(super) slice_glyph_indices: Vec<u32>,
}
impl NativeSourceFitting {
    pub fn first_logical_glyph(&self) -> usize {
        self.first
    }
    pub fn width_policy(&self) -> NativeSourceWidthPolicy {
        self.width_policy
    }
    pub fn fitting_glyphs(&self) -> &[ShapingGlyph] {
        &self.glyphs
    }
    pub fn metrics(&self) -> &[NativeSourceGlyphMetrics] {
        &self.metrics
    }
    pub fn lines(&self) -> &[NativeSourceFittedLine] {
        &self.lines
    }
    pub fn slices(&self) -> &[NativeSourceFittedSlice] {
        &self.slices
    }
    pub fn slice_indices(&self) -> &[u32] {
        &self.slice_indices
    }
    pub fn slice_glyph_indices(&self) -> &[u32] {
        &self.slice_glyph_indices
    }
}
fn unsafe_break(g: ShapingGlyph) -> bool {
    g.flags & 1 != 0
}
fn policies(s: &NativeParagraphSource) -> Result<&[NativeSourcePolicy], NativeSourceError> {
    s.source_policies
        .as_deref()
        .ok_or(NativeSourceError::InvalidArgument)
}
fn prepared_edge(run: &NativeDeviceRun, cluster: i32) -> usize {
    run.prepared_glyphs()
        .partition_point(|g| g.cluster < cluster)
}
pub(super) fn safe_boundary(s: &NativeParagraphSource, index: usize, whole: bool) -> bool {
    if index == 0 || index == s.logical.len() {
        return true;
    }
    let g = s.logical[index];
    if g.cluster == s.logical[index - 1].cluster {
        return false;
    }
    let owner = s.owners[index];
    if owner.run_index != s.owners[index - 1].run_index || !unsafe_break(g) {
        return true;
    }
    let run = &s.runs[owner.run_index as usize].generation;
    if run.positioning_recipe().is_none() && !whole {
        return false;
    }
    let prepared = run.prepared_glyphs();
    let edge = prepared_edge(run, g.cluster);
    edge < prepared.len()
        && prepared[edge].cluster == g.cluster
        && (edge == 0 || prepared[edge - 1].cluster != g.cluster && !unsafe_break(prepared[edge]))
}
fn occurrence_map(s: &NativeParagraphSource) -> Result<Vec<Vec<u32>>, NativeSourceError> {
    let count = s.logical.len();
    require(
        count <= u32::MAX as usize
            && s.owners.len() == count
            && s.breaks.len() == count
            && policies(s)?.len() == s.styles.len(),
    )?;
    let mut output = reserve(s.runs.len())?;
    let mut end = 0;
    for run in &s.runs {
        require(
            (run.style_index as usize) < s.styles.len()
                && run.logical_range.start as usize == end
                && run.logical_range.end >= run.logical_range.start
                && run.logical_range.end as usize <= count
                && run.generation.glyphs().len()
                    == (run.logical_range.end - run.logical_range.start) as usize,
        )?;
        end = run.logical_range.end as usize;
        output.push(alloc(run.generation.glyphs().len(), u32::MAX)?);
    }
    require(end == count)?;
    for (i, o) in s.owners.iter().enumerate() {
        let map = output
            .get_mut(o.run_index as usize)
            .ok_or(NativeSourceError::InvalidArgument)?;
        require(
            (o.run_glyph_index as usize) < map.len() && map[o.run_glyph_index as usize] == u32::MAX,
        )?;
        let r = &s.runs[o.run_index as usize];
        require(i >= r.logical_range.start as usize && i < r.logical_range.end as usize)?;
        map[o.run_glyph_index as usize] = i as u32;
    }
    Ok(output)
}
#[derive(Debug, Default)]
struct Candidate {
    start: usize,
    end: usize,
    extent: NativeSourceWidthAccumulator,
    visible: NativeSourceWidthAccumulator,
    nonnegative: bool,
    glyphs: Vec<ShapingGlyph>,
    metrics: Vec<NativeSourceGlyphMetrics>,
    slices: Vec<NativeSourceFittedSlice>,
}
impl Candidate {
    fn try_copy(&self) -> Result<Self, NativeSourceError> {
        Ok(Self {
            start: self.start,
            end: self.end,
            extent: self.extent,
            visible: self.visible,
            nonnegative: self.nonnegative,
            glyphs: copy(&self.glyphs)?,
            metrics: copy(&self.metrics)?,
            slices: copy(&self.slices)?,
        })
    }
}
fn compose(
    s: &NativeParagraphSource,
    map: &[Vec<u32>],
    start: usize,
    end: usize,
    visits: &mut usize,
    width: NativeSourceWidthPolicy,
    whole: bool,
) -> Result<Candidate, NativeSourceError> {
    require(end > start && end - start <= MAX_VISITS - *visits)?;
    if whole && (!safe_boundary(s, start, whole) || !safe_boundary(s, end, whole)) {
        return Err(NativeSourceError::Unsupported);
    }
    *visits += end - start;
    let mut c = Candidate {
        start,
        end,
        nonnegative: true,
        glyphs: copy(&s.logical[start..end])?,
        metrics: alloc(end - start, NativeSourceGlyphMetrics::default())?,
        ..Default::default()
    };
    let policies = policies(s)?;
    let mut first = start;
    while first < end {
        let run_index = s.owners[first].run_index as usize;
        let run = &s.runs[run_index];
        let last = end.min(run.logical_range.end as usize);
        require(last > first)?;
        let left = first > run.logical_range.start as usize && unsafe_break(s.logical[first]);
        let right = last < run.logical_range.end as usize && unsafe_break(s.logical[last]);
        if (left || right) && !whole {
            let recipe = run
                .generation
                .positioning_recipe()
                .ok_or(NativeSourceError::Unsupported)?;
            let pf = prepared_edge(&run.generation, s.logical[first].cluster);
            let pl = if last == run.logical_range.end as usize {
                recipe.prepared_glyphs().len()
            } else {
                prepared_edge(&run.generation, s.logical[last].cluster)
            };
            require(pf < pl && pl <= u32::MAX as usize)?;
            let slice = run
                .generation
                .recompose_positioning(pf as u32, (pl - pf) as u32)?;
            require(slice.glyphs().len() == last - first)?;
            for (i, &raw) in slice.original_glyph_indices().iter().enumerate() {
                let logical = *map[run_index]
                    .get(raw as usize)
                    .ok_or(NativeSourceError::InvalidArgument)?
                    as usize;
                require(logical >= first && logical < last)?;
                c.glyphs[logical - start] =
                    policies[run.style_index as usize].wire(slice.glyphs()[i])?;
            }
            c.slices.try_reserve(1).map_err(|_| FontError::Capacity)?;
            c.slices.push(NativeSourceFittedSlice {
                run_index: run_index as u32,
                placement: slice,
            });
        }
        first = last;
    }
    for i in start..end {
        let g = c.glyphs[i - start];
        let original = s.logical[i];
        let style = s.runs[s.owners[i].run_index as usize].style_index as usize;
        require(
            g.glyph_id == original.glyph_id
                && g.cluster == original.cluster
                && g.code_point == original.code_point
                && g.advance_y == 0,
        )?;
        let m = policies[style].project_geometry(g)?;
        c.metrics[i - start] = m;
        c.extent.append(m.advance_x, width)?;
        c.nonnegative &= m.advance_x >= 0.0;
        if !is_native_trailing_space(g.code_point) {
            c.visible = c.extent;
        }
    }
    Ok(c)
}
pub(super) fn fit(
    s: &NativeParagraphSource,
    first: usize,
    maximum_lines: u32,
    o: NativeSourceOptions,
) -> Result<NativeSourceFitting, NativeSourceError> {
    let count = s.logical.len();
    require(
        first <= count
            && o.maximum_width.is_finite()
            && o.maximum_width >= 0.0
            && o.maximum_width <= f64::from(f32::MAX)
            && (!o.preserve_soft_wrap_space_geometry || s.levels.len() == count),
    )?;
    let policies = policies(s)?;
    let width = NativeSourceWidthPolicy::select(policies);
    let map = occurrence_map(s)?;
    let mut f = NativeSourceFitting {
        first,
        width_policy: width,
        glyphs: copy(&s.logical)?,
        metrics: alloc(count, NativeSourceGlyphMetrics::default())?,
        lines: Vec::new(),
        slices: Vec::new(),
        slice_indices: alloc(count, u32::MAX)?,
        slice_glyph_indices: alloc(count, u32::MAX)?,
    };
    for i in 0..count {
        let style = s.runs[s.owners[i].run_index as usize].style_index as usize;
        f.metrics[i] = policies[style].project_geometry(s.logical[i])?;
    }
    if !safe_boundary(s, first, o.preserve_whole_run_placement) {
        return Err(NativeSourceError::Unsupported);
    }
    let mut visits = 0;
    let mut start = first;
    while start < count {
        let mut hard_end = start + 1;
        while hard_end < count && s.breaks[hard_end - 1] != LineBreakKind::Mandatory {
            hard_end += 1;
        }
        let complete = compose(
            s,
            &map,
            start,
            hard_end,
            &mut visits,
            width,
            o.preserve_whole_run_placement,
        )?;
        let mut selected = if o.maximum_width <= 0.0
            || complete.nonnegative
                && (complete.extent.fits(o.maximum_width, width)
                    || is_native_trailing_space(s.logical[hard_end - 1].code_point)
                        && complete.visible.fits(o.maximum_width, width))
        {
            Some(complete)
        } else {
            None
        };
        let mut soft = false;
        let mut last_fit: Option<Candidate> = None;
        let mut last_break: Option<Candidate> = None;
        for end in start + 1..=hard_end {
            if selected.is_some() {
                break;
            }
            if !safe_boundary(s, end, o.preserve_whole_run_placement) {
                continue;
            }
            let candidate = compose(
                s,
                &map,
                start,
                end,
                &mut visits,
                width,
                o.preserve_whole_run_placement,
            )?;
            let legal = s.breaks[end - 1] != LineBreakKind::Prohibited;
            let mandatory = s.breaks[end - 1] == LineBreakKind::Mandatory;
            let tail = if o.preserve_soft_wrap_space_geometry {
                native_soft_wrap_space_start(&candidate.glyphs)
            } else {
                candidate.glyphs.len()
            };
            let opposed = end < hard_end
                && tail != candidate.glyphs.len()
                && s.levels[end - 1] & 1 != s.paragraph_level & 1;
            let first_space = opposed
                && candidate.glyphs[tail].cluster == candidate.glyphs.last().unwrap().cluster;
            let fits = candidate.extent.fits(o.maximum_width, width)
                || (first_space
                    || !opposed
                        && legal
                        && is_native_trailing_space(s.logical[end - 1].code_point))
                    && candidate.visible.fits(o.maximum_width, width);
            if !fits {
                let space_start = start + tail;
                if opposed
                    && o.allow_emergency_break
                    && last_fit
                        .as_ref()
                        .is_some_and(|c| c.end > start && c.end >= space_start)
                {
                    selected = last_fit.take();
                    soft = true;
                } else if last_break.as_ref().is_some_and(|c| c.end > start) {
                    selected = last_break.take();
                    soft = true;
                } else if o.allow_emergency_break {
                    if last_fit.as_ref().is_some_and(|c| c.end > start) {
                        selected = last_fit.take();
                    } else {
                        if s.logical[start].cluster != s.logical[end - 1].cluster {
                            return Err(NativeSourceError::Unsupported);
                        }
                        selected = Some(candidate);
                    };
                    soft = true;
                } else if legal || end == hard_end {
                    selected = Some(candidate);
                    soft = true;
                } else {
                    continue;
                }
                break;
            }
            if mandatory || end == hard_end {
                selected = Some(candidate);
                break;
            }
            if fits {
                if legal {
                    last_break = Some(candidate.try_copy()?);
                }
                last_fit = Some(candidate);
            }
        }
        let selected = selected.ok_or(NativeSourceError::InvalidArgument)?;
        require(selected.end > start && selected.extent.width >= 0.0)?;
        let final_line = maximum_lines != 0 && f.lines.len() + 1 >= maximum_lines as usize;
        f.lines.try_reserve(1).map_err(|_| FontError::Capacity)?;
        f.lines.push(NativeSourceFittedLine {
            glyph_start: (start - first) as u32,
            glyph_count: (selected.end - start) as u32,
            width: selected.extent.width,
            clipped: u32::from(final_line && soft),
            reserved: 0,
        });
        f.glyphs[start..selected.end].copy_from_slice(&selected.glyphs);
        f.metrics[start..selected.end].copy_from_slice(&selected.metrics);
        for slice in selected.slices {
            let index = f.slices.len();
            for (i, &raw) in slice.placement.original_glyph_indices().iter().enumerate() {
                let logical = map[slice.run_index as usize][raw as usize] as usize;
                require(f.slice_indices[logical] == u32::MAX)?;
                f.slice_indices[logical] = index as u32;
                f.slice_glyph_indices[logical] = i as u32;
            }
            f.slices.try_reserve(1).map_err(|_| FontError::Capacity)?;
            f.slices.push(slice);
        }
        start = selected.end;
        if final_line {
            break;
        }
    }
    Ok(f)
}
pub(super) fn intrinsic(
    s: &NativeParagraphSource,
    o: NativeSourceOptions,
) -> Result<NativeSourceIntrinsicWidths, NativeSourceError> {
    let map = occurrence_map(s)?;
    let width = NativeSourceWidthPolicy::select(policies(s)?);
    let count = s.logical.len();
    let mut whitespace = alloc(count, false)?;
    let mut scalar = 0;
    let mut first = 0;
    while first < count {
        let cluster = s.logical[first].cluster;
        require(
            cluster >= 0
                && scalar < s.source.len()
                && s.source[scalar].input_index == cluster as u32,
        )?;
        let mut end = first + 1;
        while end < count && s.logical[end].cluster == cluster {
            end += 1;
        }
        require(end == count || s.logical[end].cluster > cluster)?;
        let next = if end < count {
            s.logical[end].cluster as u64
        } else {
            let last = s.source.last().ok_or(NativeSourceError::InvalidArgument)?;
            u64::from(last.input_index) + u64::from(last.input_length)
        };
        let mut spaces = true;
        while scalar < s.source.len() && u64::from(s.source[scalar].input_index) < next {
            spaces &= is_native_trailing_space(s.source[scalar].code_point);
            scalar += 1;
        }
        whitespace[first..end].fill(spaces);
        first = end;
    }
    let visible = |c: &Candidate| -> Result<f64, NativeSourceError> {
        let mut advance = NativeSourceWidthAccumulator::default();
        let mut result = 0.0;
        for (i, m) in c.metrics.iter().enumerate() {
            advance.append(m.advance_x, width)?;
            require(advance.width >= 0.0)?;
            if !whitespace[c.start + i] {
                result = advance.width;
            }
        }
        Ok(result)
    };
    let mut measured = NativeSourceIntrinsicWidths::default();
    let mut visits = 0;
    let mut word_start = 0;
    let mut paragraph_start = 0;
    for end in 1..=count {
        let final_end = end == count;
        let kind = s.breaks[end - 1];
        if !final_end && kind == LineBreakKind::Prohibited {
            continue;
        }
        if !safe_boundary(s, end, o.preserve_whole_run_placement) {
            return Err(NativeSourceError::Unsupported);
        }
        let word = compose(
            s,
            &map,
            word_start,
            end,
            &mut visits,
            width,
            o.preserve_whole_run_placement,
        )?;
        let w = visible(&word)?;
        if measured.minimum < w {
            measured.minimum = w;
        }
        word_start = end;
        if final_end || kind == LineBreakKind::Mandatory {
            let row = compose(
                s,
                &map,
                paragraph_start,
                end,
                &mut visits,
                width,
                o.preserve_whole_run_placement,
            )?;
            let w = visible(&row)?;
            if measured.maximum < w {
                measured.maximum = w;
            }
            paragraph_start = end;
        }
    }
    Ok(measured)
}
/// Original actual-generation witness admission, before retained transport.
pub(super) fn admit(p: &NativeSourceParagraph) -> Result<(), NativeSourceError> {
    let s = &p.raster.source;
    let f = &p.fitting;
    let count = s.logical.len();
    let policies = policies(s)?;
    require(
        f.first <= count
            && f.glyphs.len() == count
            && f.metrics.len() == count
            && f.slice_indices.len() == count
            && f.slice_glyph_indices.len() == count
            && s.owners.len() == count
            && s.breaks.len() == count
            && f.slices.len() <= u32::MAX as usize
            && (p.raster.options.maximum_lines == 0
                || f.lines.len() <= p.raster.options.maximum_lines as usize)
            && (!p.options.preserve_whole_run_placement || f.slices.is_empty())
            && f.width_policy == NativeSourceWidthPolicy::select(policies),
    )?;
    let mut used = 0;
    for i in 0..count {
        let owner = s.owners[i];
        let run = s
            .runs
            .get(owner.run_index as usize)
            .ok_or(NativeSourceError::InvalidArgument)?;
        let mut raw = *run
            .generation
            .glyphs()
            .get(owner.run_glyph_index as usize)
            .ok_or(NativeSourceError::InvalidArgument)?;
        let policy = *policies
            .get(run.style_index as usize)
            .ok_or(NativeSourceError::InvalidArgument)?;
        let si = f.slice_indices[i];
        let gi = f.slice_glyph_indices[i];
        if si != u32::MAX {
            used += 1;
            let slice = f
                .slices
                .get(si as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
            require(
                slice.run_index == owner.run_index
                    && Arc::ptr_eq(slice.placement.original(), &run.generation)
                    && slice.placement.original_glyph_indices().get(gi as usize)
                        == Some(&owner.run_glyph_index),
            )?;
            raw = *slice
                .placement
                .glyphs()
                .get(gi as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
        } else {
            require(gi == u32::MAX)?;
        }
        let expected = policy.wire(raw)?;
        require(
            expected == f.glyphs[i]
                && expected.glyph_id == s.logical[i].glyph_id
                && expected.code_point == s.logical[i].code_point
                && expected.cluster == s.logical[i].cluster
                && policy.project_geometry(expected)? == f.metrics[i],
        )?;
    }
    let mut declared = 0;
    for slice in &f.slices {
        let n = slice.placement.glyphs().len();
        require(
            n != 0 && n == slice.placement.original_glyph_indices().len() && n <= count - declared,
        )?;
        declared += n;
    }
    require(declared == used && safe_boundary(s, f.first, p.options.preserve_whole_run_placement))?;
    let mut end = f.first;
    for line in &f.lines {
        require(
            line.glyph_start as usize == end - f.first
                && line.glyph_count != 0
                && line.glyph_count as usize <= count - end
                && line.width.is_finite()
                && line.width >= 0.0
                && safe_boundary(
                    s,
                    end + line.glyph_count as usize,
                    p.options.preserve_whole_run_placement,
                ),
        )?;
        let last = end + line.glyph_count as usize;
        let mut width = NativeSourceWidthAccumulator::default();
        for i in end..last {
            width.append(f.metrics[i].advance_x, f.width_policy)?;
            require(i + 1 == last || s.breaks[i] != LineBreakKind::Mandatory)?;
        }
        require(width.width == line.width)?;
        end = last;
    }
    require(
        end == count
            || p.raster.options.maximum_lines != 0
                && f.lines.len() == p.raster.options.maximum_lines as usize,
    )?;
    for i in 0..count {
        require(i >= f.first && i < end || f.slice_indices[i] == u32::MAX)?;
    }
    Ok(())
}
