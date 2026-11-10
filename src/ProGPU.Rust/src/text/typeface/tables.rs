//! Original unique, first-insertion-order table inventory with last valid payload.
use super::Typeface;
use crate::font::Tag;
use std::{collections::HashSet, sync::Arc};
impl Typeface {
    pub fn table_tags(&self) -> &[Tag] {
        self.0.tables.get_or_init(|| {
            if self.is_empty() {
                return Arc::from([]);
            }
            let face = self.font().source();
            let data = face.data();
            let directory = face.face_offset() as usize + 12;
            let mut seen = HashSet::new();
            let mut tags = Vec::new();
            for index in 0..usize::from(face.table_count()) {
                let Some(record) = data.get(directory + index * 16..directory + (index + 1) * 16)
                else {
                    break;
                };
                let tag = Tag(u32::from_be_bytes(record[..4].try_into().unwrap()));
                let offset = u32::from_be_bytes(record[8..12].try_into().unwrap()) as usize;
                let length = u32::from_be_bytes(record[12..16].try_into().unwrap()) as usize;
                if offset <= data.len() && length <= data.len() - offset && seen.insert(tag) {
                    tags.push(tag);
                }
            }
            tags.into()
        })
    }
    pub fn try_table_tags(&self) -> Option<&[Tag]> {
        let tags = self.table_tags();
        (!tags.is_empty()).then_some(tags)
    }
    pub fn table_count(&self) -> usize {
        self.table_tags().len()
    }
    pub fn table_size(&self, tag: Tag) -> usize {
        if self.is_empty() {
            0
        } else {
            self.font()
                .source()
                .table(tag)
                .map_or(0, |table| table.bytes.len())
        }
    }
    pub fn table_data(&self, tag: Tag) -> Option<Vec<u8>> {
        if self.is_empty() {
            None
        } else {
            self.font()
                .source()
                .table(tag)
                .map(|table| table.bytes.to_vec())
        }
    }
    /// Exact positive-length region copy. The complete request is admitted before
    /// writing; a short output or invalid offset leaves every output byte alone.
    pub fn write_table_data(&self, tag: Tag, offset: i32, length: i32, output: &mut [u8]) -> bool {
        if self.is_empty() || offset < 0 || length <= 0 {
            return false;
        }
        let Some(table) = self.font().source().table(tag) else {
            return false;
        };
        let (offset, length) = (offset as usize, length as usize);
        let Some(end) = offset.checked_add(length) else {
            return false;
        };
        let (Some(source), Some(target)) = (table.bytes.get(offset..end), output.get_mut(..length))
        else {
            return false;
        };
        target.copy_from_slice(source);
        true
    }
}
