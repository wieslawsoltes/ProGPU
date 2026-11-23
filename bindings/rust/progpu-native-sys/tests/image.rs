//! The image module of the engine through its C ABI (no device): the size of a PNG and its pixels.

use progpu_native_sys::image::*;
use std::ptr;

fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for &byte in bytes {
        crc ^= byte as u32;
        for _ in 0..8 {
            crc = if crc & 1 != 0 { (crc >> 1) ^ 0xEDB8_8320 } else { crc >> 1 };
        }
    }
    !crc
}

fn adler32(bytes: &[u8]) -> u32 {
    let (mut a, mut b) = (1u32, 0u32);
    for &byte in bytes {
        a = (a + byte as u32) % 65521;
        b = (b + a) % 65521;
    }
    (b << 16) | a
}

fn chunk(png: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    png.extend_from_slice(&(data.len() as u32).to_be_bytes());
    let start = png.len();
    png.extend_from_slice(kind);
    png.extend_from_slice(data);
    let crc = crc32(&png[start..]);
    png.extend_from_slice(&crc.to_be_bytes());
}

/// A PNG of `width` x `height` pixels of `channels` eight bit channels (3: RGB, 4: RGBA), its
/// rows unfiltered and its stream stored, not compressed.
fn png(width: u32, height: u32, channels: usize, pixels: &[u8]) -> Vec<u8> {
    assert_eq!(pixels.len(), width as usize * height as usize * channels);
    let mut png = vec![0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A];
    let mut header = Vec::new();
    header.extend_from_slice(&width.to_be_bytes());
    header.extend_from_slice(&height.to_be_bytes());
    header.extend_from_slice(&[8, if channels == 4 { 6 } else { 2 }, 0, 0, 0]);
    chunk(&mut png, b"IHDR", &header);
    let mut rows = Vec::new();
    for row in pixels.chunks(width as usize * channels) {
        rows.push(0);
        rows.extend_from_slice(row);
    }
    let mut stream = vec![0x78, 0x01, 0x01];
    stream.extend_from_slice(&(rows.len() as u16).to_le_bytes());
    stream.extend_from_slice(&(!(rows.len() as u16)).to_le_bytes());
    stream.extend_from_slice(&rows);
    stream.extend_from_slice(&adler32(&rows).to_be_bytes());
    chunk(&mut png, b"IDAT", &stream);
    chunk(&mut png, b"IEND", &[]);
    png
}

fn read_size(data: &[u8]) -> Result<(u32, u32, usize), u32> {
    let (mut width, mut height, mut size, mut error) = (0u32, 0u32, 0usize, u32::MAX);
    // SAFETY: `data` is a live slice and every out pointer is a live local.
    let ok = unsafe { progpu_native_image_png_read_size(data.as_ptr(), data.len(), &mut width, &mut height, &mut size, &mut error) };
    if ok != 0 {
        assert_eq!(error, PROGPU_NATIVE_IMAGE_ERROR_NONE);
        Ok((width, height, size))
    } else {
        Err(error)
    }
}

fn decode(data: &[u8], rgba: &mut [u8]) -> Result<(), u32> {
    let mut error = u32::MAX;
    // SAFETY: `data` and `rgba` are live slices of the sizes given.
    let ok = unsafe { progpu_native_image_png_decode_rgba(data.as_ptr(), data.len(), rgba.as_mut_ptr(), rgba.len(), &mut error) };
    if ok != 0 {
        assert_eq!(error, PROGPU_NATIVE_IMAGE_ERROR_NONE);
        Ok(())
    } else {
        Err(error)
    }
}

#[test]
fn a_png_is_read_as_rgba_that_is_not_premultiplied() {
    let pixels = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120];
    let file = png(3, 2, 4, &pixels);
    assert_eq!(read_size(&file), Ok((3, 2, 24)));
    let mut rgba = [0u8; 24];
    assert_eq!(decode(&file, &mut rgba), Ok(()));
    assert_eq!(rgba, pixels);

    // RGB gets an opaque alpha.
    let file = png(2, 1, 3, &[1, 2, 3, 250, 251, 252]);
    assert_eq!(read_size(&file), Ok((2, 1, 8)));
    let mut rgba = [0u8; 8];
    assert_eq!(decode(&file, &mut rgba), Ok(()));
    assert_eq!(rgba, [1, 2, 3, 255, 250, 251, 252, 255]);
}

