//! Official fixture reader for original canonical forms; no reference engine.
use progpu::text::unicode::{DecodedText, normalization::*};
use std::{
    error::Error,
    fs::File,
    io::{BufRead, BufReader},
};
fn main() -> Result<(), Box<dyn Error>> {
    let path = std::env::args_os()
        .nth(1)
        .ok_or("usage: normalization_conformance NormalizationTest.txt")?;
    let (mut cases, mut checks, mut failures) = (0, 0, 0);
    for (line_number, line) in BufReader::new(File::open(path)?).lines().enumerate() {
        let line = line?;
        let body = line.split('#').next().unwrap().trim();
        if body.is_empty() || body.starts_with('@') {
            continue;
        }
        let columns: Vec<Vec<u32>> = body
            .split(';')
            .take(5)
            .map(|column| {
                column
                    .split_whitespace()
                    .map(|v| u32::from_str_radix(v, 16))
                    .collect()
            })
            .collect::<Result<_, _>>()?;
        if columns.len() != 5 {
            return Err("fixture lacks five columns".into());
        }
        for (index, column) in columns.iter().enumerate() {
            let text: String = column
                .iter()
                .map(|&cp| char::from_u32(cp).ok_or("invalid fixture scalar"))
                .collect::<Result<_, _>>()?;
            for decoded in [
                DecodedText::from_utf8(text.as_bytes().to_vec())?,
                DecodedText::from_utf16(text.encode_utf16().collect::<Vec<_>>())?,
            ] {
                for form in [
                    NormalizationForm::CanonicalDecomposition,
                    NormalizationForm::CanonicalComposition,
                ] {
                    let expected_index = match (form, index < 3) {
                        (NormalizationForm::CanonicalDecomposition, true) => 2,
                        (NormalizationForm::CanonicalDecomposition, false) => 4,
                        (NormalizationForm::CanonicalComposition, true) => 1,
                        (NormalizationForm::CanonicalComposition, false) => 3,
                    };
                    let normalized = NormalizedText::new(decoded.clone(), form)?;
                    let actual: Vec<_> =
                        normalized.scalars().iter().map(|s| s.code_point).collect();
                    checks += 1;
                    if actual != columns[expected_index] {
                        failures += 1;
                        if failures <= 16 {
                            eprintln!(
                                "line {} column {} {form:?}: expected {:x?}, actual {actual:x?}",
                                line_number + 1,
                                index + 1,
                                columns[expected_index]
                            );
                        }
                    }
                }
            }
        }
        cases += 1;
    }
    println!(
        "normalization_conformance: {cases} cases, {checks} UTF-8/UTF-16 canonical checks, {failures} failures"
    );
    if cases == 0 || failures != 0 {
        return Err("Unicode canonical normalization conformance failed".into());
    }
    Ok(())
}
