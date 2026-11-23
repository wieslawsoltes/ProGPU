//! Strict original VVAR checks kept separate from legacy HVAR/store admission.
use super::{DeltaSetIndexMap, FontError, ItemVariationStore, bytes};

impl ItemVariationStore<'_> {
    pub(in crate::font) fn valid_vertical_reference(self, outer: u16, inner: u16) -> bool {
        if outer == u16::MAX && inner == u16::MAX {
            return true;
        }
        if outer >= self.subtables {
            return false;
        }
        self.subtable(outer)
            .is_none_or(|at| inner < bytes::u16(self.data, at).unwrap())
    }
    pub(in crate::font) fn validate_vertical(self) -> Result<(), FontError> {
        if self.region_count >= 0x8000 || bytes::u32(self.data, self.offset + 2) == Some(0) {
            return Err(FontError::InvalidFace);
        }
        for region in 0..usize::from(self.region_count) {
            for axis in 0..usize::from(self.axes) {
                let at = self.regions + 4 + (region * usize::from(self.axes) + axis) * 6;
                let start = bytes::i16(self.data, at).unwrap();
                let peak = bytes::i16(self.data, at + 2).unwrap();
                let end = bytes::i16(self.data, at + 4).unwrap();
                if start < -16384
                    || end > 16384
                    || start > peak
                    || peak > end
                    || (start < 0 && end > 0 && peak != 0)
                {
                    return Err(FontError::InvalidFace);
                }
            }
        }
        for outer in 0..self.subtables {
            let Some(at) = self.subtable(outer) else {
                continue;
            };
            let count = bytes::u16(self.data, at + 4).unwrap();
            for region in 0..usize::from(count) {
                if bytes::u16(self.data, at + 6 + region * 2).unwrap() >= self.region_count {
                    return Err(FontError::InvalidFace);
                }
            }
        }
        Ok(())
    }
}
impl DeltaSetIndexMap<'_> {
    pub(in crate::font) fn validate_vertical(
        self,
        store: ItemVariationStore<'_>,
    ) -> Result<(), FontError> {
        if self.is_empty() {
            return Err(FontError::InvalidFace);
        }
        for entry in self.entries.chunks_exact(usize::from(self.size)) {
            let packed = entry
                .iter()
                .fold(0u32, |value, byte| (value << 8) | u32::from(*byte));
            let outer = packed >> self.bits;
            let inner = (packed & ((1u32 << self.bits) - 1)) as u16;
            if outer > u32::from(u16::MAX) || !store.valid_vertical_reference(outer as u16, inner) {
                return Err(FontError::InvalidFace);
            }
        }
        Ok(())
    }
}
