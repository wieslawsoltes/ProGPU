//! Original native retained-page identities. Family dependencies retain actual
//! source owners; stream generation and serialization offsets are independent.
use super::*;
use wire::{constants::*, WireRecord};
mod commands;
mod content;
mod suffix;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSceneContentHashes {
    pub brush: u64,
    pub text_style: u64,
    pub analytic: u64,
    pub path: u64,
    pub glyph: u64,
    pub image: u64,
    pub three_d: u64,
    pub hit_test: u64,
}

/// Original uniform/full-target fast path leaves the family identity unchanged.
/// Nonuniform or subviewport presentation appends exact original float bits.
pub fn presentation_content_hash(
    hash: u64,
    frame_size: [u32; 2],
    frame_dpi: f32,
    p: &wire::Presentation,
) -> u64 {
    if p.viewport_x == 0
        && p.viewport_y == 0
        && p.viewport_width == frame_size[0]
        && p.viewport_height == frame_size[1]
        && p.dpi_scale_x == frame_dpi
        && p.dpi_scale_y == frame_dpi
    {
        return hash;
    }
    let mut result = hash;
    for value in [
        p.viewport_x,
        p.viewport_y,
        p.viewport_width,
        p.viewport_height,
        p.dpi_scale_x.to_bits(),
        p.dpi_scale_y.to_bits(),
    ] {
        result = append(result, &value.to_le_bytes());
    }
    finish(result)
}

/// Exact alignment-safe independent byte lanes; only the final short tail is
/// scalar on the original supported ISAs. Other targets retain the standard
/// optimized slice comparison, matching the source's platform routine route.
pub fn scene_bytes_equal(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    #[cfg(any(
        target_arch = "aarch64",
        target_arch = "x86_64",
        all(target_arch = "x86", target_feature = "sse2"),
        all(target_arch = "wasm32", target_feature = "simd128")
    ))]
    {
        use wide::CmpEq;
        let mut offset = 0;
        while left.len() - offset >= 16 {
            let a = wide::u8x16::new(
                left[offset..offset + 16]
                    .try_into()
                    .expect("complete SIMD lane"),
            );
            let b = wide::u8x16::new(
                right[offset..offset + 16]
                    .try_into()
                    .expect("complete SIMD lane"),
            );
            if !a.cmp_eq(b).all() {
                return false;
            }
            offset += 16;
        }
        left[offset..] == right[offset..]
    }
    #[cfg(not(any(
        target_arch = "aarch64",
        target_arch = "x86_64",
        all(target_arch = "x86", target_feature = "sse2"),
        all(target_arch = "wasm32", target_feature = "simd128")
    )))]
    {
        left == right
    }
}

impl NativeScene {
    /// A private nested-picture cache changes scope only on an actual owner or
    /// resource-generation collision. Missing prior capture has no collision.
    pub fn picture_capture_changes_resource_scope(&self, previous: Option<&Self>) -> bool {
        previous.is_some_and(|prior| {
            prior.envelope().header().scene_id != self.envelope().header().scene_id
                || self.admit_resource_generations_after(prior).is_err()
        })
    }
}

const FNV_OFFSET: u64 = 14695981039346656037;
fn append(mut hash: u64, bytes: &[u8]) -> u64 {
    // FNV is one ordered dependency chain, not an independent data-parallel loop.
    for &byte in bytes {
        hash = (hash ^ u64::from(byte)).wrapping_mul(1099511628211);
    }
    hash
}
fn finish(hash: u64) -> u64 {
    if hash == 0 {
        1
    } else {
        hash
    }
}
fn record<T: WireRecord>(hash: u64, value: T) -> u64 {
    // Only original scalar/prefix records of at most 64 bytes are hashed here.
    let mut bytes = [0_u8; 64];
    value
        .write_into(&mut bytes)
        .expect("fixed native hash record");
    append(hash, &bytes[..T::WIRE_SIZE])
}
fn read<T: WireRecord>(bytes: &[u8]) -> T {
    T::read(bytes).expect("admitted native prefix")
}
fn resource_identity(mut hash: u64, r: wire::Resource) -> u64 {
    hash = record(hash, r.kind);
    hash = record(hash, r.flags);
    hash = record(hash, r.resource_id);
    hash = record(hash, r.generation);
    hash = record(hash, r.payload_size);
    record(hash, r.auxiliary_size)
}
fn reference(hash: u64, scene: &NativeScene, index: u32) -> u64 {
    let resource = scene.envelope().resource(index);
    let marker = if index == NO_INDEX {
        0_u32
    } else if resource.is_some() {
        1
    } else {
        2
    };
    let hash = record(hash, marker);
    if marker == 1 {
        resource_identity(hash, resource.expect("selected original resource").record)
    } else {
        hash
    }
}
fn analytic_resource(kind: u32) -> bool {
    matches!(
        kind,
        SCENE_RESOURCE_ANALYTIC_BATCH
            | SCENE_RESOURCE_GEOMETRY_BATCH
            | SCENE_RESOURCE_POINT_BATCH
            | SCENE_RESOURCE_VERTEX_MESH
            | SCENE_RESOURCE_STROKE_BATCH
    )
}
fn analytic_command(kind: u32) -> bool {
    matches!(
        kind,
        SCENE_COMMAND_DRAW_ANALYTIC
            | SCENE_COMMAND_DRAW_GEOMETRY
            | SCENE_COMMAND_DRAW_POINT_BATCH
            | SCENE_COMMAND_DRAW_VERTEX_MESH
            | SCENE_COMMAND_DRAW_SOURCE_COVERAGE
            | SCENE_COMMAND_DRAW_STROKE_BATCH
    )
}
fn three_d_resource(kind: u32) -> bool {
    matches!(
        kind,
        SCENE_RESOURCE_LINE_3D_BATCH | SCENE_RESOURCE_MESH_3D_BATCH
    )
}
fn three_d_command(kind: u32) -> bool {
    matches!(
        kind,
        SCENE_COMMAND_DRAW_LINE_3D_BATCH | SCENE_COMMAND_DRAW_MESH_3D_BATCH
    )
}
