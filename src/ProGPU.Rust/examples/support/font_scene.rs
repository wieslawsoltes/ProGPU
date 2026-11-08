//! Authored CFF source fixture passing through the production decoder and GPU paths.
use progpu::{
    font::{SfntFace, cff::OutlineTransform},
    path::{FillRule, Path},
};
pub fn cff_rectangle() -> Result<Path, Box<dyn std::error::Error>> {
    let cff = [
        1, 0, 4, 4, 0, 0, 0, 1, 1, 1, 3, 156, 17, 0, 0, 0, 0, 0, 1, 1, 1, 12, 139, 139, 21, 239,
        139, 139, 239, 39, 139, 5, 14,
    ];
    let mut data = vec![0u8; 28 + cff.len()];
    data[..4].copy_from_slice(b"OTTO");
    data[4..6].copy_from_slice(&1u16.to_be_bytes());
    data[12..16].copy_from_slice(b"CFF ");
    data[20..24].copy_from_slice(&28u32.to_be_bytes());
    data[24..28].copy_from_slice(&(cff.len() as u32).to_be_bytes());
    data[28..].copy_from_slice(&cff);
    let face = SfntFace::new(data, 0)?;
    let font = face.cff1(1)?;
    let outline =
        font.outline_transformed(0, OutlineTransform([0.165, 0.0, 0.0, 0.16, 4.25, 4.5]))?;
    drop(font);
    drop(face);
    Ok(Path::new(
        outline.segments().to_vec(),
        [4.25, 4.5, 20.75, 20.5],
        FillRule::NonZero,
    )?)
}

/// Variable CFF2 source lowered through the production evaluator at half peak.
pub fn cff2_rectangle() -> Result<Path, Box<dyn std::error::Error>> {
    fn operand(p: &mut Vec<u8>, value: f32) {
        p.push(255);
        p.extend(((value * 65536.0) as i32).to_be_bytes());
    }
    fn index(data: &[u8]) -> Vec<u8> {
        let mut b = vec![0, 0, 0, 1, 1, 1, (data.len() + 1) as u8];
        b.extend(data);
        b
    }
    fn offset(top: &mut Vec<u8>, value: usize, op: &[u8]) {
        top.push(29);
        top.extend((value as u32).to_be_bytes());
        top.extend(op);
    }
    let mut program = Vec::new();
    operand(&mut program, 4.25);
    operand(&mut program, 4.5);
    program.push(21);
    for (base, delta) in [(15.5, 2.0), (-15.5, -2.0)] {
        operand(&mut program, base);
        operand(&mut program, delta);
        program.extend([140, 16, 6]);
        if base > 0.0 {
            operand(&mut program, 16.0);
            program.push(7);
        }
    }
    let chars = index(&program);
    let dictionaries = index(&[139, 139, 18]);
    let mut top = Vec::new();
    offset(&mut top, 28, &[17]);
    offset(&mut top, 28 + chars.len(), &[12, 36]);
    offset(&mut top, 28 + chars.len() + dictionaries.len(), &[24]);
    let mut cff = vec![2, 0, 5, 0, 19];
    cff.extend(top);
    cff.extend([0; 4]);
    cff.extend(chars);
    cff.extend(dictionaries);
    cff.extend([
        0, 30, 0, 1, 0, 0, 0, 12, 0, 1, 0, 0, 0, 22, 0, 1, 0, 1, 0, 0, 32, 0, 64, 0, 0, 0, 0, 0, 0,
        1, 0, 0,
    ]);
    let mut head = vec![0; 54];
    head[18..20].copy_from_slice(&1000u16.to_be_bytes());
    let mut fvar = vec![0; 36];
    for (at, value) in [(4, 16u16), (8, 1), (10, 20)] {
        fvar[at..at + 2].copy_from_slice(&value.to_be_bytes());
    }
    let mut data = vec![0; 60];
    data[..4].copy_from_slice(b"OTTO");
    data[4..6].copy_from_slice(&3u16.to_be_bytes());
    for (i, (tag, table)) in [(*b"CFF2", cff), (*b"head", head), (*b"fvar", fvar)]
        .into_iter()
        .enumerate()
    {
        let at = 12 + i * 16;
        data[at..at + 4].copy_from_slice(&tag);
        let offset = data.len() as u32;
        data[at + 8..at + 12].copy_from_slice(&offset.to_be_bytes());
        data[at + 12..at + 16].copy_from_slice(&(table.len() as u32).to_be_bytes());
        data.extend(table);
    }
    let face = SfntFace::new(data, 0)?;
    let font = face.cff2(1)?;
    let outline = font.outline(0, &[4096])?;
    drop(font);
    drop(face);
    Ok(Path::new(
        outline.segments().to_vec(),
        [4.25, 4.5, 20.75, 20.5],
        FillRule::NonZero,
    )?)
}
