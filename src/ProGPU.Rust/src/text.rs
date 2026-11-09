//! Original ProGPU text contracts. Simple runs retain UTF-16 source positions;
//! they do not substitute for OpenType shaping, bidi or paragraph layout.

mod managed_shaping;
mod shaping;
pub use managed_shaping::ManagedShapingGlyph;
pub mod preparation;
mod simple;
pub use shaping::ShapingGlyph;
pub mod unicode;
pub use simple::{
    SimpleAdvanceScale, SimpleGlyphMetrics, SimpleGlyphRun, SimpleGlyphRunRequirements,
    build_simple_glyph_run_into, fill_simple_glyph_advances, is_simple_formatting_control,
    read_simple_code_point, simple_glyph_run_requirements,
};
