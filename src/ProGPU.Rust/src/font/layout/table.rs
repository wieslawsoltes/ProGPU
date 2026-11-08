use super::{Arc, FontError, SfntFace, Storage, Tag, fits, u16_at, u32_at};

#[derive(Clone, Copy, Debug)]
struct Meta {
    scripts: usize,
    features: usize,
    lookups: usize,
    variations: usize,
    lookup_count: u16,
}
/// Original common GSUB/GPOS header, borrowing an immutable table generation.
/// Top-level arrays validate eagerly; complete individual lookups validate when
/// requested. Script/feature contents and actual lookup execution remain separate.
#[derive(Clone, Copy, Debug)]
pub struct LayoutTable<'a> {
    data: &'a [u8],
    meta: Meta,
}
fn array_fits(data: &[u8], offset: usize, stride: usize) -> bool {
    fits(data, offset, 2) && fits(data, offset + 2, usize::from(u16_at(data, offset)) * stride)
}
impl<'a> LayoutTable<'a> {
    /// Validate original versions 1.0/1.1, top-level arrays and variation header extent.
    pub fn parse(data: &'a [u8]) -> Result<Self, FontError> {
        if !fits(data, 0, 10) || u16_at(data, 0) != 1 {
            return Err(FontError::InvalidFace);
        }
        let minor = u16_at(data, 2);
        if minor > 1 || (minor == 1 && !fits(data, 0, 14)) {
            return Err(FontError::InvalidFace);
        }
        let scripts = usize::from(u16_at(data, 4));
        let features = usize::from(u16_at(data, 6));
        let lookups = usize::from(u16_at(data, 8));
        let variations = if minor == 1 {
            u32_at(data, 10) as usize
        } else {
            0
        };
        if scripts == 0
            || features == 0
            || lookups == 0
            || !array_fits(data, scripts, 6)
            || !array_fits(data, features, 6)
            || !array_fits(data, lookups, 2)
            || (variations != 0 && !fits(data, variations, 8))
        {
            return Err(FontError::InvalidFace);
        }
        Ok(Self {
            data,
            meta: Meta {
                scripts,
                features,
                lookups,
                variations,
                lookup_count: u16_at(data, lookups),
            },
        })
    }
    /// Exact borrowed table bytes, without reconstruction.
    pub fn bytes(&self) -> &'a [u8] {
        self.data
    }
    /// Original ScriptList offset; its deeper records have their own validation.
    pub fn script_list_offset(&self) -> usize {
        self.meta.scripts
    }
    /// Original FeatureList offset, independent of feature selection.
    pub fn feature_list_offset(&self) -> usize {
        self.meta.features
    }
    /// Original optional FeatureVariations offset; only its header extent is qualified here.
    pub fn feature_variations_offset(&self) -> Option<usize> {
        (self.meta.variations != 0).then_some(self.meta.variations)
    }
    /// Original lookup count.
    pub fn lookup_count(&self) -> u16 {
        self.meta.lookup_count
    }
    /// Validate every subtable offset and optional mark-filtering field before
    /// publishing this lookup. Its type and flags stay raw and unmodified.
    pub fn lookup(&self, index: u16) -> Result<Lookup<'a>, FontError> {
        if index >= self.meta.lookup_count {
            return Err(FontError::InvalidArgument);
        }
        let relative = usize::from(u16_at(
            self.data,
            self.meta.lookups + 2 + usize::from(index) * 2,
        ));
        let offset = self
            .meta
            .lookups
            .checked_add(relative)
            .ok_or(FontError::InvalidFace)?;
        if relative == 0 || !fits(self.data, offset, 6) {
            return Err(FontError::InvalidFace);
        }
        let kind = u16_at(self.data, offset);
        let flags = u16_at(self.data, offset + 2);
        let count = u16_at(self.data, offset + 4);
        let size = usize::from(count) * 2;
        if !fits(self.data, offset + 6, size) {
            return Err(FontError::InvalidFace);
        }
        let mark_set = if flags & 0x10 != 0 {
            let end = offset + 6 + size;
            if !fits(self.data, end, 2) {
                return Err(FontError::InvalidFace);
            }
            u16_at(self.data, end)
        } else {
            u16::MAX
        };
        for index in 0..usize::from(count) {
            let relative = usize::from(u16_at(self.data, offset + 6 + index * 2));
            let subtable = offset.checked_add(relative).ok_or(FontError::InvalidFace)?;
            if relative == 0 || !fits(self.data, subtable, 2) {
                return Err(FontError::InvalidFace);
            }
        }
        Ok(Lookup {
            data: self.data,
            offset,
            kind,
            flags,
            count,
            mark_set,
        })
    }
}
/// Validated immutable lookup header. Private fields prevent constructing a
/// forged view whose subtable records have not passed the complete preflight.
#[derive(Clone, Copy, Debug)]
pub struct Lookup<'a> {
    data: &'a [u8],
    offset: usize,
    kind: u16,
    flags: u16,
    count: u16,
    mark_set: u16,
}
impl<'a> Lookup<'a> {
    /// Original backing table.
    pub fn bytes(&self) -> &'a [u8] {
        self.data
    }
    /// Absolute offset within that table.
    pub fn offset(&self) -> usize {
        self.offset
    }
    /// Raw original lookup type, including unknown types.
    pub fn lookup_type(&self) -> u16 {
        self.kind
    }
    /// Raw original lookup flags.
    pub fn flags(&self) -> u16 {
        self.flags
    }
    /// Number of validated subtable offsets.
    pub fn subtable_count(&self) -> u16 {
        self.count
    }
    /// Exact original field; absent flags retain the original 0xffff sentinel.
    pub fn mark_filtering_set(&self) -> u16 {
        self.mark_set
    }
    /// Return one validated absolute subtable offset in O(1).
    pub fn subtable_offset(&self, index: u16) -> Result<usize, FontError> {
        if index >= self.count {
            return Err(FontError::InvalidArgument);
        }
        Ok(self.offset + usize::from(u16_at(self.data, self.offset + 6 + usize::from(index) * 2)))
    }
}
/// Owned table or exact selected font generation, with its header validated once.
#[derive(Clone, Debug)]
pub struct OwnedLayoutTable {
    storage: Storage,
    meta: Meta,
}
impl OwnedLayoutTable {
    /// Own an immutable standalone GSUB/GPOS table.
    pub fn new(bytes: impl Into<Arc<[u8]>>) -> Result<Self, FontError> {
        let storage = Storage::Bytes(bytes.into());
        let meta = LayoutTable::parse(storage.bytes())?.meta;
        Ok(Self { storage, meta })
    }
    /// Select an original table without copying its bytes. Missing data remains
    /// None; invalid present data rejects. Ordinary face duplicate policy stays intact.
    pub fn from_face(face: SfntFace, tag: Tag) -> Result<Option<Self>, FontError> {
        let Some(storage) = Storage::from_face(face, tag) else {
            return Ok(None);
        };
        let meta = LayoutTable::parse(storage.bytes())?.meta;
        Ok(Some(Self { storage, meta }))
    }
    /// Borrow the already-validated header in O(1), tied to this owner.
    pub fn view(&self) -> LayoutTable<'_> {
        LayoutTable {
            data: self.storage.bytes(),
            meta: self.meta,
        }
    }
    /// Original font and selected tag, absent for standalone table bytes.
    pub fn source(&self) -> Option<(&SfntFace, Tag)> {
        self.storage.source()
    }
}
