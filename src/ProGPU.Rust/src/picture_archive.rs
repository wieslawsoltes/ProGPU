//! Original versioned owned picture archive, independent of GPU/device handles.
use std::sync::Arc;
mod capture;
mod command;
mod geometry;
mod io;
mod material;
mod replay;
mod vector_draw;
pub(crate) use replay::drawing_error;
pub use replay::{ArchiveReplayOptions, ArchiveTextEnvironment};
pub use vector_draw::ArchiveVectorDraw;
mod picture;
mod recorder;
pub use command::{ArchiveCommand, ArchiveCommandKind, ArchiveTextureSampling};
pub use geometry::{ArchivePath, ArchiveTexturePatch, ArchiveTexturePatchKind, ArchiveVertexMesh};
pub use material::{ArchiveBrush, ArchivePen};
pub use picture::ArchivePicture;
pub use recorder::{ArchiveBufferRange, ArchiveRecorder};

/// Original current source archive version.
pub const CURRENT_VERSION: i32 = 10;
/// Original total encoded byte bound.
pub const MAX_ARCHIVE_BYTES: usize = 256 * 1024 * 1024;
pub(super) const MAX_ARRAY_ELEMENTS: usize = 16_000_000;
pub(super) const MAX_STRING_BYTES: usize = 16_000_000;
pub(super) const MAX_COMMANDS: usize = 1_000_000;
pub(super) const MAX_DEPTH: u32 = 64;
const MAGIC: u64 = 0x314349504b534750;

/// Safe archive errors, preserving font and geometry producer failures.
#[derive(Debug, thiserror::Error)]
pub enum ArchiveError {
    /// Original version, count, enum or descriptor admission failed.
    #[error("invalid picture archive data")]
    InvalidData,
    /// The complete declared original payload was not present.
    #[error("truncated picture archive")]
    Truncated,
    /// Resource family or lossy older-version write is unsupported by the source.
    #[error("unsupported picture archive feature")]
    Unsupported,
    /// Actual output or allocation exceeds the bounded archive capacity.
    #[error("picture archive capacity exceeded")]
    Capacity,
    /// Exact selected font producer failed.
    #[error(transparent)]
    Font(#[from] crate::font::FontError),
    /// Original text shaping/layout producer failed.
    #[error(transparent)]
    Text(#[from] crate::text::layout::TextLayoutError),
    /// Exact retained geometry/material producer failed.
    #[error(transparent)]
    Drawing(#[from] crate::Error),
    /// Caller-provided stream failed.
    #[error(transparent)]
    Io(#[from] std::io::Error),
}
pub(super) type Result<T> = std::result::Result<T, ArchiveError>;
pub(super) fn reserve<T>(count: usize) -> Result<Vec<T>> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| ArchiveError::Capacity)?;
    Ok(values)
}
pub(super) fn depth(value: u32) -> Result<()> {
    if value > MAX_DEPTH {
        Err(ArchiveError::InvalidData)
    } else {
        Ok(())
    }
}
/// Complete immutable original archive generation and independent cull edges.
#[derive(Clone, Debug)]
pub struct PictureArchive {
    /// Original decoded version; new archives select version ten.
    pub version: i32,
    /// Original left/top/right/bottom, with raw float components preserved.
    pub cull_bounds: [f32; 4],
    /// Actual immutable original command and side-buffer owners.
    pub picture: Arc<ArchivePicture>,
}
impl PictureArchive {
    /// Own a completed original recording, without executing font or GPU work.
    pub fn new(picture: ArchivePicture, cull_bounds: [f32; 4]) -> Self {
        Self {
            version: CURRENT_VERSION,
            cull_bounds,
            picture: Arc::new(picture),
        }
    }
    /// Read a complete v1..v10 generation. No partial owner is published on error,
    /// and trailing bytes retain the original whole-input rejection.
    pub fn deserialize(data: &[u8]) -> Result<Self> {
        if data.is_empty() || data.len() > MAX_ARCHIVE_BYTES {
            return Err(ArchiveError::InvalidData);
        }
        let mut reader = io::Reader::new(data);
        if reader.read::<u64>()? != MAGIC {
            return Err(ArchiveError::InvalidData);
        }
        let version = reader.read::<i32>()?;
        if !(1..=CURRENT_VERSION).contains(&version) {
            return Err(ArchiveError::InvalidData);
        }
        reader.version = version;
        let cull_bounds = reader.read()?;
        let picture = Arc::new(ArchivePicture::read(&mut reader, 0)?);
        if !reader.finished() {
            return Err(ArchiveError::InvalidData);
        }
        Ok(Self {
            version,
            cull_bounds,
            picture,
        })
    }
    /// Serialize a private complete candidate in its original decoded version.
    pub fn serialize(&self) -> Result<Vec<u8>> {
        self.serialize_version(self.version)
    }
    /// Original older-version writers reject the new explicitly versioned glyph
    /// and pen policies instead of silently dropping their intent.
    pub fn serialize_version(&self, version: i32) -> Result<Vec<u8>> {
        if !(1..=CURRENT_VERSION).contains(&version) {
            return Err(ArchiveError::InvalidData);
        }
        let mut writer = io::Writer::new(version);
        writer.write(MAGIC)?;
        writer.write(version)?;
        writer.write(self.cull_bounds)?;
        self.picture.write(&mut writer, 0)?;
        Ok(writer.bytes)
    }
    /// Bounded original nonseekable stream read; no application callback occurs
    /// while decoding or replaying the immutable resulting recording.
    pub fn read_from(reader: &mut impl std::io::Read) -> Result<Self> {
        let mut data = Vec::new();
        let mut buffer = [0u8; 8192];
        loop {
            let count = reader.read(&mut buffer)?;
            if count == 0 {
                break;
            }
            if data
                .len()
                .checked_add(count)
                .is_none_or(|n| n > MAX_ARCHIVE_BYTES)
            {
                return Err(ArchiveError::Capacity);
            }
            data.try_reserve(count)
                .map_err(|_| ArchiveError::Capacity)?;
            data.extend_from_slice(&buffer[..count]);
        }
        Self::deserialize(&data)
    }
    /// Publish to a caller stream only after complete private serialization.
    pub fn write_to(&self, output: &mut impl std::io::Write) -> Result<()> {
        output.write_all(&self.serialize()?)?;
        Ok(())
    }
}
