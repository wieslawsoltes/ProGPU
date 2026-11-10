//! Native execution resource pages. Stream admission, resolved target state and
//! final rendering remain distinct contracts; raw page production performs no GPU work.
use super::{NativeScene, wire};
use crate::{Error, geometry::VectorVertex};
use std::sync::Arc;
use wire::{WireRecord, constants::*};
mod three_d;
#[cfg(feature = "gpu")]
pub use glyph::{
    NativeGlyphPageEngine, NativeGlyphPaintBindings, NativeGlyphPaintTextureBinding,
    PreparedNativeGlyphPage,
};
pub use path::{NativePathCombineUniforms, NativePathUniforms};
#[cfg(feature = "gpu")]
pub use path::{
    NativePathCoverageCommands, NativePathPageEngine, NativeRasterResourceMetrics,
    PreparedNativePathPage,
};
pub use three_d::{
    NativeThreeDCamera, NativeThreeDDraw, NativeThreeDEdge, NativeThreeDLine,
    NativeThreeDLoweringPath, NativeThreeDMesh, NativeThreeDMeshDraw, NativeThreeDMetrics,
    NativeThreeDPage,
};
#[cfg(feature = "gpu")]
pub use three_d::{NativeThreeDDepthTarget, NativeThreeDPageEngine, PreparedNativeThreeDPage};
mod geometry;
mod glyph;
pub(crate) mod path;
pub use glyph::{
    NativeColorGlyphRaster, NativeGlyphDrawRange, NativeGlyphPage, NativeGlyphPageMetrics,
    NativeGlyphPaintResource, NativeGlyphRaster, NativeGlyphRasterPage,
};
pub use path::{
    NativePathDrawRange, NativePathPage, NativePathPageMetrics, NativePathRaster,
    NativePathRasterPage, NativeSourcePathFrame,
};

mod image;
pub use image::{
    NativeImageCommandInput, NativeImageDrawPage, NativeImagePage, NativeImageStorage,
    NativePictureImageSource,
};
#[cfg(feature = "gpu")]
mod external;
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use external::{
    NativeExternalImageBinding, NativeExternalImages, NativeImageTexture, NativeImageTextureOwner,
};
#[cfg(feature = "gpu")]
pub(crate) use gpu::native_image_sampler;
#[cfg(feature = "gpu")]
pub use gpu::{
    NativeImageLayouts, NativeImagePageEngine, NativePictureImageResolver, PreparedNativeImageDraw,
    PreparedNativeImagePage,
};

/// Original sideband image role; one source can own three independently sized planes.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
#[repr(u32)]
pub enum NativeImageRole {
    Primary = 0,
    Chroma = 1,
    Mask = 2,
}
/// Device-independent original external-image metadata. Ordering is by ID then
/// role; generation participates in exact lookup, not in sort order.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeExternalImageInfo {
    pub resource_id: u64,
    pub generation: u64,
    pub role: NativeImageRole,
    pub size: [u32; 2],
}
fn external_info_valid(info: &[NativeExternalImageInfo]) -> bool {
    info.len() <= SCENE_MAX_RESOURCES as usize
        && info.iter().enumerate().all(|(index, b)| {
            b.resource_id != 0
                && b.generation != 0
                && b.size.into_iter().all(|v| (1..=16384).contains(&v))
                && (index == 0
                    || (info[index - 1].resource_id, info[index - 1].role)
                        < (b.resource_id, b.role))
        })
}
fn external_slot(
    info: &[NativeExternalImageInfo],
    id: u64,
    generation: u64,
    role: NativeImageRole,
) -> Option<usize> {
    info.binary_search_by_key(&(id, role), |i| (i.resource_id, i.role))
        .ok()
        .filter(|&index| info[index].generation == generation)
}
fn allocate<T>(count: usize) -> Result<Vec<T>, Error> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| Error::Capacity)?;
    Ok(values)
}
/// Original page costs and actual resource preparation counters stay separate.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeImageResourceMetrics {
    pub draws: u64,
    pub vertex_bytes: u64,
    pub index_bytes: u64,
    pub texture_bytes: u64,
    pub texture_upload_bytes: u64,
    pub uniform_upload_bytes: u64,
    pub texture_allocations: u64,
    pub nested_pictures: u64,
    pub live_blur_passes: u64,
}
impl NativeImageResourceMetrics {
    pub fn checked_add(self, other: Self) -> Result<Self, Error> {
        macro_rules! add {
            ($field:ident) => {
                self.$field
                    .checked_add(other.$field)
                    .ok_or(Error::Capacity)?
            };
        }
        Ok(Self {
            draws: add!(draws),
            vertex_bytes: add!(vertex_bytes),
            index_bytes: add!(index_bytes),
            texture_bytes: add!(texture_bytes),
            texture_upload_bytes: add!(texture_upload_bytes),
            uniform_upload_bytes: add!(uniform_upload_bytes),
            texture_allocations: add!(texture_allocations),
            nested_pictures: add!(nested_pictures),
            live_blur_passes: add!(live_blur_passes),
        })
    }
}

mod vector;
pub use vector::{NativeVectorDrawRange, NativeVectorPage, NativeVectorPageMetrics};
