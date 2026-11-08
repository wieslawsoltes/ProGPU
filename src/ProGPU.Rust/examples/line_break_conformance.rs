//! Parse official Unicode fixtures; no reference line-break engine is embedded.
use progpu::text::unicode::{DecodedText, line_break::*};
use std::{
    error::Error,
    fs::File,
    io::{BufRead, BufReader},
};
fn main() -> Result<(), Box<dyn Error>> {
    let path = std::env::args_os()
        .nth(1)
        .ok_or("usage: line_break_conformance LineBreakTest.txt")?;
    let (mut cases, mut failures) = (0, 0);
    for (number, line) in BufReader::new(File::open(path)?).lines().enumerate() {
        let line = line?;
        let body = line.split('#').next().unwrap().trim();
        if body.is_empty() {
            continue;
        }
        let (mut text, mut boundaries) = (String::new(), Vec::new());
        let mut count = 0usize;
        for token in body.split_whitespace() {
            match token {
                "÷" => boundaries.push(count),
                "×" => {}
                value => {
                    text.push(
                        char::from_u32(u32::from_str_radix(value, 16)?)
                            .ok_or("invalid fixture scalar")?,
                    );
                    count += 1;
                }
            }
        }
        if boundaries.first() == Some(&0) || boundaries.last() != Some(&count) {
            return Err("invalid fixture endpoints".into());
        }
        for decoded in [
            DecodedText::from_utf8(text.as_bytes().to_vec())?,
            DecodedText::from_utf16(text.encode_utf16().collect::<Vec<_>>())?,
        ] {
            let result = LineBreakText::new(decoded, LineBreakPolicy::Unicode17)?;
            let actual: Vec<_> = result
                .breaks_after()
                .iter()
                .enumerate()
                .filter_map(|(i, &b)| (b != LineBreakKind::Prohibited).then_some(i + 1))
                .collect();
            if actual != boundaries {
                failures += 1;
                if failures <= 16 {
                    eprintln!(
                        "line {}: expected {boundaries:?}, actual {actual:?}",
                        number + 1
                    );
                }
            }
        }
        cases += 1;
    }
    println!("line_break_conformance: {cases} cases, UTF-8 and UTF-16, {failures} failures");
    if cases == 0 || failures != 0 {
        return Err("Unicode line-break conformance failed".into());
    }
    Ok(())
}
