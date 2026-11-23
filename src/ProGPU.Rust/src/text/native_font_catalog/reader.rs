//! Original bounded native file reader and metadata-only compact SFNT builder.
use super::*;
use std::{
    fs::File,
    io::{Read, Seek, SeekFrom},
};
pub(super) struct Reader<'a> {
    file: File,
    pub length: u64,
    measured: Option<&'a mut u64>,
}
pub(super) struct SourceTable {
    pub tag: Tag,
    pub offset: u32,
    pub length: u32,
}
struct Payload {
    tag: Tag,
    bytes: Vec<u8>,
}
impl<'a> Reader<'a> {
    pub fn open(path: &Path, measured: Option<&'a mut u64>) -> Result<Self, NativeCatalogError> {
        let mut file = File::open(path).map_err(|_| NativeCatalogError::Filesystem)?;
        let length = file
            .seek(SeekFrom::End(0))
            .map_err(|_| NativeCatalogError::Filesystem)?;
        Ok(Self {
            file,
            length,
            measured,
        })
    }
    pub fn read_into(&mut self, offset: u64, output: &mut [u8]) -> Result<(), NativeCatalogError> {
        if offset > self.length
            || output.len() as u64 > self.length - offset
            || offset > i64::MAX as u64
            || output.len() > isize::MAX as usize
        {
            return Err(NativeCatalogError::InvalidFont);
        }
        self.file
            .seek(SeekFrom::Start(offset))
            .map_err(|_| NativeCatalogError::Filesystem)?;
        self.file
            .read_exact(output)
            .map_err(|_| NativeCatalogError::Filesystem)?;
        if let Some(measured) = &mut self.measured {
            **measured = (**measured).wrapping_add(output.len() as u64);
        }
        Ok(())
    }
    fn read(&mut self, offset: u64, length: usize) -> Result<Vec<u8>, NativeCatalogError> {
        let mut output = zeros(length)?;
        self.read_into(offset, &mut output)?;
        Ok(output)
    }
    pub fn face_offsets(&mut self) -> Result<Vec<u32>, NativeCatalogError> {
        let mut header = [0; 12];
        self.read_into(0, &mut header)?;
        if &header[..4] != b"ttcf" {
            let mut output = reserved(1)?;
            output.push(0);
            return Ok(output);
        }
        let count = dword(&header, 8);
        if count == 0 || count > 4096 || 12 + u64::from(count) * 4 > self.length {
            return Err(NativeCatalogError::InvalidFont);
        }
        let bytes = self.read(12, count as usize * 4)?;
        let mut output = reserved(count as usize)?;
        for index in 0..count as usize {
            output.push(dword(&bytes, index * 4));
        }
        Ok(output)
    }
    pub fn directory(&mut self, face: u32) -> Result<(u32, Vec<SourceTable>), NativeCatalogError> {
        let mut header = [0; 12];
        self.read_into(u64::from(face), &mut header)?;
        let count = usize::from(word(&header, 4));
        if count == 0 || count > 4096 || u64::from(face) + 12 + count as u64 * 16 > self.length {
            return Err(NativeCatalogError::InvalidFont);
        }
        let bytes = self.read(u64::from(face) + 12, count * 16)?;
        let mut output = reserved(count)?;
        for index in 0..count {
            let at = index * 16;
            let offset = dword(&bytes, at + 8);
            let length = dword(&bytes, at + 12);
            if u64::from(offset) > self.length
                || u64::from(length) > self.length - u64::from(offset)
            {
                return Err(NativeCatalogError::InvalidFont);
            }
            output.push(SourceTable {
                tag: Tag(dword(&bytes, at)),
                offset,
                length,
            });
        }
        Ok((dword(&header, 0), output))
    }
    pub fn compact(&mut self, face: u32, selected: &[Tag]) -> Result<SfntFace, NativeCatalogError> {
        let (version, directory) = self.directory(face)?;
        let mut payloads = reserved(selected.len())?;
        for &tag in selected {
            let Some(found) = directory.iter().find(|table| table.tag == tag) else {
                continue;
            };
            if found.length == 0 {
                continue;
            }
            if found.length > 64 * 1024 * 1024 {
                return Err(NativeCatalogError::InvalidFont);
            }
            payloads.push(Payload {
                tag,
                bytes: self.read(u64::from(found.offset), found.length as usize)?,
            });
        }
        SfntFace::new(compact(version, payloads)?, 0).map_err(map_font_error)
    }
}
fn compact(version: u32, mut tables: Vec<Payload>) -> Result<Vec<u8>, NativeCatalogError> {
    if tables.is_empty() || tables.len() > 4096 {
        return Err(NativeCatalogError::InvalidFont);
    }
    tables.sort_unstable_by_key(|table| table.tag.0);
    let align = |value: usize| {
        value
            .checked_add(3)
            .map(|value| value & !3)
            .ok_or(NativeCatalogError::InvalidFont)
    };
    let mut size = align(12 + tables.len() * 16)?;
    for table in &tables {
        if table.bytes.len() > u32::MAX as usize || size > u32::MAX as usize - table.bytes.len() {
            return Err(NativeCatalogError::InvalidFont);
        }
        size = align(size + table.bytes.len())?;
    }
    let mut output = zeros(size)?;
    put32(&mut output, 0, version);
    put16(&mut output, 4, tables.len() as u16);
    search_parameters(&mut output, tables.len() as u16);
    let mut target = align(12 + tables.len() * 16)?;
    for (index, table) in tables.iter().enumerate() {
        let record = 12 + index * 16;
        put32(&mut output, record, table.tag.0);
        put32(&mut output, record + 8, target as u32);
        put32(&mut output, record + 12, table.bytes.len() as u32);
        output[target..target + table.bytes.len()].copy_from_slice(&table.bytes);
        target = align(target + table.bytes.len())?;
    }
    Ok(output)
}
pub(super) fn search_parameters(output: &mut [u8], count: u16) {
    let mut power = 1u16;
    let mut selector = 0u16;
    while power <= count / 2 {
        power *= 2;
        selector += 1;
    }
    put16(output, 6, power.wrapping_mul(16));
    put16(output, 8, selector);
    put16(
        output,
        10,
        count.wrapping_mul(16).wrapping_sub(power.wrapping_mul(16)),
    );
}
