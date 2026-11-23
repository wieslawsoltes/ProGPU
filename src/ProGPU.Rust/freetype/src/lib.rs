//! Exclusive safe ownership over ProGPU's original pinned FreeType dependency.
//! No rasterizer, shaping engine, auto-hinter or bitmap-rendering API is exposed.
#![deny(unsafe_code)]
#![deny(unsafe_op_in_unsafe_fn)]
#[cfg(not(all(
    target_pointer_width = "64",
    any(target_os = "windows", target_os = "linux", target_os = "macos"),
    any(target_arch = "x86_64", target_arch = "aarch64")
)))]
compile_error!("progpu-freetype supports only the original six 64-bit native RIDs");
// The only unsafe implementation is this private, documented C ABI boundary.
#[allow(unsafe_code)]
mod ffi;
pub use ffi::{Face, Library, dependency_evidence};
mod receipt;
pub use receipt::{BUILD_RECEIPT, DependencyEvidence, DependencyReceipt};

/// Original native failure classes; C errors remain available for diagnosis.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Error {
    InvalidArgument,
    InvalidFont,
    UnsupportedFont,
    DependencyMismatch,
    ResourceExhausted,
    HintingFailed,
    /// Actual FreeType public API error, without relabelling a successful call.
    FreeType(i32),
}
impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "original FreeType producer: {self:?}")
    }
}
impl std::error::Error for Error {}
pub type Result<T> = std::result::Result<T, Error>;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum Interpreter {
    TrueType35 = 35,
    TrueType40 = 40,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum OutlineTarget {
    #[default]
    Normal,
    Monochrome,
    HorizontalLcd,
    Unhinted,
}
impl OutlineTarget {
    pub const fn load_flags(self) -> i32 {
        let common = (1 << 3) | (1 << 15) | (1 << 7);
        common
            | match self {
                Self::Normal => 0,
                Self::Monochrome => 2 << 16,
                Self::HorizontalLcd => 3 << 16,
                Self::Unhinted => 1 << 1,
            }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FaceInformation {
    pub true_type_format: bool,
    pub scalable: bool,
    pub color: bool,
    pub glyph_count: i64,
    pub multiple_masters: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VariationAxis {
    pub minimum: i64,
    pub default: i64,
    pub maximum: i64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DeviceMetrics {
    pub units_per_em: u16,
    pub pixels_per_em: [u32; 2],
    pub scale_16_16: [i64; 2],
    pub driver_line_metrics_26_6: [i64; 4],
    pub design_line_metrics: [i32; 4],
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SlotMetrics {
    pub advance_26_6: [i64; 2],
    pub horizontal_bearing_26_6: [i64; 2],
    pub size_26_6: [i64; 2],
    pub horizontal_advance_26_6: i64,
    pub vertical_bearing_26_6: [i64; 2],
    pub vertical_advance_26_6: i64,
    pub linear_advances_16_16: [i64; 2],
    pub side_bearing_deltas_26_6: [i64; 2],
}
/// An immediate owned copy of one actual checked FreeType glyph slot. Its raw
/// phase, flags, tags and no-ink contour slots are never normalized or rewritten.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct CapturedGlyph {
    pub glyph_id: u32,
    pub metrics: SlotMetrics,
    pub outline_flags: i32,
    pub points_26_6: Vec<[i64; 2]>,
    pub tags: Vec<u8>,
    pub contour_ends: Vec<i16>,
}
