//! Original canonical normalization using ProGPU's shared .NET-generated plan.
//! This preserves the plan's frozen repertoire and original native source-range
//! arithmetic. Shaping-specific exceptions and grapheme policy are separate.
use super::{
    DecodedText, UnicodeError, UnicodeScalar, allocate, canonical_combining_class, is_scalar,
    script,
};
use std::sync::{Arc, OnceLock};

/// Original canonical forms; compatibility normalization is not this contract.
#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NormalizationForm {
    /// Fully decompose using the selected plan and stably order combining marks.
    CanonicalDecomposition = 0,
    /// Decompose/order, then compose the selected plan's unblocked pairs.
    CanonicalComposition = 1,
}
impl TryFrom<u8> for NormalizationForm {
    type Error = UnicodeError;
    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::CanonicalDecomposition),
            1 => Ok(Self::CanonicalComposition),
            _ => Err(UnicodeError::InvalidArgument),
        }
    }
}
#[derive(Clone, Copy, Debug, Default)]
struct Layout {
    decompositions: usize,
    scalar_offset: usize,
    composition_offset: usize,
    compositions: usize,
}
fn read(bytes: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap())
}
impl Layout {
    fn parse(bytes: &[u8]) -> Result<Self, UnicodeError> {
        let invalid = UnicodeError::InvalidArgument;
        if bytes.len() < 20 || read(bytes, 0) != 0x4e554750 || read(bytes, 4) != 1 {
            return Err(invalid);
        }
        let decompositions = read(bytes, 8) as usize;
        let scalars = read(bytes, 12) as usize;
        let compositions = read(bytes, 16) as usize;
        let scalar_offset = decompositions
            .checked_mul(12)
            .and_then(|n| n.checked_add(20))
            .ok_or(invalid)?;
        let composition_offset = scalars
            .checked_mul(4)
            .and_then(|n| n.checked_add(scalar_offset))
            .ok_or(invalid)?;
        let expected = compositions
            .checked_mul(12)
            .and_then(|n| n.checked_add(composition_offset))
            .ok_or(invalid)?;
        if expected != bytes.len() {
            return Err(invalid);
        }
        let mut previous = 0;
        for index in 0..decompositions {
            let offset = 20 + index * 12;
            let cp = read(bytes, offset);
            let start = read(bytes, offset + 4) as usize;
            let count = read(bytes, offset + 8) as usize;
            if !is_scalar(cp)
                || (index != 0 && cp <= previous)
                || start > scalars
                || count > scalars - start
            {
                return Err(invalid);
            }
            previous = cp;
            // The original validates referenced components only; unused slots,
            // zero-length records and overlapping slices retain their admission.
            for component in start..start + count {
                if !is_scalar(read(bytes, scalar_offset + component * 4)) {
                    return Err(invalid);
                }
            }
        }
        let mut previous = 0u64;
        for index in 0..compositions {
            let offset = composition_offset + index * 12;
            let first = read(bytes, offset);
            let second = read(bytes, offset + 4);
            let composed = read(bytes, offset + 8);
            let pair = (u64::from(first) << 32) | u64::from(second);
            if !is_scalar(first)
                || !is_scalar(second)
                || !is_scalar(composed)
                || (index != 0 && pair <= previous)
            {
                return Err(invalid);
            }
            previous = pair;
        }
        Ok(Self {
            decompositions,
            scalar_offset,
            composition_offset,
            compositions,
        })
    }
}
/// Validated borrowed little-endian plan, with no allocation or native pointers.
/// Construction preserves the complete original native validation contract.
#[derive(Clone, Copy, Debug)]
pub struct NormalizationData<'a> {
    bytes: &'a [u8],
    layout: Layout,
}
impl<'a> NormalizationData<'a> {
    /// Validate exact size, ordered keys, scalar values and every referenced span.
    pub fn parse(bytes: &'a [u8]) -> Result<Self, UnicodeError> {
        Ok(Self {
            bytes,
            layout: Layout::parse(bytes)?,
        })
    }
    /// Borrow the exact original plan bytes.
    pub fn bytes(&self) -> &'a [u8] {
        self.bytes
    }
    /// Look up the original fully decomposed little-endian scalar slice.
    /// `Some(&[])` is a present zero-length record, distinct from absence.
    pub fn decomposition(&self, code_point: u32) -> Option<&'a [u8]> {
        let (mut low, mut high) = (0, self.layout.decompositions);
        while low < high {
            let middle = low + (high - low) / 2;
            let offset = 20 + middle * 12;
            let current = read(self.bytes, offset);
            if code_point < current {
                high = middle;
            } else if code_point > current {
                low = middle + 1;
            } else {
                let start = self.layout.scalar_offset + read(self.bytes, offset + 4) as usize * 4;
                let count = read(self.bytes, offset + 8) as usize * 4;
                return Some(&self.bytes[start..start + count]);
            }
        }
        None
    }
    /// Return only the exact original ordered pair's composition, if present.
    pub fn compose(&self, first: u32, second: u32) -> Option<u32> {
        let requested = (u64::from(first) << 32) | u64::from(second);
        let (mut low, mut high) = (0, self.layout.compositions);
        while low < high {
            let middle = low + (high - low) / 2;
            let offset = self.layout.composition_offset + middle * 12;
            let current = (u64::from(read(self.bytes, offset)) << 32)
                | u64::from(read(self.bytes, offset + 4));
            if requested < current {
                high = middle;
            } else if requested > current {
                low = middle + 1;
            } else {
                return Some(read(self.bytes, offset + 8));
            }
        }
        None
    }
}
/// Owned custom plan; borrowed views cannot outlive this immutable generation.
#[derive(Clone, Debug)]
pub struct NormalizationPlan {
    bytes: Arc<[u8]>,
    layout: Layout,
}
impl NormalizationPlan {
    /// Validate and retain the exact selected plan without copying its tables.
    pub fn new(bytes: impl Into<Arc<[u8]>>) -> Result<Self, UnicodeError> {
        let bytes = bytes.into();
        let layout = Layout::parse(&bytes)?;
        Ok(Self { bytes, layout })
    }
    /// Borrow this plan's already-validated generation.
    pub fn data(&self) -> NormalizationData<'_> {
        NormalizationData {
            bytes: &self.bytes,
            layout: self.layout,
        }
    }
}
/// Process-wide validated view of the exact shared ProGPU normalization resource.
/// Its repertoire comes from the original .NET-generated plan, independently of
/// the version of the other Unicode property inventories. No host file is read.
pub fn default_data() -> Result<&'static NormalizationData<'static>, UnicodeError> {
    static DATA: OnceLock<Result<NormalizationData<'static>, UnicodeError>> = OnceLock::new();
    DATA.get_or_init(|| {
        NormalizationData::parse(include_bytes!(
            "../../../../ProGPU.Text/UnicodeNormalizationData.bin"
        ))
    })
    .as_ref()
    .map_err(|e| *e)
}
/// Required capacity is the complete decomposition inventory, even for composed
/// output. Validate every scalar before writing; reject counts above u32::MAX.
pub fn scalar_capacity(
    input: &[UnicodeScalar],
    data: &NormalizationData<'_>,
) -> Result<u32, UnicodeError> {
    let mut count = 0u32;
    for scalar in input {
        if !is_scalar(scalar.code_point) {
            return Err(UnicodeError::InvalidArgument);
        }
        let increment = data
            .decomposition(scalar.code_point)
            .map_or(1, |b| b.len() / 4);
        count = count
            .checked_add(u32::try_from(increment).map_err(|_| UnicodeError::InvalidArgument)?)
            .ok_or(UnicodeError::InvalidArgument)?;
    }
    Ok(count)
}
fn component(code_point: u32, source: &UnicodeScalar) -> UnicodeScalar {
    UnicodeScalar {
        code_point,
        input_index: source.input_index,
        input_length: source.input_length,
        canonical_combining_class: canonical_combining_class(code_point),
        reserved: 0,
        script: script(code_point),
    }
}
fn merge_range(destination: &mut UnicodeScalar, source: UnicodeScalar) {
    let end = (u64::from(destination.input_index) + u64::from(destination.input_length))
        .max(u64::from(source.input_index) + u64::from(source.input_length));
    let start = destination.input_index.min(source.input_index);
    destination.input_index = start;
    destination.input_length = (end - u64::from(start)).min(u64::from(u16::MAX)) as u16;
}
/// Normalize without allocation and return the original written scalar count.
/// Validation/capacity failures leave output untouched. Bytes past decomposition
/// capacity stay untouched; composition may leave intermediate records between
/// the returned count and that capacity, exactly as in the original native API.
/// Original coordinates are propagated and composition unions source ranges with
/// a u16 length clamp. Safe borrows prohibit input/output aliasing.
pub fn normalize_into(
    input: &[UnicodeScalar],
    data: &NormalizationData<'_>,
    form: NormalizationForm,
    output: &mut [UnicodeScalar],
) -> Result<u32, UnicodeError> {
    let required = scalar_capacity(input, data)? as usize;
    if output.len() < required {
        return Err(UnicodeError::Capacity);
    }
    let mut count = 0;
    for scalar in input {
        if let Some(bytes) = data.decomposition(scalar.code_point) {
            for offset in (0..bytes.len()).step_by(4) {
                output[count] = component(read(bytes, offset), scalar);
                count += 1;
            }
        } else {
            output[count] = component(scalar.code_point, scalar);
            count += 1;
        }
    }
    // Stable insertion ordering preserves original equal-class order and stops
    // at starters. Custom plans are consumed once, never recursively expanded.
    for index in 1..count {
        let value = output[index];
        if value.canonical_combining_class == 0 {
            continue;
        }
        let mut destination = index;
        while destination > 0 {
            let previous = output[destination - 1].canonical_combining_class;
            if previous == 0 || previous <= value.canonical_combining_class {
                break;
            }
            output[destination] = output[destination - 1];
            destination -= 1;
        }
        output[destination] = value;
    }
    if form == NormalizationForm::CanonicalComposition && count > 1 {
        let (mut destination, mut starter, mut previous_class) = (1, 0, 0);
        for source in 1..count {
            let current = output[source];
            let class = current.canonical_combining_class;
            let blocked = previous_class != 0 && previous_class >= class;
            if !blocked
                && let Some(composed) = data.compose(output[starter].code_point, current.code_point)
            {
                merge_range(&mut output[starter], current);
                output[starter].code_point = composed;
                output[starter].canonical_combining_class = canonical_combining_class(composed);
                output[starter].script = script(composed);
                continue;
            }
            output[destination] = current;
            if class == 0 {
                starter = destination;
            }
            previous_class = class;
            destination += 1;
        }
        count = destination;
    }
    Ok(count as u32)
}