#[test]
fn what_is_not_a_sound_png_is_refused_with_its_error() {
    let file = png(2, 2, 4, &[7; 16]);
    let mut rgba = [0u8; 16];

    // Too little room for the pixels: nothing is written.
    assert_eq!(decode(&file, &mut rgba[..15]), Err(PROGPU_NATIVE_IMAGE_ERROR_INSUFFICIENT_BUFFER));
    assert_eq!(rgba, [0u8; 16]);

    // Another signature.
    let mut other = file.clone();
    other[1] = b'J';
    assert_eq!(read_size(&other), Err(PROGPU_NATIVE_IMAGE_ERROR_INVALID_SIGNATURE));
    assert_eq!(decode(&other, &mut rgba), Err(PROGPU_NATIVE_IMAGE_ERROR_INVALID_SIGNATURE));

    // A byte of the pixels changed: the checksum of its chunk no longer holds.
    let mut damaged = file.clone();
    let at = damaged.len() - 12 - 4 - 4 - 3;
    damaged[at] ^= 0x55;
    assert!(decode(&damaged, &mut rgba).is_err());

    // A file cut short.
    assert!(read_size(&file[..file.len() - 20]).is_err());

    // No data, no places for the answers.
    let (mut width, mut height, mut size, mut error) = (0u32, 0u32, 0usize, 0u32);
    // SAFETY: null and empty arguments are answered with a failure and nothing is read or written.
    unsafe {
        assert_eq!(progpu_native_image_png_read_size(ptr::null(), 0, &mut width, &mut height, &mut size, &mut error), 0);
        assert_eq!(error, PROGPU_NATIVE_IMAGE_ERROR_INVALID_ARGUMENT);
        assert_eq!(progpu_native_image_png_read_size(file.as_ptr(), file.len(), ptr::null_mut(), &mut height, &mut size, ptr::null_mut()), 0);
        assert_eq!(progpu_native_image_png_decode_rgba(file.as_ptr(), file.len(), ptr::null_mut(), 16, &mut error), 0);
        assert_eq!(error, PROGPU_NATIVE_IMAGE_ERROR_INVALID_ARGUMENT);
    }
}

/// Two colours side by side, 16 x 16 pixels, written by libjpeg.
const BASELINE_JPEG: &str = concat!(
    "ffd8ffe000104a46494600010100000100010000ffdb00430002010101010102010101020202020204030202",
    "02020504040304060506060605060606070908060709070606080b08090a0a0a0a0a06080b0c0b0a0c090a0a",
    "0affdb004301020202020202050303050a0706070a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a",
    "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0affc000110800100010030111000211010311",
    "01ffc4001f0000010501010101010100000000000000000102030405060708090a0bffc400b5100002010303",
    "020403050504040000017d01020300041105122131410613516107227114328191a1082342b1c11552d1f024",
    "33627282090a161718191a25262728292a3435363738393a434445464748494a535455565758595a63646566",
    "6768696a737475767778797a838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7",
    "b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9faffc400",
    "1f0100030101010101010101010000000000000102030405060708090a0bffc400b511000201020404030407",
    "05040400010277000102031104052131061241510761711322328108144291a1b1c109233352f0156272d10a",
    "162434e125f11718191a262728292a35363738393a434445464748494a535455565758595a63646566676869",
    "6a737475767778797a82838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9",
    "bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae2e3e4e5e6e7e8e9eaf2f3f4f5f6f7f8f9faffda000c030100",
    "02110311003f00f96ebf9fcff510f2faff006b0ff14cf50aff0014cff6b0f2faff006b0ff14cffd9",
);

