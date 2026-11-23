//! Original bounded breadth-first composite closure and sparse long-loca writer.
use super::*;
pub(super) fn read_loca(data: &[u8], count: u16, format: i16) -> Result<Vec<u32>> {
    let length = usize::from(count) + 1;
    let mut result = reserved(length)?;
    match format {
        0 => {
            slice(data, 0, length * 2)?;
            for index in 0..length {
                result.push(u32::from(u16be(data, index * 2)?) * 2);
            }
        }
        1 => {
            slice(data, 0, length * 4)?;
            for index in 0..length {
                result.push(u32be(data, index * 4)?);
            }
        }
        _ => return Err(INVALID),
    }
    Ok(result)
}
pub(super) fn component_end(offset: usize, flags: u16, length: usize) -> Result<usize> {
    let args = if flags & 1 != 0 { 4 } else { 2 };
    let transform = if flags & 8 != 0 {
        2
    } else if flags & 0x40 != 0 {
        4
    } else if flags & 0x80 != 0 {
        8
    } else {
        0
    };
    let end = offset.checked_add(4 + args + transform).ok_or(INVALID)?;
    if end > length { Err(INVALID) } else { Ok(end) }
}
pub(super) fn include(
    glyf: &[u8],
    offsets: &[u32],
    count: u16,
    requested: &[u16],
    policy: Policy,
) -> Result<Vec<bool>> {
    let mut included = filled(usize::from(count), false)?;
    included[0] = true;
    for &glyph in requested {
        if let Some(slot) = included.get_mut(usize::from(glyph)) {
            *slot = true;
        }
    }
    // Each ID enters exactly once. A vector plus cursor retains original FIFO
    // order and bounds all scratch by the original u16 glyph inventory.
    let mut queue = reserved(usize::from(count))?;
    for (index, &keep) in included.iter().enumerate() {
        if keep {
            queue.push(index as u16);
        }
    }
    let mut cursor = 0;
    while cursor < queue.len() {
        let glyph = usize::from(queue[cursor]);
        cursor += 1;
        let start = offsets[glyph] as usize;
        let end = offsets[glyph + 1] as usize;
        if policy == Policy::Managed {
            // Managed uses the whole glyf table as the read boundary and keeps
            // already-found components when the next component is truncated.
            if start >= end
                || end > glyf.len()
                || slice(glyf, start, 10).is_err()
                || i16be(glyf, start)? >= 0
            {
                continue;
            }
            let mut offset = start + 10;
            while slice(glyf, offset, 4).is_ok() {
                let flags = u16be(glyf, offset)?;
                let component = u16be(glyf, offset + 2)?;
                if let Some(keep) = included.get_mut(usize::from(component))
                    && !*keep
                {
                    *keep = true;
                    queue.push(component);
                }
                offset = component_end(offset, flags, usize::MAX)?;
                if flags & 0x20 == 0 {
                    break;
                }
            }
            continue;
        }
        // Exact empty offset pairs skip even when the offset exceeds glyf.
        if start == end {
            continue;
        }
        let data = slice(glyf, start, end.checked_sub(start).ok_or(INVALID)?)?;
        if data.len() < 10 || i16be(data, 0)? >= 0 {
            continue;
        }
        let mut offset = 10;
        loop {
            slice(data, offset, 4)?;
            let flags = u16be(data, offset)?;
            let component = u16be(data, offset + 2)?;
            if let Some(keep) = included.get_mut(usize::from(component))
                && !*keep
            {
                *keep = true;
                queue.push(component);
            }
            offset = component_end(offset, flags, data.len())?;
            if flags & 0x20 == 0 {
                break;
            }
        }
    }
    Ok(included)
}
pub(super) fn append(glyf: &mut Vec<u8>, data: &[u8]) -> Result<()> {
    let end = glyf.len().checked_add(data.len()).ok_or(INVALID)?;
    let padded = align4(end)?;
    u32::try_from(padded).map_err(|_| INVALID)?;
    glyf.try_reserve(padded - glyf.len()).map_err(|_| INVALID)?;
    glyf.extend_from_slice(data);
    glyf.resize(padded, 0);
    Ok(())
}
pub(super) fn make_loca(offsets: &[u32]) -> Result<Vec<u8>> {
    let mut result = filled(offsets.len().checked_mul(4).ok_or(INVALID)?, 0)?;
    for (index, &offset) in offsets.iter().enumerate() {
        put32(&mut result, index * 4, offset)?;
    }
    Ok(result)
}
pub(super) fn preserving(
    data: &[u8],
    directory: usize,
    requested: &[u16],
    policy: Policy,
) -> Result<Vec<u8>> {
    let mut face = tables::parse(data, directory, policy)?;
    let original = match (
        face.find(tag(b"head")),
        face.find(tag(b"maxp")),
        face.find(tag(b"loca")),
        face.find(tag(b"glyf")),
    ) {
        (Some(head), Some(maxp), Some(loca), Some(glyf)) => Some((head, maxp, loca, glyf)),
        _ => None,
    };
    let Some((head, maxp, loca, source_glyf)) = original else {
        face.tables.retain(|table| table.tag != tag(b"DSIG"));
        return tables::build(face);
    };
    if head.len() < 54 || maxp.len() < 6 {
        return Err(INVALID);
    }
    let count = u16be(maxp, 4)?;
    if count == 0 {
        face.tables.retain(|table| table.tag != tag(b"DSIG"));
        return tables::build(face);
    }
    let source_offsets = read_loca(loca, count, i16be(head, 50)?)?;
    let included = include(source_glyf, &source_offsets, count, requested, policy)?;
    let mut offsets = reserved(source_offsets.len())?;
    let mut glyf = Vec::new();
    for (glyph, &keep) in included.iter().enumerate() {
        offsets.push(u32::try_from(glyf.len()).map_err(|_| INVALID)?);
        if keep {
            let start = source_offsets[glyph] as usize;
            let end = source_offsets[glyph + 1] as usize;
            if policy == Policy::Managed && start == end {
                continue;
            }
            append(
                &mut glyf,
                slice(source_glyf, start, end.checked_sub(start).ok_or(INVALID)?)?,
            )?;
        }
    }
    offsets.push(u32::try_from(glyf.len()).map_err(|_| INVALID)?);
    let loca = make_loca(&offsets)?;
    let mut head = copy(head)?;
    put32(&mut head, 8, 0)?;
    put16(&mut head, 50, 1)?;
    face.tables.retain(|table| {
        ![tag(b"DSIG"), tag(b"head"), tag(b"loca"), tag(b"glyf")].contains(&table.tag)
    });
    face.tables.try_reserve(3).map_err(|_| INVALID)?;
    face.tables.extend([
        Table::owned(tag(b"head"), head),
        Table::owned(tag(b"loca"), loca),
        Table::owned(tag(b"glyf"), glyf),
    ]);
    tables::build(face)
}
