//! Canonical native header/descriptor/arena serialization from immutable sources.
//! Semantic producers supply actual resolved resource and command payload bytes.
use super::*;
use NativeSceneValidationError as E;
#[derive(Clone, Debug)]
pub struct NativeSceneResourceSource {
    pub record: wire::Resource,
    pub payload: Arc<[u8]>,
    pub auxiliary: Arc<[u8]>,
}
impl NativeSceneResourceSource {
    pub fn new(
        kind: NativeSceneResourceKind,
        identity: u64,
        generation: u64,
        payload: impl Into<Arc<[u8]>>,
        auxiliary: impl Into<Arc<[u8]>>,
    ) -> Self {
        Self {
            record: wire::Resource {
                struct_size: wire::Resource::WIRE_SIZE as u32,
                kind: kind as u32,
                flags: RECORD_REQUIRED,
                resource_id: identity,
                generation,
                ..Default::default()
            },
            payload: payload.into(),
            auxiliary: auxiliary.into(),
        }
    }
}
#[derive(Clone, Debug)]
pub struct NativeSceneCommandSource {
    pub record: wire::Command,
    pub payload: Arc<[u8]>,
}
impl NativeSceneCommandSource {
    pub fn new(kind: NativeSceneCommandKind, identity: u64, payload: impl Into<Arc<[u8]>>) -> Self {
        Self {
            record: wire::Command {
                struct_size: wire::Command::WIRE_SIZE as u32,
                kind: kind as u32,
                flags: RECORD_REQUIRED,
                command_id: identity,
                state_index: NO_INDEX,
                resource_index: NO_INDEX,
                ..Default::default()
            },
            payload: payload.into(),
        }
    }
}
/// Complete owned resolved packet, before its canonical pointer-free encoding.
/// Source records are never edited to insert offsets; each output uses local
/// descriptor copies and retains the exact input float bits and payload bytes.
#[derive(Clone, Debug)]
pub struct NativeScenePacket {
    pub scene_id: u64,
    pub generation: u64,
    pub resources: Arc<[NativeSceneResourceSource]>,
    pub commands: Arc<[NativeSceneCommandSource]>,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSceneStreamLayout {
    pub command_offset: u32,
    pub resource_offset: u32,
    pub arena_offset: u32,
    pub total_size: u32,
    pub maximum_stack_depth: u32,
}
fn align8(value: u64) -> u64 {
    (value + 7) & !7
}
fn append_size(cursor: &mut u64, size: usize) -> TransportResult<()> {
    if size == 0 {
        return Ok(());
    }
    *cursor = align8(*cursor);
    if *cursor > u64::from(MAX_STREAM_BYTES) || size as u64 > u64::from(MAX_STREAM_BYTES) - *cursor
    {
        return Err(error(E::Range, 0));
    }
    *cursor += size as u64;
    Ok(())
}
impl NativeScenePacket {
    /// Original byte accounting and canonical table placement. No payload is
    /// dereferenced as a foreign typed object and no provider callback is invoked.
    pub fn layout(&self) -> TransportResult<NativeSceneStreamLayout> {
        if self.scene_id == 0 || self.generation == 0 {
            return Err(error(E::Header, 0));
        }
        if self.commands.len() > MAX_COMMANDS as usize
            || self.resources.len() > MAX_RESOURCES as usize
        {
            return Err(error(E::Range, 0));
        }
        let command_offset = align8(wire::Header::WIRE_SIZE as u64);
        let resource_offset =
            align8(command_offset + self.commands.len() as u64 * wire::Command::WIRE_SIZE as u64);
        let arena_offset = align8(
            resource_offset + self.resources.len() as u64 * wire::Resource::WIRE_SIZE as u64,
        );
        let mut cursor = arena_offset;
        let mut previous_id = 0;
        for (index, source) in self.resources.iter().enumerate() {
            let at = (resource_offset + index as u64 * wire::Resource::WIRE_SIZE as u64) as u32;
            if source.record.resource_id <= previous_id {
                return Err(error(E::Id, at));
            }
            if source.record.generation == 0 {
                return Err(error(E::Generation, at));
            }
            if source.record.struct_size != wire::Resource::WIRE_SIZE as u32 {
                return Err(error(E::Record, at));
            }
            previous_id = source.record.resource_id;
            append_size(&mut cursor, source.payload.len())?;
            append_size(&mut cursor, source.auxiliary.len())?;
        }
        let mut stack = [0u8; MAX_STACK_DEPTH as usize];
        let mut depth = 0usize;
        let mut maximum_depth = 0usize;
        for (index, source) in self.commands.iter().enumerate() {
            let at = (command_offset + index as u64 * wire::Command::WIRE_SIZE as u64) as u32;
            if source.record.struct_size != wire::Command::WIRE_SIZE as u32 {
                return Err(error(E::Record, at));
            }
            append_size(&mut cursor, source.payload.len())?;
            match NativeSceneCommandKind::try_from(source.record.kind) {
                Ok(NativeSceneCommandKind::Save | NativeSceneCommandKind::PushLayer) => {
                    if depth == stack.len() {
                        return Err(error(E::Stack, at));
                    }
                    stack[depth] = if source.record.kind == NativeSceneCommandKind::Save as u32 {
                        1
                    } else {
                        2
                    };
                    depth += 1;
                    maximum_depth = maximum_depth.max(depth);
                }
                Ok(NativeSceneCommandKind::Restore | NativeSceneCommandKind::PopLayer) => {
                    let expected = if source.record.kind == NativeSceneCommandKind::Restore as u32 {
                        1
                    } else {
                        2
                    };
                    if depth == 0 || stack[depth - 1] != expected {
                        return Err(error(E::Stack, at));
                    }
                    depth -= 1;
                }
                _ => {}
            }
        }
        if depth != 0 {
            return Err(error(E::Stack, resource_offset as u32));
        }
        if cursor > u64::from(MAX_STREAM_BYTES) {
            return Err(error(E::Range, 0));
        }
        Ok(NativeSceneStreamLayout {
            command_offset: command_offset as u32,
            resource_offset: resource_offset as u32,
            arena_offset: arena_offset as u32,
            total_size: cursor as u32,
            maximum_stack_depth: maximum_depth as u32,
        })
    }
    /// Build privately and admit framing before publication. This does not grant
    /// semantic/source/device admission or attach external native image handles.
    pub fn build(&self) -> TransportResult<NativeSceneEnvelope> {
        let layout = self.layout()?;
        let mut output = reserve(layout.total_size as usize, 0)?;
        output.resize(layout.total_size as usize, 0u8);
        let mut cursor = layout.arena_offset as usize;
        for (index, source) in self.resources.iter().enumerate() {
            let mut record = source.record;
            record.payload_offset = append(&mut output, &mut cursor, &source.payload);
            record.payload_size = source.payload.len() as u32;
            record.auxiliary_offset = append(&mut output, &mut cursor, &source.auxiliary);
            record.auxiliary_size = source.auxiliary.len() as u32;
            record.write_into(
                &mut output[layout.resource_offset as usize + index * wire::Resource::WIRE_SIZE..],
            )?;
        }
        for (index, source) in self.commands.iter().enumerate() {
            let mut record = source.record;
            record.payload_offset = append(&mut output, &mut cursor, &source.payload);
            record.payload_size = source.payload.len() as u32;
            record.write_into(
                &mut output[layout.command_offset as usize + index * wire::Command::WIRE_SIZE..],
            )?;
        }
        let header = wire::Header {
            struct_size: wire::Header::WIRE_SIZE as u32,
            magic: STREAM_MAGIC,
            stream_version: STREAM_VERSION,
            endian_marker: ENDIAN_MARKER,
            total_size: layout.total_size,
            scene_id: self.scene_id,
            generation: self.generation,
            command_offset: layout.command_offset,
            command_count: self.commands.len() as u32,
            command_stride: wire::Command::WIRE_SIZE as u32,
            resource_offset: layout.resource_offset,
            resource_count: self.resources.len() as u32,
            resource_stride: wire::Resource::WIRE_SIZE as u32,
            arena_offset: layout.arena_offset,
            arena_size: layout.total_size - layout.arena_offset,
            ..Default::default()
        };
        header.write_into(&mut output)?;
        NativeSceneEnvelope::new(Arc::<[u8]>::from(output))
    }
    /// Atomic caller copy from a completely built candidate. Retain build()'s
    /// envelope when multiple consumers need the same generation without copying.
    pub fn build_into(&self, destination: &mut [u8]) -> TransportResult<usize> {
        let candidate = self.build()?;
        let length = candidate.bytes().len();
        if destination.len() < length {
            return Err(error(E::Range, 0));
        }
        destination[..length].copy_from_slice(candidate.bytes());
        Ok(length)
    }
}
fn append(destination: &mut [u8], cursor: &mut usize, source: &[u8]) -> u32 {
    if source.is_empty() {
        return 0;
    }
    *cursor = align8(*cursor as u64) as usize;
    let offset = *cursor;
    destination[offset..offset + source.len()].copy_from_slice(source);
    *cursor += source.len();
    offset as u32
}
