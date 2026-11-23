//! Transport only: complete original managed font snapshots and ordered operations.
use progpu::font::{FontError, ManagedFont, ManagedVariationSetting};
pub fn emit(out: &mut Vec<u8>, v: u32) {
    out.extend(v.to_le_bytes());
}
pub fn text(out: &mut Vec<u8>, value: impl IntoIterator<Item = u16>) {
    let v: Vec<_> = value.into_iter().collect();
    emit(out, v.len() as u32);
    for c in v {
        emit(out, c.into());
    }
}
#[derive(Clone)]
pub struct Op {
    pub kind: u32,
    pub from: usize,
    pub index: i32,
    pub settings: Vec<ManagedVariationSetting>,
    pub colors: Vec<(u16, u32)>,
}
#[derive(Clone)]
pub struct Case {
    pub font: Vec<u8>,
    pub face_index: u32,
    pub ops: Vec<Op>,
    pub queries: Vec<(u32, u16, u16, u32)>,
}
#[allow(dead_code)] // Browser consumes captured input; native tests author typed cases.
pub fn decode(input: &[u8]) -> Result<Case, FontError> {
    struct Reader<'a> {
        bytes: &'a [u8],
        at: usize,
    }
    impl Reader<'_> {
        fn take(&mut self, n: usize) -> Result<&[u8], FontError> {
            let end = self.at.checked_add(n).ok_or(FontError::InvalidArgument)?;
            let value = self
                .bytes
                .get(self.at..end)
                .ok_or(FontError::InvalidArgument)?;
            self.at = end;
            Ok(value)
        }
        fn word(&mut self) -> Result<u32, FontError> {
            Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))
        }
        fn count(&mut self, min: usize) -> Result<usize, FontError> {
            let n = self.word()? as usize;
            if n > (self.bytes.len() - self.at) / min {
                Err(FontError::InvalidArgument)
            } else {
                Ok(n)
            }
        }
        fn short(&mut self) -> Result<u16, FontError> {
            u16::try_from(self.word()?).map_err(|_| FontError::InvalidArgument)
        }
    }
    let mut r = Reader {
        bytes: input,
        at: 0,
    };
    let n = r.count(1)?;
    let font = r.take(n)?.to_vec();
    let face_index = r.word()?;
    let n = r.count(20)?;
    let mut ops = Vec::with_capacity(n);
    for _ in 0..n {
        let kind = r.word()?;
        let from = r.word()? as usize;
        let index = r.word()? as i32;
        let count = r.count(8)?;
        let mut settings = Vec::with_capacity(count);
        for _ in 0..count {
            let count = r.count(4)?;
            let mut tag = Vec::with_capacity(count);
            for _ in 0..count {
                tag.push(r.short()?);
            }
            let value = f32::from_bits(r.word()?);
            settings.push(ManagedVariationSetting::new(tag, value));
        }
        let count = r.count(8)?;
        let mut colors = Vec::with_capacity(count);
        for _ in 0..count {
            colors.push((r.short()?, r.word()?));
        }
        ops.push(Op {
            kind,
            from,
            index,
            settings,
            colors,
        });
    }
    let count = r.count(16)?;
    let mut queries = Vec::with_capacity(count);
    for _ in 0..count {
        queries.push((r.word()?, r.short()?, r.short()?, r.word()?));
    }
    if r.at != input.len() {
        return Err(FontError::InvalidArgument);
    }
    Ok(Case {
        font,
        face_index,
        ops,
        queries,
    })
}
fn error(result: Result<f32, FontError>, out: &mut Vec<u8>) {
    emit(out, result.err().map_or(0, |e| e as u32));
    emit(out, result.unwrap_or(0.0).to_bits());
}
pub fn snapshot(
    f: &ManagedFont,
    history: &[Option<ManagedFont>],
    queries: &[(u32, u16, u16, u32)],
    out: &mut Vec<u8>,
) -> Result<(), FontError> {
    let m = f.metadata();
    for s in [&m.family, &m.subfamily, &m.full_name, &m.postscript_name] {
        text(out, s.encode_utf16());
    }
    for v in [
        u32::from(m.weight_class),
        u32::from(m.width_class),
        u32::from(m.italic),
        u32::from(m.has_true_type_outlines),
        u32::from(m.has_cff_outlines),
        u32::from(m.has_bitmap_glyphs),
        u32::from(m.has_color_glyphs),
        u32::from(m.fixed_pitch),
        u32::from(m.units_per_em),
        u32::from(m.glyph_count),
    ] {
        emit(out, v);
    }
    for v in m
        .bounds
        .into_iter()
        .chain([m.ascender, m.descender, m.line_gap])
    {
        emit(out, v as i32 as u32);
    }
    for v in [
        m.underline_position,
        m.underline_thickness,
        m.strikeout_position,
        m.strikeout_thickness,
        m.x_height,
        m.cap_height,
    ] {
        emit(out, u32::from(v.is_some()));
        emit(out, v.unwrap_or(0) as i32 as u32);
    }
    emit(out, m.palette.len() as u32);
    for color in m.palette.iter() {
        for v in color {
            emit(out, v.to_bits());
        }
    }
    emit(out, u32::from(f.has_active_variations()));
    let axes = f.variation_axes()?;
    emit(out, axes.len() as u32);
    for a in axes {
        text(out, a.tag.map(u16::from));
        text(out, a.name.iter().copied());
        for v in [a.minimum, a.default, a.maximum] {
            emit(out, v.to_bits());
        }
        emit(out, u32::from(a.hidden));
    }
    let settings = f.variation_settings();
    emit(out, settings.len() as u32);
    for s in settings {
        text(out, s.tag.iter().copied());
        emit(out, s.value.to_bits());
    }
    for axis in -1..axes.len() as i32 + 1 {
        let c = f.normalized_coordinate(axis)?;
        emit(out, u32::from(c.is_some()));
        emit(out, c.unwrap_or(0) as i32 as u32);
    }
    emit(out, history.len() as u32);
    for h in history {
        emit(
            out,
            u32::from(h.as_ref().is_some_and(|h| f.same_instance(h))),
        );
        let equal = h.as_ref().is_some_and(|h| f == h);
        emit(out, u32::from(equal));
    }
    for &(kind, a, b, bits) in queries {
        match kind {
            0 => error(f.advance_width(a, f32::from_bits(bits)), out),
            1 => error(f.advance_height(a, f32::from_bits(bits)), out),
            2 => error(f.layout_delta(a, b), out),
            3 => {
                let result = f.component_variations(a, usize::from(b));
                emit(out, result.as_ref().err().map_or(0, |e| *e as u32));
                let points = result.unwrap_or_default();
                emit(out, points.len() as u32);
                for p in points {
                    for v in p {
                        emit(out, v.to_bits());
                    }
                }
            }
            4 => {
                let mut points = [[0.0, 3.0], [17.0, 32.0], [33.0, -15.0], [55.0, 5.0]];
                let result = f.apply_simple_variations(a, &mut points, &[3]);
                emit(out, result.err().map_or(0, |e| e as u32));
                for p in points {
                    for v in p {
                        emit(out, v.to_bits());
                    }
                }
            }
            _ => return Err(FontError::InvalidArgument),
        }
    }
    Ok(())
}
impl Case {
    #[allow(dead_code)] // The benchmark serializes its actual timed snapshot.
    pub fn expected(&self) -> Result<Vec<u8>, FontError> {
        let mut out = Vec::new();
        let root = ManagedFont::new(self.font.clone(), self.face_index);
        emit(&mut out, root.as_ref().err().map_or(0, |e| *e as u32));
        let Ok(root) = root else { return Ok(out) };
        let second = ManagedFont::new(self.font.clone(), self.face_index)?;
        let mut frames = vec![Some(root), Some(second)];
        snapshot(
            frames[0].as_ref().unwrap(),
            &frames,
            &self.queries,
            &mut out,
        )?;
        for op in &self.ops {
            let result = frames
                .get(op.from)
                .and_then(|f| f.as_ref())
                .ok_or(FontError::InvalidArgument)
                .and_then(|f| match op.kind {
                    0 => f.with_variations(&op.settings),
                    1 => f.with_color_palette(op.index, &op.colors),
                    2 => Ok(f.root()),
                    3 => Ok(f.clone()),
                    _ => Err(FontError::InvalidArgument),
                });
            emit(&mut out, result.as_ref().err().map_or(0, |e| *e as u32));
            if let Ok(f) = &result {
                snapshot(f, &frames, &self.queries, &mut out)?;
            }
            frames.push(result.ok());
        }
        Ok(out)
    }
    #[allow(dead_code)]
    pub fn write(&self, out: &mut Vec<u8>) {
        emit(out, self.font.len() as u32);
        out.extend(&self.font);
        emit(out, self.face_index);
        emit(out, self.ops.len() as u32);
        for o in &self.ops {
            emit(out, o.kind);
            emit(out, o.from as u32);
            emit(out, o.index as u32);
            emit(out, o.settings.len() as u32);
            for s in &o.settings {
                text(out, s.tag.iter().copied());
                emit(out, s.value.to_bits());
            }
            emit(out, o.colors.len() as u32);
            for &(i, c) in &o.colors {
                emit(out, i.into());
                emit(out, c);
            }
        }
        emit(out, self.queries.len() as u32);
        for &(kind, a, b, bits) in &self.queries {
            for v in [kind, a.into(), b.into(), bits] {
                emit(out, v);
            }
        }
    }
}
