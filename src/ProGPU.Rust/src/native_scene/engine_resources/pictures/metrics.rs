//! Actual subtree diagnostics, distinct from original parent FrameMetrics fields.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePictureOutputMetrics {
    pub allocations: u64,
    pub bytes: u64,
    pub descriptor_budget_bytes: u64,
}
impl NativePictureWorkMetrics {
    pub fn checked_add(self, other: Self) -> Result<Self, Error> {
        let (a, b) = (self.frame, other.frame);
        macro_rules! frame {
            ($f:ident) => {
                a.$f.checked_add(b.$f).ok_or(Error::Capacity)?
            };
        }
        macro_rules! work {
            ($f:ident) => {
                self.$f.checked_add(other.$f).ok_or(Error::Capacity)?
            };
        }
        Ok(Self {
            frame: wire::FrameMetrics {
                struct_size: wire::FrameMetrics::WIRE_SIZE as u32,
                command_count: frame!(command_count),
                draw_call_count: frame!(draw_call_count),
                family_switch_count: frame!(family_switch_count),
                submission_count: frame!(submission_count),
                vertex_upload_bytes: frame!(vertex_upload_bytes),
                index_upload_bytes: frame!(index_upload_bytes),
                texture_upload_bytes: frame!(texture_upload_bytes),
                uniform_upload_bytes: frame!(uniform_upload_bytes),
                coverage_staging_bytes: frame!(coverage_staging_bytes),
                // This is an aggregate of independently owned source scenes.
                payload_hash: 0,
                brush_upload_bytes: frame!(brush_upload_bytes),
                gradient_stop_upload_bytes: frame!(gradient_stop_upload_bytes),
                text_style_upload_bytes: frame!(text_style_upload_bytes),
                color_glyph_upload_bytes: frame!(color_glyph_upload_bytes),
                cpu_preflight_nanoseconds: frame!(cpu_preflight_nanoseconds),
                cpu_resource_nanoseconds: frame!(cpu_resource_nanoseconds),
                cpu_encode_nanoseconds: frame!(cpu_encode_nanoseconds),
                cpu_flush_nanoseconds: frame!(cpu_flush_nanoseconds),
                cpu_finalize_nanoseconds: frame!(cpu_finalize_nanoseconds),
                cpu_total_nanoseconds: frame!(cpu_total_nanoseconds),
            },
            texture_bytes: work!(texture_bytes),
            texture_allocations: work!(texture_allocations),
            render_passes: work!(render_passes),
            compute_passes: work!(compute_passes),
            live_blur_passes: work!(live_blur_passes),
            path_rasters: work!(path_rasters),
            nested_captures: work!(nested_captures),
            cache_hits: work!(cache_hits),
            seed_copy_bytes: work!(seed_copy_bytes),
        })
    }
}
