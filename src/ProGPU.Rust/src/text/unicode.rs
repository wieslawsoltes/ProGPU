//! Original strict Unicode decoding, static properties and initial script runs.
//! Property facts come from ProGPU's checked-in native projection of C# data,
//! preserving the original generators' Unicode/.NET versions and provenance.
//! Bidi levels and grapheme boundaries are separate from shaping and paragraph layout.
use crate::font::Tag;
use std::sync::Arc;
pub mod bidi;
mod data;
pub mod grapheme;
pub mod line_break;
mod mark_profile;
pub mod normalization;
mod properties;
pub use mark_profile::ManagedMarkProfile;
mod types;
pub use properties::{
    bidi_bracket, bidi_class, canonical_combining_class, decimal_digit_value, general_category,
    infer_script, mirrored_code_point, script, vertical_code_point,
};
pub use types::{BidiBracketKind, BidiClass, GeneralCategory};

/// Original error codes for Unicode decoding and itemization boundaries.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[non_exhaustive]
#[repr(u32)]
pub enum UnicodeError {
    /// A scalar, source inventory or request is invalid.
    #[error("invalid Unicode argument")]
    InvalidArgument = 1,
    /// Invalid UTF encoding; the strict route never substitutes replacement text.
    #[error("invalid Unicode encoding")]
    InvalidEncoding = 2,
    /// Output cannot fit or cannot be allocated.
    #[error("Unicode capacity exceeded")]
    Capacity = 3,
}
/// Original 16-byte Unicode value record, including source-unit coordinates.
/// Public fields allow original caller-authored metadata; itemization validates
/// scalar values while preserving raw scripts and the original source arithmetic.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct UnicodeScalar {
    /// Unicode scalar value. Strict decoding rejects surrogate code points.
    pub code_point: u32,
    /// Byte offset for UTF-8 or code-unit offset for UTF-16.
    pub input_index: u32,
    /// Original consumed source units.
    pub input_length: u16,
    /// Original canonical combining class.
    pub canonical_combining_class: u8,
    /// Original reserved byte. Decoders emit zero; itemization does not rewrite it.
    pub reserved: u8,
    /// Original resolved OpenType script tag.
    pub script: Tag,
}
/// Original initial script-run record. Common/Inherited attaches to the preceding
/// resolved script, or the first following script for a leading sequence.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ScriptRun {
    /// First scalar slot.
    pub scalar_start: u32,
    /// Number of original scalar slots.
    pub scalar_count: u32,
    /// Original first input-unit index.
    pub input_start: u32,
    /// Original clamped end-minus-start, not a recomputed Unicode length.
    pub input_length: u32,
    /// Resolved original run script.
    pub script: Tag,
}
/// Original paired-bracket metadata; absence remains `None`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BidiBracket {
    /// Original paired scalar value.
    pub paired_code_point: u32,
    /// Original open/close classification.
    pub kind: BidiBracketKind,
}
/// Complete immutable original encoding retained alongside decoded scalar ranges.
#[derive(Clone, Debug)]
pub enum UnicodeSource {
    /// Exact original strict UTF-8 bytes.
    Utf8(Arc<[u8]>),
    /// Exact original strict UTF-16 code units.
    Utf16(Arc<[u16]>),
}
/// Owned decoded generation with exact source ranges and generated properties.
#[derive(Clone, Debug)]
pub struct DecodedText {
    source: UnicodeSource,
    scalars: Vec<UnicodeScalar>,
}
impl DecodedText {
    /// Validate and own a complete UTF-8 generation without replacing invalid bytes.
    pub fn from_utf8(source: impl Into<Arc<[u8]>>) -> Result<Self, UnicodeError> {
        let source = source.into();
        let mut scalars = allocate(utf8_scalar_count(&source)? as usize)?;
        decode_utf8_into(&source, &mut scalars)?;
        Ok(Self {
            source: UnicodeSource::Utf8(source),
            scalars,
        })
    }
    /// Validate and own a complete UTF-16 generation. Unpaired surrogates reject;
    /// the independent simple-glyph-run API retains its original different policy.
    pub fn from_utf16(source: impl Into<Arc<[u16]>>) -> Result<Self, UnicodeError> {
        let source = source.into();
        let mut scalars = allocate(utf16_scalar_count(&source)? as usize)?;
        decode_utf16_into(&source, &mut scalars)?;
        Ok(Self {
            source: UnicodeSource::Utf16(source),
            scalars,
        })
    }
    /// Original immutable encoded source, including its encoding kind.
    pub fn source(&self) -> &UnicodeSource {
        &self.source
    }
    /// Decoded records in original logical source order.
    pub fn scalars(&self) -> &[UnicodeScalar] {
        &self.scalars
    }
    /// Own the original initial script-run inventory. Language/Script_Extensions
    /// tailoring and bidi visual reordering remain independent operations.
    pub fn script_runs(&self) -> Result<Vec<ScriptRun>, UnicodeError> {
        let mut result = allocate(script_run_count(&self.scalars)? as usize)?;
        itemize_scripts_into(&self.scalars, &mut result)?;
        Ok(result)
    }
}
/// Whether a raw value is a Unicode scalar under the original strict contract.
pub fn is_scalar(value: u32) -> bool {
    value <= 0x10ffff && !(0xd800..=0xdfff).contains(&value)
}
/// Whole-input strict UTF-8 count in O(N) time and O(1) scratch.
pub fn utf8_scalar_count(input: &[u8]) -> Result<u32, UnicodeError> {
    let text = validated_utf8(input)?;
    Ok(text.chars().count() as u32)
}
/// Whole-input strict UTF-16 count in O(N) time and O(1) scratch.
pub fn utf16_scalar_count(input: &[u16]) -> Result<u32, UnicodeError> {
    u32::try_from(input.len()).map_err(|_| UnicodeError::InvalidArgument)?;
    let mut count = 0;
    for c in char::decode_utf16(input.iter().copied()) {
        c.map_err(|_| UnicodeError::InvalidEncoding)?;
        count += 1;
    }
    Ok(count)
}
/// Strict, allocation-free decoding. Complete encoding validation precedes output
/// capacity checking; any failure leaves the entire output and its tail untouched.
pub fn decode_utf8_into(input: &[u8], output: &mut [UnicodeScalar]) -> Result<u32, UnicodeError> {
    let text = validated_utf8(input)?;
    let count = text.chars().count();
    if output.len() < count {
        return Err(UnicodeError::Capacity);
    }
    for ((offset, c), slot) in text.char_indices().zip(output) {
        *slot = make_scalar(c as u32, offset as u32, c.len_utf8() as u16);
    }
    Ok(count as u32)
}
/// Strict UTF-16 counterpart, preserving original code-unit indices and lengths.
/// Validates the whole source before output mutation, with O(1) scratch.
pub fn decode_utf16_into(input: &[u16], output: &mut [UnicodeScalar]) -> Result<u32, UnicodeError> {
    let count = utf16_scalar_count(input)?;
    if output.len() < count as usize {
        return Err(UnicodeError::Capacity);
    }
    let mut offset = 0;
    for (c, slot) in char::decode_utf16(input.iter().copied()).zip(output) {
        let c = c.map_err(|_| UnicodeError::InvalidEncoding)?;
        let length = c.len_utf16() as u16;
        *slot = make_scalar(c as u32, offset, length);
        offset += u32::from(length);
    }
    Ok(count)
}
/// Count the original initial script runs; caller script tags are not normalized.
/// Invalid code points reject even when their raw script tag would be resolvable.
pub fn script_run_count(input: &[UnicodeScalar]) -> Result<u32, UnicodeError> {
    u32::try_from(input.len()).map_err(|_| UnicodeError::InvalidArgument)?;
    let mut active = first_resolved(input);
    let mut previous = DEFAULT_SCRIPT;
    let mut count = 0;
    for scalar in input {
        if !is_scalar(scalar.code_point) {
            return Err(UnicodeError::InvalidArgument);
        }
        let current = effective(scalar, active);
        if count == 0 || current != previous {
            count += 1;
            previous = current;
        }
        if scalar.script != DEFAULT_SCRIPT {
            active = scalar.script;
        }
    }
    Ok(count)
}
/// Itemize original script runs without allocation, preserving output rollback and
/// tails. Source end saturates at u32::MAX; reversed original ranges clamp to zero.
/// No ordering, reserved-byte or script-tag admission is added to the native API.
pub fn itemize_scripts_into(
    input: &[UnicodeScalar],
    output: &mut [ScriptRun],
) -> Result<u32, UnicodeError> {
    let count = script_run_count(input)?;
    if output.len() < count as usize {
        return Err(UnicodeError::Capacity);
    }
    if input.is_empty() {
        return Ok(0);
    }
    let mut active = first_resolved(input);
    let mut run_script = effective(&input[0], active);
    let mut start = 0;
    let mut written = 0;
    if input[0].script != DEFAULT_SCRIPT {
        active = input[0].script;
    }
    for (index, scalar) in input.iter().enumerate().skip(1) {
        let current = effective(scalar, active);
        if current != run_script {
            output[written] = make_run(input, start, index, run_script);
            written += 1;
            start = index;
            run_script = current;
        }
        if scalar.script != DEFAULT_SCRIPT {
            active = scalar.script;
        }
    }
    output[written] = make_run(input, start, input.len(), run_script);
    Ok(count)
}
const DEFAULT_SCRIPT: Tag = Tag::new(*b"DFLT");
fn first_resolved(input: &[UnicodeScalar]) -> Tag {
    input
        .iter()
        .find(|v| v.script != DEFAULT_SCRIPT)
        .map_or(DEFAULT_SCRIPT, |v| v.script)
}
fn effective(s: &UnicodeScalar, active: Tag) -> Tag {
    if s.script == DEFAULT_SCRIPT {
        active
    } else {
        s.script
    }
}
fn make_run(input: &[UnicodeScalar], start: usize, end: usize, script: Tag) -> ScriptRun {
    let first = input[start].input_index;
    let last = &input[end - 1];
    let source_end = last
        .input_index
        .saturating_add(u32::from(last.input_length));
    ScriptRun {
        scalar_start: start as u32,
        scalar_count: (end - start) as u32,
        input_start: first,
        input_length: source_end.saturating_sub(first),
        script,
    }
}
fn make_scalar(code_point: u32, input_index: u32, input_length: u16) -> UnicodeScalar {
    UnicodeScalar {
        code_point,
        input_index,
        input_length,
        canonical_combining_class: canonical_combining_class(code_point),
        reserved: 0,
        script: script(code_point),
    }
}
fn validated_utf8(input: &[u8]) -> Result<&str, UnicodeError> {
    u32::try_from(input.len()).map_err(|_| UnicodeError::InvalidArgument)?;
    std::str::from_utf8(input).map_err(|_| UnicodeError::InvalidEncoding)
}
fn allocate<T: Default + Clone>(count: usize) -> Result<Vec<T>, UnicodeError> {
    let mut v = Vec::new();
    v.try_reserve_exact(count)
        .map_err(|_| UnicodeError::Capacity)?;
    v.resize(count, T::default());
    Ok(v)
}
