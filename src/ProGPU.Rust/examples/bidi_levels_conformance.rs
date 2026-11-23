//! Check resolved levels against official Unicode data, independently of C++/C#.
//! Visual L2 ordering is deliberately not asserted by this level-only runner.
use progpu::text::unicode::{
    UnicodeScalar,
    bidi::{BidiLevel, BidiWorkspace},
    bidi_class,
};
use std::{
    error::Error,
    fs::File,
    io::{BufRead, BufReader},
};
const NAMES: [&str; 23] = [
    "L", "R", "AL", "EN", "ES", "ET", "AN", "CS", "NSM", "BN", "B", "S", "WS", "ON", "LRE", "LRO",
    "RLE", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI",
];
const POINTS: [u32; 23] = [
    0x41, 0x5d0, 0x627, 0x31, 0x2b, 0x24, 0x661, 0x2c, 0x301, 0xad, 10, 9, 0x20, 0x21, 0x202a,
    0x202d, 0x202b, 0x202e, 0x202c, 0x2066, 0x2067, 0x2068, 0x2069,
];
fn expected_levels(value: &str) -> Result<Vec<Option<i8>>, Box<dyn Error>> {
    value
        .split_whitespace()
        .map(|s| {
            if s == "x" {
                Ok(None)
            } else {
                Ok(Some(s.parse()?))
            }
        })
        .collect()
}
fn check(
    points: &[u32],
    requested: i8,
    expected_paragraph: Option<i8>,
    expected: &[Option<i8>],
) -> Result<bool, Box<dyn Error>> {
    if points.len() != expected.len() {
        return Err("invalid expected level count".into());
    }
    let scalars: Vec<_> = points
        .iter()
        .enumerate()
        .map(|(i, &code_point)| UnicodeScalar {
            code_point,
            input_index: i as u32,
            input_length: 1,
            ..Default::default()
        })
        .collect();
    let mut workspace = BidiWorkspace::new(scalars.len())?;
    let mut output = vec![BidiLevel::default(); scalars.len()];
    let r = workspace.resolve_into(&scalars, requested, &mut output)?;
    Ok(expected_paragraph.is_none_or(|v| v == r.paragraph_level)
        && output
            .iter()
            .zip(expected)
            .all(|(actual, expected)| expected.is_none_or(|v| actual.level == v)))
}
fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() != 2 {
        return Err("usage: bidi_levels_conformance BidiCharacterTest.txt BidiTest.txt".into());
    }
    for (i, &cp) in POINTS.iter().enumerate() {
        assert_eq!(bidi_class(cp) as usize, i);
    }
    let (mut character_cases, mut type_cases, mut failed) = (0u64, 0u64, 0u64);
    for (line_no, line) in BufReader::new(File::open(&args[0])?).lines().enumerate() {
        let line = line?;
        let line = line.split('#').next().unwrap().trim();
        if line.is_empty() {
            continue;
        }
        let fields: Vec<_> = line.split(';').map(str::trim).collect();
        if fields.len() != 5 {
            return Err(format!("invalid character record {}", line_no + 1).into());
        }
        let points: Vec<_> = fields[0]
            .split_whitespace()
            .map(|v| u32::from_str_radix(v, 16))
            .collect::<Result<_, _>>()?;
        let requested = fields[1].parse()?;
        let expected_paragraph = Some(fields[2].parse()?);
        let levels = expected_levels(fields[3])?;
        character_cases += 1;
        if !check(&points, requested, expected_paragraph, &levels)? {
            failed += 1;
            if failed <= 16 {
                eprintln!("character level mismatch at line {}: {line}", line_no + 1);
            }
        }
    }
    let mut levels = Vec::new();
    for (line_no, line) in BufReader::new(File::open(&args[1])?).lines().enumerate() {
        let line = line?;
        let line = line.split('#').next().unwrap().trim();
        if line.is_empty() {
            continue;
        }
        if let Some(value) = line.strip_prefix("@Levels:") {
            levels = expected_levels(value)?;
            continue;
        }
        if line.starts_with("@Reorder:") {
            continue;
        }
        let fields: Vec<_> = line.split(';').map(str::trim).collect();
        if fields.len() != 2 {
            return Err(format!("invalid type record {}", line_no + 1).into());
        }
        let points: Vec<_> = fields[0]
            .split_whitespace()
            .map(|name| {
                NAMES
                    .iter()
                    .position(|&v| v == name)
                    .map(|i| POINTS[i])
                    .ok_or("unknown bidi class")
            })
            .collect::<Result<_, _>>()?;
        let mask = u8::from_str_radix(fields[1], 16)?;
        if mask == 0 || mask & !7 != 0 {
            return Err("invalid direction mask".into());
        }
        for (bit, requested) in [(1, 2), (2, 0), (4, 1)] {
            if mask & bit != 0 {
                type_cases += 1;
                if !check(&points, requested, None, &levels)? {
                    failed += 1;
                    if failed <= 16 {
                        eprintln!(
                            "type level mismatch at line {}, requested {requested}: {line}",
                            line_no + 1
                        );
                    }
                }
            }
        }
    }
    println!(
        "unicode_bidi_levels: character_cases={character_cases} type_direction_cases={type_cases} failures={failed}; X9 x-slots excluded by fixture contract; L2 visual ordering is a separate gate"
    );
    if character_cases == 0 || type_cases == 0 || failed != 0 {
        return Err("Unicode level conformance failed".into());
    }
    Ok(())
}
