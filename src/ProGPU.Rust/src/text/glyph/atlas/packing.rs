//! Original height-segregated shelves, eight-pixel classes, growth-before-LRU.
use super::*;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Region {
    pub origin: [u32; 2],
    pub size: [u32; 2],
    pub color: bool,
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
        color: bool,
    ) -> Result<Option<Region>, Error> {
        if size.contains(&0) {
            return Ok(None);
        }
        loop {
            let atlas_size = if color {
                self.color_size()
            } else {
                self.size()
            };
            if size.iter().any(|&v| v > atlas_size - 4) {
                if self.grow(size, color).await? {
                    continue;
                }
                self.statistics.capacity_exceeded = true;
                return Ok(None);
            }
            let allocation = size.map(|v| ((v.max(8) + 7) & !7).min(atlas_size - 4));
            let shelves = if color {
                &mut self.color_shelves
            } else {
                &mut self.shelves
            };
            let next_y = if color {
                &mut self.next_color_shelf_y
            } else {
                &mut self.next_shelf_y
            };
            for shelf in &mut *shelves {
                if shelf.height == allocation[1]
                    && u64::from(shelf.next_x) + u64::from(allocation[0]) + 2
                        <= u64::from(atlas_size)
                {
                    let region = Region {
                        origin: [shelf.next_x, shelf.y],
                        size: allocation,
                        color,
                    };
                    shelf.next_x += allocation[0] + 2;
                    return Ok(Some(region));
                }
            }
            if u64::from(*next_y) + u64::from(allocation[1]) + 2 > u64::from(atlas_size) {
                if self.grow(size, color).await? {
                    continue;
                }
                if prefer && let Some(region) = self.evict(allocation, color) {
                    self.statistics.capacity_exceeded = false;
                    return Ok(Some(region));
                }
                self.statistics.capacity_exceeded = true;
                return Ok(None);
            }
            shelves.try_reserve(1).map_err(|_| Error::Capacity)?;
            let region = Region {
                origin: [2, *next_y],
                size: allocation,
                color,
            };
            shelves.push(Shelf {
                y: *next_y,
                height: allocation[1],
                next_x: allocation[0] + 4,
            });
            *next_y += allocation[1] + 2;
            return Ok(Some(region));
        }
    }
    async fn grow(&mut self, size: [u32; 2], color: bool) -> Result<bool, Error> {
        let current = if color {
            self.color_size()
        } else {
            self.size()
        };
        let maximum = if color {
            self.maximum_color_size()
        } else {
            self.maximum_size()
        };
        if current >= maximum {
            return Ok(false);
        }
        let required = size[0].max(size[1]).checked_add(4).ok_or(Error::Capacity)?;
        let mut next = current;
        loop {
            next = next
                .checked_add(1)
                .ok_or(Error::Capacity)?
                .checked_next_power_of_two()
                .unwrap_or(maximum)
                .min(maximum);
            if next >= required || next >= maximum {
                break;
            }
        }
        if next < required {
            return Ok(false);
        }
        // Raster writes must precede the base-level copy on the same queue.
        self.flush_batch().await?;
        if color {
            self.color = self
                .rasterizer
                .grow_color_texture(&self.color, [next; 2])
                .await?;
        } else {
            self.coverage = self
                .rasterizer
                .grow_coverage_texture(&self.coverage, [next; 2])
                .await?;
        }
        for cached in self.cache.iter_mut() {
            if cached.info.is_color_bitmap != color || cached.info.size.contains(&0) {
                continue;
            }
            cached.info.update_uv(next);
        }
        self.statistics.texture_revision = self.statistics.texture_revision.wrapping_add(1);
        self.statistics.capacity_exceeded = false;
        Ok(true)
    }
    fn evict(&mut self, size: [u32; 2], color: bool) -> Option<Region> {
        let mut candidate: Option<(&CachedGlyph, u64)> = None;
        for entry in self.cache.iter() {
            let region = legacy_region(entry.info);
            if entry.frame == self.frame
                || entry.info.is_color_bitmap != color
                || region.size.contains(&0)
                || region.size[0] < size[0]
                || region.size[1] < size[1]
            {
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
        let region = legacy_region(candidate.info);
        self.cache.remove(&key);
        self.statistics.eviction_count = self.statistics.eviction_count.wrapping_add(1);
        self.statistics.generation = self.statistics.generation.wrapping_add(1);
        Some(region)
    }
    pub(super) fn return_failed(&mut self, region: Region) {
        if !self.failed.contains(&region) {
            self.failed.push(region);
        }
    }
    pub(super) fn reuse_failed(&mut self, size: [u32; 2], color: bool) -> Option<Region> {
        let mut candidate = None;
        let mut best = u64::MAX;
        for (index, region) in self.failed.iter().enumerate() {
            if region.color != color || region.size[0] < size[0] || region.size[1] < size[1] {
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
fn legacy_region(info: GlyphInfo) -> Region {
    // Preserve the original LRU's fallback dimensions separately from strict
    // retained-residency admission, which requires an actual allocated region.
    info.region.unwrap_or(Region {
        origin: info.origin,
        size: info.size,
        color: info.is_color_bitmap,
    })
}
