//! Original append-only reuse admission; the full color-only replay cannot
//! preserve independent 3D depth history or external image mutations.
use super::*;
impl NativeScene {
    pub fn append_only_suffix(&self, previous: &Self) -> Option<u32> {
        let old = previous.envelope().header();
        let next = self.envelope().header();
        if old.scene_id != next.scene_id
            || old.flags != next.flags
            || next.generation < old.generation
            || old.resource_count > next.resource_count
            || old.command_count > next.command_count
        {
            return None;
        }
        for (i, current) in self.envelope().resources().enumerate() {
            if current.record.flags & EXTERNAL_IMAGE != 0 {
                return None;
            }
            if i >= old.resource_count as usize {
                continue;
            }
            let prior = previous.envelope().resource(i as u32)?;
            let append_table = matches!(
                prior.record.kind,
                SCENE_RESOURCE_BRUSH_TABLE | SCENE_RESOURCE_TEXT_STYLE_TABLE
            );
            if prior.payload.len() > current.payload.len()
                || prior.auxiliary.len() > current.auxiliary.len()
                || (!append_table
                    && (prior.payload.len() != current.payload.len()
                        || prior.auxiliary.len() != current.auxiliary.len()))
                || !scene_bytes_equal(prior.payload, &current.payload[..prior.payload.len()])
                || !scene_bytes_equal(prior.auxiliary, &current.auxiliary[..prior.auxiliary.len()])
            {
                return None;
            }
            let normalize = |r: wire::Resource| wire::Resource {
                payload_offset: 0,
                auxiliary_offset: 0,
                payload_size: 0,
                auxiliary_size: 0,
                ..r
            };
            if !same_record(normalize(prior.record), normalize(current.record)) {
                return None;
            }
        }
        for (i, current) in self.commands().enumerate() {
            if three_d_command(current.record.kind) {
                return None;
            }
            if i >= old.command_count as usize {
                continue;
            }
            let prior = previous.envelope().command(i as u32)?;
            if !scene_bytes_equal(prior.payload, current.payload) {
                return None;
            }
            let normalize = |c: wire::Command| wire::Command {
                payload_offset: 0,
                ..c
            };
            if !same_record(normalize(prior.record), normalize(current.record)) {
                return None;
            }
        }
        Some(old.command_count)
    }
}
fn same_record<T: WireRecord>(left: T, right: T) -> bool {
    let mut a = [0_u8; 64];
    let mut b = [0_u8; 64];
    left.write_into(&mut a).expect("fixed original prefix");
    right.write_into(&mut b).expect("fixed original prefix");
    scene_bytes_equal(&a[..T::WIRE_SIZE], &b[..T::WIRE_SIZE])
}
