//! Original selected color spaces, named matrices/transfers and owned ICC metadata.
//! Conversion reuses the existing exact original transfer implementation; no
//! external ICC/Skia color engine or new gamut-conversion policy is substituted.
#![allow(clippy::excessive_precision)]
use crate::image_filter::ImageFilterColorSpace;
pub use crate::image_filter::{
    TransferFunction as ColorSpaceTransferFunction,
    TransferFunctionKind as ColorSpaceTransferFunctionKind,
};
use std::sync::{Arc, OnceLock};
mod icc;
mod primaries;
mod xyz;
use self::ColorSpaceTransferFunction as Transfer;
use self::ColorSpaceTransferFunctionKind as Kind;
pub use icc::ColorSpaceIccProfile;
pub use primaries::ColorSpacePrimaries;
pub use xyz::ColorSpaceXyz;

/// Original CICP primary identifiers. Raw unknown identifiers remain separately
/// queryable through ColorSpace::create_cicp_raw, without unsafe enum conversion.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum ColorPrimariesCicp {
    /// Original unspecified primary identifier.
    #[default]
    Unknown = 0,
    /// Original Rec.709 primary matrix.
    Rec709 = 1,
    /// Original Rec.470 System M.
    Rec470SystemM = 4,
    /// Original Rec.470 System BG.
    Rec470SystemBg = 5,
    /// Original Rec.601.
    Rec601 = 6,
    /// Original SMPTE ST 240.
    SmpteSt240 = 7,
    /// Original generic film primaries.
    GenericFilm = 8,
    /// Original Rec.2020.
    Rec2020 = 9,
    /// Original SMPTE ST 428-1.
    SmpteSt4281 = 10,
    /// Original SMPTE RP 431-2.
    SmpteRp4312 = 11,
    /// Original SMPTE EG 432-1 / Display P3.
    SmpteEg4321 = 12,
    /// Original ITU-T H.273 value 22.
    ItuTH273Value22 = 22,
}
/// Original independent CICP transfer identifiers, including named HDR markers.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum ColorTransferCicp {
    /// Original unspecified transfer identifier.
    #[default]
    Unknown = 0,
    /// Original Rec.709 selected transfer policy.
    Rec709 = 1,
    /// Original Rec.470 System M.
    Rec470SystemM = 4,
    /// Original Rec.470 System BG.
    Rec470SystemBg = 5,
    /// Original Rec.601.
    Rec601 = 6,
    /// Original SMPTE ST 240.
    SmpteSt240 = 7,
    /// Original linear transfer.
    Linear = 8,
    /// Original IEC 61966-2-4.
    Iec6196624 = 11,
    /// Original IEC 61966-2-1 / sRGB.
    Iec6196621 = 13,
    /// Original Rec.2020 10-bit selection.
    Rec202010bit = 14,
    /// Original Rec.2020 12-bit selection.
    Rec202012bit = 15,
    /// Original named PQ metadata.
    Pq = 16,
    /// Original SMPTE ST 428-1 selected transfer.
    SmpteSt4281 = 17,
    /// Original named HLG metadata.
    Hlg = 18,
}
#[derive(Debug)]
struct Generation {
    transfer: Transfer,
    xyz: ColorSpaceXyz,
    // One conversion-table owner per actual selected color-space generation.
    // Conversion itself intentionally uses the original transfer-only policy.
    conversion: ImageFilterColorSpace,
}
/// Immutable original selected transfer/XYZ owner. Canonical sRGB and linear
/// sRGB retain shared identities; arbitrary RGB spaces retain their exact matrix.
#[derive(Clone, Debug)]
pub struct ColorSpace(Arc<Generation>);
impl PartialEq for ColorSpace {
    fn eq(&self, other: &Self) -> bool {
        self.same_owner(other) || (self.transfer() == other.transfer() && self.xyz() == other.xyz())
    }
}
impl ColorSpace {
    fn selected(transfer: Transfer, xyz: ColorSpaceXyz) -> Self {
        Self(Arc::new(Generation {
            transfer,
            xyz,
            conversion: ImageFilterColorSpace::new(transfer),
        }))
    }
    /// Original shared canonical sRGB owner. Dropping a lease does not retire
    /// the named process-lifetime generation or another retained image source.
    pub fn srgb() -> Self {
        static SPACE: OnceLock<ColorSpace> = OnceLock::new();
        SPACE
            .get_or_init(|| Self::selected(Transfer::SRGB, ColorSpaceXyz::SRGB))
            .clone()
    }
    /// Original independently shared canonical linear-sRGB owner.
    pub fn srgb_linear() -> Self {
        static SPACE: OnceLock<ColorSpace> = OnceLock::new();
        SPACE
            .get_or_init(|| Self::selected(Transfer::LINEAR, ColorSpaceXyz::SRGB))
            .clone()
    }
    /// Original CreateRgb admission and canonicalization order: selected sRGB,
    /// gamma 2.2, then linear. Tolerance bounds remain strictly less-than.
    pub fn create_rgb(transfer: Transfer, xyz: ColorSpaceXyz) -> Option<Self> {
        if transfer.kind() == Kind::Invalid || !xyz.is_finite() {
            return None;
        }
        let canonical = if transfer_close(transfer, Transfer::SRGB) {
            if matrix_close(xyz, ColorSpaceXyz::SRGB) {
                return Some(Self::srgb());
            }
            Transfer::SRGB
        } else if transfer_two_dot_two(transfer) {
            Transfer::TWO_DOT_TWO
        } else if transfer_linear(transfer) {
            if matrix_close(xyz, ColorSpaceXyz::SRGB) {
                return Some(Self::srgb_linear());
            }
            Transfer::LINEAR
        } else {
            transfer
        };
        Some(Self::selected(canonical, xyz))
    }
    /// Select the original complete supported common-RGB ICC metadata, then
    /// apply the same original RGB canonicalization. Unsupported stays absent.
    pub fn create_icc(profile: &ColorSpaceIccProfile) -> Option<Self> {
        let (transfer, xyz) = profile.color_space_parameters()?;
        Self::create_rgb(transfer, xyz)
    }
    /// Parse and select an owned ICC source from the complete original bytes.
    pub fn parse_icc(bytes: &[u8]) -> Option<Self> {
        Self::create_icc(&ColorSpaceIccProfile::parse(bytes)?)
    }
    /// Original typed named-primary/transfer factory.
    pub fn create_cicp(primaries: ColorPrimariesCicp, transfer: ColorTransferCicp) -> Option<Self> {
        Self::create_cicp_raw(primaries as u32, transfer as u32)
    }
    /// Original raw-enum selection. Unrecognized primaries reject. The source
    /// transfer switch retains EMPTY for unrecognized nonzero values, whose
    /// original numerical classifier still admits it; do not invent a new gate.
    pub fn create_cicp_raw(primaries: u32, transfer: u32) -> Option<Self> {
        Self::create_rgb(cicp_transfer(transfer)?, cicp_primaries(primaries)?)
    }
    /// Actual immutable selected seven-parameter transfer, including named HDR.
    pub fn transfer(&self) -> Transfer {
        self.0.transfer
    }
    /// Actual original RGB-to-XYZ D50 matrix, never silently recomputed.
    pub fn xyz(&self) -> ColorSpaceXyz {
        self.0.xyz
    }
    /// Exact selected sRGB transfer classification, independent of primaries.
    pub fn gamma_is_close_to_srgb(&self) -> bool {
        self.transfer() == Transfer::SRGB
    }
    /// Exact selected linear transfer classification.
    pub fn is_linear(&self) -> bool {
        self.transfer() == Transfer::LINEAR
    }
    /// Original numerical transfer capability; named HDR remains separate.
    pub fn is_numerical_transfer_function(&self) -> bool {
        self.transfer().kind() == Kind::Numerical
    }
    /// Original optional numerical query. The complete named descriptor remains
    /// available through transfer() even when this capability is absent.
    pub fn numerical_transfer(&self) -> Option<Transfer> {
        self.is_numerical_transfer_function()
            .then_some(self.transfer())
    }
    /// Original strict canonical sRGB owner identity, not merely matching gamma.
    pub fn is_srgb(&self) -> bool {
        self.same_owner(&Self::srgb())
    }
    /// Exact independently retained owner identity.
    pub fn same_owner(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    /// Preserve the owner if already linear; otherwise retain its original XYZ
    /// matrix through the original shared/general RGB factory.
    pub fn to_linear_gamma(&self) -> Self {
        if self.is_linear() {
            self.clone()
        } else {
            Self::create_rgb(Transfer::LINEAR, self.xyz())
                .expect("retained color-space matrix is finite")
        }
    }
    /// Preserve the owner if already sRGB gamma; otherwise retain its original
    /// XYZ matrix through the same original factory.
    pub fn to_srgb_gamma(&self) -> Self {
        if self.gamma_is_close_to_srgb() {
            self.clone()
        } else {
            Self::create_rgb(Transfer::SRGB, self.xyz())
                .expect("retained color-space matrix is finite")
        }
    }
    /// Original metadata-only profile. This does not fabricate serialized bytes.
    pub fn to_profile(&self) -> ColorSpaceIccProfile {
        ColorSpaceIccProfile::from_color_space(self.transfer(), self.xyz())
    }
    /// Retain the exact selected Image/Picture source conversion generation.
    /// Original SKCanvas conversion is transfer-only and bypasses exact sRGB
    /// gamma independently of this space's canonical identity/XYZ primaries.
    pub fn to_filter_color_space(&self) -> ImageFilterColorSpace {
        self.0.conversion.clone()
    }
    /// Original once-per-owner 256-entry conversion table. This delegates to the
    /// selected original transfer evaluator and original MathF.Pow sRGB encoding.
    pub fn to_srgb_table(&self) -> &[u8; 256] {
        self.0.conversion.to_srgb_table()
    }
}
pub(super) fn transfer_close(a: Transfer, b: Transfer) -> bool {
    a.0.iter().zip(b.0).all(|(a, b)| (*a - b).abs() < 0.001)
}
fn matrix_close(a: ColorSpaceXyz, b: ColorSpaceXyz) -> bool {
    a.0.iter().zip(b.0).all(|(a, b)| (*a - b).abs() < 0.01)
}
fn transfer_linear(value: Transfer) -> bool {
    let [g, a, b, c, d, e, f] = value.0;
    ((a - 1.0).abs() < 0.001
        && b.abs() < 0.001
        && e.abs() < 0.001
        && (g - 1.0).abs() < 0.001
        && d <= 0.0)
        || ((c - 1.0).abs() < 0.001 && f.abs() < 0.001 && d >= 1.0)
}
fn transfer_two_dot_two(value: Transfer) -> bool {
    let [g, a, b, _, d, e, _] = value.0;
    (a - 1.0).abs() < 0.001
        && b.abs() < 0.001
        && e.abs() < 0.001
        && (g - 2.2).abs() < 0.001
        && d <= 0.0
}
fn cicp_transfer(value: u32) -> Option<Transfer> {
    let transfer = match value {
        1 | 6 | 11 | 14 | 15 => Transfer([2.4, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
        4 => Transfer::TWO_DOT_TWO,
        5 => Transfer([2.8, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
        7 => Transfer([
            2.222222222222,
            0.899626676224,
            0.100373323776,
            0.25,
            0.091286342118,
            0.0,
            0.0,
        ]),
        8 => Transfer::LINEAR,
        13 => Transfer::SRGB,
        16 => Transfer::PQ,
        17 => Transfer([2.6, 1.034080527699, 0.0, 0.0, 0.0, 0.0, 0.0]),
        18 => Transfer::HLG,
        _ => Transfer::EMPTY,
    };
    (value != 0 && transfer.kind() != Kind::Invalid).then_some(transfer)
}
fn cicp_primaries(value: u32) -> Option<ColorSpaceXyz> {
    Some(match value {
        1 => ColorSpaceXyz::SRGB,
        4 => ColorSpaceXyz([
            0.6344442,
            0.1851249,
            0.14465098,
            0.31099266,
            0.5914824,
            0.0975251,
            -0.0011835243,
            0.055515826,
            0.77087766,
        ]),
        5 => ColorSpaceXyz([
            0.45523217,
            0.36758426,
            0.1414035,
            0.23227724,
            0.7078136,
            0.05990914,
            0.014539732,
            0.1049015,
            0.7057687,
        ]),
        6 | 7 => ColorSpaceXyz([
            0.4162788,
            0.39318287,
            0.15475832,
            0.22167054,
            0.70327383,
            0.07505579,
            0.013651822,
            0.09134888,
            0.72020936,
        ]),
        8 => ColorSpaceXyz([
            0.56563556,
            0.25386554,
            0.14471897,
            0.26423603,
            0.68506896,
            0.05069512,
            -0.0013230182,
            0.054992165,
            0.77154076,
        ]),
        9 => ColorSpaceXyz::REC2020,
        10 => ColorSpaceXyz([
            0.9977545,
            -0.0041633165,
            -0.029371241,
            -0.009767767,
            1.018317,
            -0.008549048,
            -0.007416941,
            0.013441581,
            0.8191854,
        ]),
        11 => ColorSpaceXyz([
            0.48614508,
            0.32383734,
            0.1542375,
            0.22667699,
            0.7103254,
            0.06299767,
            -0.0008005162,
            0.043238446,
            0.78277206,
        ]),
        12 => ColorSpaceXyz::DISPLAY_P3,
        22 => ColorSpaceXyz([
            0.45425415,
            0.35330448,
            0.1566613,
            0.24189259,
            0.6736451,
            0.084462374,
            0.014897218,
            0.09064627,
            0.71966654,
        ]),
        _ => return None,
    })
}
