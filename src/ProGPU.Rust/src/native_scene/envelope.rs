//! Original stream framing, raw descriptor inventories and generation comparison.
use super::*;
use NativeSceneValidationError as E;
fn fits(offset: u32, size: u32, total: u32) -> bool {
    offset <= total && size <= total - offset
}
fn table_fits(offset: u32, count: u32, stride: u32, total: u32) -> bool {
    offset <= total
        && stride != 0
        && u64::from(count) * u64::from(stride) <= u64::from(total - offset)
}
fn overlaps(a: u32, n: u32, b: u32, m: u32) -> bool {
    n != 0
        && m != 0
        && u64::from(a) < u64::from(b) + u64::from(m)
        && u64::from(b) < u64::from(a) + u64::from(n)
}
pub(super) fn arena(offset: u32, size: u32, h: &wire::Header) -> bool {
    if size == 0 {
        return offset == 0;
    }
    offset >= h.arena_offset
        && fits(offset, size, h.total_size)
        && u64::from(offset) + u64::from(size)
            <= u64::from(h.arena_offset) + u64::from(h.arena_size)
}
pub(super) fn read_header(bytes: &[u8]) -> TransportResult<wire::Header> {
    if cfg!(target_endian = "big")
        || bytes.len() < wire::Header::WIRE_SIZE
        || bytes.len() > MAX_STREAM_BYTES as usize
    {
        return Err(error(E::Header, 0));
    }
    let h = wire::Header::read(&bytes).ok_or_else(|| error(E::Header, 0))?;
    if h.struct_size < wire::Header::WIRE_SIZE as u32
        || h.struct_size as usize > bytes.len()
        || h.magic != STREAM_MAGIC
        || h.stream_version != STREAM_VERSION
        || h.endian_marker != ENDIAN_MARKER
        || h.flags != 0
        || h.scene_id == 0
        || h.generation == 0
        || h.total_size as usize != bytes.len()
        || h.reserved0 != 0
        || h.reserved1 != 0
    {
        return Err(error(E::Header, 0));
    }
    if h.command_count > MAX_COMMANDS
        || h.resource_count > MAX_RESOURCES
        || h.command_stride < wire::Command::WIRE_SIZE as u32
        || h.resource_stride < wire::Resource::WIRE_SIZE as u32
        || h.command_stride & 7 != 0
        || h.resource_stride & 7 != 0
        || h.command_offset & 7 != 0
        || h.resource_offset & 7 != 0
        || h.arena_offset & 7 != 0
        || h.command_offset < h.struct_size
        || h.resource_offset < h.struct_size
        || h.arena_offset < h.struct_size
        || !table_fits(
            h.command_offset,
            h.command_count,
            h.command_stride,
            h.total_size,
        )
        || !table_fits(
            h.resource_offset,
            h.resource_count,
            h.resource_stride,
            h.total_size,
        )
        || !fits(h.arena_offset, h.arena_size, h.total_size)
    {
        return Err(error(E::Range, 0));
    }
    let command_bytes = h.command_count * h.command_stride;
    let resource_bytes = h.resource_count * h.resource_stride;
    if overlaps(
        h.command_offset,
        command_bytes,
        h.resource_offset,
        resource_bytes,
    ) || overlaps(
        h.command_offset,
        command_bytes,
        h.arena_offset,
        h.arena_size,
    ) || overlaps(
        h.resource_offset,
        resource_bytes,
        h.arena_offset,
        h.arena_size,
    ) {
        return Err(error(E::Range, 0));
    }
    Ok(h)
}
#[derive(Clone, Debug)]
pub struct NativeSceneEnvelope {
    bytes: Arc<[u8]>,
    header: wire::Header,
}
impl NativeSceneEnvelope {
    pub(super) fn from_admitted(bytes: Arc<[u8]>, header: wire::Header) -> Self {
        Self { bytes, header }
    }

