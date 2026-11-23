use super::coverage::ClassMeta;
use super::{
    Arc, ClassDefinition, Coverage, FontError, SfntFace, Storage, Tag, fits, u16_at, u32_at,
};

/// Original GDEF classification; undefined raw class values map to Unclassified.
#[repr(u16)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum GlyphClass {
    /// No admitted GDEF class.
    #[default]
    Unclassified = 0,
    /// Base glyph.
    Base = 1,
    /// Ligature glyph.
    Ligature = 2,
    /// Mark glyph.
    Mark = 3,
    /// Component glyph.
    Component = 4,
}
#[derive(Clone, Copy, Debug)]
struct Meta {
    glyphs: Option<ClassMeta>,
    attachments: Option<ClassMeta>,
    sets_offset: usize,
    set_count: u16,
}
/// Validated original GDEF 1.0/1.2/1.3 classification and mark-set view.
/// AttachList and LigCaretList remain outside this API. ItemVariationStore only
/// receives the original header-extent check; its contents need their own parser.
#[derive(Clone, Copy, Debug)]
pub struct Gdef<'a> {
    data: &'a [u8],
    meta: Meta,
}
impl<'a> Gdef<'a> {
    /// Validate complete class definitions and every mark-set coverage before
    /// publishing. Compatibility blocklist selection is separate caller policy.
    pub fn parse(data: &'a [u8]) -> Result<Self, FontError> {
        if !fits(data, 0, 12) || u16_at(data, 0) != 1 {
            return Err(FontError::InvalidFace);
        }
        let minor = u16_at(data, 2);
        if !matches!(minor, 0 | 2 | 3)
            || (minor >= 2 && !fits(data, 0, 14))
            || (minor >= 3 && !fits(data, 0, 18))
        {
            return Err(FontError::InvalidFace);
        }
        let class = |at| {
            let offset = usize::from(u16_at(data, at));
            if offset == 0 {
                Ok(None)
            } else {
                ClassDefinition::parse(data, offset).map(|c| Some(c.meta))
            }
        };
        let glyphs = class(4)?;
        let attachments = class(10)?;
        let sets_offset = if minor >= 2 {
            usize::from(u16_at(data, 12))
        } else {
            0
        };
        let mut set_count = 0;
        if sets_offset != 0 {
            if !fits(data, sets_offset, 4) || u16_at(data, sets_offset) != 1 {
                return Err(FontError::InvalidFace);
            }
            set_count = u16_at(data, sets_offset + 2);
            if !fits(data, sets_offset + 4, usize::from(set_count) * 4) {
                return Err(FontError::InvalidFace);
            }
            for index in 0..usize::from(set_count) {
                let relative = u32_at(data, sets_offset + 4 + index * 4) as usize;
                if relative == 0 || relative > data.len() - sets_offset {
                    return Err(FontError::InvalidFace);
                }
                Coverage::parse(data, sets_offset + relative)?;
            }
        }
        if minor >= 3 {
            let store = u32_at(data, 14) as usize;
            if store != 0 && !fits(data, store, 8) {
                return Err(FontError::InvalidFace);
            }
        }
        Ok(Self {
            data,
            meta: Meta {
                glyphs,
                attachments,
                sets_offset,
                set_count,
            },
        })
    }
    /// Original immutable table bytes.
    pub fn bytes(&self) -> &'a [u8] {
        self.data
    }
    /// Whether an original class definition is present, independent of values.
    pub fn has_glyph_classes(&self) -> bool {
        self.meta.glyphs.is_some()
    }
    /// Whether an original mark attachment definition is present.
    pub fn has_mark_attachment_classes(&self) -> bool {
        self.meta.attachments.is_some()
    }
    /// Original count of mark-filtering sets.
    pub fn mark_set_count(&self) -> u16 {
        self.meta.set_count
    }
    /// Original interpreted glyph class; absent and undefined values map to zero.
    pub fn glyph_class(&self, glyph: u16) -> GlyphClass {
        let value = self.meta.glyphs.map_or(0, |meta| {
            ClassDefinition {
                data: self.data,
                meta,
            }
            .get(glyph)
        });
        match value {
            1 => GlyphClass::Base,
            2 => GlyphClass::Ligature,
            3 => GlyphClass::Mark,
            4 => GlyphClass::Component,
            _ => GlyphClass::Unclassified,
        }
    }
    /// Raw original mark attachment class, or zero when absent.
    pub fn mark_attachment_class(&self, glyph: u16) -> u16 {
        self.meta.attachments.map_or(0, |meta| {
            ClassDefinition {
                data: self.data,
                meta,
            }
            .get(glyph)
        })
    }
    /// Original membership query; an unavailable set is false. No reparsing or
    /// allocation occurs on this path after the complete construction preflight.
    pub fn is_in_mark_set(&self, set_index: u16, glyph: u16) -> bool {
        if set_index >= self.meta.set_count {
            return false;
        }
        let relative = u32_at(
            self.data,
            self.meta.sets_offset + 4 + usize::from(set_index) * 4,
        ) as usize;
        Coverage::validated(self.data, self.meta.sets_offset + relative)
            .find(glyph)
            .is_some()
    }
}
/// Owned GDEF generation, retaining standalone bytes or its selected font owner.
#[derive(Clone, Debug)]
pub struct OwnedGdef {
    storage: Storage,
    meta: Meta,
}
impl OwnedGdef {
    /// Own and validate a standalone immutable GDEF table.
    pub fn new(bytes: impl Into<Arc<[u8]>>) -> Result<Self, FontError> {
        let storage = Storage::Bytes(bytes.into());
        let meta = Gdef::parse(storage.bytes())?.meta;
        Ok(Self { storage, meta })
    }
    /// Select GDEF without copying original bytes. Missing data remains None;
    /// invalid present data rejects. This does not silently apply a blocklist.
    pub fn from_face(face: SfntFace) -> Result<Option<Self>, FontError> {
        let Some(storage) = Storage::from_face(face, Tag::new(*b"GDEF")) else {
            return Ok(None);
        };
        let meta = Gdef::parse(storage.bytes())?.meta;
        Ok(Some(Self { storage, meta }))
    }
    /// Borrow already-validated metadata in O(1), tied to this owner.
    pub fn view(&self) -> Gdef<'_> {
        Gdef {
            data: self.storage.bytes(),
            meta: self.meta,
        }
    }
    /// Original selected font generation, absent for standalone bytes.
    pub fn source(&self) -> Option<&SfntFace> {
        self.storage.source().map(|(face, _)| face)
    }
}

