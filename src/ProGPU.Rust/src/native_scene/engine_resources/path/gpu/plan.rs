//! Owned source-independent view of the original native compute raster pages.
use super::*;
use crate::vector::GpuPathRecord;
/// Read-only original raster records. Implementations retain their actual source
/// owners; this contract does not manufacture a path draw or target frame.
pub trait NativeRasterPlan: std::fmt::Debug + Send + Sync {
    fn segments(&self) -> &[wire::PathSegment];
    fn records(&self) -> &[GpuPathRecord];
    fn uniforms(&self) -> &[NativePathUniforms];
    fn split_leaf_uniforms(&self) -> &[Arc<[NativePathUniforms]>];
    fn signed_leaf_uniforms(&self) -> &[Arc<[NativePathUniforms]>];
    fn combine_uniforms(&self) -> &[NativePathCombineUniforms];
    fn signed_combine_uniforms(&self) -> &[NativePathCombineUniforms];
    fn rasters(&self) -> &[NativePathRaster];
    fn atlas_size(&self) -> u32;
    fn coverage_staging_bytes(&self) -> u64;
    fn has_inline_operations(&self) -> bool;
    fn has_inline_signed_winding(&self) -> bool;
    fn has_only_lines(&self) -> bool;
    fn has_only_lines_or_cubics(&self) -> bool;
    fn vertex_bytes(&self) -> &[u8];
    fn index_bytes(&self) -> &[u8];
}
impl NativeRasterPlan for NativePathRasterPage {
    fn segments(&self) -> &[wire::PathSegment] {
        self.source().segments()
    }
    fn records(&self) -> &[GpuPathRecord] {
        self.records()
    }
    fn uniforms(&self) -> &[NativePathUniforms] {
        self.uniforms()
    }
    fn split_leaf_uniforms(&self) -> &[Arc<[NativePathUniforms]>] {
        self.split_leaf_uniforms()
    }
    fn signed_leaf_uniforms(&self) -> &[Arc<[NativePathUniforms]>] {
        self.signed_leaf_uniforms()
    }
    fn combine_uniforms(&self) -> &[NativePathCombineUniforms] {
        self.combine_uniforms()
    }
    fn signed_combine_uniforms(&self) -> &[NativePathCombineUniforms] {
        self.signed_combine_uniforms()
    }
    fn rasters(&self) -> &[NativePathRaster] {
        self.rasters()
    }
    fn atlas_size(&self) -> u32 {
        self.atlas_size()
    }
    fn coverage_staging_bytes(&self) -> u64 {
        self.coverage_staging_bytes()
    }
    fn has_inline_operations(&self) -> bool {
        self.has_inline_operations()
    }
    fn has_inline_signed_winding(&self) -> bool {
        self.has_inline_signed_winding()
    }
    fn has_only_lines(&self) -> bool {
        self.has_only_lines()
    }
    fn has_only_lines_or_cubics(&self) -> bool {
        self.has_only_lines_or_cubics()
    }
    fn vertex_bytes(&self) -> &[u8] {
        bytemuck::cast_slice(self.vertices())
    }
    fn index_bytes(&self) -> &[u8] {
        bytemuck::cast_slice(self.indices())
    }
}
