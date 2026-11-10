//! Independent original native GPU family keys. Only actual submitted pages
//! enter this cache; a prepared but abandoned atlas cannot become a warm hit.
use super::*;
use crate::native_scene::{
    engine_gpu::cache::{NativePageCacheIdentity, NativeReplayCacheIdentity},
    engine_resources::{NativeGlyphRasterPage, NativePathRasterPage},
    execution::NativeSceneResourcePlan,
};

struct Key {
    family: NativePageCacheIdentity,
    frame_layout: wgpu::BindGroupLayout,
    flags: u32,
    antialias_bits: u32,
    resource_scope: u64,
}
impl Key {
    fn new(
        family: NativePageCacheIdentity,
        plan: &NativeSceneResourcePlan,
        frame_layout: &wgpu::BindGroupLayout,
    ) -> Self {
        let options = plan.options();
        Self {
            family,
            frame_layout: frame_layout.clone(),
            flags: options.engine_flags,
            antialias_bits: options.antialias_padding_pixels.to_bits(),
            resource_scope: options.resource_scope,
        }
    }
    fn matches(
        &self,
        family: NativePageCacheIdentity,
        plan: &NativeSceneResourcePlan,
        pipelines: &NativePipelineCache,
    ) -> bool {
        let options = plan.options();
        family.hit(Some(&self.family))
            && self.frame_layout == pipelines.layouts().frame
            && self.flags == options.engine_flags
            && self.antialias_bits == options.antialias_padding_pixels.to_bits()
            && self.resource_scope == options.resource_scope
    }
}
struct Entry<T> {
    key: Key,
    page: Arc<T>,
}
struct GlyphEntry {
    key: Key,
    page: Arc<NativeGlyphRenderPage>,
    external: NativeExternalImages,
}
struct ImageEntry {
    key: Key,
    page: Arc<PreparedNativeImagePage>,
    external: NativeExternalImages,
}