fn validate_managed_input(input: &[UnicodeScalar]) -> Result<(), UnicodeError> {
    // Observed through the original .NET generator/shaper's canonical-form
    // call: U+FFFE rejects independently of UTF validity. Other noncharacters
    // retain their original admission; no Unicode-category approximation.
    if input
        .iter()
        .any(|s| !is_scalar(s.code_point) || s.code_point == 0xfffe)
    {
        return Err(UnicodeError::InvalidArgument);
    }
    Ok(())
}

/// Explicit original managed canonical-form input admission over the shared plan.
/// Reject U+FFFE and invalid scalars before capacity checks or output mutation;
/// ordinary native normalization continues to admit U+FFFE. This is the canonical
/// operation only, not the managed shaper's per-grapheme/script selection policy.
pub fn normalize_managed_into(
    input: &[UnicodeScalar],
    form: NormalizationForm,
    output: &mut [UnicodeScalar],
) -> Result<u32, UnicodeError> {
    validate_managed_input(input)?;
    normalize_into(input, default_data()?, form, output)
}
/// Owned normalized generation retaining the exact original decoded source.
#[derive(Clone, Debug)]
pub struct NormalizedText {
    text: DecodedText,
    scalars: Vec<UnicodeScalar>,
    form: NormalizationForm,
}
impl NormalizedText {
    /// Normalize with the shared original plan; preserve the encoded source.
    pub fn new(text: DecodedText, form: NormalizationForm) -> Result<Self, UnicodeError> {
        Self::with_data(text, default_data()?, form)
    }
    /// Use the original managed canonical-form admission, including U+FFFE
    /// rejection, while retaining the exact original source generation.
    pub fn new_managed(text: DecodedText, form: NormalizationForm) -> Result<Self, UnicodeError> {
        validate_managed_input(text.scalars())?;
        Self::new(text, form)
    }
    /// Normalize with an explicitly selected validated plan. The resulting
    /// records own all derived data and remain valid after the plan is dropped.
    pub fn with_data(
        text: DecodedText,
        data: &NormalizationData<'_>,
        form: NormalizationForm,
    ) -> Result<Self, UnicodeError> {
        let mut scalars = allocate(scalar_capacity(text.scalars(), data)? as usize)?;
        let written = normalize_into(text.scalars(), data, form, &mut scalars)?;
        scalars.truncate(written as usize);
        Ok(Self {
            text,
            scalars,
            form,
        })
    }
    /// Exact unmodified source and its initial decoded records.
    pub fn text(&self) -> &DecodedText {
        &self.text
    }
    /// Original normalized records, including propagated/clamped source ranges.
    pub fn scalars(&self) -> &[UnicodeScalar] {
        &self.scalars
    }
    /// Selected canonical form.
    pub fn form(&self) -> NormalizationForm {
        self.form
    }
}
