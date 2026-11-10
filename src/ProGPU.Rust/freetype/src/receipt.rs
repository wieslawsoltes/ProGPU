//! Build-verified original source receipt plus independent live image observation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DependencyReceipt {
    pub rid: &'static str,
    pub header_version: [u32; 3],
    pub manifest_sha256: [u8; 32],
    pub archive_sha256: [u8; 32],
    pub headers_sha256: [u8; 32],
    pub notices_sha256: [u8; 32],
}
include!(concat!(env!("OUT_DIR"), "/receipt.rs"));
/// Actual platform module lookup for the original ordered seventeen functions.
/// Image addresses are opaque identity tokens and are never dereferenced by API users.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DependencyEvidence {
    pub receipt: DependencyReceipt,
    pub producer_image: usize,
    pub function_images: [usize; 17],
}