/// Two colours side by side, 16 x 16 pixels, written by libjpeg.
const PROGRESSIVE_JPEG: &str = concat!(
    "ffd8ffe000104a46494600010100000100010000ffdb00430002010101010102010101020202020204030202",
    "02020504040304060506060605060606070908060709070606080b08090a0a0a0a0a06080b0c0b0a0c090a0a",
    "0affdb004301020202020202050303050a0706070a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a",
    "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0affc200110800100010030122000211010311",
    "01ffc4001500010100000000000000000000000000000607ffc4001401010000000000000000000000000000",
    "0006ffda000c030100021003100000019697505d5b7fffc40014100100000000000000000000000000000020",
    "ffda00080101000105021fffc40018110002030000000000000000000000000000074483c3ffda0008010301",
    "013f0170c2b333ffc40018110002030000000000000000000000000000074483c3ffda0008010201013f014f",
    "4daf43ffc40014100100000000000000000000000000000020ffda0008010100063f021fffc4001410010000",
    "0000000000000000000000000020ffda0008010100013f211fffda000c0301000200030000001003ffc40014",
    "110100000000000000000000000000000000ffda0008010301013f1037ffc400141101000000000000000000",
    "00000000000000ffda0008010201013f1037ffc40014100100000000000000000000000000000020ffda0008",
    "010100013f101fffd9",
);

fn from_hex(text: &str) -> Vec<u8> {
    (0..text.len() / 2).map(|at| u8::from_str_radix(&text[at * 2..at * 2 + 2], 16).unwrap()).collect()
}

/// The format, size and RGBA pixels of a file through the C ABI of every format.
fn decode_any(file: &[u8]) -> Result<(u32, u32, u32, Vec<u8>), u32> {
    let (mut format, mut width, mut height, mut size, mut error) = (0u32, 0u32, 0u32, 0usize, 0u32);
    // SAFETY: the file and the answers outlive the calls; `rgba` holds `size` bytes.
    unsafe {
        if progpu_native_image_read_size(file.as_ptr(), file.len(), &mut format, &mut width, &mut height, &mut size, &mut error) == 0 {
            return Err(error);
        }
        let mut rgba = vec![0u8; size];
        if progpu_native_image_decode_rgba(file.as_ptr(), file.len(), rgba.as_mut_ptr(), rgba.len(), &mut error) == 0 {
            return Err(error);
        }
        Ok((format, width, height, rgba))
    }
}

fn rgba_at(rgba: &[u8], width: u32, x: u32, y: u32) -> [u8; 4] {
    let at = ((y * width + x) * 4) as usize;
    [rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]]
}

fn near(actual: [u8; 4], expected: [u8; 4], tolerance: u8) -> bool {
    actual.iter().zip(expected).all(|(a, b)| a.abs_diff(b) <= tolerance)
}

#[test]
fn baseline_and_progressive_jpegs_decode() {
    for (name, file) in [("baseline", BASELINE_JPEG), ("progressive", PROGRESSIVE_JPEG)] {
        let (format, width, height, rgba) = decode_any(&from_hex(file)).unwrap_or_else(|error| panic!("{name}: {error}"));
        assert_eq!((format, width, height), (PROGPU_NATIVE_IMAGE_FORMAT_JPEG, 16, 16), "{name}");
        assert!(near(rgba_at(&rgba, width, 2, 8), [200, 40, 30, 255], 6), "{name}: {:?}", rgba_at(&rgba, width, 2, 8));
        assert!(near(rgba_at(&rgba, width, 13, 8), [30, 60, 200, 255], 6), "{name}: {:?}", rgba_at(&rgba, width, 13, 8));
    }
}

#[test]
fn a_jpeg_cut_short_shows_what_it_holds() {
    let file = from_hex(BASELINE_JPEG);
    let (_, width, _, rgba) = decode_any(&file[..file.len() - 12]).expect("the headers and part of the scan");
    assert!(near(rgba_at(&rgba, width, 2, 2), [200, 40, 30, 255], 8));
    assert!(decode_any(&file[..20]).is_err(), "no frame header");
}

