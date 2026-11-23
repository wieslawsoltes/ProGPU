//! One deterministic, allocation-free write into caller storage after complete
//! layout preflight. Source-only balanced scopes retain original command IDs.
use super::*;

#[derive(Clone, Copy)]
struct Layout {
    header: wire::Header,
    metrics: NativeSceneBuildMetrics,
}

impl NativeSceneBuilder {
    pub fn required_stream_size(&self) -> BuildResult<usize> {
        Ok(self.measure_stream()?.header.total_size as usize)
    }

    /// Fresh independent bytes and exact serialized counts. The builder's
    /// reserve hint affects allocation capacity only, never stream identity.
    pub fn build(&self) -> BuildResult<(Vec<u8>, NativeSceneBuildMetrics)> {
        let layout = self.measure_stream()?;
        let maximum = u64::from(MAX_STREAM_BYTES);
        let arena = u64::from(layout.header.arena_offset);
        let reserved = if self.arena_reserve > maximum - arena {
            maximum
        } else {
            arena + self.arena_reserve
        };
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(reserved.max(u64::from(layout.header.total_size)) as usize)
            .map_err(|_| B::OutOfMemory)?;
        bytes.resize(layout.header.total_size as usize, 0);
        self.write_stream(layout, &mut bytes);
        Ok((bytes, layout.metrics))
    }

    /// On failure the complete caller destination is unchanged. On success
    /// bytes after `metrics.stream_bytes` are untouched. No private byte copy or
    /// payload-sized allocation is needed by this path.
    pub fn build_into(&self, destination: &mut [u8]) -> BuildResult<NativeSceneBuildMetrics> {
        let layout = self.measure_stream()?;
        if destination.len() < layout.header.total_size as usize {
            return Err(B::CapacityExceeded);
        }
        self.write_stream(layout, destination);
        Ok(layout.metrics)
    }

    fn measure_stream(&self) -> BuildResult<Layout> {
        if self.scene_id == 0 || self.generation == 0 || self.stack_depth != 0 {
            return Err(if self.stack_depth == 0 {
                B::InvalidState
            } else {
                B::UnbalancedStack
            });
        }
        let mut previous = 0;
        for resource in &self.resources {
            if resource.record.resource_id <= previous || resource.record.generation == 0 {
                return Err(B::InvalidState);
            }
            previous = resource.record.resource_id;
        }
        if self.commands.len() > MAX_COMMANDS as usize
            || self.resources.len() > MAX_RESOURCES as usize
        {
            return Err(B::CapacityExceeded);
        }
        let command_count = self.render_commands().count() as u32;
        let command_offset = align8(wire::Header::WIRE_SIZE as u64);
        let resource_offset =
            align8(command_offset + u64::from(command_count) * wire::Command::WIRE_SIZE as u64);
        let arena_offset = align8(
            resource_offset + self.resources.len() as u64 * wire::Resource::WIRE_SIZE as u64,
        );
        let mut cursor = arena_offset;
        let mut append = |size: u64| -> BuildResult<()> {
            if size == 0 {
                return Ok(());
            }
            cursor = align8(cursor);
            if size > u64::from(u32::MAX)
                || cursor > u64::from(MAX_STREAM_BYTES)
                || size > u64::from(MAX_STREAM_BYTES) - cursor
            {
                return Err(B::CapacityExceeded);
            }
            cursor += size;
            Ok(())
        };
        for source in &self.resources {
            let payload = if source.brush_table {
                self.brushes.len() as u64 * wire::Brush::WIRE_SIZE as u64
            } else if source.text_style_table {
                self.text_styles.len() as u64 * wire::TextStyle::WIRE_SIZE as u64
            } else {
                source.payload.len() as u64
            };
            let auxiliary = if source.brush_table {
                self.gradient_stops.len() as u64 * wire::GradientStop::WIRE_SIZE as u64
            } else {
                source.auxiliary.len() as u64
            };
            append(payload)?;
            append(auxiliary)?;
        }
        let mut depth = 0_u32;
        let mut maximum_depth = 0;
        for command in self.render_commands() {
            append(command.payload.len() as u64)?;
            match command.record.kind {
                SCENE_COMMAND_SAVE | SCENE_COMMAND_PUSH_LAYER => {
                    depth += 1;
                    maximum_depth = maximum_depth.max(depth);
                }
                SCENE_COMMAND_RESTORE | SCENE_COMMAND_POP_LAYER => {
                    depth = depth.checked_sub(1).ok_or(B::UnbalancedStack)?;
                }
                _ => {}
            }
        }
        if cursor > u64::from(MAX_STREAM_BYTES) || cursor > u64::from(u32::MAX) {
            return Err(B::CapacityExceeded);
        }
        let header = wire::Header {
            struct_size: wire::Header::WIRE_SIZE as u32,
            magic: super::super::STREAM_MAGIC,
            stream_version: super::super::STREAM_VERSION,
            endian_marker: super::super::ENDIAN_MARKER,
            total_size: cursor as u32,
            scene_id: self.scene_id,
            generation: self.generation,
            command_offset: command_offset as u32,
            command_count,
            command_stride: wire::Command::WIRE_SIZE as u32,
            resource_offset: resource_offset as u32,
            resource_count: self.resources.len() as u32,
            resource_stride: wire::Resource::WIRE_SIZE as u32,
            arena_offset: arena_offset as u32,
            arena_size: (cursor - arena_offset) as u32,
            ..Default::default()
        };
        let metrics = NativeSceneBuildMetrics {
            command_count,
            resource_count: header.resource_count,
            brush_count: self.brushes.len() as u32,
            text_style_count: self.text_styles.len() as u32,
            maximum_stack_depth: maximum_depth,
            arena_bytes: u64::from(header.arena_size),
            stream_bytes: cursor,
        };
        Ok(Layout { header, metrics })
    }

