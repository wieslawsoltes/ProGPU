//! Original WOFF1 normalization, with separate native and managed inflate policy.
use super::FontError;
use std::{io::Read, sync::Arc};
const WOFF1: u32 = 0x774f4646;
const WOFF2: u32 = 0x774f4632;
const HEADER: usize = 44;
const ENTRY: usize = 20;

/// Exact complete native container inventory; ordinary bytes pass through.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SfntContainerRequirements {
    /// Exact output prefix, including original four-byte table alignment.
    pub normalized_bytes: usize,
    /// Maximum original expanded size among compressed tables only.
    pub table_scratch_bytes: usize,
    /// Original WOFF directory count; zero for ordinary passthrough.
    pub table_count: u16,
    /// Whether the original signature selects WOFF1 normalization.
    pub requires_normalization: bool,
}
fn u16be(data: &[u8], at: usize) -> u16 {
    u16::from_be_bytes([data[at], data[at + 1]])
}
fn u32be(data: &[u8], at: usize) -> u32 {
    u32::from_be_bytes([data[at], data[at + 1], data[at + 2], data[at + 3]])
}
fn write16(data: &mut [u8], at: usize, value: u16) {
    data[at..at + 2].copy_from_slice(&value.to_be_bytes());
}
fn write32(data: &mut [u8], at: usize, value: u32) {
    data[at..at + 4].copy_from_slice(&value.to_be_bytes());
}
fn align4(value: usize) -> Result<usize, FontError> {
    value
        .checked_add(3)
        .map(|v| v & !3)
        .ok_or(FontError::InvalidContainer)
}
fn search_parameters(count: u16) -> Result<[u16; 3], FontError> {
    let mut power = 1u32;
    let mut selector = 0u16;
    while power <= u32::from(count) / 2 {
        power *= 2;
        selector += 1;
    }
    let search = power * 16;
    Ok([
        u16::try_from(search).map_err(|_| FontError::InvalidContainer)?,
        selector,
        u16::try_from(u32::from(count) * 16 - search).map_err(|_| FontError::InvalidContainer)?,
    ])
}
fn requirements(data: &[u8], managed: bool) -> Result<SfntContainerRequirements, FontError> {
    let pass = SfntContainerRequirements {
        normalized_bytes: data.len(),
        ..Default::default()
    };
    if data.len() < 4 {
        return Ok(pass);
    }
    match u32be(data, 0) {
        WOFF2 => return Err(FontError::UnsupportedContainer),
        WOFF1 => {}
        _ => return Ok(pass),
    }
    if data.len() < HEADER {
        return Err(FontError::InvalidContainer);
    }
    let declared = u32be(data, 8) as usize;
    let count = u16be(data, 12);
    let directory_end = HEADER + usize::from(count) * ENTRY;
    if declared < HEADER
        || declared > data.len()
        || count == 0
        || u16be(data, 14) != 0
        || directory_end > declared
    {
        return Err(FontError::InvalidContainer);
    }
    let mut offset = 12 + usize::from(count) * 16;
    let mut scratch = 0;
    for index in 0..usize::from(count) {
        let record = HEADER + index * ENTRY;
        let source = u32be(data, record + 4) as usize;
        let compressed = u32be(data, record + 8) as usize;
        let original = u32be(data, record + 12) as usize;
        if compressed > original
            || source > declared
            || compressed > declared - source
            || (managed && original > i32::MAX as usize)
        {
            return Err(FontError::InvalidContainer);
        }
        if compressed < original {
            scratch = scratch.max(original);
        }
        let end = offset
            .checked_add(original)
            .ok_or(FontError::InvalidContainer)?;
        // C# performs both addition and Align4's +3 in checked signed int.
        if managed && end > i32::MAX as usize - 3 {
            return Err(FontError::Capacity);
        }
        offset = align4(end)?;
    }
    if offset > u32::MAX as usize || offset != u32be(data, 16) as usize {
        return Err(FontError::InvalidContainer);
    }
    search_parameters(count)?;
    Ok(SfntContainerRequirements {
        normalized_bytes: offset,
        table_scratch_bytes: scratch,
        table_count: count,
        requires_normalization: true,
    })
}
/// Original O(T) native complete-directory requirements. Tag order, duplicate
/// tags, checksum words and metadata/private blocks retain their original policy.
pub fn sfnt_container_requirements(data: &[u8]) -> Result<SfntContainerRequirements, FontError> {
    requirements(data, false)
}
fn native_inflate(input: &[u8], output: &mut [u8]) -> Result<(), FontError> {
    let written = crate::image::inflate_native_zlib(input, output)
        .map_err(|_| FontError::InvalidCompressedData)?;
    if written != output.len() {
        return Err(FontError::InvalidCompressedData);
    }
    Ok(())
}
fn header(input: &[u8], output: &mut [u8], count: u16) -> Result<(), FontError> {
    write32(output, 0, u32be(input, 4));
    write16(output, 4, count);
    let search = search_parameters(count)?;
    for (i, value) in search.into_iter().enumerate() {
        write16(output, 6 + i * 2, value);
    }
    Ok(())
}
fn write_tables(
    input: &[u8],
    output: &mut [u8],
    count: u16,
    mut inflate: impl FnMut(&[u8], &mut [u8]) -> Result<(), FontError>,
) -> Result<(), FontError> {
    let mut target = 12 + usize::from(count) * 16;
    for index in 0..usize::from(count) {
        let record = HEADER + index * ENTRY;
        let directory = 12 + index * 16;
        let source = u32be(input, record + 4) as usize;
        let compressed = u32be(input, record + 8) as usize;
        let original = u32be(input, record + 12) as usize;
        write32(output, directory, u32be(input, record));
        write32(output, directory + 4, u32be(input, record + 16));
        write32(output, directory + 8, target as u32);
        write32(output, directory + 12, original as u32);
        let src = &input[source..source + compressed];
        let dst = &mut output[target..target + original];
        if compressed == original {
            dst.copy_from_slice(src);
        } else {
            inflate(src, dst)?;
        }
        target = align4(target + original)?;
    }
    Ok(())
}
/// Original native atomic-output normalization. All capacity and compressed
/// table checks finish before any output write. Scratch may change on failure;
/// untouched output tails remain caller-owned. Safe slices enforce disjointness.
pub fn normalize_sfnt_container_into(
    input: &[u8],
    table_scratch: &mut [u8],
    output: &mut [u8],
) -> Result<SfntContainerRequirements, FontError> {
    let request = sfnt_container_requirements(input)?;
    if output.len() < request.normalized_bytes || table_scratch.len() < request.table_scratch_bytes
    {
        return Err(FontError::Capacity);
    }
    if !request.requires_normalization {
        output[..input.len()].copy_from_slice(input);
        return Ok(request);
    }
    for index in 0..usize::from(request.table_count) {
        let record = HEADER + index * ENTRY;
        let source = u32be(input, record + 4) as usize;
        let compressed = u32be(input, record + 8) as usize;
        let original = u32be(input, record + 12) as usize;
        if compressed != original {
            native_inflate(
                &input[source..source + compressed],
                &mut table_scratch[..original],
            )?;
        }
    }
    let output = &mut output[..request.normalized_bytes];
    output.fill(0);
    header(input, output, request.table_count)?;
    write_tables(input, output, request.table_count, native_inflate)?;
    Ok(request)
}
fn buffer(len: usize) -> Result<Vec<u8>, FontError> {
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(len)
        .map_err(|_| FontError::Capacity)?;
    bytes.resize(len, 0);
    Ok(bytes)
}
/// Own the exact native normalized generation. Ordinary SFNT/TTC/unrecognized
/// bytes return the same Arc; container normalization does not validate a font.
pub fn normalize_sfnt_container(input: Arc<[u8]>) -> Result<Arc<[u8]>, FontError> {
    let request = sfnt_container_requirements(&input)?;
    if !request.requires_normalization {
        return Ok(input);
    }
    let mut scratch = buffer(request.table_scratch_bytes)?;
    let mut output = buffer(request.normalized_bytes)?;
    normalize_sfnt_container_into(&input, &mut scratch, &mut output)?;
    Ok(output.into())
}
/// Original managed SfntFontContainer policy. Native exact compressed-byte
/// consumption is deliberately separate from ZLibStream's bounded-output route.
/// A complete private candidate is published only after every table succeeds.
pub fn normalize_managed_sfnt_container(input: Arc<[u8]>) -> Result<Arc<[u8]>, FontError> {
    let request = requirements(&input, true)?;
    if !request.requires_normalization {
        return Ok(input);
    }
    let mut output = buffer(request.normalized_bytes)?;
    header(&input, &mut output, request.table_count)?;
    write_tables(
        &input,
        &mut output,
        request.table_count,
        |source, target| {
            let mut stream = flate2::read::ZlibDecoder::new(source);
            stream
                .read_exact(target)
                .map_err(|_| FontError::InvalidCompressedData)?;
            let mut extra = [0u8; 1];
            if stream
                .read(&mut extra)
                .map_err(|_| FontError::InvalidCompressedData)?
                != 0
            {
                return Err(FontError::InvalidCompressedData);
            }
            Ok(())
        },
    )?;
    Ok(output.into())
}
