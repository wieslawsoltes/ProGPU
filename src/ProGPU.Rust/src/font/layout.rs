//! Original OpenType coverage, class, GSUB/GPOS lookup and GDEF data boundaries.
//! These views prepare immutable shaping data with explicit native/managed feature
//! policies. Native GSUB and GPOS execution mutate caller-owned shaping records;
//! full shaping orchestration and source raster policy remain separate. Unknown lookup flags/types retain
//! their raw values, and compatibility blocklists remain explicit caller policy.
use super::{FontError, SfntFace, Span, Tag, bytes};
use std::sync::Arc;
mod coverage;
mod digest;
mod gdef;
mod gsub;
mod managed_context;
pub(crate) use managed_context::default_ignorable;
mod managed_gpos;
mod native_gpos;
pub use native_gpos::{
    GposArithmetic, GposAttachment, GposAttachmentKind, GposDeviceFrame, GposDeviceSource,
    GposDirection, GposOptions, GposState, NativePositioningPlan, resolve_gpos_attachments,
};
mod managed_gsub;
mod managed_selection;
mod selection;
mod table;
pub use coverage::{ClassDefinition, Coverage};
pub use digest::{
    ContextCoverage, ContextRequirements, ContextSubtable, GlyphSetDigest, ManagedGlyphSetDigest,
    PreparedContext,
};
pub use gdef::{Gdef, GlyphClass, OwnedGdef, is_gdef_blocklisted};
pub use gsub::{GsubOptions, GsubState};
pub use managed_context::{
    ManagedContextGlyph, ManagedContextInput, ManagedContextRecord, ManagedContextState,
    ManagedContextView, ManagedCoverageMatcher, PreparedManagedContext,
};
pub use managed_gpos::{
    ManagedGposLimits, ManagedGposTable, ManagedPositionBuffer, ManagedPositionDirection,
    ManagedPositioningPlan,
};
pub use managed_gsub::{
    ManagedGlyphBuffer, ManagedGlyphState, ManagedGsubLimits, ManagedGsubTable,
    ManagedSubstitutionPlan,
};
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

mod managed_features;
pub use managed_features::{ManagedFeatureOptions, ManagedFeatureRange};

mod managed_stage;
pub use managed_stage::ManagedSubstitutionStage;

mod managed_variations;
pub use managed_variations::ManagedLayoutVariations;

pub(crate) use managed_variations::ManagedVariationStore;
