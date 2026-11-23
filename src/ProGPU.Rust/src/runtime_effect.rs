//! Original ProGPU runtime-effect declarations and immutable binding ownership.
//!
//! The managed producer reflects uniforms and children; it does not compile or
//! execute SkSL function bodies. These APIs preserve that boundary. Shader and
//! blender execution remain unavailable; color filters retain the producer's
//! independent image-pass and single-color policies.
mod bindings;
mod ownership;
mod parser;
mod program;

pub use bindings::{RuntimeEffectChildren, RuntimeEffectUniforms, RuntimeUniformValue};
pub use program::{
    RuntimeBlenderBuilder, RuntimeChildDescriptor, RuntimeChildKind, RuntimeColorFilterBuilder,
    RuntimeEffect, RuntimeEffectBuilder, RuntimeEffectChild, RuntimeEffectInstance,
    RuntimeEffectKind, RuntimeEffectProgram, RuntimeLocalMatrix, RuntimeShaderBuilder,
    RuntimeUniformDescriptor, RuntimeUniformType,
};

/// Errors detected before publishing a program, binding mutation or instance.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
pub enum RuntimeEffectError {
    /// Empty or whitespace-only original source.
    #[error("The SkSL source is empty.")]
    EmptySource,
    /// The producer's lexical main-function admission did not match.
    #[error("The SkSL source does not declare a main function.")]
    MissingMain,
    /// Original shader/color-filter/blender children cannot be arrays.
    #[error("Child arrays are not supported for '{0}'.")]
    ChildArray(String),
    /// A recognized uniform declaration has no supported original byte layout.
    #[error("Unsupported runtime-effect uniform type '{0}'.")]
    UnsupportedUniformType(String),
    /// A packed offset, allocation or original signed byte count overflowed.
    #[error("Runtime-effect capacity exceeded.")]
    Capacity,
    /// Original first-match name lookup failed.
    #[error("Unknown runtime-effect uniform '{0}'.")]
    UnknownUniform(String),
    /// The complete value size differs; the uniform storage remains unchanged.
    #[error(
        "Uniform '{name}' requires {required} bytes, but the supplied value contains {actual} bytes."
    )]
    UniformSize {
        /// Original uniform name.
        name: String,
        /// Complete declared byte count.
        required: usize,
        /// Complete supplied byte count.
        actual: usize,
    },
    /// Original first-match child name lookup failed.
    #[error("Unknown runtime-effect child '{0}'.")]
    UnknownChild(String),
    /// The actual immutable effect owner differs, even if source text matches.
    #[error("The uniforms belong to a different runtime effect.")]
    UniformOwner,
    /// The child builder belongs to another actual immutable effect owner.
    #[error("The children belong to a different runtime effect.")]
    ChildOwner,
    /// The selected instance factory differs from the program's original kind.
    #[error("The runtime effect was compiled for {actual:?}, not {expected:?}.")]
    Kind {
        /// Actual declaration kind.
        actual: RuntimeEffectKind,
        /// Requested instance kind.
        expected: RuntimeEffectKind,
    },
    /// The caller output is too short; no bytes were written.
    #[error("The destination is smaller than the runtime-effect uniform.")]
    DestinationTooSmall,
}
