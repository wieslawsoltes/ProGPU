//! Original stage identities and independent feature, joiner and syllable policies.
use super::FontError;
/// Original current managed substitution stage. These select lookups; assigning
/// script categories, actions, masks and syllables remains a separate operation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum ManagedSubstitutionStage {
    /// Original All stage.
    #[default]
    All = 0,
    /// Original Directional stage.
    Directional = 1,
    /// Original Preprocessing stage.
    Preprocessing = 2,
    /// Original Repha stage.
    Repha = 3,
    /// Original Prebase stage.
    Prebase = 4,
    /// Original Basic stage.
    Basic = 5,
    /// Original Topographical stage.
    Topographical = 6,
    /// Original Presentation stage.
    Presentation = 7,
    /// Original IndicPreprocessing stage.
    IndicPreprocessing = 8,
    /// Original IndicNukta stage.
    IndicNukta = 9,
    /// Original IndicAkhand stage.
    IndicAkhand = 10,
    /// Original IndicRakar stage.
    IndicRakar = 11,
    /// Original IndicBelow stage.
    IndicBelow = 12,
    /// Original IndicAbove stage.
    IndicAbove = 13,
    /// Original IndicHalf stage.
    IndicHalf = 14,
    /// Original IndicPost stage.
    IndicPost = 15,
    /// Original IndicVattu stage.
    IndicVattu = 16,
    /// Original IndicConjunct stage.
    IndicConjunct = 17,
    /// Original IndicPresentation stage.
    IndicPresentation = 18,
    /// Original KhmerBasic stage.
    KhmerBasic = 19,
    /// Original KhmerPresentation stage.
    KhmerPresentation = 20,
    /// Original MyanmarPreprocessing stage.
    MyanmarPreprocessing = 21,
    /// Original MyanmarRepha stage.
    MyanmarRepha = 22,
    /// Original MyanmarPrebase stage.
    MyanmarPrebase = 23,
    /// Original MyanmarBelow stage.
    MyanmarBelow = 24,
    /// Original MyanmarPostbase stage.
    MyanmarPostbase = 25,
    /// Original MyanmarPresentation stage.
    MyanmarPresentation = 26,
    /// Original ArabicStretch stage.
    ArabicStretch = 27,
    /// Original ArabicPreprocessing stage.
    ArabicPreprocessing = 28,
    /// Original ArabicIsolated stage.
    ArabicIsolated = 29,
    /// Original ArabicFinal stage.
    ArabicFinal = 30,
    /// Original ArabicFinal2 stage.
    ArabicFinal2 = 31,
    /// Original ArabicFinal3 stage.
    ArabicFinal3 = 32,
    /// Original ArabicMedial stage.
    ArabicMedial = 33,
    /// Original ArabicMedial2 stage.
    ArabicMedial2 = 34,
    /// Original ArabicInitial stage.
    ArabicInitial = 35,
    /// Original ArabicRequiredLigatures stage.
    ArabicRequiredLigatures = 36,
    /// Original ArabicContextual stage.
    ArabicContextual = 37,
    /// Original ArabicPostRequired stage.
    ArabicPostRequired = 38,
    /// Original ArabicPostContextual stage.
    ArabicPostContextual = 39,
    /// Original ArabicLigatures stage.
    ArabicLigatures = 40,
    /// Original ArabicPresentation stage.
    ArabicPresentation = 41,
}
impl TryFrom<u8> for ManagedSubstitutionStage {
    type Error = FontError;
    fn try_from(value: u8) -> Result<Self, FontError> {
        match value {
            0 => Ok(Self::All),
            1 => Ok(Self::Directional),
            2 => Ok(Self::Preprocessing),
            3 => Ok(Self::Repha),
            4 => Ok(Self::Prebase),
            5 => Ok(Self::Basic),
            6 => Ok(Self::Topographical),
            7 => Ok(Self::Presentation),
            8 => Ok(Self::IndicPreprocessing),
            9 => Ok(Self::IndicNukta),
            10 => Ok(Self::IndicAkhand),
            11 => Ok(Self::IndicRakar),
            12 => Ok(Self::IndicBelow),
            13 => Ok(Self::IndicAbove),
            14 => Ok(Self::IndicHalf),
            15 => Ok(Self::IndicPost),
            16 => Ok(Self::IndicVattu),
            17 => Ok(Self::IndicConjunct),
            18 => Ok(Self::IndicPresentation),
            19 => Ok(Self::KhmerBasic),
            20 => Ok(Self::KhmerPresentation),
            21 => Ok(Self::MyanmarPreprocessing),
            22 => Ok(Self::MyanmarRepha),
            23 => Ok(Self::MyanmarPrebase),
            24 => Ok(Self::MyanmarBelow),
            25 => Ok(Self::MyanmarPostbase),
            26 => Ok(Self::MyanmarPresentation),
            27 => Ok(Self::ArabicStretch),
            28 => Ok(Self::ArabicPreprocessing),
            29 => Ok(Self::ArabicIsolated),
            30 => Ok(Self::ArabicFinal),
            31 => Ok(Self::ArabicFinal2),
            32 => Ok(Self::ArabicFinal3),
            33 => Ok(Self::ArabicMedial),
            34 => Ok(Self::ArabicMedial2),
            35 => Ok(Self::ArabicInitial),
            36 => Ok(Self::ArabicRequiredLigatures),
            37 => Ok(Self::ArabicContextual),
            38 => Ok(Self::ArabicPostRequired),
            39 => Ok(Self::ArabicPostContextual),
            40 => Ok(Self::ArabicLigatures),
            41 => Ok(Self::ArabicPresentation),
            _ => Err(FontError::InvalidArgument),
        }
    }
}
impl ManagedSubstitutionStage {
    /// Original exact feature membership for this stage.
    pub fn enables(self, tag: [u16; 4]) -> bool {
        match self {
            Self::All => true,
            Self::Directional => [*b"ltra", *b"ltrm", *b"rtla", *b"rtlm"]
                .iter()
                .any(|t| tag == t.map(u16::from)),
            Self::Preprocessing => [
                *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"locl", *b"ccmp", *b"nukt", *b"akhn",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::Repha => [*b"rphf"].iter().any(|t| tag == t.map(u16::from)),
            Self::Prebase => [*b"pref"].iter().any(|t| tag == t.map(u16::from)),
            Self::Basic => [
                *b"rkrf", *b"abvf", *b"blwf", *b"half", *b"pstf", *b"vatu", *b"cjct",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::Topographical => [*b"isol", *b"init", *b"medi", *b"fina"]
                .iter()
                .any(|t| tag == t.map(u16::from)),
            Self::Presentation => ![
                *b"ltra", *b"ltrm", *b"rtla", *b"rtlm", *b"rvrn", *b"frac", *b"numr", *b"dnom",
                *b"locl", *b"ccmp", *b"nukt", *b"akhn", *b"rphf", *b"pref", *b"rkrf", *b"abvf",
                *b"blwf", *b"half", *b"pstf", *b"vatu", *b"cjct", *b"isol", *b"init", *b"medi",
                *b"fina",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::IndicPreprocessing => {
                [*b"rvrn", *b"frac", *b"numr", *b"dnom", *b"locl", *b"ccmp"]
                    .iter()
                    .any(|t| tag == t.map(u16::from))
            }
            Self::IndicNukta => [*b"nukt"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicAkhand => [*b"akhn"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicRakar => [*b"rkrf"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicBelow => [*b"blwf"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicAbove => [*b"abvf"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicHalf => [*b"half"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicPost => [*b"pstf"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicVattu => [*b"vatu"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicConjunct => [*b"cjct"].iter().any(|t| tag == t.map(u16::from)),
            Self::IndicPresentation => ![
                *b"ltra", *b"ltrm", *b"rtla", *b"rtlm", *b"rvrn", *b"frac", *b"numr", *b"dnom",
                *b"locl", *b"ccmp", *b"nukt", *b"akhn", *b"rphf", *b"rkrf", *b"pref", *b"blwf",
                *b"abvf", *b"half", *b"pstf", *b"vatu", *b"cjct",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::KhmerBasic => [
                *b"rvrn", *b"frac", *b"numr", *b"dnom", *b"locl", *b"ccmp", *b"pref", *b"blwf",
                *b"abvf", *b"pstf", *b"cfar",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::KhmerPresentation => ![
                *b"ltra", *b"ltrm", *b"rtla", *b"rtlm", *b"rvrn", *b"frac", *b"numr", *b"dnom",
                *b"locl", *b"ccmp", *b"pref", *b"blwf", *b"abvf", *b"pstf", *b"cfar",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::MyanmarPreprocessing => {
                [*b"rvrn", *b"frac", *b"numr", *b"dnom", *b"locl", *b"ccmp"]
                    .iter()
                    .any(|t| tag == t.map(u16::from))
            }
            Self::MyanmarRepha => [*b"rphf"].iter().any(|t| tag == t.map(u16::from)),
            Self::MyanmarPrebase => [*b"pref"].iter().any(|t| tag == t.map(u16::from)),
            Self::MyanmarBelow => [*b"blwf"].iter().any(|t| tag == t.map(u16::from)),
            Self::MyanmarPostbase => [*b"pstf"].iter().any(|t| tag == t.map(u16::from)),
            Self::MyanmarPresentation => ![
                *b"ltra", *b"ltrm", *b"rtla", *b"rtlm", *b"rvrn", *b"frac", *b"numr", *b"dnom",
                *b"locl", *b"ccmp", *b"rphf", *b"pref", *b"blwf", *b"pstf",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::ArabicStretch => [*b"stch"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicPreprocessing => {
                [*b"rvrn", *b"frac", *b"numr", *b"dnom", *b"ccmp", *b"locl"]
                    .iter()
                    .any(|t| tag == t.map(u16::from))
            }
            Self::ArabicIsolated => [*b"isol"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicFinal => [*b"fina"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicFinal2 => [*b"fin2"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicFinal3 => [*b"fin3"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicMedial => [*b"medi"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicMedial2 => [*b"med2"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicInitial => [*b"init"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicRequiredLigatures => [*b"rlig"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicContextual => [*b"calt", *b"rclt"].iter().any(|t| tag == t.map(u16::from)),
            Self::ArabicPostRequired => ![
                *b"ltra", *b"ltrm", *b"rtla", *b"rtlm", *b"rvrn", *b"frac", *b"numr", *b"dnom",
                *b"stch", *b"ccmp", *b"locl", *b"isol", *b"fina", *b"fin2", *b"fin3", *b"medi",
                *b"med2", *b"init", *b"rlig",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::ArabicPostContextual => ![
                *b"ltra", *b"ltrm", *b"rtla", *b"rtlm", *b"rvrn", *b"frac", *b"numr", *b"dnom",
                *b"stch", *b"ccmp", *b"locl", *b"isol", *b"fina", *b"fin2", *b"fin3", *b"medi",
                *b"med2", *b"init", *b"rlig", *b"calt", *b"rclt",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
            Self::ArabicLigatures => [*b"liga", *b"clig", *b"mset"]
                .iter()
                .any(|t| tag == t.map(u16::from)),
            Self::ArabicPresentation => ![
                *b"ltra", *b"ltrm", *b"rtla", *b"rtlm", *b"rvrn", *b"frac", *b"numr", *b"dnom",
                *b"stch", *b"ccmp", *b"locl", *b"isol", *b"fina", *b"fin2", *b"fin3", *b"medi",
                *b"med2", *b"init", *b"rlig", *b"calt", *b"rclt", *b"liga", *b"clig", *b"mset",
            ]
            .iter()
            .any(|t| tag == t.map(u16::from)),
        }
    }
    /// Original manual-joiner admission for this stage.
    pub fn manual_joiners(self) -> bool {
        matches!(
            self,
            Self::IndicNukta
                | Self::IndicAkhand
                | Self::Repha
                | Self::IndicRakar
                | Self::Prebase
                | Self::IndicBelow
                | Self::IndicAbove
                | Self::IndicHalf
                | Self::IndicPost
                | Self::IndicVattu
                | Self::IndicConjunct
                | Self::IndicPresentation
                | Self::KhmerBasic
                | Self::KhmerPresentation
                | Self::MyanmarRepha
                | Self::MyanmarPrebase
                | Self::MyanmarBelow
                | Self::MyanmarPostbase
                | Self::MyanmarPresentation
        )
    }
    /// Original per-syllable admission, independent of feature membership.
    pub fn restricts_syllable(self, tag: [u16; 4]) -> bool {
        !matches!(
            self,
            Self::All | Self::KhmerPresentation | Self::MyanmarPresentation
        ) && ([
            *b"locl", *b"ccmp", *b"nukt", *b"akhn", *b"rphf", *b"pref", *b"rkrf", *b"abvf",
            *b"blwf", *b"half", *b"pstf", *b"vatu", *b"cjct",
        ]
        .iter()
        .any(|t| tag == t.map(u16::from))
            || self == Self::IndicPresentation
                && [*b"init", *b"pres", *b"abvs", *b"blws", *b"psts", *b"haln"]
                    .iter()
                    .any(|t| tag == t.map(u16::from)))
    }
}
