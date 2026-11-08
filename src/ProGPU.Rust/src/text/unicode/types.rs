//! Original numeric Unicode categories and bidi property values.

/// Original ProGPU unicode general category numeric values.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum GeneralCategory {
    /// Uppercase letter.
    UppercaseLetter = 0,
    /// Lowercase letter.
    LowercaseLetter = 1,
    /// Titlecase letter.
    TitlecaseLetter = 2,
    /// Modifier letter.
    ModifierLetter = 3,
    /// Other letter.
    OtherLetter = 4,
    /// Nonspacing mark.
    NonspacingMark = 5,
    /// Spacing combining mark.
    SpacingCombiningMark = 6,
    /// Enclosing mark.
    EnclosingMark = 7,
    /// Decimal digit number.
    DecimalDigitNumber = 8,
    /// Letter number.
    LetterNumber = 9,
    /// Other number.
    OtherNumber = 10,
    /// Space separator.
    SpaceSeparator = 11,
    /// Line separator.
    LineSeparator = 12,
    /// Paragraph separator.
    ParagraphSeparator = 13,
    /// Control.
    Control = 14,
    /// Format.
    Format = 15,
    /// Surrogate.
    Surrogate = 16,
    /// Private use.
    PrivateUse = 17,
    /// Connector punctuation.
    ConnectorPunctuation = 18,
    /// Dash punctuation.
    DashPunctuation = 19,
    /// Open punctuation.
    OpenPunctuation = 20,
    /// Close punctuation.
    ClosePunctuation = 21,
    /// Initial quote punctuation.
    InitialQuotePunctuation = 22,
    /// Final quote punctuation.
    FinalQuotePunctuation = 23,
    /// Other punctuation.
    OtherPunctuation = 24,
    /// Math symbol.
    MathSymbol = 25,
    /// Currency symbol.
    CurrencySymbol = 26,
    /// Modifier symbol.
    ModifierSymbol = 27,
    /// Other symbol.
    OtherSymbol = 28,
    /// Other not assigned.
    #[default]
    OtherNotAssigned = 29,
}
impl GeneralCategory {
    pub(super) fn from_data(value: u32) -> Self {
        const VALUES: &[GeneralCategory] = &[
            GeneralCategory::UppercaseLetter,
            GeneralCategory::LowercaseLetter,
            GeneralCategory::TitlecaseLetter,
            GeneralCategory::ModifierLetter,
            GeneralCategory::OtherLetter,
            GeneralCategory::NonspacingMark,
            GeneralCategory::SpacingCombiningMark,
            GeneralCategory::EnclosingMark,
            GeneralCategory::DecimalDigitNumber,
            GeneralCategory::LetterNumber,
            GeneralCategory::OtherNumber,
            GeneralCategory::SpaceSeparator,
            GeneralCategory::LineSeparator,
            GeneralCategory::ParagraphSeparator,
            GeneralCategory::Control,
            GeneralCategory::Format,
            GeneralCategory::Surrogate,
            GeneralCategory::PrivateUse,
            GeneralCategory::ConnectorPunctuation,
            GeneralCategory::DashPunctuation,
            GeneralCategory::OpenPunctuation,
            GeneralCategory::ClosePunctuation,
            GeneralCategory::InitialQuotePunctuation,
            GeneralCategory::FinalQuotePunctuation,
            GeneralCategory::OtherPunctuation,
            GeneralCategory::MathSymbol,
            GeneralCategory::CurrencySymbol,
            GeneralCategory::ModifierSymbol,
            GeneralCategory::OtherSymbol,
            GeneralCategory::OtherNotAssigned,
        ];
        VALUES[value as usize]
    }
}

/// Original ProGPU unicode bidi class numeric values.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum BidiClass {
    /// Left to right.
    #[default]
    LeftToRight = 0,
    /// Right to left.
    RightToLeft = 1,
    /// Arabic letter.
    ArabicLetter = 2,
    /// European number.
    EuropeanNumber = 3,
    /// European separator.
    EuropeanSeparator = 4,
    /// European terminator.
    EuropeanTerminator = 5,
    /// Arabic number.
    ArabicNumber = 6,
    /// Common separator.
    CommonSeparator = 7,
    /// Nonspacing mark.
    NonspacingMark = 8,
    /// Boundary neutral.
    BoundaryNeutral = 9,
    /// Paragraph separator.
    ParagraphSeparator = 10,
    /// Segment separator.
    SegmentSeparator = 11,
    /// Whitespace.
    Whitespace = 12,
    /// Other neutral.
    OtherNeutral = 13,
    /// Left to right embedding.
    LeftToRightEmbedding = 14,
    /// Left to right override.
    LeftToRightOverride = 15,
    /// Right to left embedding.
    RightToLeftEmbedding = 16,
    /// Right to left override.
    RightToLeftOverride = 17,
    /// Pop directional format.
    PopDirectionalFormat = 18,
    /// Left to right isolate.
    LeftToRightIsolate = 19,
    /// Right to left isolate.
    RightToLeftIsolate = 20,
    /// First strong isolate.
    FirstStrongIsolate = 21,
    /// Pop directional isolate.
    PopDirectionalIsolate = 22,
}
impl BidiClass {
    pub(super) fn from_data(value: u32) -> Self {
        const VALUES: &[BidiClass] = &[
            BidiClass::LeftToRight,
            BidiClass::RightToLeft,
            BidiClass::ArabicLetter,
            BidiClass::EuropeanNumber,
            BidiClass::EuropeanSeparator,
            BidiClass::EuropeanTerminator,
            BidiClass::ArabicNumber,
            BidiClass::CommonSeparator,
            BidiClass::NonspacingMark,
            BidiClass::BoundaryNeutral,
            BidiClass::ParagraphSeparator,
            BidiClass::SegmentSeparator,
            BidiClass::Whitespace,
            BidiClass::OtherNeutral,
            BidiClass::LeftToRightEmbedding,
            BidiClass::LeftToRightOverride,
            BidiClass::RightToLeftEmbedding,
            BidiClass::RightToLeftOverride,
            BidiClass::PopDirectionalFormat,
            BidiClass::LeftToRightIsolate,
            BidiClass::RightToLeftIsolate,
            BidiClass::FirstStrongIsolate,
            BidiClass::PopDirectionalIsolate,
        ];
        VALUES[value as usize]
    }
}

/// Original ProGPU unicode bidi bracket kind numeric values.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum BidiBracketKind {
    /// None.
    #[default]
    None = 0,
    /// Open.
    Open = 1,
    /// Close.
    Close = 2,
}
impl BidiBracketKind {
    pub(super) fn from_data(value: u32) -> Self {
        const VALUES: &[BidiBracketKind] = &[
            BidiBracketKind::None,
            BidiBracketKind::Open,
            BidiBracketKind::Close,
        ];
        VALUES[value as usize]
    }
}
