//! Private owned counterparts keep validated encoded views without reparsing.
use super::{DeltaSetIndexMap, FontError, ItemVariationStore};
use crate::font::{SfntFace, Span};

#[derive(Clone, Copy, Debug)]
struct StoreLayout {
    offset: usize,
    regions: usize,
    axes: u16,
    region_count: u16,
    subtables: u16,
}
#[derive(Clone, Debug)]
pub(in crate::font) struct OwnedStore {
    source: SfntFace,
    table: Span,
    layout: StoreLayout,
}
impl OwnedStore {
    pub(in crate::font) fn parse(
        source: &SfntFace,
        table: Span,
        offset: usize,
        axes: u16,
    ) -> Result<Self, FontError> {
        let s = ItemVariationStore::parse(table.get(source.data()), offset, axes)?;
        Ok(Self {
            source: source.clone(),
            table,
            layout: StoreLayout {
                offset: s.offset,
                regions: s.regions,
                axes: s.axes,
                region_count: s.region_count,
                subtables: s.subtables,
            },
        })
    }
    pub(in crate::font) fn view(&self) -> ItemVariationStore<'_> {
        let s = self.layout;
        ItemVariationStore {
            data: self.table.get(self.source.data()),
            offset: s.offset,
            regions: s.regions,
            axes: s.axes,
            region_count: s.region_count,
            subtables: s.subtables,
        }
    }
}
#[derive(Clone, Debug)]
pub(in crate::font) struct OwnedMap {
    source: SfntFace,
    entries: Span,
    count: u32,
    size: u8,
    bits: u8,
}
impl OwnedMap {
    pub(in crate::font) fn parse(
        source: &SfntFace,
        table: Span,
        offset: usize,
    ) -> Result<Self, FontError> {
        let data = table.get(source.data());
        let map = DeltaSetIndexMap::parse(data, offset)?;
        // Both map formats and complete entry extents were validated above.
        let start = offset + if data[offset] == 0 { 4 } else { 6 };
        Ok(Self {
            source: source.clone(),
            entries: Span {
                offset: table.offset + start,
                length: map.entries.len(),
            },
            count: map.count,
            size: map.size,
            bits: map.bits,
        })
    }
    pub(in crate::font) fn view(&self) -> DeltaSetIndexMap<'_> {
        DeltaSetIndexMap {
            entries: self.entries.get(self.source.data()),
            count: self.count,
            size: self.size,
            bits: self.bits,
        }
    }
}
