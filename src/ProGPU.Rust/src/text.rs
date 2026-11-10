//! Original ProGPU text contracts and independent native/managed shaping routes.
//! Simple runs retain UTF-16 source positions; their admission remains separate
//! from OpenType shaping, bidi and paragraph layout.

mod font_face;
pub mod canvas_font;
pub mod text_blob;
pub mod font_policy;
pub mod glyph;
pub mod hinted_replay;
pub mod hinted_source;
pub mod hinting;
pub mod layout;
pub mod managed_pipeline;
mod managed_shaping;
pub mod native_layout;
pub mod native_paragraph;
pub mod native_pipeline;
pub mod paint;
pub use font_face::{ManagedShapingFontFace, ShapingFontFace, ShapingGlyphExtents};
mod shaping;
pub use managed_shaping::ManagedShapingGlyph;
pub mod preparation;
pub mod script_actions;
mod simple;
pub use shaping::ShapingGlyph;
pub mod unicode;
pub use simple::{
    SimpleAdvanceScale, SimpleGlyphMetrics, SimpleGlyphRun, SimpleGlyphRunRequirements,
    build_simple_glyph_run_into, fill_simple_glyph_advances, is_simple_formatting_control,
    read_simple_code_point, simple_glyph_run_requirements,
};

/// Actual native design-unit outline ownership and explicit vector Scene replay.
pub mod native_design_geometry;

/// Ordinary native atlas placement and explicit original color-bitmap transport.
pub mod native_atlas;

/// Immutable original selected typeface and raw style factories.
pub mod typeface;
/// Explicit owned/native/browser font catalog and fallback matching.
pub mod font_manager;

/// Original native nested block/table, anchor and pagination flow.
pub mod native_document;