    /// Retain one immutable stream after original header, table/arena, raw
    /// descriptor, ID and generic flag admission. Resource contents, command
    /// references, scope semantics and target/device policy are NOT admitted.
    pub fn new(bytes: impl Into<Arc<[u8]>>) -> TransportResult<Self> {
        let bytes = bytes.into();
        let h = read_header(&bytes)?;
        let result = Self { bytes, header: h };
        let mut previous_id = 0;
        for index in 0..h.resource_count {
            let offset = h.resource_offset + index * h.resource_stride;
            let resource = wire::Resource::read(&result.bytes[offset as usize..])
                .ok_or_else(|| error(E::Record, offset))?;
            let flags = resource.flags;
            if resource.struct_size < wire::Resource::WIRE_SIZE as u32
                || resource.struct_size > h.resource_stride
                || flags
                    & !(RECORD_REQUIRED
                        | COLOR_GLYPH_BITMAPS
                        | EXTERNAL_IMAGE
                        | IMAGE_BGRA8
                        | IMAGE_R8
                        | IMAGE_PICTURE)
                    != 0
                || resource.reserved != 0
                || resource.resource_id == 0
                || !arena(resource.payload_offset, resource.payload_size, &h)
                || !arena(resource.auxiliary_offset, resource.auxiliary_size, &h)
            {
                return Err(error(E::Record, offset));
            }
            if flags & COLOR_GLYPH_BITMAPS != 0
                && resource.kind != NativeSceneResourceKind::GlyphRun as u32
            {
                return Err(error(E::Record, offset));
            }
            let image = resource.kind == NativeSceneResourceKind::Image as u32;
            let external = flags & EXTERNAL_IMAGE != 0;
            let bgra = flags & IMAGE_BGRA8 != 0;
            let r8 = flags & IMAGE_R8 != 0;
            if flags & IMAGE_PICTURE != 0
                && (external
                    || bgra
                    || r8
                    || !image
                    || resource.auxiliary_size < wire::Header::WIRE_SIZE as u32)
            {
                return Err(error(E::Value, offset));
            }
            if external
                && (!image
                    || resource.payload_size != 0
                    || resource.auxiliary_size != 0
                    || bgra
                    || r8)
                || bgra && r8
                || (bgra || r8) && !image
            {
                return Err(error(E::Record, offset));
            }
            if resource.resource_id <= previous_id {
                return Err(error(E::Id, offset));
            }
            if resource.generation == 0 {
                return Err(error(E::Generation, offset));
            }
            let known = NativeSceneResourceKind::try_from(resource.kind).is_ok();
            if !known && flags & RECORD_REQUIRED != 0 {
                return Err(error(E::Unsupported, offset));
            }
            if known && resource.payload_size == 0 && !external {
                return Err(error(E::Record, offset));
            }
            previous_id = resource.resource_id;
        }
        result.unique_command_ids()?;
        for index in 0..h.command_count {
            let offset = h.command_offset + index * h.command_stride;
            let command = wire::Command::read(&result.bytes[offset as usize..])
                .ok_or_else(|| error(E::Record, offset))?;
            if command.struct_size < wire::Command::WIRE_SIZE as u32
                || command.struct_size > h.command_stride
                || command.flags & !(RECORD_REQUIRED | GLYPH_STYLED) != 0
                || command.reserved != 0
                || command.command_id == 0
                || command.reserved0 != 0
                || command.reserved1 != 0
                || !arena(command.payload_offset, command.payload_size, &h)
                || ![
                    command.bounds_x,
                    command.bounds_y,
                    command.bounds_width,
                    command.bounds_height,
                ]
                .iter()
                .all(|v| v.is_finite())
                || command.bounds_width < 0.0
                || command.bounds_height < 0.0
            {
                return Err(error(E::Record, offset));
            }
            if NativeSceneCommandKind::try_from(command.kind).is_err()
                && command.flags & RECORD_REQUIRED != 0
            {
                return Err(error(E::Unsupported, offset));
            }
        }
        Ok(result)
    }
    /// Full original snapshot, including extension bytes, gaps and overlapping
    /// payload spans. Envelope admission never rewrites or deduplicates bytes.
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }
    pub fn owner(&self) -> &Arc<[u8]> {
        &self.bytes
    }
    pub fn header(&self) -> wire::Header {
        self.header
    }
    pub fn resources(&self) -> Resources<'_> {
        Resources {
            source: self,
            next: 0,
        }
    }
    pub fn commands(&self) -> Commands<'_> {
        Commands {
            source: self,
            next: 0,
        }
    }
    pub fn resource(&self, index: u32) -> Option<ResourceView<'_>> {
        let h = self.header;
        if index >= h.resource_count {
            return None;
        }
        let offset = h.resource_offset + index * h.resource_stride;
        let raw = self
            .bytes
            .get(offset as usize..(offset + h.resource_stride) as usize)?;
        let record = wire::Resource::read(raw)?;
        Some(ResourceView {
            record,
            offset,
            raw,
            payload: &self.bytes[record.payload_offset as usize
                ..(record.payload_offset + record.payload_size) as usize],
            auxiliary: &self.bytes[record.auxiliary_offset as usize
                ..(record.auxiliary_offset + record.auxiliary_size) as usize],
        })
    }
    pub fn command(&self, index: u32) -> Option<CommandView<'_>> {
        let h = self.header;
        if index >= h.command_count {
            return None;
        }
        let offset = h.command_offset + index * h.command_stride;
        let raw = self
            .bytes
            .get(offset as usize..(offset + h.command_stride) as usize)?;
        let record = wire::Command::read(raw)?;
        Some(CommandView {
            record,
            offset,
            raw,
            payload: &self.bytes[record.payload_offset as usize
                ..(record.payload_offset + record.payload_size) as usize],
        })
    }
    /// Original merge walk: same-ID generations cannot regress; equal generations
    /// require exact kind/flags/sizes and payload/auxiliary bytes. Offsets and
    /// unrelated scene/command identities are not substituted for that contract.
    pub fn admit_resource_generations_after(&self, previous: &Self) -> TransportResult<()> {
        let mut old = previous.resources().peekable();
        let mut new = self.resources().peekable();
        while let (Some(a), Some(b)) = (old.peek(), new.peek()) {
            match a.record.resource_id.cmp(&b.record.resource_id) {
                std::cmp::Ordering::Less => {
                    old.next();
                }
                std::cmp::Ordering::Greater => {
                    new.next();
                }
                std::cmp::Ordering::Equal => {
                    if b.record.generation < a.record.generation
                        || b.record.generation == a.record.generation
                            && (b.record.kind != a.record.kind
                                || b.record.flags != a.record.flags
                                || b.payload != a.payload
                                || b.auxiliary != a.auxiliary)
                    {
                        return Err(error(E::Generation, b.offset));
                    }
                    old.next();
                    new.next();
                }
            }
        }
        Ok(())
    }
    pub(super) fn unique_command_ids(&self) -> TransportResult<()> {
        unique_command_ids(&self.bytes, self.header)
    }
}
#[derive(Clone, Copy, Debug)]
pub struct ResourceView<'a> {
    pub record: wire::Resource,
    pub offset: u32,
    pub raw: &'a [u8],
    pub payload: &'a [u8],
    pub auxiliary: &'a [u8],
}
#[derive(Clone, Copy, Debug)]
pub struct CommandView<'a> {
    pub record: wire::Command,
    pub offset: u32,
    pub raw: &'a [u8],
    pub payload: &'a [u8],
}
pub struct Resources<'a> {
    source: &'a NativeSceneEnvelope,
    next: u32,
}
impl<'a> Iterator for Resources<'a> {
    type Item = ResourceView<'a>;
    fn next(&mut self) -> Option<Self::Item> {
        let result = self.source.resource(self.next)?;
        self.next += 1;
        Some(result)
    }
    fn size_hint(&self) -> (usize, Option<usize>) {
        let n = (self.source.header.resource_count - self.next) as usize;
        (n, Some(n))
    }
}
impl ExactSizeIterator for Resources<'_> {}
impl std::iter::FusedIterator for Resources<'_> {}
pub struct Commands<'a> {
    source: &'a NativeSceneEnvelope,
    next: u32,
}
impl<'a> Iterator for Commands<'a> {
    type Item = CommandView<'a>;
    fn next(&mut self) -> Option<Self::Item> {
        let result = self.source.command(self.next)?;
        self.next += 1;
        Some(result)
    }
    fn size_hint(&self) -> (usize, Option<usize>) {
        let n = (self.source.header.command_count - self.next) as usize;
        (n, Some(n))
    }
}
impl ExactSizeIterator for Commands<'_> {}
impl std::iter::FusedIterator for Commands<'_> {}

