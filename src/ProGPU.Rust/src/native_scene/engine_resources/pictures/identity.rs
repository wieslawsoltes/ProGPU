//! Shared original picture-raster eligibility and byte identity.
use super::*;
pub(crate) fn descriptor(s: &NativePictureMaskSource) -> wire::PictureImage {
    wire::PictureImage {
        struct_size: wire::PictureImage::WIRE_SIZE as u32,
        width: s.frame.width,
        height: s.frame.height,
        dpi_scale: s.frame.dpi_scale,
        clear_color: s.frame.clear_color,
        ..Default::default()
    }
}
pub(crate) fn same<T: WireRecord>(a: T, b: T) -> bool {
    let (mut left, mut right) = ([0u8; 80], [0u8; 80]);
    if T::WIRE_SIZE > left.len()
        || a.write_into(&mut left).is_err()
        || b.write_into(&mut right).is_err()
    {
        return false;
    }
    left[..T::WIRE_SIZE] == right[..T::WIRE_SIZE]
}
pub(crate) fn generation_independent(a: &NativeScene, b: &NativeScene) -> bool {
    let (mut first, mut second) = (a.envelope().header(), b.envelope().header());
    first.generation = 0;
    second.generation = 0;
    if !same(first, second) {
        return false;
    }
    for (left, right) in a.envelope().resources().zip(b.envelope().resources()) {
        let (mut left, mut right) = (left.record, right.record);
        left.generation = 0;
        right.generation = 0;
        if !same(left, right) {
            return false;
        }
    }
    for (left, right) in a.envelope().commands().zip(b.envelope().commands()) {
        if !same(left.record, right.record) {
            return false;
        }
    }
    let a = a.envelope().bytes();
    let b = b.envelope().bytes();
    let offset = first.arena_offset as usize;
    let end = offset + first.arena_size as usize;
    a.get(offset..end) == b.get(offset..end)
}
fn slice(bytes: &[u8], offset: u32, size: u32) -> Option<&[u8]> {
    let start = offset as usize;
    bytes.get(start..start.checked_add(size as usize)?)
}
pub(crate) fn external(bytes: &[u8], depth: u32) -> bool {
    if depth >= 64 {
        return true;
    }
    let Some(header) = wire::Header::read(bytes) else {
        return true;
    };
    for index in 0..header.resource_count {
        let Some(offset) = index
            .checked_mul(header.resource_stride)
            .and_then(|n| header.resource_offset.checked_add(n))
        else {
            return true;
        };
        let Some(record) =
            slice(bytes, offset, wire::Resource::WIRE_SIZE as u32).and_then(wire::Resource::read)
        else {
            return true;
        };
        if record.flags & SCENE_EXTERNAL_IMAGE != 0 {
            return true;
        }
        let Some(aux) = slice(bytes, record.auxiliary_offset, record.auxiliary_size) else {
            return true;
        };
        if record.flags & SCENE_IMAGE_PICTURE != 0 && external(aux, depth + 1) {
            return true;
        }
        if record.kind != SCENE_RESOURCE_LAYER_MASK {
            continue;
        }
        let Some(payload) = slice(bytes, record.payload_offset, record.payload_size) else {
            return true;
        };
        let Some(kind) = payload.get(4..).and_then(u32::read) else {
            return true;
        };
        if kind == SCENE_LAYER_MASK_PICTURE && external(aux, depth + 1) {
            return true;
        }
        if kind != SCENE_LAYER_MASK_COMPOSITE {
            continue;
        }
        let mut raw = [0u8; 64];
        let Some(dst) = raw.get_mut(..payload.len()) else {
            return true;
        };
        dst.copy_from_slice(payload);
        let Some(m) = wire::LayerCompositeMask::read(&raw) else {
            return true;
        };
        let offset = u64::from(m.brush_mask_count) * 320
            + u64::from(m.geometry_mask_count) * 336
            + u64::from(m.geometry_primitive_count) * 88;
        let stream_offset = offset + u64::from(m.picture_mask_count) * 72;
        let Ok(stream_offset) = usize::try_from(stream_offset) else {
            return true;
        };
        let Some(streams) = aux.get(stream_offset..) else {
            return true;
        };
        for i in 0..m.picture_mask_count {
            let Ok(offset) = usize::try_from(offset + u64::from(i) * 72) else {
                return true;
            };
            let Some(p) = aux.get(offset..).and_then(wire::LayerPictureMask::read) else {
                return true;
            };
            let Some(scene) = slice(streams, p.stream_offset, p.stream_size) else {
                return true;
            };
            if external(scene, depth + 1) {
                return true;
            }
        }
    }
    false
}
pub(crate) fn eligible(source: &NativePictureMaskSource) -> bool {
    source.scene.append_only_suffix(&source.scene)
        == Some(source.scene.envelope().header().command_count)
        && !external(source.scene.envelope().bytes(), 0)
}
