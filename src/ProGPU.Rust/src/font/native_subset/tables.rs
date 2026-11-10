//! Exact original directory, duplicate, padding and checksum construction.
use super::*;
pub(super) fn parse(data: &[u8], directory: usize, policy: Policy) -> Result<Face<'_>> {
    slice(data, directory, 12)?;
    let version = u32be(data, directory)?;
    let count = usize::from(u16be(data, directory + 4)?);
    if policy == Policy::Native && count > 4096 {
        return Err(INVALID);
    }
    slice(data, directory + 12, count * 16)?;
    let mut tables = reserved(count)?;
    for index in 0..count {
        let record = directory + 12 + index * 16;
        let tag = u32be(data, record)?;
        let offset = u32be(data, record + 8)? as usize;
        let length = u32be(data, record + 12)? as usize;
        if policy == Policy::Managed && (offset > i32::MAX as usize || length > i32::MAX as usize) {
            return Err(INVALID);
        }
        tables.push(Table {
            tag,
            bytes: Cow::Borrowed(slice(data, offset, length)?),
        });
    }
    Ok(Face {
        version,
        tables,
        policy,
    })
}
fn checksum(data: &[u8]) -> u32 {
    // Zero padding remains conceptual; ordinary wrapping arithmetic is original.
    let mut sum = 0u32;
    let mut chunks = data.chunks_exact(4);
    for chunk in &mut chunks {
        sum = sum.wrapping_add(u32::from_be_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]));
    }
    let mut tail = [0; 4];
    tail[..chunks.remainder().len()].copy_from_slice(chunks.remainder());
    sum.wrapping_add(u32::from_be_bytes(tail))
}
pub(super) fn build(face: Face<'_>) -> Result<Vec<u8>> {
    // A source ordinal makes duplicate ordering exact without allocating inside
    // stable sort. Keep the final original table for each raw four-byte tag.
    let mut order: Vec<usize> = reserved(face.tables.len())?;
    order.extend(0..face.tables.len());
    order.sort_unstable_by_key(|&i| (face.tables[i].tag, i));
    let mut selected: Vec<usize> = reserved(order.len())?;
    for index in order {
        if let Some(last) = selected.last_mut()
            && face.tables[*last].tag == face.tables[index].tag
        {
            *last = index;
        } else {
            selected.push(index);
        }
    }
    let count = u16::try_from(selected.len()).map_err(|_| INVALID)?;
    if count == 0 || (face.policy == Policy::Managed && count >= 4096) {
        return Err(INVALID);
    }
    let directory_bytes = 12 + selected.len() * 16;
    let mut size = align4(directory_bytes)?;
    for &index in &selected {
        if face.policy == Policy::Managed
            && face.tables[index].bytes.len() > (i32::MAX - 3) as usize
        {
            return Err(INVALID);
        }
        size = size
            .checked_add(align4(face.tables[index].bytes.len())?)
            .ok_or(INVALID)?;
        u32::try_from(size).map_err(|_| INVALID)?;
        if face.policy == Policy::Managed && size > i32::MAX as usize {
            return Err(INVALID);
        }
    }
    let mut output = filled(size, 0)?;
    put32(&mut output, 0, face.version)?;
    put16(&mut output, 4, count)?;
    let mut power = 1u16;
    while power <= count / 2 {
        power *= 2;
    }
    // Original u16 casts intentionally wrap the 4096-table searchRange to zero.
    let range = power.wrapping_mul(16);
    let mut selector = 0u16;
    let mut range_power = range / 16;
    while range_power > 1 {
        range_power /= 2;
        selector += 1;
    }
    put16(&mut output, 6, range)?;
    put16(&mut output, 8, selector)?;
    put16(
        &mut output,
        10,
        (u32::from(count) * 16 - u32::from(range)) as u16,
    )?;
    let mut offset = align4(directory_bytes)?;
    let mut head_offset = None;
    for (record_index, index) in selected.into_iter().enumerate() {
        let table = &face.tables[index];
        let record = 12 + record_index * 16;
        put32(&mut output, record, table.tag)?;
        // Whole-font fallback retains the original pre-adjustment head checksum.
        put32(&mut output, record + 4, checksum(&table.bytes))?;
        put32(&mut output, record + 8, offset as u32)?;
        put32(&mut output, record + 12, table.bytes.len() as u32)?;
        output[offset..offset + table.bytes.len()].copy_from_slice(&table.bytes);
        if table.tag == tag(b"head") && table.bytes.len() >= 12 {
            head_offset = Some(offset);
        }
        offset += align4(table.bytes.len())?;
    }
    if let Some(head) = head_offset {
        put32(&mut output, head + 8, 0)?;
        let adjustment = 0xb1b0afbau32.wrapping_sub(checksum(&output));
        put32(&mut output, head + 8, adjustment)?;
    }
    Ok(output)
}
