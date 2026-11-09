//! Transport for actual C# variation-data replay; no engine algorithm lives here.
use progpu::font::{
    FontError, ManagedVariationData, ManagedVariationInstance, ManagedVariationSetting, SfntFace,
};
fn emit(out: &mut Vec<u8>, n: u32) {
    out.extend(n.to_le_bytes());
}
fn text(out: &mut Vec<u8>, v: &[u16]) {
    emit(out, v.len() as u32);
    for &c in v {
        emit(out, c.into());
    }
}
pub fn source_glyph_count(font: &[u8]) -> Result<u16, FontError> {
    let count = u16::from_be_bytes(
        font.get(4..6)
            .ok_or(FontError::InvalidFace)?
            .try_into()
            .unwrap(),
    );
    let mut result = 0;
    for i in 0..usize::from(count) {
        let entry = font
            .get(12 + i * 16..28 + i * 16)
            .ok_or(FontError::InvalidFace)?;
        if &entry[..4] == b"maxp" {
            let at = u32::from_be_bytes(entry[8..12].try_into().unwrap()) as usize;
            result = u16::from_be_bytes(
                font.get(at + 4..at + 6)
                    .ok_or(FontError::InvalidFace)?
                    .try_into()
                    .unwrap(),
            );
        }
    }
    Ok(result)
}
pub fn name(n: u16) -> Option<std::sync::Arc<[u16]>> {
    match n % 3 {
        0 => None,
        1 => Some(vec![65, 120, 105, 115, 0, n].into()),
        _ => Some(vec![0xd800, n].into()),
    }
}
#[derive(Clone, Debug)]
pub struct Query {
    pub kind: u32,
    pub a: u16,
    pub b: u16,
    pub count: usize,
    pub points: Vec<[f32; 2]>,
    pub ends: Vec<u16>,
}
#[derive(Clone)]
pub struct Case {
    pub font: Vec<u8>,
    pub settings: Vec<ManagedVariationSetting>,
    pub queries: Vec<Query>,
}
impl Case {
    pub fn expected(&self) -> Result<Vec<u8>, FontError> {
        let face = SfntFace::new(self.font.clone(), 0)?;
        let data = ManagedVariationData::from_face(face, name)?;
        self.capture(data, None)
    }
    pub fn capture(
        &self,
        data: Option<ManagedVariationData>,
        selected: Option<Result<ManagedVariationInstance, FontError>>,
    ) -> Result<Vec<u8>, FontError> {
        let mut out = Vec::new();
        emit(&mut out, u32::from(data.is_some()));
        let Some(data) = data else { return Ok(out) };
        emit(&mut out, u32::from(data.uses_glyph_phantom_advance()));
        emit(&mut out, data.axes().len() as u32);
        for a in data.axes() {
            text(&mut out, &a.tag.map(u16::from));
            text(&mut out, &a.name);
            for v in [a.minimum, a.default, a.maximum] {
                emit(&mut out, v.to_bits());
            }
            emit(&mut out, u32::from(a.hidden));
        }
        let instance = selected.unwrap_or_else(|| data.create_instance(&self.settings));
        emit(&mut out, instance.as_ref().err().map_or(0, |e| *e as u32));
        let Ok(i) = instance else { return Ok(out) };
        drop(data);
        emit(&mut out, u32::from(i.is_default()));
        emit(&mut out, i.coordinates().len() as u32);
        for &c in i.coordinates() {
            emit(&mut out, c as i32 as u32);
        }
        text(
            &mut out,
            &i.coordinates()
                .iter()
                .map(|&v| v as u16)
                .collect::<Vec<_>>(),
        );
        emit(&mut out, i.settings().len() as u32);
        for s in i.settings() {
            text(&mut out, &s.tag);
            emit(&mut out, s.value.to_bits());
        }
        emit(&mut out, i.region_scalars().len() as u32);
        for &v in i.region_scalars() {
            emit(&mut out, v.to_bits());
        }
        for q in &self.queries {
            let mut delta = 0.0f32;
            let mut points = Vec::new();
            let result = (|| {
                match q.kind {
                    0 => delta = i.layout_delta(q.a, q.b)?,
                    1 => delta = i.advance_delta(q.a)?,
                    2 => {
                        delta = i.metric_delta(
                            &["hasc", "hdsc", "xhgt", "test", "???x"][usize::from(q.a) % 5]
                                .encode_utf16()
                                .collect::<Vec<_>>(),
                        )?
                    }
                    3 => {
                        points = q.points.clone();
                        i.apply_simple_deltas(q.a, &mut points, &q.ends)?;
                    }
                    4 => points = i.composite_deltas(q.a, q.count)?,
                    5 => delta = i.phantom_advance_delta(q.a, q.count)?,
                    _ => unreachable!(),
                }
                Ok::<_, FontError>(())
            })();
            emit(&mut out, result.err().map_or(0, |e| e as u32));
            emit(&mut out, delta.to_bits());
            emit(&mut out, points.len() as u32);
            for p in points {
                emit(&mut out, p[0].to_bits());
                emit(&mut out, p[1].to_bits());
            }
        }
        Ok(out)
    }
}

pub fn decode(font: Vec<u8>, words: &[u32]) -> Result<Case, FontError> {
    struct Reader<'a> {
        words: &'a [u32],
        at: usize,
    }
    impl Reader<'_> {
        fn take(&mut self, n: usize) -> Result<&[u32], FontError> {
            let end = self.at.checked_add(n).ok_or(FontError::InvalidArgument)?;
            let slice = self
                .words
                .get(self.at..end)
                .ok_or(FontError::InvalidArgument)?;
            self.at = end;
            Ok(slice)
        }
        fn word(&mut self) -> Result<u32, FontError> {
            Ok(self.take(1)?[0])
        }
        fn string(&mut self) -> Result<Vec<u16>, FontError> {
            let n = self.word()? as usize;
            let v = self.take(n)?;
            if v.iter().any(|&v| v > 65535) {
                return Err(FontError::InvalidArgument);
            }
            Ok(v.iter().map(|&v| v as u16).collect())
        }
    }
    let mut r = Reader { words, at: 0 };
    if r.word()? != u32::from(source_glyph_count(&font)?) {
        return Err(FontError::InvalidArgument);
    }
    let count = r.word()?;
    let mut settings = Vec::new();
    for _ in 0..count {
        let tag = r.string()?;
        let value = f32::from_bits(r.word()?);
        settings.push(ManagedVariationSetting::new(tag, value));
    }
    let count = r.word()?;
    let mut queries = Vec::new();
    for _ in 0..count {
        let kind = r.word()?;
        let a = r.word()?;
        let b = r.word()?;
        let count = r.word()? as usize;
        let n = r.word()? as usize;
        if kind > 5 || a > 65535 || b > 65535 || count > i32::MAX as usize {
            return Err(FontError::InvalidArgument);
        }
        let raw = r.take(n.checked_mul(2).ok_or(FontError::InvalidArgument)?)?;
        let points = raw
            .chunks_exact(2)
            .map(|p| [f32::from_bits(p[0]), f32::from_bits(p[1])])
            .collect();
        let ends = r.string()?;
        queries.push(Query {
            kind,
            a: a as u16,
            b: b as u16,
            count,
            points,
            ends,
        });
    }
    if r.at != words.len() {
        return Err(FontError::InvalidArgument);
    }
    Ok(Case {
        font,
        settings,
        queries,
    })
}
