use super::{
    NormalizationData, NormalizationForm, Source, UnicodeError, UnicodeScalar,
    canonical_combining_class, capacity, default_data, normalize_source, validate_managed_input,
};
#[path = "profile_data.rs"]
mod data;

/// Explicit Unicode data identity for a source managed canonical-form operation.
/// The caller must establish its source runtime's actual repertoire; platform
/// names and SDK versions do not select a profile. Native/default APIs retain
/// the original frozen ProGPU data. These profiles do not select script policy.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u16)]
pub enum ManagedNormalizationProfile {
    /// Unicode 15.1 canonical decomposition/composition and combining classes.
    Unicode15_1 = 151,
    /// Unicode 16.0 canonical decomposition/composition and combining classes.
    Unicode16_0 = 160,
    /// Unicode 17.0 canonical decomposition/composition and combining classes.
    Unicode17_0 = 170,
}
impl TryFrom<u16> for ManagedNormalizationProfile {
    type Error = UnicodeError;
    fn try_from(value: u16) -> Result<Self, Self::Error> {
        match value {
            151 => Ok(Self::Unicode15_1),
            160 => Ok(Self::Unicode16_0),
            170 => Ok(Self::Unicode17_0),
            _ => Err(UnicodeError::InvalidArgument),
        }
    }
}
impl ManagedNormalizationProfile {
    /// Source ordering class, independent of the original ProGPU class metadata
    /// returned in normalized scalar records. Invalid/unassigned scalars are zero.
    pub fn canonical_class(self, cp: u32) -> u8 {
        use super::super::{is_scalar, properties::range_value};
        if !is_scalar(cp) {
            return 0;
        }
        match self {
            Self::Unicode15_1 => range_value(cp, data::CLASSES_15_1_0, 0) as u8,
            Self::Unicode16_0 => range_value(cp, data::CLASSES_16_0_0, 0) as u8,
            Self::Unicode17_0 => canonical_combining_class(cp),
        }
    }
    fn absent(self) -> &'static [u32] {
        match self {
            Self::Unicode15_1 => data::ABSENT_15_1_0,
            Self::Unicode16_0 => data::ABSENT_16_0_0,
            Self::Unicode17_0 => data::ABSENT_17_0_0,
        }
    }
    fn source(self) -> Result<ProfileSource, UnicodeError> {
        Ok(ProfileSource {
            plan: default_data()?,
            profile: self,
        })
    }
}
struct ProfileSource {
    plan: &'static NormalizationData<'static>,
    profile: ManagedNormalizationProfile,
}
impl Source for ProfileSource {
    fn decomposition(&self, cp: u32) -> Option<&[u8]> {
        if self.profile.absent().binary_search(&cp).is_ok() {
            None
        } else {
            self.plan.decomposition(cp)
        }
    }
    fn compose(&self, first: u32, second: u32) -> Option<u32> {
        self.plan
            .compose(first, second)
            .filter(|cp| self.profile.absent().binary_search(cp).is_err())
    }
    fn class(&self, cp: u32) -> u8 {
        self.profile.canonical_class(cp)
    }
}
/// Complete source-profile decomposition capacity, with original managed input
/// admission before output allocation. No runtime discovery or allocation here.
pub fn managed_profile_capacity(
    input: &[UnicodeScalar],
    profile: ManagedNormalizationProfile,
) -> Result<u32, UnicodeError> {
    validate_managed_input(input)?;
    capacity(input, &profile.source()?)
}
/// Run the original algorithm using explicit immutable source Unicode facts.
/// Ordering/composition use source classes; published scalar metadata keeps
/// ProGPU's original class/script inventory. Source ranges and intermediate
/// tails follow the original native rules. Validation/capacity failure is atomic.
pub fn normalize_managed_profile_into(
    input: &[UnicodeScalar],
    profile: ManagedNormalizationProfile,
    form: NormalizationForm,
    output: &mut [UnicodeScalar],
) -> Result<u32, UnicodeError> {
    validate_managed_input(input)?;
    let (written, required) = normalize_source(input, &profile.source()?, form, output)?;
    for scalar in &mut output[..required] {
        scalar.canonical_combining_class = canonical_combining_class(scalar.code_point);
    }
    Ok(written)
}
