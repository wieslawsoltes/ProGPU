//! Original bounded immutable mask-picture raster retention, independent of IDs.
use super::*;
#[derive(Default)]
pub(super) struct PictureCache {
    entries: Vec<Entry>,
}
struct Entry {
    source: NativePictureMaskSource,
    engine_flags: u64,
    texture: NativeImageTexture,
}
impl Entry {
    fn cost(&self) -> u64 {
        u64::from(self.source.frame.width) * u64::from(self.source.frame.height) * 4
            + self.source.scene.envelope().bytes().len() as u64
    }
}
use crate::native_scene::engine_resources::pictures::identity::{
    descriptor, eligible, generation_independent, same,
};
impl PictureCache {
    pub fn find(
        &self,
        source: &NativePictureMaskSource,
        engine_flags: u64,
    ) -> Option<NativeImageTexture> {
        if !eligible(source) {
            return None;
        }
        self.entries
            .iter()
            .find(|entry| {
                entry.engine_flags == engine_flags
                    && same(descriptor(&entry.source), descriptor(source))
                    && same(entry.source.frame.presentation, source.frame.presentation)
                    && generation_independent(&entry.source.scene, &source.scene)
            })
            .map(|entry| entry.texture.clone())
    }
    pub fn retain(
        &mut self,
        source: NativePictureMaskSource,
        engine_flags: u64,
        texture: NativeImageTexture,
    ) {
        const LIMIT: u64 = 64 * 1024 * 1024;
        let candidate = Entry {
            source,
            engine_flags,
            texture,
        };
        if !eligible(&candidate.source) || candidate.cost() > LIMIT {
            return;
        }
        let id = candidate.source.scene.envelope().header().scene_id;
        self.entries.retain(|entry| {
            !(entry.source.scene.envelope().header().scene_id == id
                && same(descriptor(&entry.source), descriptor(&candidate.source))
                && generation_independent(&entry.source.scene, &candidate.source.scene))
        });
        let mut bytes = self.entries.iter().map(Entry::cost).sum::<u64>();
        while !self.entries.is_empty()
            && (self.entries.len() >= 64 || bytes > LIMIT - candidate.cost())
        {
            bytes -= self.entries.remove(0).cost();
        }
        if self.entries.try_reserve(1).is_ok() {
            self.entries.push(candidate);
        }
    }
}
