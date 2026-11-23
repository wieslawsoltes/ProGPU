//! Independent Unicode fixture reader; no reference segmentation implementation.
use progpu::text::unicode::{DecodedText, grapheme::*};
use std::{
    error::Error,
    fs::File,
    io::{BufRead, BufReader},
};
fn main() -> Result<(), Box<dyn Error>> {
    let path = std::env::args_os()
        .nth(1)
        .ok_or("usage: grapheme_conformance GraphemeBreakTest.txt")?;
    let mut cases = 0;
    let mut failures = 0;
    for (line_number, line) in BufReader::new(File::open(path)?).lines().enumerate() {
        let line = line?;
        let body = line.split('#').next().unwrap().trim();
        if body.is_empty() {
            continue;
        }
        let (mut text, mut boundaries) = (String::new(), Vec::new());
        let mut scalars = 0;
        for token in body.split_whitespace() {
            match token {
                "÷" => boundaries.push(scalars),
                "×" => {}
                value => {
                    text.push(
                        char::from_u32(u32::from_str_radix(value, 16)?)
                            .ok_or("fixture has invalid scalar")?,
                    );
                    scalars += 1;
                }
            }
        }
        if boundaries.first() != Some(&0) || boundaries.last() != Some(&scalars) {
            return Err("fixture has incomplete endpoints".into());
        }
        let source16: Vec<_> = text.encode_utf16().collect();
        for decoded in [
            DecodedText::from_utf8(text.into_bytes())?,
            DecodedText::from_utf16(source16.clone())?,
        ] {
            let segmented = GraphemeText::new(decoded, GraphemePolicy::Unicode17)?;
            let mut actual: Vec<_> = segmented
                .clusters()
                .iter()
                .map(|c| c.scalar_index)
                .collect();
            actual.push(scalars);
            let ranges_match =
                segmented
                    .clusters()
                    .iter()
                    .zip(boundaries.windows(2))
                    .all(|(c, b)| {
                        let source = segmented.text().scalars();
                        let first = &source[b[0] as usize];
                        let last = &source[b[1] as usize - 1];
                        c.input_index == first.input_index
                            && c.input_length
                                == last.input_index + u32::from(last.input_length)
                                    - first.input_index
                            && c.scalar_count == b[1] - b[0]
                    });
            if actual != boundaries || !ranges_match {
                failures += 1;
                if failures <= 16 {
                    eprintln!(
                        "line {}: expected {boundaries:?}, actual {actual:?}",
                        line_number + 1
                    );
                }
            }
        }
        let mut starts = vec![0; source16.len()];
        utf16_starts_into(&source16, &mut starts)?;
        let decoded = DecodedText::from_utf16(source16)?;
        let mut expected = vec![0; starts.len()];
        for &boundary in boundaries.iter().take(boundaries.len() - 1) {
            expected[decoded.scalars()[boundary as usize].input_index as usize] = 1;
        }
        if starts != expected {
            failures += 1;
            if failures <= 16 {
                eprintln!("line {}: UTF-16 starts differ", line_number + 1);
            }
        }
        cases += 1;
    }
    println!(
        "grapheme_conformance: {cases} cases, UTF-8 + UTF-16 records + starts, {failures} failures"
    );
    if cases == 0 || failures != 0 {
        return Err("Unicode grapheme conformance failed".into());
    }
    Ok(())
}
