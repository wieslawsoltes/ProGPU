//! Explicit typed recording of the original command and side-buffer contract.
mod materials;
use super::{
    ArchiveCommand, ArchiveError, ArchivePicture, MAX_ARRAY_ELEMENTS, MAX_COMMANDS, Result,
};

/// Original signed offset/count pair into one immutable picture buffer.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ArchiveBufferRange {
    pub offset: i32,
    pub count: i32,
}

/// Safe CPU recording owner for original raw commands. This is distinct from
/// Canvas/PictureRecorder, which lower paint and path APIs into retained draws.
/// Buffers are owned here until finish; published pictures never borrow a writer.
#[derive(Debug, Default)]
pub struct ArchiveRecorder {
    commands: Vec<ArchiveCommand>,
    points: Vec<[f32; 2]>,
    doubles: Vec<f64>,
    lines_3d: Vec<[[f32; 3]; 2]>,
    floats: Vec<f32>,
}
impl ArchiveRecorder {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn command_count(&self) -> usize {
        self.commands.len()
    }
    /// Record the complete original command, including inactive metadata.
    pub fn record(&mut self, command: ArchiveCommand) -> Result<()> {
        if self.commands.len() >= MAX_COMMANDS {
            return Err(ArchiveError::Capacity);
        }
        self.commands
            .try_reserve(1)
            .map_err(|_| ArchiveError::Capacity)?;
        self.commands.push(command);
        Ok(())
    }
    pub fn append_points(&mut self, values: &[[f32; 2]]) -> Result<ArchiveBufferRange> {
        append(&mut self.points, values)
    }
    pub fn append_doubles(&mut self, values: &[f64]) -> Result<ArchiveBufferRange> {
        append(&mut self.doubles, values)
    }
    pub fn append_lines_3d(&mut self, values: &[[[f32; 3]; 2]]) -> Result<ArchiveBufferRange> {
        append(&mut self.lines_3d, values)
    }
    pub fn append_floats(&mut self, values: &[f32]) -> Result<ArchiveBufferRange> {
        append(&mut self.floats, values)
    }
    /// Publish a complete immutable generation by ownership transfer. No GPU,
    /// font callback or serialization occurs; serialization admission is separate.
    pub fn finish(&mut self) -> ArchivePicture {
        let data = std::mem::take(self);
        ArchivePicture {
            commands: data.commands.into(),
            points: data.points.into(),
            doubles: data.doubles.into(),
            lines_3d: data.lines_3d.into(),
            floats: data.floats.into(),
        }
    }
    /// Release actual retained source owners without executing or flushing them.
    pub fn clear(&mut self) {
        *self = Self::default();
    }
}
fn append<T: Copy>(destination: &mut Vec<T>, values: &[T]) -> Result<ArchiveBufferRange> {
    let end = destination
        .len()
        .checked_add(values.len())
        .ok_or(ArchiveError::Capacity)?;
    if end > MAX_ARRAY_ELEMENTS {
        return Err(ArchiveError::Capacity);
    }
    destination
        .try_reserve(values.len())
        .map_err(|_| ArchiveError::Capacity)?;
    let range = ArchiveBufferRange {
        offset: destination.len() as i32,
        count: values.len() as i32,
    };
    destination.extend_from_slice(values);
    Ok(range)
}
