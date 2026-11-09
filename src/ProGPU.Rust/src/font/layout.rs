//! Original OpenType coverage, class, GSUB/GPOS lookup and GDEF data boundaries.
//! These views prepare immutable shaping data with explicit native/managed feature
//! policies; they do not apply substitutions, positioning or rasterization. Unknown lookup flags/types retain
//! their raw values, and compatibility blocklists remain explicit caller policy.
use super::{FontError, SfntFace, Span, Tag, bytes};
use std::sync::Arc;
mod coverage;
mod digest;
mod gdef;
mod managed_selection;
mod selection;
mod table;
pub use coverage::{ClassDefinition, Coverage};
pub use digest::{
    ContextCoverage, ContextRequirements, ContextSubtable, GlyphSetDigest, ManagedGlyphSetDigest,
    PreparedContext,
};
pub use gdef::{Gdef, GlyphClass, OwnedGdef, is_gdef_blocklisted};
pub use managed_selection::{
    ManagedEnabledLookup, ManagedFeatureSetting, ManagedLookupSelection, ManagedSelectionRequest,
};
pub use selection::{LookupSelection, SelectionRequest};
pub use table::{LayoutTable, Lookup, OwnedLayoutTable};

fn fits(data: &[u8], offset: usize, length: usize) -> bool {
    bytes::bytes(data, offset, length).is_some()
}
fn u16_at(data: &[u8], offset: usize) -> u16 {
    bytes::u16(data, offset).unwrap()
}
fn u32_at(data: &[u8], offset: usize) -> u32 {
    bytes::u32(data, offset).unwrap()
}

#[derive(Clone, Debug)]
enum Storage {
    Bytes(Arc<[u8]>),
    Face {
        face: SfntFace,
        span: Span,
        tag: Tag,
    },
}
impl Storage {
    fn from_face(face: SfntFace, tag: Tag) -> Option<Self> {
        let (_, span) = face.table_span(tag)?;
        Some(Self::Face { face, span, tag })
    }
    fn bytes(&self) -> &[u8] {
        match self {
            Self::Bytes(b) => b,
            Self::Face { face, span, .. } => span.get(&face.data),
        }
    }
    fn source(&self) -> Option<(&SfntFace, Tag)> {
        match self {
            Self::Bytes(_) => None,
            Self::Face { face, tag, .. } => Some((face, *tag)),
        }
    }
}
