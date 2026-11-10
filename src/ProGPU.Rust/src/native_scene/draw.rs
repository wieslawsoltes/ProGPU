//! Original contiguous retained-family draw merges. Reuse never crosses source
//! order or changes shader data; callers still admit equal state/target scopes.
use super::NO_INDEX;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeAnalyticDraw {
    pub vertex_offset_bytes: u64,
    pub index_offset_bytes: u64,
    pub vertex_count: u32,
    pub index_count: u32,
}
impl NativeAnalyticDraw {
    pub fn merge(&mut self, next: Self) -> bool {
        if self.vertex_count == 0
            || self.index_count == 0
            || next.vertex_count == 0
            || next.index_count == 0
            || self.vertex_count > u32::MAX - next.vertex_count
            || self.index_count > u32::MAX - next.index_count
            || self
                .vertex_offset_bytes
                .wrapping_add(u64::from(self.vertex_count) * 56)
                != next.vertex_offset_bytes
            || self
                .index_offset_bytes
                .wrapping_add(u64::from(self.index_count) * 4)
                != next.index_offset_bytes
        {
            return false;
        }
        self.vertex_count += next.vertex_count;
        self.index_count += next.index_count;
        true
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePathDraw {
    pub first_index: u32,
    pub index_count: u32,
}
impl NativePathDraw {
    pub fn merge(&mut self, next: Self) -> bool {
        if self.index_count == 0
            || next.index_count == 0
            || self.index_count > u32::MAX - next.index_count
            || self.first_index.wrapping_add(self.index_count) != next.first_index
        {
            return false;
        }
        self.index_count += next.index_count;
        true
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeGlyphDraw {
    pub first_instance: u32,
    pub instance_count: u32,
    pub paint_index: u32,
}
impl Default for NativeGlyphDraw {
    fn default() -> Self {
        Self {
            first_instance: 0,
            instance_count: 0,
            paint_index: NO_INDEX,
        }
    }
}
impl NativeGlyphDraw {
    pub fn merge(&mut self, next: Self) -> bool {
        if self.paint_index != next.paint_index
            || self.instance_count == 0
            || next.instance_count == 0
            || self.instance_count > u32::MAX - next.instance_count
            || self.first_instance.wrapping_add(self.instance_count) != next.first_instance
        {
            return false;
        }
        self.instance_count += next.instance_count;
        true
    }
}
