//! Exact bounded random-access original record extraction and standalone writer.
use super::*;
use std::io::SeekFrom;
use wide::u32x4;
fn invalid() -> FontStorageError {
    FontError::InvalidFace.into()
}
fn word(bytes: &[u8], at: usize) -> u16 {
    u16::from_be_bytes(bytes[at..at + 2].try_into().unwrap())
}
fn long(bytes: &[u8], at: usize) -> u32 {
    u32::from_be_bytes(bytes[at..at + 4].try_into().unwrap())
}
fn put16(bytes: &mut [u8], at: usize, value: u16) {
    bytes[at..at + 2].copy_from_slice(&value.to_be_bytes());
}
fn put32(bytes: &mut [u8], at: usize, value: u32) {
    bytes[at..at + 4].copy_from_slice(&value.to_be_bytes());
}
fn add(a: usize, b: usize) -> Result<usize, FontStorageError> {
    a.checked_add(b)
        .filter(|value| *value <= i32::MAX as usize)
        .ok_or_else(invalid)
}
fn align(value: usize) -> Result<usize, FontStorageError> {
    Ok(add(value, 3)? & !3)
}
fn allocate(size: usize) -> Result<Vec<u8>, FontStorageError> {
    if size > i32::MAX as usize {
        return Err(invalid());
    }
    let mut output = Vec::new();
    output
        .try_reserve_exact(size)
        .map_err(|_| FontError::Capacity)?;
    output.resize(size, 0);
    Ok(output)
}
struct Reader<'a, R> {
    stream: &'a mut R,
    length: u64,
    position: u64,
    metrics: ResidentFontMetrics,
}
impl<'a, R: Read + Seek> Reader<'a, R> {
    fn new(stream: &'a mut R, execution: ResidentFontExecution) -> Result<Self, FontStorageError> {
        let length = stream.seek(SeekFrom::End(0))?;
        Ok(Self {
            stream,
            length,
            position: length,
            metrics: ResidentFontMetrics::new(execution),
        })
    }
    fn into_slice(&mut self, offset: u64, output: &mut [u8]) -> Result<(), FontStorageError> {
        if offset > self.length || output.len() as u64 > self.length - offset {
            return Err(invalid());
        }
        let distance =
            i64::try_from(i128::from(offset) - i128::from(self.position)).map_err(|_| invalid())?;
        self.stream.seek_relative(distance)?;
        self.position = offset;
        self.stream.read_exact(output)?;
        self.position += output.len() as u64;
        self.metrics.source_bytes_read += output.len() as u64;
        Ok(())
    }
    fn fixed<const N: usize>(&mut self, offset: u64) -> Result<[u8; N], FontStorageError> {
        let mut value = [0; N];
        self.into_slice(offset, &mut value)?;
        Ok(value)
    }
    fn read(&mut self, offset: u64, length: usize) -> Result<Vec<u8>, FontStorageError> {
        if offset > self.length || length as u64 > self.length - offset {
            return Err(invalid());
        }
        let mut value = allocate(length)?;
        self.into_slice(offset, &mut value)?;
        Ok(value)
    }
    fn face(&mut self, index: u32) -> Result<Option<u32>, FontStorageError> {
        let header = self.fixed::<12>(0)?;
        if &header[..4] != b"ttcf" {
            return Ok((index == 0).then_some(0));
        }
        let count = long(&header, 8);
        if count == 0 || count > 4096 {
            return Err(invalid());
        }
        // Original collection reader loads every offset, even for an early face.
        let offsets = self.read(12, count as usize * 4)?;
        Ok((index < count).then(|| long(&offsets, index as usize * 4)))
    }
    fn tables(&mut self, offset: u32) -> Result<(u32, Vec<Table>), FontStorageError> {
        let header = self.fixed::<12>(u64::from(offset))?;
        let count = word(&header, 4);
        if count == 0 || count > 4096 {
            return Err(invalid());
        }
        let mut tables = Vec::with_capacity(usize::from(count));
        for index in 0..count {
            let record = self.fixed::<16>(u64::from(offset) + 12 + u64::from(index) * 16)?;
            let table = Table {
                tag: record[..4].try_into().map_err(|_| invalid())?,
                checksum: long(&record, 4),
                offset: long(&record, 8),
                length: long(&record, 12),
            };
            if u64::from(table.offset) > self.length
                || u64::from(table.length) > self.length - u64::from(table.offset)
            {
                return Err(invalid());
            }
            // Source metadata reader decodes and later encodes ASCII tags.
            let table = Table {
                tag: table.tag.map(|b| if b < 128 { b } else { b'?' }),
                ..table
            };
            tables.push(table);
        }
        self.metrics.source_tables = u32::from(count);
        Ok((long(&header, 0), tables))
    }
    fn table(&mut self, table: &Table) -> Result<Vec<u8>, FontStorageError> {
        self.read(u64::from(table.offset), table.length as usize)
    }
}
#[derive(Clone, Copy)]
struct Table {
    tag: [u8; 4],
    checksum: u32,
    offset: u32,
    length: u32,
}
struct Selection {
    version: u32,
    tables: Vec<Table>,
    sbix: usize,
    maxp: usize,
    glyph_count: u16,
    maxp_data: Vec<u8>,
}
fn select<R: Read + Seek>(
    reader: &mut Reader<'_, R>,
    face: u32,
    glyph: u16,
) -> Result<Option<Selection>, FontStorageError> {
    let Some(offset) = reader.face(face)? else {
        return Ok(None);
    };
    let (version, tables) = reader.tables(offset)?;
    let (Some(sbix), Some(maxp)) = (
        tables.iter().position(|t| t.tag == *b"sbix"),
        tables.iter().position(|t| t.tag == *b"maxp"),
    ) else {
        return Ok(None);
    };
    if tables[sbix].length < 8 {
        return Ok(None);
    }
    let maxp_data = reader.table(&tables[maxp])?;
    if maxp_data.len() < 6 {
        return Ok(None);
    }
    let glyph_count = word(&maxp_data, 4);
    if glyph >= glyph_count {
        return Ok(None);
    }
    Ok(Some(Selection {
        version,
        tables,
        sbix,
        maxp,
        glyph_count,
        maxp_data,
    }))
}
pub(super) fn sbix(
    reader: &mut (impl Read + Seek),
    face: u32,
    glyph: u16,
    execution: ResidentFontExecution,
) -> Result<Option<GlyphResidentSbix>, FontStorageError> {
    let mut reader = Reader::new(reader, execution)?;
    let Some(selected) = select(&mut reader, face, glyph)? else {
        return Ok(None);
    };
    let Some(data) = build_sbix(
        &mut reader,
        &selected.tables[selected.sbix],
        selected.glyph_count,
        glyph,
    )?
    else {
        return Ok(None);
    };
    reader.metrics.output_bytes = data.len() as u64;
    Ok(Some(GlyphResidentSbix {
        data: FontData::owned(data),
        glyph_count: selected.glyph_count,
        metrics: reader.metrics,
    }))
}
pub(super) fn font(
    reader: &mut (impl Read + Seek),
    face: u32,
    glyph: u16,
    execution: ResidentFontExecution,
) -> Result<Option<GlyphResidentFontData>, FontStorageError> {
    let mut reader = Reader::new(reader, execution)?;
    let Some(selected) = select(&mut reader, face, glyph)? else {
        return Ok(None);
    };
    let Some(sbix) = build_sbix(
        &mut reader,
        &selected.tables[selected.sbix],
        selected.glyph_count,
        glyph,
    )?
    else {
        return Ok(None);
    };
    let mut sbix = Some(sbix);
    let mut maxp = Some(selected.maxp_data);
    let mut payloads = Vec::with_capacity(selected.tables.len());
    for (index, table) in selected.tables.iter().enumerate() {
        payloads.push(if index == selected.sbix {
            sbix.take().unwrap()
        } else if index == selected.maxp {
            maxp.take().unwrap()
        } else {
            reader.table(table)?
        });
    }
    let data = build_font(
        selected.version,
        &selected.tables,
        &payloads,
        &mut reader.metrics,
    )?;
    reader.metrics.output_bytes = data.len() as u64;
    Ok(Some(GlyphResidentFontData {
        data: FontData::owned(data),
        source_face_index: face,
        glyph_index: glyph,
        glyph_count: selected.glyph_count,
        metrics: reader.metrics,
    }))
}
fn build_sbix<R: Read + Seek>(
    reader: &mut Reader<'_, R>,
    table: &Table,
    count: u16,
    glyph: u16,
) -> Result<Option<Vec<u8>>, FontStorageError> {
    let header = reader.fixed::<8>(u64::from(table.offset))?;
    let strikes = long(&header, 4);
    if word(&header, 0) != 1
        || strikes == 0
        || strikes > 4096
        || 8 + u64::from(strikes) * 4 > u64::from(table.length)
    {
        return Ok(None);
    }
    let mut offsets = Vec::with_capacity(strikes as usize + 1);
    for index in 0..strikes {
        offsets.push(long(
            &reader.fixed::<4>(u64::from(table.offset) + 8 + u64::from(index) * 4)?,
            0,
        ));
    }
    offsets.push(table.length);
    reader.metrics.source_strikes = strikes;
    let mut output_strikes = Vec::with_capacity(strikes as usize);
    for index in 0..strikes as usize {
        let (start, end) = (offsets[index], offsets[index + 1]);
        if start >= end || end > table.length {
            return Ok(None);
        }
        let strike_header = reader.fixed::<4>(u64::from(table.offset.wrapping_add(start)))?;
        let record =
            glyph_record(reader, table.offset, start, end, count, glyph)?.unwrap_or_default();
        let record_offset = 4 + (usize::from(count) + 1) * 4;
        let mut strike = allocate(add(record_offset, record.len())?)?;
        strike[..4].copy_from_slice(&strike_header);
        let split = 4 + (usize::from(glyph) + 1) * 4;
        let strike_length = strike.len() as u32;
        fill_offsets(
            &mut strike[4..split],
            record_offset as u32,
            reader.metrics.execution,
        );
        fill_offsets(
            &mut strike[split..record_offset],
            strike_length,
            reader.metrics.execution,
        );
        strike[record_offset..].copy_from_slice(&record);
        output_strikes.push(strike);
    }
    let header_size = 8 + strikes as usize * 4;
    let total = output_strikes
        .iter()
        .try_fold(header_size, |sum, strike| add(sum, strike.len()))?;
    let mut output = allocate(total)?;
    output[..8].copy_from_slice(&header);
    let mut target = header_size;
    for (index, strike) in output_strikes.into_iter().enumerate() {
        put32(&mut output, 8 + index * 4, target as u32);
        output[target..target + strike.len()].copy_from_slice(&strike);
        target += strike.len();
    }
    Ok(Some(output))
}
fn glyph_record<R: Read + Seek>(
    reader: &mut Reader<'_, R>,
    table: u32,
    strike: u32,
    end: u32,
    count: u16,
    original: u16,
) -> Result<Option<Vec<u8>>, FontStorageError> {
    let mut glyph = original;
    let mut outer_origin = None;
    for depth in 0..=16 {
        if glyph >= count || u64::from(strike) + 4 + (u64::from(count) + 1) * 4 > u64::from(end) {
            return Ok(None);
        }
        let offsets =
            reader.fixed::<8>(u64::from(table) + u64::from(strike) + 4 + u64::from(glyph) * 4)?;
        let (first, last) = (long(&offsets, 0), long(&offsets, 4));
        // Preserve the original uint addition in this independent limit check.
        if first >= last || strike.wrapping_add(last) > end || last - first < 8 {
            return Ok(None);
        }
        let offset = u64::from(table) + u64::from(strike) + u64::from(first);
        let header = reader.fixed::<8>(offset)?;
        let kind = long(&header, 4);
        if kind == u32::from_be_bytes(*b"dupe") {
            if last - first < 10 {
                return Ok(None);
            }
            let next = word(&reader.fixed::<2>(offset + 8)?, 0);
            if next == original || depth == 16 {
                return Ok(None);
            }
            outer_origin.get_or_insert(<[u8; 4]>::try_from(&header[..4]).unwrap());
            glyph = next;
            continue;
        }
        if !matches!(kind, 0x706e6720 | 0x6a706720 | 0x74696666) {
            return Ok(None);
        }
        let mut record = reader.read(offset, (last - first) as usize)?;
        if let Some(origin) = outer_origin {
            record[..4].copy_from_slice(&origin);
        }
        return Ok(Some(record));
    }
    Ok(None)
}
fn build_font(
    version: u32,
    tables: &[Table],
    payloads: &[Vec<u8>],
    metrics: &mut ResidentFontMetrics,
) -> Result<Vec<u8>, FontStorageError> {
    let mut target = align(12 + tables.len() * 16)?;
    let total = payloads
        .iter()
        .try_fold(target, |sum, bytes| align(add(sum, bytes.len())?))?;
    let mut output = allocate(total)?;
    put32(&mut output, 0, version);
    put16(&mut output, 4, tables.len() as u16);
    let mut power = 1usize;
    let mut selector = 0u16;
    while power <= tables.len() / 2 {
        power *= 2;
        selector += 1;
    }
    put16(
        &mut output,
        6,
        u16::try_from(power * 16).map_err(|_| invalid())?,
    );
    put16(&mut output, 8, selector);
    put16(
        &mut output,
        10,
        u16::try_from(tables.len() * 16 - power * 16).map_err(|_| invalid())?,
    );
    for (index, (table, data)) in tables.iter().zip(payloads).enumerate() {
        let record = 12 + index * 16;
        output[record..record + 4].copy_from_slice(&table.tag);
        // Every sbix tag is recomputed, including retained duplicate records.
        let checksum = if table.tag == *b"sbix" {
            metrics.checksum_bytes += data.len() as u64;
            checksum(data, metrics.execution)
        } else {
            table.checksum
        };
        put32(&mut output, record + 4, checksum);
        put32(&mut output, record + 8, target as u32);
        put32(&mut output, record + 12, data.len() as u32);
        output[target..target + data.len()].copy_from_slice(data);
        target = align(add(target, data.len())?)?;
    }
    Ok(output)
}
fn checksum(data: &[u8], execution: ResidentFontExecution) -> u32 {
    let mut offset = 0;
    let mut sum = 0u32;
    if execution == ResidentFontExecution::CpuSimd {
        let mut lanes = u32x4::ZERO;
        while offset + 16 <= data.len() {
            lanes = lanes + u32x4::from(std::array::from_fn(|lane| long(data, offset + lane * 4)));
            offset += 16;
        }
        for value in lanes.to_array() {
            sum = sum.wrapping_add(value);
        }
    }
    while offset < data.len() {
        let mut value = [0; 4];
        let count = (data.len() - offset).min(4);
        value[..count].copy_from_slice(&data[offset..offset + count]);
        sum = sum.wrapping_add(u32::from_be_bytes(value));
        offset += count;
    }
    sum
}

fn fill_offsets(bytes: &mut [u8], value: u32, execution: ResidentFontExecution) {
    let mut offset = 0;
    if execution == ResidentFontExecution::CpuSimd {
        let lanes = u32x4::splat(value.to_be()).to_array();
        let block = bytemuck::bytes_of(&lanes);
        while offset + 16 <= bytes.len() {
            bytes[offset..offset + 16].copy_from_slice(block);
            offset += 16;
        }
    }
    while offset < bytes.len() {
        bytes[offset..offset + 4].copy_from_slice(&value.to_be_bytes());
        offset += 4;
    }
}
