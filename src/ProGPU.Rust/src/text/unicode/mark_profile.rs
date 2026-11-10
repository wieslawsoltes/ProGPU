use super::{GeneralCategory, UnicodeError, general_category};
#[path = "mark_profile_data.rs"]
mod data;
/// Explicit source Rune mark-category repertoire. This is independent of the
/// source runtime's canonical-normalization repertoire and of native frozen data.
/// Select from observed source behavior; OS/SDK names do not establish the profile.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(u16)]
pub enum ManagedMarkProfile {
    /// Unicode 15.1 NonspacingMark, SpacingCombiningMark and EnclosingMark facts.
    Unicode15_1 = 151,
    /// Unicode 16 mark facts; matches the original frozen category inventory.
    #[default]
    Unicode16_0 = 160,
    /// Unicode 17 mark facts, including later assigned characters.
    Unicode17_0 = 170,
}
impl TryFrom<u16> for ManagedMarkProfile {
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
impl ManagedMarkProfile {
    /// Match original Rune classification, rejecting non-scalars instead of
    /// treating them as unassigned. Native `general_category` remains unchanged.
    pub fn is_mark(self, cp: u32) -> Result<bool, UnicodeError> {
        if char::from_u32(cp).is_none() {
            return Err(UnicodeError::InvalidArgument);
        }
        let (absent, added) = match self {
            Self::Unicode15_1 => (data::ABSENT_15_1_0, data::ADDED_15_1_0),
            Self::Unicode16_0 => (data::ABSENT_16_0_0, data::ADDED_16_0_0),
            Self::Unicode17_0 => (data::ABSENT_17_0_0, data::ADDED_17_0_0),
        };
        Ok(added.binary_search(&cp).is_ok()
            || absent.binary_search(&cp).is_err()
                && matches!(
                    general_category(cp),
                    GeneralCategory::NonspacingMark
                        | GeneralCategory::SpacingCombiningMark
                        | GeneralCategory::EnclosingMark
                ))
    }
}
