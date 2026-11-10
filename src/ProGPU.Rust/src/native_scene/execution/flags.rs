//! Original complete native engine-flag admission, independent of scene ink.
use super::*;

/// Admit the original six flags before resource work, including on an empty
/// scene. The legacy compute-fallback name is the same bit as intrinsic CPU
/// fallback; it is never interpreted as an additional execution route.
pub fn validate_engine_flags(flags: u64) -> Result<(), Error> {
    let glyph = u64::from(
        ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK
            | ENGINE_GLYPH_RASTER_SHADER_FALLBACK
            | ENGINE_GLYPH_SCALAR_CPU_FALLBACK,
    );
    let sampling =
        u64::from(ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING | ENGINE_IMAGE_REQUIRE_NATIVE_SAMPLING);
    let supported = glyph | sampling | u64::from(ENGINE_ORDERED_HIT_QUERIES);
    if flags & !supported != 0
        || (flags & glyph).count_ones() > 1
        || (flags & sampling).count_ones() > 1
    {
        return Err(Error::InvalidInput);
    }
    Ok(())
}
