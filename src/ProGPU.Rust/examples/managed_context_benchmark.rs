//! Exact original managed context packet and preparation/query baseline.
use progpu::font::layout::*;
use std::{hint::black_box, io::Write, sync::Arc, time::Instant};
struct Reader {
    data: Vec<u8>,
    at: usize,
}
impl Reader {
    fn n(&mut self) -> u32 {
        let value = u32::from_le_bytes(self.data[self.at..self.at + 4].try_into().unwrap());
        self.at += 4;
        value
    }
    fn bytes(&mut self, n: usize) -> &[u8] {
        let at = self.at;
        self.at += n;
        &self.data[at..at + n]
    }
}
fn emit(b: &mut Vec<u8>, n: u32) {
    b.extend(n.to_le_bytes());
}
fn report(name: &str, mut times: Vec<f64>) {
    times.sort_by(f64::total_cmp);
    eprintln!(
        "{name}_p50_us={:.3} {name}_p95_us={:.3} {name}_p99_us={:.3}",
        times[19], times[37], times[39]
    );
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    assert_eq!(args.len(), 2);
    let mut r = Reader {
        data: std::fs::read(&args[1]).unwrap(),
        at: 0,
    };
    let font = r.n() as usize;
    r.bytes(font);
    let length = r.n() as usize;
    let index = r.n() as u16;
    let extension = r.n() as u16;
    let queries = r.n() as usize;
    assert_eq!(r.n(), 1);
    let data: Arc<[u8]> = r.bytes(length).into();
    let queries: Vec<_> = (0..queries).map(|_| r.n() as u16).collect();
    let bits = r.n();
    let state = ManagedContextState {
        manual_joiners: bits & 1 != 0,
        restrict_to_syllable: bits & 2 != 0,
        lookup_syllable: r.n() as u8,
    };
    let count = r.n() as usize;
    let all = r.n() != 0;
    let glyphs: Vec<_> = (0..count)
        .map(|_| ManagedContextGlyph {
            glyph_index: r.n() as u16,
            code_point: r.n(),
            substituted: r.n() as u8,
            script_shaper: r.n() as u8,
            script_feature_mask: r.n() as u8,
            use_syllable: r.n() as u8,
        })
        .collect();
    assert_eq!(r.at, r.data.len());
    let mut input = ManagedContextInput::new(&glyphs, state);
    if all {
        let mut digest = input.digest();
        digest.add_range(0, 65535);
        input = ManagedContextInput::with_digest(&glyphs, state, digest);
    }
    let mut times = vec![];
    for round in 0..45 {
        let start = Instant::now();
        let p = PreparedManagedContext::new(data.clone(), index, extension)
            .unwrap()
            .unwrap();
        black_box(&p);
        let us = start.elapsed().as_secs_f64() * 1e6;
        if round >= 5 {
            times.push(us);
        }
    }
    report("prepare", times);
    let prepared = PreparedManagedContext::new(data, index, extension)
        .unwrap()
        .unwrap();
    let view = prepared.view();
    let matched = view.may_match(input);
    let mut times = vec![];
    for round in 0..45 {
        let start = Instant::now();
        let mut matches = 0;
        for _ in 0..100 {
            matches += u32::from(black_box(view).may_match(black_box(input)));
        }
        let us = start.elapsed().as_secs_f64() * 1e6 / 100.;
        assert_eq!(matches, if matched { 100 } else { 0 });
        if round >= 5 {
            times.push(us);
        }
    }
    report("query", times);
    let mut out = vec![];
    for n in [
        1,
        u32::from(view.flags()),
        view.subtables().len() as u32,
        view.coverage_count() as u32,
    ] {
        emit(&mut out, n);
    }
    for sub in view.subtables() {
        for n in [
            sub.coverage_offset,
            sub.coverage_count,
            u32::from(sub.backtrack_count),
            u32::from(sub.input_count),
        ] {
            emit(&mut out, n);
        }
    }
    for i in 0..view.coverage_count() {
        let c = view.coverage(i).unwrap();
        for m in c.digest().masks() {
            out.extend(m.to_le_bytes());
        }
        for &q in &queries {
            emit(&mut out, u32::from(c.contains(q)));
        }
    }
    emit(&mut out, u32::from(matched));
    std::io::stdout().write_all(&out).unwrap();
}
