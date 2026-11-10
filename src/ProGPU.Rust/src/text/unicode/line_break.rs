//! Original Unicode 17 UAX #14 revision-55 line boundaries.
//! Boundaries follow logical scalar slots and remain independent of line fitting,
//! shaping, hyphenation and visual ordering. Original context walks take O(N²)
//! worst-case time and O(N) caller scratch; no per-boundary allocation or interop.
use super::{
    DecodedText, UnicodeError, UnicodeScalar, allocate, data, grapheme::is_extended_pictographic,
    properties::range_value,
};

/// Original line-break property values, before contextual resolution.
#[repr(u8)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LineBreakClass {
    /// Original `unknown` class.
    #[default]
    Unknown = 0,
    /// Original `ambiguous` class.
    Ambiguous = 1,
    /// Original `aksara` class.
    Aksara = 2,
    /// Original `alphabetic` class.
    Alphabetic = 3,
    /// Original `aksara_prebase` class.
    AksaraPrebase = 4,
    /// Original `aksara_start` class.
    AksaraStart = 5,
    /// Original `break_both` class.
    BreakBoth = 6,
    /// Original `break_after` class.
    BreakAfter = 7,
    /// Original `break_before` class.
    BreakBefore = 8,
    /// Original `mandatory` class.
    Mandatory = 9,
    /// Original `contingent` class.
    Contingent = 10,
    /// Original `conditional_japanese` class.
    ConditionalJapanese = 11,
    /// Original `close_punctuation` class.
    ClosePunctuation = 12,
    /// Original `combining_mark` class.
    CombiningMark = 13,
    /// Original `close_parenthesis` class.
    CloseParenthesis = 14,
    /// Original `carriage_return` class.
    CarriageReturn = 15,
    /// Original `emoji_base` class.
    EmojiBase = 16,
    /// Original `emoji_modifier` class.
    EmojiModifier = 17,
    /// Original `exclamation` class.
    Exclamation = 18,
    /// Original `glue` class.
    Glue = 19,
    /// Original `hangul_lv` class.
    HangulLv = 20,
    /// Original `hangul_lvt` class.
    HangulLvt = 21,
    /// Original `unambiguous_hyphen` class.
    UnambiguousHyphen = 22,
    /// Original `hebrew_letter` class.
    HebrewLetter = 23,
    /// Original `hyphen` class.
    Hyphen = 24,
    /// Original `ideographic` class.
    Ideographic = 25,
    /// Original `inseparable` class.
    Inseparable = 26,
    /// Original `infix_numeric` class.
    InfixNumeric = 27,
    /// Original `hangul_l` class.
    HangulL = 28,
    /// Original `hangul_t` class.
    HangulT = 29,
    /// Original `hangul_v` class.
    HangulV = 30,
    /// Original `line_feed` class.
    LineFeed = 31,
    /// Original `next_line` class.
    NextLine = 32,
    /// Original `nonstarter` class.
    Nonstarter = 33,
    /// Original `numeric` class.
    Numeric = 34,
    /// Original `open_punctuation` class.
    OpenPunctuation = 35,
    /// Original `postfix_numeric` class.
    PostfixNumeric = 36,
    /// Original `prefix_numeric` class.
    PrefixNumeric = 37,
    /// Original `quotation` class.
    Quotation = 38,
    /// Original `regional_indicator` class.
    RegionalIndicator = 39,
    /// Original `complex_context` class.
    ComplexContext = 40,
    /// Original `surrogate` class.
    Surrogate = 41,
    /// Original `space` class.
    Space = 42,
    /// Original `break_symbol` class.
    BreakSymbol = 43,
    /// Original `virama_final` class.
    ViramaFinal = 44,
    /// Original `virama` class.
    Virama = 45,
    /// Original `word_joiner` class.
    WordJoiner = 46,
    /// Original `zero_width_space` class.
    ZeroWidthSpace = 47,
    /// Original `zero_width_joiner` class.
    ZeroWidthJoiner = 48,
}
impl LineBreakClass {
    fn from_value(value: u32) -> Self {
        match value {
            1 => Self::Ambiguous,
            2 => Self::Aksara,
            3 => Self::Alphabetic,
            4 => Self::AksaraPrebase,
            5 => Self::AksaraStart,
            6 => Self::BreakBoth,
            7 => Self::BreakAfter,
            8 => Self::BreakBefore,
            9 => Self::Mandatory,
            10 => Self::Contingent,
            11 => Self::ConditionalJapanese,
            12 => Self::ClosePunctuation,
            13 => Self::CombiningMark,
            14 => Self::CloseParenthesis,
            15 => Self::CarriageReturn,
            16 => Self::EmojiBase,
            17 => Self::EmojiModifier,
            18 => Self::Exclamation,
            19 => Self::Glue,
            20 => Self::HangulLv,
            21 => Self::HangulLvt,
            22 => Self::UnambiguousHyphen,
            23 => Self::HebrewLetter,
            24 => Self::Hyphen,
            25 => Self::Ideographic,
            26 => Self::Inseparable,
            27 => Self::InfixNumeric,
            28 => Self::HangulL,
            29 => Self::HangulT,
            30 => Self::HangulV,
            31 => Self::LineFeed,
            32 => Self::NextLine,
            33 => Self::Nonstarter,
            34 => Self::Numeric,
            35 => Self::OpenPunctuation,
            36 => Self::PostfixNumeric,
            37 => Self::PrefixNumeric,
            38 => Self::Quotation,
            39 => Self::RegionalIndicator,
            40 => Self::ComplexContext,
            41 => Self::Surrogate,
            42 => Self::Space,
            43 => Self::BreakSymbol,
            44 => Self::ViramaFinal,
            45 => Self::Virama,
            46 => Self::WordJoiner,
            47 => Self::ZeroWidthSpace,
            48 => Self::ZeroWidthJoiner,
            _ => Self::Unknown,
        }
    }
}
/// Boundary after one original scalar; an empty input has no boundary slots.
#[repr(u8)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LineBreakKind {
    /// A break is forbidden.
    #[default]
    Prohibited = 0,
    /// A line fitter may select this boundary.
    Opportunity = 1,
    /// A hard break or the final boundary of a nonempty inventory.
    Mandatory = 2,
}
/// Original default and native selection profiles retain independent semantics.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LineBreakPolicy {
    /// Default Unicode 17 rules; also the original managed resolver's policy.
    #[default]
    Unicode17,
    /// Original native EDIT selection projection: pinned BMP AL/ID symbols and
    /// no LB8a after ZWJ. This does not implement complete EDIT word selection.
    EditSelection,
}
/// Look up the original unresolved class. Invalid scalar values yield Unknown.
pub fn break_class(code_point: u32) -> LineBreakClass {
    if char::from_u32(code_point).is_none() {
        return LineBreakClass::Unknown;
    }
    LineBreakClass::from_value(range_value(code_point, &data::LINE_RANGES, 0))
}
use LineBreakClass::*;
use LineBreakKind::{Mandatory as Forced, Opportunity, Prohibited};
fn hard(c: LineBreakClass) -> bool {
    matches!(c, Mandatory | CarriageReturn | LineFeed | NextLine)
}
fn alphabetic(c: LineBreakClass) -> bool {
    matches!(c, Alphabetic | HebrewLetter)
}
fn hangul(c: LineBreakClass) -> bool {
    matches!(c, HangulL | HangulV | HangulT | HangulLv | HangulLvt)
}
fn ideographic(c: LineBreakClass) -> bool {
    matches!(c, Ideographic | EmojiBase | EmojiModifier)
}
fn east_asian(cp: u32) -> bool {
    range_value(cp, &data::LINE_EAST_ASIAN_RANGES, 0) != 0
}
fn quotation_category(cp: u32) -> u32 {
    range_value(cp, &data::LINE_QUOTATION_RANGES, 0)
}
fn mark(cp: u32) -> bool {
    range_value(cp, &data::LINE_MARK_RANGES, 0) != 0
}
fn unassigned(cp: u32) -> bool {
    range_value(cp, &data::LINE_UNASSIGNED_RANGES, 0) != 0
}
fn combining(cp: u32, raw: LineBreakClass) -> bool {
    matches!(raw, CombiningMark | ZeroWidthJoiner) || (raw == ComplexContext && mark(cp))
}
fn resolve_class(cp: u32, policy: LineBreakPolicy) -> LineBreakClass {
    let edit = if policy == LineBreakPolicy::EditSelection {
        LineBreakClass::from_value(range_value(cp, &data::EDIT_LINE_RANGES, 0))
    } else {
        Unknown
    };
    match if edit == Unknown {
        break_class(cp)
    } else {
        edit
    } {
        Ambiguous | Surrogate | Unknown => Alphabetic,
        ConditionalJapanese => Nonstarter,
        ComplexContext => {
            if mark(cp) {
                CombiningMark
            } else {
                Alphabetic
            }
        }
        c => c,
    }
}
fn previous_non_space(classes: &[LineBreakClass], mut before: usize) -> usize {
    while before != 0 {
        before -= 1;
        if classes[before] != Space {
            return before;
        }
    }
    classes.len()
}
fn source_base(input: &[UnicodeScalar], mut index: usize) -> usize {
    while index != 0
        && combining(
            input[index].code_point,
            break_class(input[index].code_point),
        )
    {
        index -= 1;
    }
    index
}
fn next_source_base(input: &[UnicodeScalar], mut index: usize) -> usize {
    while index < input.len()
        && combining(
            input[index].code_point,
            break_class(input[index].code_point),
        )
    {
        index += 1;
    }
    index
}
fn numeric_left(classes: &[LineBreakClass], mut left: usize) -> bool {
    while matches!(classes[left], BreakSymbol | InfixNumeric) {
        if left == 0 {
            return false;
        }
        left -= 1;
    }
    classes[left] == Numeric
}
fn numeric_right(classes: &[LineBreakClass], mut right: usize) -> bool {
    if right >= classes.len() {
        return false;
    }
    if classes[right] == OpenPunctuation {
        right += 1;
    }
    if classes.get(right) == Some(&InfixNumeric) {
        right += 1;
    }
    classes.get(right) == Some(&Numeric)
}
fn boundary(
    input: &[UnicodeScalar],
    classes: &[LineBreakClass],
    right: usize,
    policy: LineBreakPolicy,
) -> LineBreakKind {
    let left_index = right - 1;
    let left = classes[left_index];
    let next = classes[right];
    let raw_left = break_class(input[left_index].code_point);
    let raw_next = break_class(input[right].code_point);
    let left_base = source_base(input, left_index);
    // LB5–LB12a: preserve non-tailorable precedence and original raw classes.
    if raw_left == CarriageReturn && raw_next == LineFeed {
        return Prohibited;
    }
    if hard(raw_left) {
        return Forced;
    }
    if hard(raw_next) || matches!(raw_next, Space | ZeroWidthSpace) {
        return Prohibited;
    }
    if combining(input[right].code_point, raw_next)
        && left != Space
        && !hard(left)
        && left != ZeroWidthSpace
    {
        return Prohibited;
    }
    let prior = previous_non_space(classes, right);
    if prior != classes.len() && break_class(input[prior].code_point) == ZeroWidthSpace {
        return Opportunity;
    }
    if (raw_left == ZeroWidthJoiner && policy == LineBreakPolicy::Unicode17)
        || left == WordJoiner
        || next == WordJoiner
        || left == Glue
    {
        return Prohibited;
    }
    if next == Glue && !matches!(left, Space | BreakAfter | Hyphen | UnambiguousHyphen) {
        return Prohibited;
    }
    // LB13–LB18: original space-sensitive quotation and punctuation context.
    if matches!(
        next,
        ClosePunctuation | CloseParenthesis | Exclamation | BreakSymbol
    ) {
        return Prohibited;
    }
    if prior != classes.len() && classes[prior] == OpenPunctuation {
        return Prohibited;
    }
    if prior != classes.len() && classes[prior] == Quotation {
        let quote_base = source_base(input, prior);
        if quotation_category(input[quote_base].code_point) == 1
            && (quote_base == 0
                || hard(classes[quote_base - 1])
                || matches!(
                    classes[quote_base - 1],
                    OpenPunctuation | Quotation | Glue | Space | ZeroWidthSpace
                ))
        {
            return Prohibited;
        }
    }
    if next == Quotation
        && quotation_category(input[right].code_point) == 2
        && (right + 1 == classes.len()
            || hard(classes[right + 1])
            || matches!(
                classes[right + 1],
                Space
                    | Glue
                    | WordJoiner
                    | ClosePunctuation
                    | Quotation
                    | CloseParenthesis
                    | Exclamation
                    | InfixNumeric
                    | BreakSymbol
                    | ZeroWidthSpace
            ))
    {
        return Prohibited;
    }
    if left == Space && next == InfixNumeric && classes.get(right + 1) == Some(&Numeric) {
        return Opportunity;
    }
    if next == InfixNumeric {
        return Prohibited;
    }
    if prior != classes.len()
        && matches!(classes[prior], ClosePunctuation | CloseParenthesis)
        && next == Nonstarter
    {
        return Prohibited;
    }
    if prior != classes.len() && classes[prior] == BreakBoth && next == BreakBoth {
        return Prohibited;
    }
    if left == Space {
        return Opportunity;
    }
    // LB19/LB19a: original Pi/Pf sides and source bases, after LB15a/LB15b.
    if left == Quotation {
        let before = if left_base == 0 {
            input.len()
        } else {
            source_base(input, left_base - 1)
        };
        if quotation_category(input[left_base].code_point) != 2
            || !east_asian(input[right].code_point)
            || before == input.len()
            || !east_asian(input[before].code_point)
        {
            return Prohibited;
        }
    }
    if next == Quotation {
        let after = next_source_base(input, right + 1);
        if quotation_category(input[right].code_point) != 1
            || !east_asian(input[left_base].code_point)
            || after == input.len()
            || !east_asian(input[after].code_point)
        {
            return Prohibited;
        }
    }
    // LB20–LB24.
    if left == Contingent || next == Contingent {
        return Opportunity;
    }
    if matches!(left, Hyphen | UnambiguousHyphen)
        && alphabetic(next)
        && (left_base == 0
            || hard(classes[left_base - 1])
            || matches!(
                classes[left_base - 1],
                Space | ZeroWidthSpace | Contingent | Glue
            ))
    {
        return Prohibited;
    }
    if matches!(next, BreakAfter | UnambiguousHyphen | Hyphen | Nonstarter) || left == BreakBefore {
        return Prohibited;
    }
    if left_base != 0
        && classes[left_base - 1] == HebrewLetter
        && matches!(left, Hyphen | UnambiguousHyphen)
        && next != HebrewLetter
    {
        return Prohibited;
    }
    if left == BreakSymbol && next == HebrewLetter {
        return Prohibited;
    }
    if next == Inseparable
        || (alphabetic(left) && next == Numeric)
        || (left == Numeric && alphabetic(next))
        || (left == PrefixNumeric && ideographic(next))
        || (ideographic(left) && next == PostfixNumeric)
        || (matches!(left, PrefixNumeric | PostfixNumeric) && alphabetic(next))
        || (alphabetic(left) && matches!(next, PrefixNumeric | PostfixNumeric))
    {
        return Prohibited;
    }
    // LB25: numeric expressions.
    if matches!(next, PostfixNumeric | PrefixNumeric | Numeric) && numeric_left(classes, left_index)
    {
        return Prohibited;
    }
    if matches!(next, PostfixNumeric | PrefixNumeric)
        && matches!(left, ClosePunctuation | CloseParenthesis)
        && left_index != 0
        && numeric_left(classes, left_index - 1)
    {
        return Prohibited;
    }
    if matches!(left, PostfixNumeric | PrefixNumeric) && numeric_right(classes, right) {
        return Prohibited;
    }
    if matches!(left, Hyphen | InfixNumeric) && next == Numeric {
        return Prohibited;
    }
    // LB26–LB30b: Hangul, Brahmic syllables, words, flags and emoji.
    if (left == HangulL && matches!(next, HangulL | HangulV | HangulLv | HangulLvt))
        || (matches!(left, HangulV | HangulLv) && matches!(next, HangulV | HangulT))
        || (matches!(left, HangulT | HangulLvt) && next == HangulT)
        || (hangul(left) && next == PostfixNumeric)
        || (left == PrefixNumeric && hangul(next))
        || (alphabetic(left) && alphabetic(next))
    {
        return Prohibited;
    }
    let left_aksara = matches!(left, Aksara | AksaraStart) || input[left_base].code_point == 0x25cc;
    let next_aksara = matches!(next, Aksara | AksaraStart) || input[right].code_point == 0x25cc;
    if (left == AksaraPrebase && next_aksara)
        || (left_aksara && matches!(next, ViramaFinal | Virama))
        || (left == Virama
            && next_aksara
            && left_base != 0
            && (matches!(
                classes[source_base(input, left_base - 1)],
                Aksara | AksaraStart
            ) || input[source_base(input, left_base - 1)].code_point == 0x25cc))
        || (left_aksara && next_aksara && classes.get(right + 1) == Some(&ViramaFinal))
        || (left == InfixNumeric && alphabetic(next))
    {
        return Prohibited;
    }
    if (alphabetic(left) || left == Numeric)
        && next == OpenPunctuation
        && !east_asian(input[right].code_point)
    {
        return Prohibited;
    }
    if left == CloseParenthesis
        && !east_asian(input[left_base].code_point)
        && (alphabetic(next) || next == Numeric)
    {
        return Prohibited;
    }
    if left == RegionalIndicator && next == RegionalIndicator {
        let mut count = 0usize;
        for scalar in input[..=left_index].iter().rev() {
            let raw = break_class(scalar.code_point);
            if combining(scalar.code_point, raw) {
                continue;
            }
            if raw != RegionalIndicator {
                break;
            }
            count += 1;
        }
        if count & 1 != 0 {
            return Prohibited;
        }
    }
    if (left == EmojiBase
        || (unassigned(input[left_base].code_point)
            && is_extended_pictographic(input[left_base].code_point)))
        && next == EmojiModifier
    {
        return Prohibited;
    }
    Opportunity
}
/// Resolve boundaries and classes without allocation. Capacities are checked
/// before any writes. An invalid later scalar may leave a resolved scratch prefix
/// (the original contract), but break output stays untouched. Unused tails and
/// every original scalar/source record remain unchanged. Rust borrows keep the
/// input, scratch and output disjoint. The final nonempty boundary is mandatory.
pub fn resolve_into(
    input: &[UnicodeScalar],
    policy: LineBreakPolicy,
    classes: &mut [LineBreakClass],
    breaks: &mut [LineBreakKind],
) -> Result<(), UnicodeError> {
    if classes.len() < input.len() || breaks.len() < input.len() {
        return Err(UnicodeError::Capacity);
    }
    for (index, scalar) in input.iter().enumerate() {
        if char::from_u32(scalar.code_point).is_none() {
            return Err(UnicodeError::InvalidArgument);
        }
        let mut resolved = resolve_class(scalar.code_point, policy);
        if matches!(resolved, CombiningMark | ZeroWidthJoiner) {
            resolved = if index != 0
                && !hard(classes[index - 1])
                && !matches!(classes[index - 1], Space | ZeroWidthSpace)
            {
                classes[index - 1]
            } else {
                Alphabetic
            };
        }
        classes[index] = resolved;
    }
    for right in 1..input.len() {
        breaks[right - 1] = boundary(input, &classes[..input.len()], right, policy);
    }
    if !input.is_empty() {
        breaks[input.len() - 1] = Forced;
    }
    Ok(())
}
/// Immutable owned logical line opportunities and the exact decoded generation.
#[derive(Clone, Debug)]
pub struct LineBreakText {
    text: DecodedText,
    classes: Vec<LineBreakClass>,
    breaks: Vec<LineBreakKind>,
    policy: LineBreakPolicy,
}
impl LineBreakText {
    /// Resolve a complete already-validated generation and retain its source.
    pub fn new(text: DecodedText, policy: LineBreakPolicy) -> Result<Self, UnicodeError> {
        let mut classes = allocate(text.scalars().len())?;
        let mut breaks = allocate(text.scalars().len())?;
        resolve_into(text.scalars(), policy, &mut classes, &mut breaks)?;
        Ok(Self {
            text,
            classes,
            breaks,
            policy,
        })
    }
    /// Exact original encoding and decoded scalar slots.
    pub fn text(&self) -> &DecodedText {
        &self.text
    }
    /// Original resolved classes, including inherited combining classes.
    pub fn classes(&self) -> &[LineBreakClass] {
        &self.classes
    }
    /// Boundary after each original logical scalar.
    pub fn breaks_after(&self) -> &[LineBreakKind] {
        &self.breaks
    }
    /// Selected original profile.
    pub fn policy(&self) -> LineBreakPolicy {
        self.policy
    }
}
