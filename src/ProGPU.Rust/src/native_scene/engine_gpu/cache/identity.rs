use super::*;
/// Original ordinary page identity, including its independent inventory guards.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativePageCacheIdentity {
    pub scene_hash: u64,
    pub dpi_scale: f32,
    pub target_size: [u32; 2],
    pub draw_count: u32,
    pub inventory: [u64; 7],
}
impl NativePageCacheIdentity {
    pub fn hit(&self, retained: Option<&Self>) -> bool {
        self.draw_count != 0 && retained.is_some_and(|prior| prior == self)
    }
}
/// Original bundle key appends the actual partial-replay bool and only then its
/// resolved physical damage rectangle. Clear color/preserve are pass metadata.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeBundleCacheIdentity {
    pub scene_hash: u64,
    pub dpi_scale: f32,
    pub target_size: [u32; 2],
    pub required_path_hash: Option<u64>,
    pub required_glyph_hash: Option<u64>,
}
impl NativeBundleCacheIdentity {
    pub fn hit(
        &self,
        retained: Option<&Self>,
        actual_path_gpu_hash: u64,
        actual_glyph_gpu_hash: u64,
    ) -> bool {
        retained.is_some_and(|prior| {
            prior.scene_hash == self.scene_hash
                && prior.dpi_scale == self.dpi_scale
                && prior.target_size == self.target_size
        }) && self
            .required_path_hash
            .is_none_or(|h| h == actual_path_gpu_hash)
            && self
                .required_glyph_hash
                .is_none_or(|h| h == actual_glyph_gpu_hash)
    }
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeReplayCacheIdentity {
    pub raw_scene_hash: u64,
    pub presented_scene_hash: u64,
    pub brush_hash: u64,
    pub text_style_hash: u64,
    pub vector: NativePageCacheIdentity,
    pub path: NativePageCacheIdentity,
    pub glyph: NativePageCacheIdentity,
    pub image: NativePageCacheIdentity,
    pub three_d_source_hash: u64,
    pub bundle: NativeBundleCacheIdentity,
}
impl NativeReplayCacheIdentity {
    pub fn from_plan(plan: &NativeSceneResourcePlan) -> Self {
        let frame = plan.frame();
        let size = [frame.width, frame.height];
        let p = plan.layers().presentation();
        let h = plan.content_hashes();
        let counts = plan.draws().metrics();
        let presented = |hash| presentation_content_hash(hash, size, frame.dpi_scale, &p);
        let page = |hash, draw_count, inventory| NativePageCacheIdentity {
            scene_hash: presented(hash),
            dpi_scale: frame.dpi_scale,
            target_size: size,
            draw_count,
            inventory,
        };
        let raw_scene_hash = raw_fnv1a64(plan.scene().envelope().bytes());
        let presented_scene_hash = presented(raw_scene_hash);
        let mut bundle_hash = append(
            presented_scene_hash,
            &[u8::from(plan.budget().partial_damage)],
        );
        if plan.budget().partial_damage {
            let d = plan.budget().damage;
            for v in [d.x, d.y, d.width, d.height] {
                bundle_hash = append(bundle_hash, &v.to_le_bytes());
            }
        }
        let vector = page(h.analytic, counts.vector_draw_count, [0; 7]);
        let path = page(
            h.path,
            counts.path_draw_count,
            [counts.path_boolean_node_count, 0, 0, 0, 0, 0, 0],
        );
        let glyph = page(
            h.glyph,
            counts.glyph_draw_count,
            [
                counts.glyph_count,
                counts.glyph_count,
                counts.glyph_count,
                u64::from(counts.glyph_paint_count),
                u64::from(counts.glyph_paint_count),
                counts.glyph_count,
                counts.color_glyph_bitmap_count,
            ],
        );
        let image = page(h.image, counts.image_draw_count, [0; 7]);
        Self {
            raw_scene_hash,
            presented_scene_hash,
            brush_hash: h.brush,
            text_style_hash: h.text_style,
            vector,
            path,
            glyph,
            image,
            three_d_source_hash: h.three_d,
            bundle: NativeBundleCacheIdentity {
                scene_hash: bundle_hash,
                dpi_scale: frame.dpi_scale,
                target_size: size,
                required_path_hash: (counts.path_draw_count != 0).then_some(path.scene_hash),
                required_glyph_hash: (counts.glyph_draw_count != 0).then_some(glyph.scene_hash),
            },
        }
    }
}
fn append(mut hash: u64, bytes: &[u8]) -> u64 {
    for &byte in bytes {
        hash = (hash ^ u64::from(byte)).wrapping_mul(1099511628211);
    }
    hash
}
