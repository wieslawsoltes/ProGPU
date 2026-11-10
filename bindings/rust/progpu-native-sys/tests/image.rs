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
