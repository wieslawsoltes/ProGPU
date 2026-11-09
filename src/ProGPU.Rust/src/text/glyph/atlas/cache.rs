//! Original reference keys and physical dictionary-slot enumeration for LRU ties.
use super::*;
use std::hash::{Hash, Hasher};

#[derive(Clone, Debug)]
pub(super) struct Key {
    // The owner also prevents allocator-address reuse while a residency key lives.
    pub font: ManagedFont,
    pub glyph: u16,
    pub size: u32,
    pub phase: u8,
}
impl Key {
    pub fn new(font: &ManagedFont, glyph: u16, size: f32, phase: u8) -> Self {
        Self {
            font: font.clone(),
            glyph,
            phase,
            // Original Single.Equals/HashCode equate signed zeros and all NaNs.
            // This affects identity only, never the original raster arithmetic.
            size: if size == 0.0 {
                0
            } else if size.is_nan() {
                f32::NAN.to_bits()
            } else {
                size.to_bits()
            },
        }
    }
}
impl PartialEq for Key {
    fn eq(&self, other: &Self) -> bool {
        self.font.same_instance(&other.font)
            && self.glyph == other.glyph
            && self.size == other.size
            && self.phase == other.phase
    }
}
impl Eq for Key {}
impl Hash for Key {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.font.instance_identity().hash(state);
        self.glyph.hash(state);
        self.size.hash(state);
        self.phase.hash(state);
    }
}
pub(super) struct CachedGlyph {
    pub key: Key,
    pub info: GlyphInfo,
    pub frame: u64,
    pub capacity_fallback: bool,
}
enum Slot {
    Occupied(CachedGlyph),
    Vacant(Option<usize>),
}
/// Lookup remains expected O(1). Iteration follows physical insertion slots;
/// removals reuse slots LIFO, preserving the original dictionary's LRU ties.
#[derive(Default)]
pub(super) struct Cache {
    indices: HashMap<Key, usize>,
    slots: Vec<Slot>,
    free: Option<usize>,
}
impl Cache {
    pub fn len(&self) -> usize {
        self.indices.len()
    }
    pub fn reserve(&mut self) -> Result<(), Error> {
        self.indices.try_reserve(1).map_err(|_| Error::Capacity)?;
        if self.free.is_none() {
            self.slots.try_reserve(1).map_err(|_| Error::Capacity)?;
        }
        Ok(())
    }
    pub fn get(&self, key: &Key) -> Option<&CachedGlyph> {
        let index = *self.indices.get(key)?;
        match &self.slots[index] {
            Slot::Occupied(value) => Some(value),
            Slot::Vacant(_) => unreachable!(),
        }
    }
    pub fn get_mut(&mut self, key: &Key) -> Option<&mut CachedGlyph> {
        let index = *self.indices.get(key)?;
        match &mut self.slots[index] {
            Slot::Occupied(value) => Some(value),
            Slot::Vacant(_) => unreachable!(),
        }
    }
    pub fn insert_reserved(&mut self, value: CachedGlyph) {
        if let Some(&index) = self.indices.get(&value.key) {
            self.slots[index] = Slot::Occupied(value);
        } else {
            let index = self.free.unwrap_or(self.slots.len());
            self.indices.insert(value.key.clone(), index);
            if let Some(free) = self.free {
                let Slot::Vacant(next) = self.slots[free] else {
                    unreachable!()
                };
                self.free = next;
                self.slots[free] = Slot::Occupied(value);
            } else {
                self.slots.push(Slot::Occupied(value));
            }
        }
    }
    pub fn remove(&mut self, key: &Key) -> Option<CachedGlyph> {
        let index = self.indices.remove(key)?;
        let Slot::Occupied(value) =
            std::mem::replace(&mut self.slots[index], Slot::Vacant(self.free))
        else {
            unreachable!()
        };
        self.free = Some(index);
        Some(value)
    }
    pub fn iter(&self) -> impl Iterator<Item = &CachedGlyph> {
        self.slots.iter().filter_map(|slot| match slot {
            Slot::Occupied(value) => Some(value),
            Slot::Vacant(_) => None,
        })
    }
    pub fn iter_mut(&mut self) -> impl Iterator<Item = &mut CachedGlyph> {
        self.slots.iter_mut().filter_map(|slot| match slot {
            Slot::Occupied(value) => Some(value),
            Slot::Vacant(_) => None,
        })
    }
    pub fn clear(&mut self) {
        self.indices.clear();
        self.slots.clear();
        self.free = None;
    }
}
