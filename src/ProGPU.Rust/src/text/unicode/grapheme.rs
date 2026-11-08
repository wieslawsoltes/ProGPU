//! Original UAX #29 revision-47 grapheme boundaries and managed shaping policy.
//! Segmentation keeps original logical scalar/source coordinates; it does not
//! normalize, shape, reorder, or divide text into words. O(N log R) time for N
//! scalars and fixed R property ranges, with O(1) scratch in caller-buffer APIs.
use super::{DecodedText, UnicodeError, UnicodeScalar, allocate, data, properties::range_value};

/// Original grapheme-break property values.
#[repr(u8)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum GraphemeBreakClass {
    /// No special boundary rule.
    #[default]
    Other = 0,
    /// CR.
    CarriageReturn,
    /// LF.
    LineFeed,
    /// Control.
    Control,
    /// Extend.
    Extend,
    /// ZWJ.
    ZeroWidthJoiner,
    /// Regional_Indicator.
    RegionalIndicator,
    /// Prepend.
    Prepend,
    /// SpacingMark.
    SpacingMark,
    /// Hangul L.
    HangulL,
    /// Hangul V.
    HangulV,
    /// Hangul T.
    HangulT,
    /// Hangul LV.
    HangulLv,
    /// Hangul LVT.
    HangulLvt,
}
/// Original Indic_Conjunct_Break property, independent of grapheme class.
#[repr(u8)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum IndicConjunctClass {
    /// None.
    #[default]
    None = 0,
    /// Consonant.
    Consonant,
    /// Extend.
    Extend,
    /// Linker.
    Linker,
}
/// Keep the original public Unicode contract and shaping compatibility distinct.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum GraphemePolicy {
    /// Unicode 17 extended graphemes, including GB9c Indic conjunct joining.
    #[default]
    Unicode17,
    /// Original native shaping compatibility: omit GB9c only. This is not a
    /// promise to follow arbitrary future/platform versions of .NET StringInfo.
    ManagedCompatible,
}
/// Original sixteen-byte cluster record, in logical order.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GraphemeCluster {
    /// Minimum original source index of the cluster.
    pub input_index: u32,
    /// Union of original source ranges, with saturated far ends.
    pub input_length: u32,
    /// First original scalar slot.
    pub scalar_index: u32,
    /// Number of original scalar slots.
    pub scalar_count: u32,
}
/// Original property lookup; raw invalid values retain the original fallback.
pub fn break_class(code_point: u32) -> GraphemeBreakClass {
    use GraphemeBreakClass::*;
    match range_value(code_point, &data::GRAPHEME_RANGES, 0) {
        1 => CarriageReturn,
        2 => LineFeed,
        3 => Control,
        4 => Extend,
        5 => ZeroWidthJoiner,
        6 => RegionalIndicator,
        7 => Prepend,
        8 => SpacingMark,
        9 => HangulL,
        10 => HangulV,
        11 => HangulT,
        12 => HangulLv,
        13 => HangulLvt,
        _ => Other,
    }
}
/// Original Indic conjunct property, with no source rewriting.
pub fn indic_conjunct_class(code_point: u32) -> IndicConjunctClass {
    match range_value(code_point, &data::CONJUNCT_RANGES, 0) {
        1 => IndicConjunctClass::Consonant,
        2 => IndicConjunctClass::Extend,
        3 => IndicConjunctClass::Linker,
        _ => IndicConjunctClass::None,
    }
}
/// Original Extended_Pictographic inventory, separate from grapheme class.
pub fn is_extended_pictographic(code_point: u32) -> bool {
    range_value(code_point, &data::PICTOGRAPHIC_RANGES, 0) != 0
}
#[derive(Default)]
struct State {
    regional_indicators: u32,
    emoji_pictographic: bool,
    emoji_zwj_ready: bool,
    indic_consonant: bool,
    indic_linker: bool,
}
impl State {
    fn advance(&mut self, point: u32, class: GraphemeBreakClass) {
        use GraphemeBreakClass::*;
        self.regional_indicators = if class == RegionalIndicator {
            self.regional_indicators + 1
        } else {
            0
        };
        if is_extended_pictographic(point) {
            self.emoji_pictographic = true;
            self.emoji_zwj_ready = false;
        } else if class == Extend && self.emoji_pictographic {
            self.emoji_zwj_ready = false;
        } else if class == ZeroWidthJoiner {
            self.emoji_zwj_ready = self.emoji_pictographic;
            self.emoji_pictographic = false;
        } else {
            self.emoji_pictographic = false;
            self.emoji_zwj_ready = false;
        }
        match indic_conjunct_class(point) {
            IndicConjunctClass::Consonant => {
                self.indic_consonant = true;
                self.indic_linker = false;
            }
            IndicConjunctClass::Extend => {}
            IndicConjunctClass::Linker => self.indic_linker |= self.indic_consonant,
            IndicConjunctClass::None => {
                self.indic_consonant = false;
                self.indic_linker = false;
            }
        }
    }
    fn boundary(
        &self,
        point: u32,
        left: GraphemeBreakClass,
        right: GraphemeBreakClass,
        policy: GraphemePolicy,
    ) -> bool {
        use GraphemeBreakClass::*;
        let control = |c| matches!(c, CarriageReturn | LineFeed | Control);
        if left == CarriageReturn && right == LineFeed {
            return false;
        }
        if control(left) || control(right) {
            return true;
        }
        if left == HangulL && matches!(right, HangulL | HangulV | HangulLv | HangulLvt) {
            return false;
        }
        if matches!(left, HangulLv | HangulV) && matches!(right, HangulV | HangulT) {
            return false;
        }
        if matches!(left, HangulLvt | HangulT) && right == HangulT {
            return false;
        }
        if matches!(right, Extend | ZeroWidthJoiner | SpacingMark) || left == Prepend {
            return false;
        }
        if policy == GraphemePolicy::Unicode17
            && indic_conjunct_class(point) == IndicConjunctClass::Consonant
            && self.indic_consonant
            && self.indic_linker
        {
            return false;
        }
        if is_extended_pictographic(point) && self.emoji_zwj_ready {
            return false;
        }
        if left == RegionalIndicator
            && right == RegionalIndicator
            && self.regional_indicators & 1 != 0
        {
            return false;
        }
        true
    }
}
fn scan(
    input: &[UnicodeScalar],
    policy: GraphemePolicy,
    mut emit: impl FnMut(GraphemeCluster),
) -> u32 {
    let Some(first) = input.first() else {
        return 0;
    };
    let end = |s: &UnicodeScalar| s.input_index.saturating_add(u32::from(s.input_length));
    let (mut start, mut source_start, mut source_end) = (0, first.input_index, end(first));
    let mut left = break_class(first.code_point);
    let mut state = State::default();
    state.advance(first.code_point, left);
    let mut count = 0;
    for (index, scalar) in input.iter().enumerate().skip(1) {
        let right = break_class(scalar.code_point);
        if state.boundary(scalar.code_point, left, right, policy) {
            emit(GraphemeCluster {
                input_index: source_start,
                input_length: source_end - source_start,
                scalar_index: start as u32,
                scalar_count: (index - start) as u32,
            });
            count += 1;
            start = index;
            source_start = scalar.input_index;
            source_end = end(scalar);
        } else {
            source_start = source_start.min(scalar.input_index);
            source_end = source_end.max(end(scalar));
        }
        state.advance(scalar.code_point, right);
        left = right;
    }
    emit(GraphemeCluster {
        input_index: source_start,
        input_length: source_end - source_start,
        scalar_index: start as u32,
        scalar_count: (input.len() - start) as u32,
    });
    count + 1
}
/// Count original clusters without allocation or scalar/source-range sanitizing.
pub fn cluster_count(input: &[UnicodeScalar], policy: GraphemePolicy) -> Result<u32, UnicodeError> {
    u32::try_from(input.len()).map_err(|_| UnicodeError::InvalidArgument)?;
    Ok(scan(input, policy, |_| {}))
}
/// Two original ordered passes: count before writing. Capacity failure leaves all
/// output unchanged; unused tails remain intact. O(1) scratch, no heap allocation.
pub fn segment_into(
    input: &[UnicodeScalar],
    policy: GraphemePolicy,
    output: &mut [GraphemeCluster],
) -> Result<u32, UnicodeError> {
    let required = cluster_count(input, policy)?;
    if output.len() < required as usize {
        return Err(UnicodeError::Capacity);
    }
    let mut slots = output.iter_mut();
    Ok(scan(input, policy, |cluster| {
        *slots.next().expect("preflighted cluster capacity") = cluster
    }))
}
/// Strict original UTF-16 starts: one byte per code unit, including zero at the
/// low surrogate of a supplementary scalar. Whole-input encoding validation
/// precedes capacity checks; errors and unused tails leave storage unchanged.
/// This route always uses the original public Unicode 17 policy.
pub fn utf16_starts_into(input: &[u16], starts: &mut [u8]) -> Result<(), UnicodeError> {
    super::utf16_scalar_count(input)?;
    if starts.len() < input.len() {
        return Err(UnicodeError::Capacity);
    }
    let mut state = State::default();
    let mut left = GraphemeBreakClass::Other;
    let mut offset = 0;
    for scalar in char::decode_utf16(input.iter().copied()) {
        let scalar = scalar.map_err(|_| UnicodeError::InvalidEncoding)?;
        let right = break_class(scalar as u32);
        starts[offset] = u8::from(
            offset == 0 || state.boundary(scalar as u32, left, right, GraphemePolicy::Unicode17),
        );
        if scalar.len_utf16() == 2 {
            starts[offset + 1] = 0;
        }
        offset += scalar.len_utf16();
        state.advance(scalar as u32, right);
        left = right;
    }
    Ok(())
}
/// Immutable encoded text, decoded scalars and original logical cluster ranges.
#[derive(Clone, Debug)]
pub struct GraphemeText {
    text: DecodedText,
    clusters: Vec<GraphemeCluster>,
    policy: GraphemePolicy,
}
impl GraphemeText {
    /// Retain one complete source and segmentation generation.
    pub fn new(text: DecodedText, policy: GraphemePolicy) -> Result<Self, UnicodeError> {
        let mut clusters = allocate(cluster_count(text.scalars(), policy)? as usize)?;
        segment_into(text.scalars(), policy, &mut clusters)?;
        Ok(Self {
            text,
            clusters,
            policy,
        })
    }
    /// Original strict source and decoded scalar ranges.
    pub fn text(&self) -> &DecodedText {
        &self.text
    }
    /// Immutable logical cluster ranges.
    pub fn clusters(&self) -> &[GraphemeCluster] {
        &self.clusters
    }
    /// Explicit segmentation policy for this generation.
    pub fn policy(&self) -> GraphemePolicy {
        self.policy
    }
}
