//! Direct original ProGPU zlib/DEFLATE port, including native malformed-stream
//! admission and caller-owned output. Tables and history remain bounded; variable
//! bitstream decoding, Huffman traversal and overlapping copies are dependent.
use super::PngDecodeError as Error;
const INVALID: Error = Error::InvalidCompressedData;
const LENGTH_BASE: [u16; 29] = [
    3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131,
    163, 195, 227, 258,
];
const LENGTH_EXTRA: [u8; 29] = [
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
];
const DISTANCE_BASE: [u16; 30] = [
    1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537,
    2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
];
const DISTANCE_EXTRA: [u8; 30] = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13,
    13,
];
const CODE_ORDER: [usize; 19] = [
    16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
];
struct Reader<'a> {
    input: &'a [u8],
    offset: usize,
    bits: u64,
    count: u8,
}
impl<'a> Reader<'a> {
    fn new(input: &'a [u8]) -> Self {
        Self {
            input,
            offset: 0,
            bits: 0,
            count: 0,
        }
    }
    fn read(&mut self, count: u8) -> Result<u32, Error> {
        if count > 24 {
            return Err(INVALID);
        }
        while self.count < count {
            let byte = *self.input.get(self.offset).ok_or(INVALID)?;
            self.offset += 1;
            self.bits |= u64::from(byte) << self.count;
            self.count += 8;
        }
        let mask = if count == 0 { 0 } else { (1u64 << count) - 1 };
        let value = (self.bits & mask) as u32;
        self.bits >>= count;
        self.count -= count;
        Ok(value)
    }
    fn align(&mut self) {
        let discard = self.count & 7;
        self.bits >>= discard;
        self.count -= discard;
    }
}
struct Huffman {
    counts: [u16; 16],
    symbols: [u16; 288],
    count: u16,
}
impl Huffman {
    fn new(lengths: &[u8]) -> Result<Self, Error> {
        let mut result = Self {
            counts: [0; 16],
            symbols: [0; 288],
            count: 0,
        };
        if lengths.is_empty() || lengths.len() > result.symbols.len() {
            return Err(INVALID);
        }
        for &length in lengths {
            if length > 15 {
                return Err(INVALID);
            }
            result.counts[usize::from(length)] += 1;
        }
        if usize::from(result.counts[0]) == lengths.len() {
            return Err(INVALID);
        }
        let mut remaining = 1i32;
        for &count in &result.counts[1..] {
            remaining = (remaining << 1) - i32::from(count);
            if remaining < 0 {
                return Err(INVALID);
            }
        }
        let mut offsets = [0u16; 16];
        for bits in 1..15 {
            offsets[bits + 1] = offsets[bits] + result.counts[bits];
        }
        for (symbol, &length) in lengths.iter().enumerate() {
            if length != 0 {
                let slot = &mut offsets[usize::from(length)];
                result.symbols[usize::from(*slot)] = symbol as u16;
                *slot += 1;
                result.count += 1;
            }
        }
        Ok(result)
    }
    fn decode(&self, reader: &mut Reader<'_>) -> Result<u16, Error> {
        let mut code = 0u32;
        let mut first = 0u32;
        let mut index = 0u32;
        for bits in 1..=15 {
            code |= reader.read(1)?;
            let count = u32::from(self.counts[bits]);
            if code >= first && code - first < count {
                let slot = index + code - first;
                if slot >= u32::from(self.count) {
                    return Err(INVALID);
                }
                return Ok(self.symbols[slot as usize]);
            }
            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }
        Err(INVALID)
    }
}
fn fixed() -> Result<(Huffman, Huffman), Error> {
    let mut literal = [0u8; 288];
    literal[..144].fill(8);
    literal[144..256].fill(9);
    literal[256..280].fill(7);
    literal[280..].fill(8);
    Ok((Huffman::new(&literal)?, Huffman::new(&[5; 32])?))
}
fn dynamic(reader: &mut Reader<'_>) -> Result<(Huffman, Huffman), Error> {
    let literals = reader.read(5)? as usize + 257;
    let distances = reader.read(5)? as usize + 1;
    let codes = reader.read(4)? as usize + 4;
    if literals > 286 || distances > 32 {
        return Err(INVALID);
    }
    let mut code_lengths = [0u8; 19];
    for &index in &CODE_ORDER[..codes] {
        code_lengths[index] = reader.read(3)? as u8;
    }
    let table = Huffman::new(&code_lengths)?;
    let mut lengths = [0u8; 320];
    let total = literals + distances;
    let mut written = 0;
    while written < total {
        let symbol = table.decode(reader)?;
        if symbol <= 15 {
            lengths[written] = symbol as u8;
            written += 1;
            continue;
        }
        let (repeat, value) = match symbol {
            16 => {
                if written == 0 {
                    return Err(INVALID);
                }
                (reader.read(2)? as usize + 3, lengths[written - 1])
            }
            17 => (reader.read(3)? as usize + 3, 0),
            18 => (reader.read(7)? as usize + 11, 0),
            _ => return Err(INVALID),
        };
        if repeat > total - written {
            return Err(INVALID);
        }
        lengths[written..written + repeat].fill(value);
        written += repeat;
    }
    if lengths[256] == 0 {
        return Err(INVALID);
    }
    Ok((
        Huffman::new(&lengths[..literals])?,
        Huffman::new(&lengths[literals..total])?,
    ))
}
fn compressed(
    reader: &mut Reader<'_>,
    literal: &Huffman,
    distance: &Huffman,
    output: &mut [u8],
    written: &mut usize,
) -> Result<(), Error> {
    loop {
        let symbol = literal.decode(reader)?;
        if symbol < 256 {
            if *written == output.len() {
                return Err(Error::InsufficientBuffer);
            }
            output[*written] = symbol as u8;
            *written += 1;
            continue;
        }
        if symbol == 256 {
            return Ok(());
        }
        if !(257..=285).contains(&symbol) {
            return Err(INVALID);
        }
        let index = usize::from(symbol - 257);
        let length = usize::from(LENGTH_BASE[index]) + reader.read(LENGTH_EXTRA[index])? as usize;
        let symbol = usize::from(distance.decode(reader)?);
        if symbol >= DISTANCE_BASE.len() {
            return Err(INVALID);
        }
        let copy_distance =
            usize::from(DISTANCE_BASE[symbol]) + reader.read(DISTANCE_EXTRA[symbol])? as usize;
        if copy_distance == 0
            || copy_distance > *written
            || copy_distance > 32768
            || length > output.len() - *written
        {
            return Err(if length > output.len() - *written {
                Error::InsufficientBuffer
            } else {
                INVALID
            });
        }
        // The source can be the byte just written: retain native overlap order.
        for _ in 0..length {
            output[*written] = output[*written - copy_distance];
            *written += 1;
        }
    }
}
fn stored(reader: &mut Reader<'_>, output: &mut [u8], written: &mut usize) -> Result<(), Error> {
    reader.align();
    let low = reader.read(8)?;
    let high = reader.read(8)?;
    let inverse_low = reader.read(8)?;
    let inverse_high = reader.read(8)?;
    let length = low | (high << 8);
    let inverse = inverse_low | (inverse_high << 8);
    if length ^ 0xffff != inverse {
        return Err(INVALID);
    }
    if length as usize > output.len() - *written {
        return Err(Error::InsufficientBuffer);
    }
    // Aligned stored bytes have no history dependency. Bulk-copy the available
    // prefix, preserving the native partial-scratch state on truncated input.
    let count = (length as usize).min(reader.input.len() - reader.offset);
    output[*written..*written + count]
        .copy_from_slice(&reader.input[reader.offset..reader.offset + count]);
    reader.offset += count;
    *written += count;
    if count != length as usize {
        return Err(INVALID);
    }
    Ok(())
}
pub(crate) fn inflate(input: &[u8], output: &mut [u8]) -> Result<usize, Error> {
    let mut reader = Reader::new(input);
    let mut written = 0;
    loop {
        let final_block = reader.read(1)? != 0;
        let kind = reader.read(2)?;
        match kind {
            0 => stored(&mut reader, output, &mut written)?,
            1 | 2 => {
                let (literal, distance) = if kind == 1 {
                    fixed()?
                } else {
                    dynamic(&mut reader)?
                };
                compressed(&mut reader, &literal, &distance, output, &mut written)?;
            }
            _ => return Err(INVALID),
        }
        if final_block {
            break;
        }
    }
    // Preserve the native exact byte-consumption gate, including trailing data.
    if reader.offset != input.len() {
        return Err(INVALID);
    }
    Ok(written)
}
fn adler32(bytes: &[u8]) -> u32 {
    let mut first = 1u32;
    let mut second = 0u32;
    for block in bytes.chunks(5552) {
        // Each Adler state depends on the immediately preceding accumulation;
        // retain original bounded blocks and exact integer arithmetic.
        for &byte in block {
            first += u32::from(byte);
            second += first;
        }
        first %= 65521;
        second %= 65521;
    }
    (second << 16) | first
}
pub(crate) fn inflate_zlib(input: &[u8], output: &mut [u8]) -> Result<usize, Error> {
    if input.len() < 6 {
        return Err(INVALID);
    }
    let cmf = input[0];
    let flags = input[1];
    if cmf & 0x0f != 8
        || cmf >> 4 > 7
        || ((u16::from(cmf) << 8) | u16::from(flags)) % 31 != 0
        || flags & 0x20 != 0
    {
        return Err(INVALID);
    }
    let written = inflate(&input[2..input.len() - 4], output)?;
    let expected = super::parse::read_u32(input, input.len() - 4);
    if adler32(&output[..written]) != expected {
        return Err(INVALID);
    }
    Ok(written)
}
