//! Native Rust port of ProGPU's owned geometry and WebGPU rendering contracts.
//!
//! See the crate README for the parity ledger. Unported families are not silently
//! approximated. GPU programs are embedded from the canonical production WGSL.

pub mod geometry;
pub mod shaders;

/// Errors detected before publishing geometry or GPU state.
#[derive(Debug, thiserror::Error)]
pub enum Error {
    /// A coordinate, extent, transform, or style is outside the supported domain.
    #[error("invalid geometry or render frame")]
    InvalidInput,
    /// The request exceeds a buffer, index, or device capacity.
    #[error("capacity exceeded")]
    Capacity,
}
