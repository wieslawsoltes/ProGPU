use super::fixture;
pub(super) fn store(axes: u16, regions: &[[i16; 3]], tables: &[Vec<u8>]) -> Vec<u8> {
    assert_eq!(regions.len() % usize::from(axes.max(1)), 0);
    let mut data = vec![0; 8 + tables.len() * 4];
    fixture::u16_at(&mut data, 0, 1);
    let region_offset = data.len() as u32;
    fixture::u32_at(&mut data, 2, region_offset);
    fixture::u16_at(&mut data, 6, tables.len() as u16);
    data.extend_from_slice(&axes.to_be_bytes());
    data.extend_from_slice(&((regions.len() / usize::from(axes.max(1))) as u16).to_be_bytes());
    for region in regions {
        for v in region {
            data.extend_from_slice(&v.to_be_bytes());
        }
    }
    for (i, table) in tables.iter().enumerate() {
        if !table.is_empty() {
            let offset = data.len() as u32;
            fixture::u32_at(&mut data, 8 + i * 4, offset);
            data.extend_from_slice(table);
        }
    }
    data
}
pub(super) fn table(indices: &[u16], words: u16, rows: &[Vec<i32>]) -> Vec<u8> {
    let mut data = Vec::new();
    for value in [rows.len() as u16, words, indices.len() as u16] {
        data.extend_from_slice(&value.to_be_bytes());
    }
    for i in indices {
        data.extend_from_slice(&i.to_be_bytes());
    }
    for row in rows {
        assert_eq!(row.len(), indices.len());
        for (i, value) in row.iter().enumerate() {
            if i < usize::from(words & 0x7fff) && words & 0x8000 != 0 {
                data.extend_from_slice(&value.to_be_bytes());
            } else if i < usize::from(words & 0x7fff) || words & 0x8000 != 0 {
                data.extend_from_slice(&(*value as i16).to_be_bytes());
            } else {
                data.push(*value as u8);
            }
        }
    }
    data
}
