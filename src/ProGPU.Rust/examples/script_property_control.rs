//! Complete original script-property response for source differential validation.
#[path = "../tests/support/script_property_replay.rs"]
mod replay;
use std::io::{Read, Write};

fn main() -> std::io::Result<()> {
    let mut request = Vec::new();
    std::io::stdin().read_to_end(&mut request)?;
    assert_eq!(request.len() % 4, 0);
    let mut output = std::io::BufWriter::new(std::io::stdout().lock());
    for raw in 0..=u16::MAX {
        output.write_all(&replay::extraction(raw))?;
    }
    for bytes in request.chunks_exact(4) {
        output.write_all(&replay::record(u32::from_le_bytes(
            bytes.try_into().unwrap(),
        )))?;
    }
    output.flush()
}
