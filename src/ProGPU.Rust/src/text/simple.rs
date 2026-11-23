//! Direct port of ProGPU's original SFNT simple glyph-run and advance helpers.
use crate::font::{FontError, SfntFace};

/// Exact required output counts. A run may contain at most 65,536 glyph slots;
/// its UTF-16 cluster indices remain representable as u16 through the final slot.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SimpleGlyphRunRequirements {
    /// One entry for each original UTF-16 code unit, including paired surrogates.
    pub cluster_map_count: u32,
    /// One glyph for each decoded scalar or original unpaired code unit.
    pub glyph_count: u32,
}
/// Original per-occurrence design metrics, aligned with the glyph output slots.
/// These are not indexed by glyph ID and do not infer a metric fallback policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SimpleGlyphMetrics {
    /// Original unsigned horizontal design advance.
    pub advance_width: u32,
    /// Original unsigned vertical design advance.
    pub advance_height: u32,
}
/// Original simple-run scaling parameters. Negative finite sizes/scales retain
/// their arithmetic; a zero units-per-em value uses the original divisor one.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SimpleAdvanceScale {
    /// Original design units per em.
    pub units_per_em: u16,
    /// Original floating font size, without clamping.
    pub font_em_size: f64,
    /// Original additional scale, without clamping.
    pub scaling_factor: f64,
    /// Select supplied height metrics. This does not rotate or place outlines.
    pub is_sideways: bool,
}

/// Immutable simple run retaining its original face, complete UTF-16 source,
/// cluster map and glyph slots. This is not a shaped paragraph occurrence.
#[derive(Clone, Debug)]
pub struct SimpleGlyphRun {
    source: SfntFace,
    text: Vec<u16>,
    clusters: Vec<u16>,
    glyphs: Vec<u16>,
}
impl SimpleGlyphRun {
    /// Build an owned original simple run, publishing only after full mapping.
    /// Controls map to `blank`; U+00AD uses nonzero `hyphen`, otherwise `blank`.
    pub fn new(
        source: &SfntFace,
        text: &[u16],
        blank: u16,
        hyphen: u16,
    ) -> Result<Self, FontError> {
        let required = simple_glyph_run_requirements(text)?;
        let mut owned_text = allocate_zeroed(text.len())?;
        owned_text.copy_from_slice(text);
        let mut clusters = allocate_zeroed(required.cluster_map_count as usize)?;
        let mut glyphs = allocate_zeroed(required.glyph_count as usize)?;
        build_simple_glyph_run_into(source, text, blank, hyphen, &mut clusters, &mut glyphs)?;
        Ok(Self {
            source: source.clone(),
            text: owned_text,
            clusters,
            glyphs,
        })
    }
    /// Exact selected immutable face, retained after the original owner retires.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original UTF-16 code units, including unpaired surrogates.
    pub fn text(&self) -> &[u16] {
        &self.text
    }
    /// Original code-unit-to-glyph-slot map; both paired units share one slot.
    pub fn cluster_map(&self) -> &[u16] {
        &self.clusters
    }
    /// Original glyph IDs, including unmapped zero and supplied control IDs.
    pub fn glyph_indices(&self) -> &[u16] {
        &self.glyphs
    }
    /// Fill scaled advances without allocating. Metrics align with occurrences;
    /// scratch follows the same publication rules as the free buffer API.
    pub fn fill_advances_into(
        &self,
        metrics: &[SimpleGlyphMetrics],
        scale: SimpleAdvanceScale,
        state: &mut [u8],
        advances: &mut [i32],
    ) -> Result<(), FontError> {
        fill_simple_glyph_advances(
            &self.text,
            &self.clusters,
            &self.glyphs,
            metrics,
            scale,
            state,
            advances,
        )
    }
}