/// Exact 40-tuple original native/managed GDEF compatibility policy. Callers
/// decide whether to use this independently of successful table parsing.
pub fn is_gdef_blocklisted(gdef_length: usize, gsub_length: usize, gpos_length: usize) -> bool {
    matches!(
        (gdef_length, gsub_length, gpos_length),
        (442, 2874, 42038)
            | (430, 2874, 40662)
            | (442, 2874, 39116)
            | (430, 2874, 39374)
            | (490, 3046, 41638)
            | (478, 3046, 41902)
            | (898, 12554, 46470)
            | (910, 12566, 47732)
            | (928, 23298, 59332)
            | (940, 23310, 60732)
            | (964, 23836, 60072)
            | (976, 23832, 61456)
            | (994, 24474, 60336)
            | (1006, 24470, 61740)
            | (1006, 24576, 61346)
            | (1018, 24572, 62828)
            | (1006, 24576, 61352)
            | (1018, 24572, 62834)
            | (832, 7324, 47162)
            | (844, 7302, 45474)
            | (180, 13054, 7254)
            | (192, 12638, 7254)
            | (192, 12690, 7254)
            | (188, 248, 3852)
            | (188, 264, 3426)
            | (1058, 47032, 11818)
            | (1046, 47030, 12600)
            | (1058, 71796, 16770)
            | (1046, 71790, 17862)
            | (1046, 71788, 17112)
            | (1058, 71794, 17514)
            | (1330, 109904, 57938)
            | (1330, 109904, 58972)
            | (1004, 59092, 14836)
            | (588, 5078, 14418)
            | (588, 5078, 14238)
            | (894, 17162, 33960)
            | (894, 17154, 34472)
            | (816, 7868, 17052)
            | (816, 7868, 17138)
    )
}
