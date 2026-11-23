//! Immutable original side-buffer and retained-command generation.
use super::{
    ArchiveCommand, MAX_COMMANDS, Result,
    io::{Reader, Writer},
};
use std::sync::Arc;

/// Original picture data, separate from GPU compilation and target ownership.
/// Clones share all arrays and complete nested resource generations. Nullable
/// command arrays retain null separately; these four picture buffers are required.
#[derive(Clone, Debug, Default)]
pub struct ArchivePicture {
    pub points: Arc<[[f32; 2]]>,
    pub doubles: Arc<[f64]>,
    pub lines_3d: Arc<[[[f32; 3]; 2]]>,
    pub floats: Arc<[f32]>,
    pub commands: Arc<[ArchiveCommand]>,
}
impl ArchivePicture {
    /// Borrow one actual retained command without materializing a second array.
    pub fn command(&self, index: usize) -> Option<&ArchiveCommand> {
        self.commands.get(index)
    }
    pub fn command_count(&self) -> usize {
        self.commands.len()
    }
    /// Exact storage identity, independent of equality of command contents.
    pub fn shares_command_storage(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.commands, &other.commands)
    }
    /// Replace only commands; all original side-buffer generations stay owned.
    pub fn with_commands(&self, commands: Arc<[ArchiveCommand]>) -> Self {
        Self {
            commands,
            ..self.clone()
        }
    }
    pub fn points(&self, offset: i32, count: i32) -> Result<&[[f32; 2]]> {
        buffer_range(&self.points, offset, count)
    }
    pub fn doubles(&self, offset: i32, count: i32) -> Result<&[f64]> {
        buffer_range(&self.doubles, offset, count)
    }
    pub fn lines_3d(&self, offset: i32, count: i32) -> Result<&[[[f32; 3]; 2]]> {
        buffer_range(&self.lines_3d, offset, count)
    }
    pub fn floats(&self, offset: i32, count: i32) -> Result<&[f32]> {
        buffer_range(&self.floats, offset, count)
    }
    /// Original operation counting replaces DrawPicture by its nested command
    /// count only when requested. Saturation retains the original signed limit.
    pub fn approximate_operation_count(&self, include_nested: bool) -> Result<i32> {
        self.operation_count_at(include_nested, 0)
    }
    fn operation_count_at(&self, nested: bool, level: u32) -> Result<i32> {
        super::depth(level)?;
        if !nested {
            return Ok(self.commands.len().min(i32::MAX as usize) as i32);
        }
        let mut count = 0i32;
        for command in self.commands.iter() {
            let add = match (&command.kind, &command.picture) {
                (super::ArchiveCommandKind::DrawPicture, Some(picture)) => {
                    picture.operation_count_at(true, level + 1)?
                }
                _ => 1,
            };
            count = count.saturating_add(add);
            if count == i32::MAX {
                break;
            }
        }
        Ok(count)
    }
    /// Actual Rust owner/command storage plus original variable payloads. This
    /// diagnostic is intentionally not managed sizeof/packed-storage parity.
    pub fn approximate_bytes_used(&self) -> Result<i32> {
        self.bytes_at(0)
    }
    fn bytes_at(&self, level: u32) -> Result<i32> {
        super::depth(level)?;
        let mut bytes = std::mem::size_of::<Self>()
            .saturating_add(std::mem::size_of_val(self.commands.as_ref()))
            .saturating_add(std::mem::size_of_val(self.points.as_ref()))
            .saturating_add(std::mem::size_of_val(self.doubles.as_ref()))
            .saturating_add(std::mem::size_of_val(self.lines_3d.as_ref()))
            .saturating_add(std::mem::size_of_val(self.floats.as_ref()));
        for command in self.commands.iter() {
            bytes = bytes.saturating_add(command.text.as_ref().map_or(0, String::len));
            bytes = bytes.saturating_add(payload_bytes(&command.polyline_points));
            bytes = bytes.saturating_add(payload_bytes(&command.spline_knots));
            bytes = bytes.saturating_add(payload_bytes(&command.spline_weights));
            bytes = bytes.saturating_add(payload_bytes(&command.gpu_points));
            bytes = bytes.saturating_add(payload_bytes(&command.glyph_indices));
            bytes = bytes.saturating_add(payload_bytes(&command.glyph_positions));
            if command.kind == super::ArchiveCommandKind::DrawPicture {
                if let Some(picture) = &command.picture {
                    bytes = bytes.saturating_add(picture.bytes_at(level + 1)? as usize);
                }
            }
            if bytes >= i32::MAX as usize {
                return Ok(i32::MAX);
            }
        }
        Ok(bytes.min(i32::MAX as usize) as i32)
    }
    pub(super) fn read(reader: &mut Reader<'_>, level: u32) -> Result<Self> {
        super::depth(level)?;
        let points = reader.array()?.into();
        let doubles = reader.array()?.into();
        let lines_3d = reader.array()?.into();
        let floats = reader.array()?.into();
        let count = reader.count(MAX_COMMANDS)?;
        // Every original command contains two complete 64-byte matrices plus
        // kind and hit-test words, independently of version and optional data.
        // Reject impossible complete inventories before allocating large owners.
        let minimum = count
            .checked_mul(136)
            .ok_or(super::ArchiveError::Capacity)?;
        if minimum > reader.remaining() {
            return Err(super::ArchiveError::Truncated);
        }
        let mut commands = super::reserve(count)?;
        for _ in 0..count {
            commands.push(ArchiveCommand::read(reader, level)?);
        }
        Ok(Self {
            points,
            doubles,
            lines_3d,
            floats,
            commands: commands.into(),
        })
    }
    pub(super) fn write(&self, writer: &mut Writer, level: u32) -> Result<()> {
        super::depth(level)?;
        writer.array(&self.points)?;
        writer.array(&self.doubles)?;
        writer.array(&self.lines_3d)?;
        writer.array(&self.floats)?;
        writer.count(self.commands.len(), MAX_COMMANDS)?;
        for command in self.commands.iter() {
            command.write(writer, level)?;
        }
        Ok(())
    }
}

fn buffer_range<T>(values: &[T], offset: i32, count: i32) -> Result<&[T]> {
    let offset = usize::try_from(offset).map_err(|_| super::ArchiveError::InvalidData)?;
    let count = usize::try_from(count).map_err(|_| super::ArchiveError::InvalidData)?;
    let end = offset
        .checked_add(count)
        .ok_or(super::ArchiveError::InvalidData)?;
    values
        .get(offset..end)
        .ok_or(super::ArchiveError::InvalidData)
}
fn payload_bytes<T>(values: &Option<Arc<[T]>>) -> usize {
    values
        .as_ref()
        .map_or(0, |v| std::mem::size_of_val(v.as_ref()))
}
