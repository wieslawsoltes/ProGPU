//! Original-category oracle transport for the production independent parser.
#[path = "../src/text/script_actions/use_syllables.rs"]
mod syllables;
use std::io::{Read, Write};
fn main() -> std::io::Result<()> {
    let mut input = Vec::new();
    std::io::stdin().read_to_end(&mut input)?;
    let mut offset = 0;
    let word = |at: &mut usize| {
        let value = u32::from_le_bytes(input[*at..*at + 4].try_into().unwrap()) as usize;
        *at += 4;
        value
    };
    let font_length = word(&mut offset);
    offset += font_length;
    let mut output = std::io::BufWriter::new(std::io::stdout().lock());
    while offset < input.len() {
        let length = word(&mut offset);
        let categories = &input[offset..offset + length];
        offset += length;
        let mut scanner = syllables::Scanner::default();
        let (mut start, mut serial) = (0, 1u8);
        while start < length {
            let (end, kind) = scanner.next(start, length, |i| categories[i]);
            assert!(end > start && end <= length);
            for _ in start..end {
                output.write_all(&[serial << 4 | kind])?;
            }
            serial = serial % 15 + 1;
            start = end;
        }
    }
    output.flush()
}
