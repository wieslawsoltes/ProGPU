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

/// Authored TrueType source: both the simple child and scaled component vary.
pub fn varied_true_type_font() -> Vec<u8> {
    fn put16(data: &mut [u8], at: usize, value: u16) {
        data[at..at + 2].copy_from_slice(&value.to_be_bytes());
    }
    fn put32(data: &mut [u8], at: usize, value: u32) {
        data[at..at + 4].copy_from_slice(&value.to_be_bytes());
    }
    fn tuple(x: &[i8], y: &[i8]) -> Vec<u8> {
        let mut data = vec![
            0,
            1,
            0,
            10,
            0,
            (x.len() + y.len() + 2) as u8,
            0x80,
            0,
            0x40,
            0,
        ];
        data.push((x.len() - 1) as u8);
        data.extend(x.iter().map(|v| *v as u8));
        data.push((y.len() - 1) as u8);
        data.extend(y.iter().map(|v| *v as u8));
        data
    }
    let mut head = vec![0; 54];
    put16(&mut head, 18, 1000);
    put16(&mut head, 50, 1);
    let mut maxp = vec![0; 6];
    put16(&mut maxp, 4, 2);
    let mut glyf = vec![0; 14];
    put16(&mut glyf, 0, 1);
    put16(&mut glyf, 10, 3);
    glyf.extend([1; 4]);
    for v in [0i16, 60, 0, -60, 0, 0, 64, 0] {
        glyf.extend(v.to_be_bytes());
    }
    let mut loca = vec![0; 12];
    put32(&mut loca, 4, glyf.len() as u32);
    let mut component = vec![0; 20];
    put16(&mut component, 0, 65535);
    put16(&mut component, 10, 0x80b);
    put16(&mut component, 14, 16);
    put16(&mut component, 16, 16);
    put16(&mut component, 18, 4096);
    glyf.extend(component);
    put32(&mut loca, 8, glyf.len() as u32);
    let mut fvar = vec![0; 36];
    put16(&mut fvar, 4, 16);
    put16(&mut fvar, 8, 1);
    put16(&mut fvar, 10, 20);
    let mut gvar = vec![0; 32];
    put32(&mut gvar, 0, 0x10000);
    put16(&mut gvar, 4, 1);
    put32(&mut gvar, 8, 32);
    put16(&mut gvar, 12, 2);
    put16(&mut gvar, 14, 1);
    put32(&mut gvar, 16, 32);
    gvar.extend(tuple(&[0, 12, 12, 0, 0, 0, 0, 0], &[0; 8]));
    let end = (gvar.len() - 32) as u32;
    put32(&mut gvar, 24, end);
    gvar.extend(tuple(&[2, 0, 0, 0, 0], &[4, 0, 0, 0, 0]));
    let end = (gvar.len() - 32) as u32;
    put32(&mut gvar, 28, end);
    let tables = [
        (*b"head", head),
        (*b"maxp", maxp),
        (*b"loca", loca),
        (*b"glyf", glyf),
        (*b"fvar", fvar),
        (*b"gvar", gvar),
    ];
    let mut data = vec![0; 12 + tables.len() * 16];
    put32(&mut data, 0, 0x10000);
    put16(&mut data, 4, tables.len() as u16);
    for (i, (tag, table)) in tables.into_iter().enumerate() {
        let at = 12 + i * 16;
        data[at..at + 4].copy_from_slice(&tag);
        let offset = data.len() as u32;
        put32(&mut data, at + 8, offset);
        put32(&mut data, at + 12, table.len() as u32);
        data.extend(table);
    }
    data
}

/// Original varied simple points and scaled component offsets become GPU paths.
pub fn varied_true_type_rectangle() -> Result<Path, Box<dyn std::error::Error>> {
    let face = SfntFace::new(varied_true_type_font(), 0)?;
    let outline = face.varied_glyph_outline(1, &[8192])?;
    drop(face);
    Ok(Path::new(
        outline.segments().to_vec(),
        [4.25, 4.5, 20.75, 20.5],
        FillRule::NonZero,
    )?)
}
