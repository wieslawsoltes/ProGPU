//! Original height-segregated shelves, eight-pixel classes, growth-before-LRU.
use super::*;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Region {
    pub origin: [u32; 2],
    pub size: [u32; 2],
}
pub(super) struct Shelf {
    y: u32,
    height: u32,
    next_x: u32,
}
impl GlyphAtlas {
    pub(super) async fn allocate(
        &mut self,
        size: [u32; 2],
        prefer: bool,
    ) -> Result<Option<Region>, Error> {
        if size.contains(&0) {
            return Ok(None);
        }
        loop {
            let atlas_size = self.size();
            if size.iter().any(|&v| v > atlas_size - 4) {
                if self.grow(size).await? {
                    continue;
                }
                self.statistics.capacity_exceeded = true;
                return Ok(None);
            }
            let allocation = size.map(|v| ((v.max(8) + 7) & !7).min(atlas_size - 4));
            for shelf in &mut self.shelves {
                if shelf.height == allocation[1]
                    && u64::from(shelf.next_x) + u64::from(allocation[0]) + 2
                        <= u64::from(atlas_size)
                {
                    let region = Region {
                        origin: [shelf.next_x, shelf.y],
                        size: allocation,
                    };
                    shelf.next_x += allocation[0] + 2;
                    return Ok(Some(region));
                }
            }
            if u64::from(self.next_shelf_y) + u64::from(allocation[1]) + 2 > u64::from(atlas_size) {
                if self.grow(size).await? {
                    continue;
                }
                if prefer && let Some(region) = self.evict(allocation) {
                    self.statistics.capacity_exceeded = false;
                    return Ok(Some(region));
                }
                self.statistics.capacity_exceeded = true;
                return Ok(None);
            }
            self.shelves.try_reserve(1).map_err(|_| Error::Capacity)?;
            let region = Region {
                origin: [2, self.next_shelf_y],
                size: allocation,
            };
            self.shelves.push(Shelf {
                y: self.next_shelf_y,
                height: allocation[1],
                next_x: allocation[0] + 4,
            });
            self.next_shelf_y += allocation[1] + 2;
            return Ok(Some(region));
        }
    }
    async fn grow(&mut self, size: [u32; 2]) -> Result<bool, Error> {
        let current = self.size();
        if current >= self.options.maximum_size {
            return Ok(false);
        }
        let required = size[0].max(size[1]).checked_add(4).ok_or(Error::Capacity)?;
        let mut next = current;
        loop {
            next = next
                .checked_add(1)
                .ok_or(Error::Capacity)?
                .checked_next_power_of_two()
                .unwrap_or(self.options.maximum_size)
                .min(self.options.maximum_size);
            if next >= required || next >= self.options.maximum_size {
                break;
            }
        }
        if next < required {
            return Ok(false);
        }
        // Raster writes must precede the base-level copy on the same queue.
        self.flush_batch().await?;
        let texture = self
            .rasterizer
            .grow_coverage_texture(&self.coverage, [next; 2])
            .await?;
        self.coverage = texture;
        for cached in self.cache.iter_mut() {
            if cached.info.size.contains(&0) {
                continue;
            }
            cached.info.update_uv(next);
        }
        self.statistics.texture_revision = self.statistics.texture_revision.wrapping_add(1);
        self.statistics.capacity_exceeded = false;
        Ok(true)
    }
    fn evict(&mut self, size: [u32; 2]) -> Option<Region> {
        let mut candidate: Option<(&CachedGlyph, u64)> = None;
        for entry in self.cache.iter() {
            let Some(region) = entry.info.region else {
                continue;
            };
            if entry.frame == self.frame || region.size[0] < size[0] || region.size[1] < size[1] {
                continue;
            }
            let waste = u64::from(region.size[0]) * u64::from(region.size[1])
                - u64::from(size[0]) * u64::from(size[1]);
            if candidate.is_none_or(|(previous, best)| {
                entry.frame < previous.frame || (entry.frame == previous.frame && waste < best)
            }) {
                candidate = Some((entry, waste));
            }
        }
        let (candidate, _) = candidate?;
        let key = candidate.key.clone();
        let region = candidate.info.region;
        self.cache.remove(&key);
        self.statistics.eviction_count = self.statistics.eviction_count.wrapping_add(1);
        self.statistics.generation = self.statistics.generation.wrapping_add(1);
        region
    }
    pub(super) fn return_failed(&mut self, region: Region) {
        if !self.failed.contains(&region) {
            self.failed.push(region);
        }
    }
    pub(super) fn reuse_failed(&mut self, size: [u32; 2]) -> Option<Region> {
        let mut candidate = None;
        let mut best = u64::MAX;
        for (index, region) in self.failed.iter().enumerate() {
            if region.size[0] < size[0] || region.size[1] < size[1] {
                continue;
            }
            let waste = u64::from(region.size[0]) * u64::from(region.size[1])
                - u64::from(size[0]) * u64::from(size[1]);
            if waste < best {
                candidate = Some(index);
                best = waste;
            }
        }
        let region = self.failed.remove(candidate?);
        self.statistics.capacity_exceeded = false;
        Some(region)
    }
}