pub(super) fn unique_command_ids(bytes: &[u8], h: wire::Header) -> TransportResult<()> {
    if h.command_count < 2 {
        return Ok(());
    }
    let mut current = reserve(h.command_count as usize, h.command_offset)?;
    let mut scratch = reserve(h.command_count as usize, h.command_offset)?;
    for index in 0..h.command_count {
        let offset = h.command_offset + index * h.command_stride;
        current.push(
            wire::Command::read(&bytes[offset as usize..])
                .ok_or_else(|| error(E::Record, offset))?
                .command_id,
        );
    }
    scratch.resize(current.len(), 0u64);
    // Original eight stable byte-radix passes; no hash or source-ID rewrite.
    for pass in 0..8 {
        let mut counts = [0usize; 256];
        let shift = pass * 8;
        for &id in &current {
            counts[(id >> shift) as u8 as usize] += 1;
        }
        let mut position = 0;
        for count in &mut counts {
            let next = position + *count;
            *count = position;
            position = next;
        }
        for &id in &current {
            let bucket = (id >> shift) as u8 as usize;
            scratch[counts[bucket]] = id;
            counts[bucket] += 1;
        }
        std::mem::swap(&mut current, &mut scratch);
    }
    if current.windows(2).any(|v| v[0] == v[1]) {
        return Err(error(E::Id, h.command_offset));
    }
    Ok(())
}