/// A 24-bit BMP of two rows, bottom up, with padded rows, and a 32-bit one with bit fields.
#[test]
fn bmps_decode() {
    let mut file = Vec::new();
    file.extend_from_slice(b"BM");
    file.extend_from_slice(&(54u32 + 16).to_le_bytes());
    file.extend_from_slice(&0u32.to_le_bytes());
    file.extend_from_slice(&54u32.to_le_bytes());
    file.extend_from_slice(&40u32.to_le_bytes());
    file.extend_from_slice(&2i32.to_le_bytes());
    file.extend_from_slice(&2i32.to_le_bytes());
    file.extend_from_slice(&1u16.to_le_bytes());
    file.extend_from_slice(&24u16.to_le_bytes());
    file.extend_from_slice(&[0u8; 24]);
    file.extend_from_slice(&[3, 2, 1, 6, 5, 4, 0, 0]); // the bottom row, blue first
    file.extend_from_slice(&[9, 8, 7, 12, 11, 10, 0, 0]);
    let (format, width, height, rgba) = decode_any(&file).expect("a 24-bit BMP");
    assert_eq!((format, width, height), (PROGPU_NATIVE_IMAGE_FORMAT_BMP, 2, 2));
    assert_eq!(rgba, [7, 8, 9, 255, 10, 11, 12, 255, 1, 2, 3, 255, 4, 5, 6, 255]);

    let mut file = Vec::new();
    file.extend_from_slice(b"BM");
    file.extend_from_slice(&(70u32 + 4).to_le_bytes());
    file.extend_from_slice(&0u32.to_le_bytes());
    file.extend_from_slice(&70u32.to_le_bytes());
    file.extend_from_slice(&56u32.to_le_bytes());
    file.extend_from_slice(&1i32.to_le_bytes());
    file.extend_from_slice(&(-1i32).to_le_bytes());
    file.extend_from_slice(&1u16.to_le_bytes());
    file.extend_from_slice(&32u16.to_le_bytes());
    file.extend_from_slice(&3u32.to_le_bytes());
    file.extend_from_slice(&[0u8; 20]);
    for mask in [0x0000_FF00u32, 0x00FF_0000, 0xFF00_0000, 0x0000_00FF] {
        file.extend_from_slice(&mask.to_le_bytes());
    }
    file.extend_from_slice(&[0x40, 0x30, 0x20, 0x10]);
    let (_, _, _, rgba) = decode_any(&file).expect("a BMP with bit fields");
    assert_eq!(rgba, [0x30, 0x20, 0x10, 0x40]);
}

/// A GIF of 2 x 2 pixels with a transparent colour: the codes 4 (clear), 0, 1, 2, 0, 5 (end).
/// The code width grows from three bits to four once the table holds eight codes.
#[test]
fn a_gif_decodes_with_its_transparent_colour() {
    let mut file = Vec::new();
    file.extend_from_slice(b"GIF89a");
    file.extend_from_slice(&[2, 0, 2, 0, 0x81, 0, 0]);
    file.extend_from_slice(&[10, 20, 30, 40, 50, 60, 70, 80, 90, 0, 0, 0]);
    file.extend_from_slice(&[0x21, 0xF9, 4, 1, 0, 0, 2, 0]);
    file.extend_from_slice(&[0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0]);
    file.push(2);
    let codes = [(4u32, 3u32), (0, 3), (1, 3), (2, 3), (0, 4), (5, 4)];
    let (mut bits, mut count, mut data) = (0u32, 0u32, Vec::new());
    for (code, width) in codes {
        bits |= code << count;
        count += width;
        while count >= 8 {
            data.push(bits as u8);
            bits >>= 8;
            count -= 8;
        }
    }
    if count > 0 {
        data.push(bits as u8);
    }
    file.push(data.len() as u8);
    file.extend_from_slice(&data);
    file.extend_from_slice(&[0, 0x3B]);
    let (format, width, height, rgba) = decode_any(&file).expect("a GIF");
    assert_eq!((format, width, height), (PROGPU_NATIVE_IMAGE_FORMAT_GIF, 2, 2));
    assert_eq!(rgba_at(&rgba, 2, 0, 0), [10, 20, 30, 255]);
    assert_eq!(rgba_at(&rgba, 2, 1, 0), [40, 50, 60, 255]);
    assert_eq!(rgba_at(&rgba, 2, 0, 1)[3], 0, "the transparent colour");
    assert_eq!(rgba_at(&rgba, 2, 1, 1), [10, 20, 30, 255]);
}

#[test]
fn the_format_of_a_png_is_told_and_other_bytes_are_refused() {
    let file = png(1, 1, 4, &[1, 2, 3, 4]);
    let (format, _, _, rgba) = decode_any(&file).expect("a PNG");
    assert_eq!(format, PROGPU_NATIVE_IMAGE_FORMAT_PNG);
    assert_eq!(rgba, [1, 2, 3, 4]);
    assert_eq!(decode_any(b"not an image"), Err(PROGPU_NATIVE_IMAGE_ERROR_INVALID_SIGNATURE));
}
