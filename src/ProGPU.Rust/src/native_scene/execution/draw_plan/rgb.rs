use super::*;
pub(super) fn preflight(
    frame: &NativeCommandFrame,
    engine_flags: u32,
    max_buffer_size: u64,
    cost: &mut NativeDrawCommandCost,
) -> Result<(), Error> {
    let command = frame.command();
    rgb_glyph::validate_rgb_glyph_draw(&input(&frame.scene), &command.record)
        .map_err(|_| Error::InvalidInput)?;
    let draw: wire::RgbGlyphDraw = read(command.payload, 0)?;
    let offset = NativeRgbGlyphPage::preflight_translation(frame, &draw)?;
    if frame.target.width > 4096
        || frame.target.height > 4096
        || engine_flags
            & (ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK | ENGINE_GLYPH_SCALAR_CPU_FALLBACK)
            != 0
    {
        return Err(Error::UnsupportedExecution);
    }
    let resource = frame
        .scene
        .envelope()
        .resource(command.record.resource_index)
        .ok_or(Error::InvalidInput)?;
    cost.vertex_bytes =
        resource.auxiliary.len() as u64 + u64::from(draw.glyph_count) * (80 + 32 + 256 + 48);
    let tiles = &command.payload[wire::RgbGlyphDraw::WIRE_SIZE..];
    let mut area = 0u64;
    let mut width = 64u32;
    for i in 0..draw.glyph_count as usize {
        let tile: wire::RgbGlyphTile = read(tiles, i)?;
        if [
            i64::from(tile.target_x) + i64::from(offset[0]),
            i64::from(tile.target_y) + i64::from(offset[1]),
        ]
        .into_iter()
        .any(|v| !(-4096..=4096).contains(&v))
        {
            return Err(Error::UnsupportedExecution);
        }
        area += u64::from(tile.width) * u64::from(tile.height);
        while width < tile.width {
            width *= 2;
        }
        cost.coverage_bytes = (cost.coverage_bytes + 511) & !511;
        cost.coverage_bytes += u64::from((tile.width * 4 + 255) & !255) * u64::from(tile.height);
    }
    while u64::from(width) * u64::from(width) < area && width < 4096 {
        width *= 2;
    }
    let (mut x, mut y, mut height) = (0u32, 0u32, 0u32);
    for i in 0..draw.glyph_count as usize {
        let tile: wire::RgbGlyphTile = read(tiles, i)?;
        if tile.width > width - x {
            x = 0;
            y += height;
            height = 0;
        }
        if y > 4096 || tile.height > 4096 - y {
            return Err(Error::UnsupportedExecution);
        }
        x += tile.width;
        height = height.max(tile.height);
    }
    cost.texture_bytes = u64::from(width) * u64::from(y + height) * 4;
    if cost.coverage_bytes
        > MAX_COVERAGE_BYTES
            .min(max_buffer_size)
            .min(128 * 1024 * 1024)
    {
        return Err(Error::Capacity);
    }
    Ok(())
}