/// Original simple control inventory: U+0000..001F and U+007F..009F only.
pub fn is_simple_formatting_control(code_point: u32) -> bool {
    code_point < 0x20 || (0x7f..=0x9f).contains(&code_point)
}
/// Decode one original UTF-16 position into (value, consumed code units).
/// Unpaired surrogates retain their original numeric value; no U+FFFD substitution.
/// Reading the low half independently returns that raw code unit.
pub fn read_simple_code_point(text: &[u16], index: usize) -> Result<(u32, usize), FontError> {
    let first = u32::from(*text.get(index).ok_or(FontError::InvalidArgument)?);
    if (0xd800..=0xdbff).contains(&first)
        && let Some(&second) = text.get(index + 1)
        && (0xdc00..=0xdfff).contains(&second)
    {
        return Ok((
            0x10000 + ((first - 0xd800) << 10) + (u32::from(second) - 0xdc00),
            2,
        ));
    }
    Ok((first, 1))
}
/// Allocation-free count/preflight with the original 65,536-glyph boundary.
pub fn simple_glyph_run_requirements(
    text: &[u16],
) -> Result<SimpleGlyphRunRequirements, FontError> {
    let cluster_map_count = u32::try_from(text.len()).map_err(|_| FontError::InvalidArgument)?;
    let (mut index, mut glyph_count) = (0, 0);
    while index < text.len() {
        if glyph_count == 65536 {
            return Err(FontError::InvalidArgument);
        }
        index += read_simple_code_point(text, index)?.1;
        glyph_count += 1;
    }
    Ok(SimpleGlyphRunRequirements {
        cluster_map_count,
        glyph_count,
    })
}
/// Fill original simple glyphs and UTF-16 cluster indices without allocation.
/// Requirements and the complete font mapping validate before either output is
/// changed. Caller tails remain untouched. Safe slices prevent mutable aliasing.
pub fn build_simple_glyph_run_into(
    font: &SfntFace,
    text: &[u16],
    blank: u16,
    hyphen: u16,
    clusters: &mut [u16],
    glyphs: &mut [u16],
) -> Result<u32, FontError> {
    let required = simple_glyph_run_requirements(text)?;
    if clusters.len() < required.cluster_map_count as usize
        || glyphs.len() < required.glyph_count as usize
    {
        return Err(FontError::Capacity);
    }
    let mut index = 0;
    while index < text.len() {
        let (point, count) = read_simple_code_point(text, index)?;
        mapped_glyph(font, point, blank, hyphen)?;
        index += count;
    }
    let (mut index, mut output) = (0, 0);
    while index < text.len() {
        let (point, count) = read_simple_code_point(text, index)?;
        glyphs[output] = mapped_glyph(font, point, blank, hyphen)?;
        clusters[index..index + count].fill(output as u16);
        output += 1;
        index += count;
    }
    Ok(required.glyph_count)
}
/// Original linear first-occurrence control classification and scaled advances.
/// Glyph IDs are not used as metric indices. Invalid cluster slots are ignored;
/// the first mapped UTF-16 position decides control status, including a low half.
///
/// Advances and their tail stay unchanged on failure. After initial admission,
/// the scratch prefix is cleared/classified even if numeric preflight fails;
/// its tail stays untouched. This preserves the native scratch contract.
pub fn fill_simple_glyph_advances(
    text: &[u16],
    clusters: &[u16],
    glyphs: &[u16],
    metrics: &[SimpleGlyphMetrics],
    scale: SimpleAdvanceScale,
    state: &mut [u8],
    advances: &mut [i32],
) -> Result<(), FontError> {
    let count = glyphs.len();
    if clusters.len() < text.len()
        || metrics.len() < count
        || state.len() < count
        || advances.len() < count
        || !scale.font_em_size.is_finite()
        || !scale.scaling_factor.is_finite()
    {
        return Err(if state.len() < count || advances.len() < count {
            FontError::Capacity
        } else {
            FontError::InvalidArgument
        });
    }
    state[..count].fill(0);
    for (index, &slot) in clusters[..text.len()].iter().enumerate() {
        let slot = usize::from(slot);
        if slot >= count || state[slot] != 0 {
            continue;
        }
        state[slot] = if is_simple_formatting_control(read_simple_code_point(text, index)?.0) {
            1
        } else {
            2
        };
    }
    for (metric, state) in metrics[..count].iter().zip(&state[..count]) {
        if *state != 1 {
            scaled_advance(*metric, scale)?;
        }
    }
    for ((metric, state), output) in metrics[..count].iter().zip(&state[..count]).zip(advances) {
        *output = if *state == 1 {
            0
        } else {
            scaled_advance(*metric, scale)?
        };
    }
    Ok(())
}
fn mapped_glyph(font: &SfntFace, point: u32, blank: u16, hyphen: u16) -> Result<u16, FontError> {
    if point == 0xad {
        return Ok(if hyphen != 0 { hyphen } else { blank });
    }
    if is_simple_formatting_control(point) {
        return Ok(blank);
    }
    font.glyph_index(point).ok_or(FontError::InvalidFace)
}
fn scaled_advance(metric: SimpleGlyphMetrics, scale: SimpleAdvanceScale) -> Result<i32, FontError> {
    let design = if scale.is_sideways {
        metric.advance_height
    } else {
        metric.advance_width
    };
    let value = f64::from(design) * scale.font_em_size * scale.scaling_factor
        / f64::from(scale.units_per_em.max(1));
    let rounded = value.round_ties_even();
    if !rounded.is_finite() || rounded < f64::from(i32::MIN) || rounded > f64::from(i32::MAX) {
        return Err(FontError::InvalidArgument);
    }
    Ok(rounded as i32)
}
fn allocate_zeroed<T: Default + Clone>(count: usize) -> Result<Vec<T>, FontError> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| FontError::Capacity)?;
    values.resize(count, T::default());
    Ok(values)
}