#[derive(Default)]
pub(super) struct FamilyCache {
    brushes: Option<Arc<PreparedNativeMaterials>>,
    styles: Option<Arc<PreparedNativeMaterials>>,
    // The Rust explicit layouts also bind canonical empty-table storage. Keep
    // that physical owner without evicting an absent family's original entry.
    fallback_material: Option<Arc<PreparedNativeMaterials>>,
    vector: Option<Entry<NativeVectorRenderPage>>,
    paths: Option<Entry<NativePathRenderPage>>,
    glyphs: Option<GlyphEntry>,
    images: Option<ImageEntry>,
}
impl FamilyCache {
    pub fn brushes(&self, hash: u64) -> Option<&PreparedNativeMaterials> {
        self.brushes
            .as_deref()
            .filter(|p| p.brushes().scene_hash() == hash)
            .or_else(|| {
                self.fallback_material
                    .as_deref()
                    .filter(|p| p.brushes().scene_hash() == hash)
            })
    }
    pub fn styles(&self, hash: u64) -> Option<&PreparedNativeMaterials> {
        self.styles
            .as_deref()
            .filter(|p| p.styles().scene_hash() == hash)
            .or_else(|| {
                self.fallback_material
                    .as_deref()
                    .filter(|p| p.styles().scene_hash() == hash)
            })
    }
    pub fn vector(
        &self,
        identity: &NativeReplayCacheIdentity,
        plan: &NativeSceneResourcePlan,
        pipelines: &NativePipelineCache,
    ) -> Option<Arc<NativeVectorRenderPage>> {
        let old = self.vector.as_ref()?;
        let source = plan.vector()?;
        old.key
            .matches(identity.vector, plan, pipelines)
            .then(|| Arc::new(old.page.rebind(source.clone())))
    }
    pub fn paths(
        &self,
        identity: &NativeReplayCacheIdentity,
        plan: &NativeSceneResourcePlan,
        source: &Arc<NativePathRasterPage>,
        pipelines: &NativePipelineCache,
    ) -> Option<Arc<NativePathRenderPage>> {
        let old = self.paths.as_ref()?;
        let raster = old.page.page().page();
        if !old.key.matches(identity.path, plan, pipelines)
            || raster.atlas_size() != source.atlas_size()
            || raster.has_inline_operations() != source.has_inline_operations()
            || raster.has_inline_signed_winding() != source.has_inline_signed_winding()
            || raster.split_leaf_uniforms().len() != source.split_leaf_uniforms().len()
            || raster.signed_leaf_uniforms().len() != source.signed_leaf_uniforms().len()
        {
            return None;
        }
        let raster = Arc::new(old.page.page().rebind_completed(source.clone()));
        Some(Arc::new(old.page.rebind(raster)))
    }
    pub fn glyphs(
        &self,
        identity: &NativeReplayCacheIdentity,
        plan: &NativeSceneResourcePlan,
        source: &Arc<NativeGlyphRasterPage>,
        external: &NativeExternalImages,
        pipelines: &NativePipelineCache,
    ) -> Option<Arc<NativeGlyphRenderPage>> {
        let old = self.glyphs.as_ref()?;
        let raster = old.page.page().page();
        if !old.key.matches(identity.glyph, plan, pipelines)
            || !old.external.same_bindings(external)
            || raster.atlas_size() != source.atlas_size()
            || raster.color_atlas_size() != source.color_atlas_size()
        {
            return None;
        }
        let raster = Arc::new(old.page.page().rebind_completed(source.clone()));
        let paints = old
            .page
            .paint_bindings()
            .map(|p| Arc::new(p.rebind(raster.clone(), external.clone())));
        Some(Arc::new(old.page.rebind(raster, paints)))
    }
    pub fn images(
        &self,
        identity: &NativeReplayCacheIdentity,
        plan: &NativeSceneResourcePlan,
        external: &NativeExternalImages,
        pipelines: &NativePipelineCache,
    ) -> Result<Option<Arc<PreparedNativeImagePage>>, Error> {
        let Some(old) = self.images.as_ref() else {
            return Ok(None);
        };
        if !old.key.matches(identity.image, plan, pipelines)
            || !old.external.same_bindings(external)
        {
            return Ok(None);
        }
        let source = plan.images().ok_or(Error::ResourceDomain)?.clone();
        Ok(Some(Arc::new(old.page.rebind(source, external.clone())?)))
    }
    pub fn submitted(&mut self, pages: &Arc<PreparedNativeScenePages>) {
        let plan = pages.source.source();
        let identity = NativeReplayCacheIdentity::from_plan(plan);
        let counts = plan.draws().metrics();
        self.fallback_material = Some(pages.material.clone());
        // An absent family leaves its independent last submitted entry intact.
        if counts.vector_draw_count != 0
            || counts.path_draw_count != 0
            || counts.glyph_paint_count != 0
        {
            self.brushes = Some(pages.material.clone());
        }
        if counts.has_styled_glyphs {
            self.styles = Some(pages.material.clone());
        }
        if let Some(page) = &pages.vector {
            self.vector = Some(Entry {
                key: Key::new(identity.vector, plan, &pages.frame_layout),
                page: page.clone(),
            });
        }
        if let Some(page) = &pages.paths {
            self.paths = Some(Entry {
                key: Key::new(identity.path, plan, &pages.frame_layout),
                page: page.clone(),
            });
        }
        if let Some(page) = &pages.glyphs {
            self.glyphs = Some(GlyphEntry {
                key: Key::new(identity.glyph, plan, &pages.frame_layout),
                page: page.clone(),
                external: pages.external.clone(),
            });
        }
        if let Some(page) = &pages.images {
            self.images = Some(ImageEntry {
                key: Key::new(identity.image, plan, &pages.frame_layout),
                page: page.clone(),
                external: pages.external.clone(),
            });
        }
    }
}
