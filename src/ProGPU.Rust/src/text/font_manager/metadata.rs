//! Original bounded random-access catalog metadata reader, before full font load.
use super::{FontCatalogFace, FontSlant, FontSourceError, FontStyleRequest};
use crate::font::{FontError, MetadataCharacterMap, names};
use std::{
    io::{BufReader, Read, Seek},
    path::Path,
    sync::Arc,
};
fn invalid() -> FontSourceError {
    FontError::InvalidFace.into()
}
struct MetadataReader {
    file: BufReader<std::fs::File>,
    length: u64,
    position: u64,
}
impl MetadataReader {
    fn open(path: &Path) -> Result<Self, FontSourceError> {
        let file = std::fs::File::open(path)?;
        let length = file.metadata()?.len();
        Ok(Self {
            file: BufReader::with_capacity(4096, file),
            length,
            position: 0,
        })
    }
    fn read_into(&mut self, offset: u64, output: &mut [u8]) -> Result<(), FontSourceError> {
        if offset > self.length || output.len() as u64 > self.length - offset {
            return Err(invalid());
        }
        let distance = i128::from(offset) - i128::from(self.position);
        let distance = i64::try_from(distance).map_err(|_| invalid())?;
        self.file.seek_relative(distance)?;
        self.position = offset;
        self.file.read_exact(output)?;
        self.position += output.len() as u64;
        Ok(())
    }
}
fn read(reader: &mut MetadataReader, offset: u64, size: usize) -> Result<Vec<u8>, FontSourceError> {
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(size)
        .map_err(|_| FontError::Capacity)?;
    bytes.resize(size, 0);
    reader.read_into(offset, &mut bytes)?;
    Ok(bytes)
}
fn fixed<const N: usize>(
    reader: &mut MetadataReader,
    offset: u64,
) -> Result<[u8; N], FontSourceError> {
    let mut value = [0; N];
    reader.read_into(offset, &mut value)?;
    Ok(value)
}
fn word(data: &[u8], at: usize) -> u16 {
    u16::from_be_bytes(data[at..at + 2].try_into().unwrap())
}
fn long(data: &[u8], at: usize) -> u32 {
    u32::from_be_bytes(data[at..at + 4].try_into().unwrap())
}
fn offsets(reader: &mut MetadataReader) -> Result<Vec<u32>, FontSourceError> {
    let header = fixed::<12>(reader, 0)?;
    if &header[..4] != b"ttcf" {
        return Ok(vec![0]);
    }
    let count = long(&header, 8) as usize;
    if count == 0 || count > 4096 {
        return Err(invalid());
    }
    let bytes = read(reader, 12, count * 4)?;
    Ok(bytes
        .chunks_exact(4)
        .map(|v| u32::from_be_bytes(v.try_into().unwrap()))
        .collect())
}
fn directory(reader: &mut MetadataReader, offset: u32) -> Result<Vec<[u8; 16]>, FontSourceError> {
    let header = fixed::<12>(reader, u64::from(offset))?;
    let count = usize::from(word(&header, 4));
    if count > 4096 {
        return Err(invalid());
    }
    Ok(read(reader, u64::from(offset) + 12, count * 16)?
        .chunks_exact(16)
        .map(|value| value.try_into().unwrap())
        .collect())
}
pub(super) fn character_map(
    path: &Path,
    face_index: u32,
) -> Result<Option<MetadataCharacterMap>, FontSourceError> {
    let mut file = MetadataReader::open(path)?;
    let offsets = offsets(&mut file)?;
    let Some(&offset) = offsets.get(face_index as usize) else {
        return Ok(None);
    };
    let header = fixed::<12>(&mut file, u64::from(offset))?;
    let count = usize::from(word(&header, 4));
    if count > 4096 {
        return Err(invalid());
    }
    for index in 0..count {
        let record = fixed::<16>(&mut file, u64::from(offset) + 12 + index as u64 * 16)?;
        if &record[..4] == b"cmap" {
            let length = long(&record, 12) as usize;
            if length == 0 || length > 64 * 1024 * 1024 {
                return Ok(None);
            }
            return Ok(Some(MetadataCharacterMap::new(read(
                &mut file,
                u64::from(long(&record, 8)),
                length,
            )?)?));
        }
    }
    Ok(None)
}
pub(super) fn catalog(path: &Path) -> Result<Vec<FontCatalogFace>, FontSourceError> {
    let mut file = MetadataReader::open(path)?;
    let offsets = offsets(&mut file)?;
    let fallback: Arc<str> = path
        .file_stem()
        .unwrap_or_default()
        .to_string_lossy()
        .as_ref()
        .into();
    let mut result = Vec::with_capacity(offsets.len());
    for (index, offset) in offsets.into_iter().enumerate() {
        let records = directory(&mut file, offset)?;
        let mut chosen: [Option<(Arc<str>, u8)>; 3] = [None, None, None];
        if let Some(record) = records.iter().find(|record| &record[..4] == b"name") {
            let table_offset = u64::from(long(record, 8));
            let table_length = u64::from(long(record, 12));
            if table_length >= 6 {
                let header = fixed::<6>(&mut file, table_offset)?;
                let count = usize::from(word(&header, 2));
                let strings = u64::from(word(&header, 4));
                if 6 + count as u64 * 12 > table_length || strings > table_length {
                    return Err(invalid());
                }
                for name in 0..count {
                    let record = fixed::<12>(&mut file, table_offset + 6 + name as u64 * 12)?;
                    let slot = match word(&record, 6) {
                        16 => 0,
                        1 => 1,
                        4 => 2,
                        _ => continue,
                    };
                    let (length, offset) = (
                        usize::from(word(&record, 8)),
                        strings + u64::from(word(&record, 10)),
                    );
                    if length == 0 || offset > table_length || length as u64 > table_length - offset
                    {
                        continue;
                    }
                    let bytes = read(&mut file, table_offset + offset, length)?;
                    let platform = word(&record, 0);
                    let value = names::decode(&bytes, platform, word(&record, 2));
                    let score = names::score(platform, word(&record, 4), &value);
                    if !value.is_empty()
                        && chosen[slot].as_ref().is_none_or(|entry| score > entry.1)
                    {
                        chosen[slot] = Some((value.into(), score));
                    }
                }
            }
        }
        let mut style = FontStyleRequest::NORMAL;
        let mut italic = false;
        for record in records {
            let length = long(&record, 12);
            let offset = u64::from(long(&record, 8));
            if &record[..4] == b"OS/2" && length >= 64 {
                let values = fixed::<64>(&mut file, offset)?;
                style.weight = i32::from(word(&values, 4)).clamp(1, 1000);
                style.width = i32::from(word(&values, 6)).clamp(1, 9);
                italic |= word(&values, 62) & 1 != 0;
            } else if &record[..4] == b"head" && length >= 46 {
                italic |= word(&fixed::<46>(&mut file, offset)?, 44) & 2 != 0;
            }
        }
        if italic {
            style.slant = FontSlant::Italic;
        }
        let family = chosen[0]
            .as_ref()
            .or(chosen[1].as_ref())
            .map_or_else(|| fallback.clone(), |v| v.0.clone());
        let name = chosen[2]
            .as_ref()
            .map_or_else(|| family.clone(), |v| v.0.clone());
        result.push(FontCatalogFace::from_file_metadata(
            path.to_owned(),
            index as u32,
            family,
            name,
            style,
        ));
    }
    Ok(result)
}