    fn write_stream(&self, layout: Layout, destination: &mut [u8]) {
        let header = layout.header;
        let destination = &mut destination[..header.total_size as usize];
        destination.fill(0);
        let mut cursor = header.arena_offset as usize;
        for (index, source) in self.resources.iter().enumerate() {
            let mut record = source.record;
            if source.brush_table {
                (record.payload_offset, record.payload_size) =
                    append_records(destination, &mut cursor, &self.brushes);
                (record.auxiliary_offset, record.auxiliary_size) =
                    append_records(destination, &mut cursor, &self.gradient_stops);
            } else {
                (record.payload_offset, record.payload_size) = if source.text_style_table {
                    append_records(destination, &mut cursor, &self.text_styles)
                } else {
                    append_bytes(destination, &mut cursor, &source.payload)
                };
                (record.auxiliary_offset, record.auxiliary_size) =
                    append_bytes(destination, &mut cursor, &source.auxiliary);
            }
            record
                .write_into(
                    &mut destination
                        [header.resource_offset as usize + index * wire::Resource::WIRE_SIZE..],
                )
                .expect("preflighted resource table");
        }
        for (index, source) in self.render_commands().enumerate() {
            let mut record = source.record;
            (record.payload_offset, record.payload_size) =
                append_bytes(destination, &mut cursor, &source.payload);
            record
                .write_into(
                    &mut destination
                        [header.command_offset as usize + index * wire::Command::WIRE_SIZE..],
                )
                .expect("preflighted command table");
        }
        header.write_into(destination).expect("preflighted header");
    }
}
fn align8(value: u64) -> u64 {
    (value + 7) & !7
}
fn allocate_span(cursor: &mut usize, size: usize) -> (u32, u32) {
    if size == 0 {
        return (0, 0);
    }
    let start = (*cursor + 7) & !7;
    *cursor = start + size;
    (start as u32, size as u32)
}
fn append_bytes(destination: &mut [u8], cursor: &mut usize, source: &[u8]) -> (u32, u32) {
    let result = allocate_span(cursor, source.len());
    if !source.is_empty() {
        destination[result.0 as usize..*cursor].copy_from_slice(source);
    }
    result
}
fn append_records<T: WireRecord>(
    destination: &mut [u8],
    cursor: &mut usize,
    source: &[T],
) -> (u32, u32) {
    let result = allocate_span(cursor, source.len() * T::WIRE_SIZE);
    if !source.is_empty() {
        write_records(source, &mut destination[result.0 as usize..*cursor])
            .expect("preflighted typed payload");
    }
    result
}
