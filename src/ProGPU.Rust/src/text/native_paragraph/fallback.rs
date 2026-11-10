//! Original grapheme-preserving native fallback inventory; no platform callbacks.
use super::*;
/// Ordered immutable face candidate. Empty slots retain the original native
/// null-face selection semantics and never become a fabricated font.
#[derive(Clone, Debug)]
pub struct NativeFallbackCandidate {
    pub font: Option<SfntFace>,
    pub identity: u64,
}
/// Original contiguous same-font/same-missing selection over complete graphemes.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeFallbackRun {
    pub scalar_index: u32,
    pub scalar_count: u32,
    pub input_index: u32,
    pub input_length: u32,
    pub font_index: u32,
    pub missing: bool,
}
fn supports(font: &SfntFace, input: &[UnicodeScalar], g: GraphemeCluster) -> bool {
    input[g.scalar_index as usize..(g.scalar_index + g.scalar_count) as usize]
        .iter()
        .all(|s| {
            matches!(s.code_point,0xfe00..=0xfe0f|0xe0100..=0xe01ef|0x200c|0x200d)
                || font.glyph_index(s.code_point).is_some_and(|g| g != 0)
        })
}
/// Select each complete grapheme using preferred-first then original inventory
/// order. Adjacent selections merge only when both font and missing state match.
/// This is explicit selection, not an implicit change to styled paragraph fonts.
pub fn native_itemize_font_fallback(
    input: &[UnicodeScalar],
    graphemes: &[GraphemeCluster],
    candidates: &[NativeFallbackCandidate],
    preferred: u32,
) -> Result<Vec<NativeFallbackRun>, FontError> {
    if candidates.is_empty()
        || preferred as usize >= candidates.len()
        || candidates.len() > u32::MAX as usize
        || input.len() > u32::MAX as usize
    {
        return Err(FontError::InvalidArgument);
    }
    let mut expected = 0usize;
    for g in graphemes {
        if g.scalar_count == 0
            || g.scalar_index as usize != expected
            || g.scalar_count as usize > input.len().saturating_sub(expected)
        {
            return Err(FontError::InvalidArgument);
        }
        expected += g.scalar_count as usize;
    }
    if expected != input.len() {
        return Err(FontError::InvalidArgument);
    }
    let mut output: Vec<NativeFallbackRun> = reserved(graphemes.len())?;
    for &g in graphemes {
        let supports_index = |i: usize| {
            candidates[i]
                .font
                .as_ref()
                .is_some_and(|f| supports(f, input, g))
        };
        let selected = if supports_index(preferred as usize) {
            Some(preferred as usize)
        } else {
            (0..candidates.len()).find(|&i| i != preferred as usize && supports_index(i))
        };
        let missing = selected.is_none();
        let font = selected.unwrap_or_else(|| {
            if candidates[preferred as usize].font.is_some() {
                preferred as usize
            } else {
                candidates
                    .iter()
                    .position(|c| c.font.is_some())
                    .unwrap_or(preferred as usize)
            }
        }) as u32;
        let first = &input[g.scalar_index as usize];
        let last = &input[(g.scalar_index + g.scalar_count - 1) as usize];
        let end = u64::from(last.input_index) + u64::from(last.input_length);
        if end > u32::MAX as u64 || end < u64::from(first.input_index) {
            return Err(FontError::InvalidArgument);
        }
        if let Some(previous) = output.last_mut() {
            if previous.font_index == font && previous.missing == missing {
                previous.scalar_count += g.scalar_count;
                previous.input_length = (end as u32)
                    .checked_sub(previous.input_index)
                    .ok_or(FontError::InvalidArgument)?;
                continue;
            }
        }
        output.push(NativeFallbackRun {
            scalar_index: g.scalar_index,
            scalar_count: g.scalar_count,
            input_index: first.input_index,
            input_length: end as u32 - first.input_index,
            font_index: font,
            missing,
        });
    }
    Ok(output)
}

impl NativeHintedParagraph {
    /// Explicit composition of the original grapheme fallback selector and styled
    /// hinted paragraph producer. `font_styles` contains one caller-resolved
    /// template per font, with `font_index` equal to its inventory slot. Template
    /// scalar ranges are replaced by complete selected fallback ranges. Number
    /// substitution must be absent: styled substitution has its separate producer
    /// and cannot retroactively alter the fallback selection's original input.
    pub fn shape_with_fallback(
        fonts: &[SfntFace],
        input: &[UnicodeScalar],
        font_styles: &[NativeParagraphStyle],
        preferred: u32,
        shaping: NativeShapeRequest<'_>,
        options: NativeTextLayoutOptions,
        projection: NativeHintedProjection,
        provider: &mut dyn NativeHintedProvider,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        let stage = NativeParagraphStage::Shaping;
        if fonts.is_empty()
            || fonts.len() != font_styles.len()
            || preferred as usize >= fonts.len()
            || font_styles.iter().enumerate().any(|(i, s)| {
                s.font_index as usize != i
                    || s.number_substitution != NativeNumberSubstitution::default()
            })
        {
            return Err(invalid(stage));
        }
        // Font selection does not execute a hinted producer. The selected whole
        // styles receive complete source admission before the first capture.
        let mut candidates = reserved(fonts.len()).map_err(|e| error(stage, e))?;
        for (i, font) in fonts.iter().enumerate() {
            candidates.push(NativeFallbackCandidate {
                font: Some(font.clone()),
                identity: i as u64,
            });
        }
        let mut graphemes =
            filled(input.len(), GraphemeCluster::default()).map_err(|e| error(stage, e))?;
        let count =
            unicode::grapheme::segment_into(input, GraphemePolicy::Unicode17, &mut graphemes)
                .map_err(|e| error(stage, e))?;
        graphemes.truncate(count as usize);
        let selection = native_itemize_font_fallback(input, &graphemes, &candidates, preferred)
            .map_err(|e| error(stage, e))?;
        let mut styles = reserved(selection.len()).map_err(|e| error(stage, e))?;
        for run in &selection {
            let mut style = font_styles[run.font_index as usize].clone();
            style.scalar_start = run.scalar_index;
            style.scalar_count = run.scalar_count;
            styles.push(style);
        }
        prepare::validate(fonts, input, &styles, shaping, options)?;
        let mut source = prepare::build(
            fonts, input, &styles, shaping, options, projection, provider,
        )?;
        source.fallback = selection;
        Self::place(Arc::new(source), 0, options)
    }
}
