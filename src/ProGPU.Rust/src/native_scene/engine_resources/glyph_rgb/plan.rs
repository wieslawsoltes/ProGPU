//! Original bounded shelf, visible destination rectangle and stable cell references.
use super::*;
pub(super) fn compile(
    frame: NativeCommandFrame,
    policy: wire::RgbGlyphDraw,
    scissor: NativeScissor,
    actual_target_size: [u32; 2],
    tiles: Vec<NativeRgbGlyphTile>,
    segments: Vec<wire::PathSegment>,
    max_buffer_size: u64,
    engine_flags: u32,
    source_vertex_bytes: u64,
) -> Result<NativeRgbGlyphPage, Error> {
    let size = actual_target_size;
    if size.contains(&0)
        || size.into_iter().any(|v| v > 4096)
        || segments.len() as u64 * 48 > max_buffer_size
    {
        return Err(Error::InvalidInput);
    }
    if scissor.drawable
        && (scissor.width == 0
            || scissor.height == 0
            || scissor.x >= size[0]
            || scissor.y >= size[1]
            || scissor.width > size[0] - scissor.x
            || scissor.height > size[1] - scissor.y)
    {
        return Err(Error::InvalidInput);
    }
    let mut width = 64u32;
    let mut area = 0u64;
    for glyph in &tiles {
        area += u64::from(glyph.width) * u64::from(glyph.height);
        while width < glyph.width {
            width *= 2;
        }
        if [
            glyph.foreground.r,
            glyph.foreground.g,
            glyph.foreground.b,
            glyph.foreground.a,
        ]
        .into_iter()
        .any(|v| !v.is_finite() || !(0.0..=1.0).contains(&v))
        {
            return Err(Error::InvalidInput);
        }
    }
    while u64::from(width) * u64::from(width) < area && width < 4096 {
        width *= 2;
    }
    let mut records = allocate(tiles.len())?;
    let mut uniforms = allocate(tiles.len())?;
    let mut instances = allocate(tiles.len())?;
    let (mut x, mut y, mut row) = (0u32, 0u32, 0u32);
    let mut staging = 0u64;
    for glyph in &tiles {
        if glyph.width > width - x {
            x = 0;
            y += row;
            row = 0;
        }
        if y > 4096 || glyph.height > 4096 - y {
            return Err(Error::UnsupportedExecution);
        }
        staging = (staging + 511) & !511;
        let row_bytes = (glyph.width * 4 + 255) & !255;
        let next = staging + u64::from(row_bytes) * u64::from(glyph.height);
        if next > max_buffer_size.min(128 * 1024 * 1024) {
            return Err(Error::Capacity);
        }
        uniforms.push(GlyphUniforms {
            x_start: glyph.x_start,
            y_start: glyph.y_start,
            scale: glyph.scale,
            glyph_index: records.len() as u32,
            output_offset_words: (staging / 4) as u32,
            output_row_words: row_bytes / 4,
            width: glyph.width,
            height: glyph.height,
            subpixel_x: glyph.subpixel_x,
            atlas_x: x as f32,
            atlas_y: y as f32,
            padding: 0.0,
        });
        records.push(glyph.outline);
        instances.push(NativeRgbInstance {
            target_origin: [glyph.target_x as f32, glyph.target_y as f32],
            extent: [glyph.width as f32, glyph.height as f32],
            atlas_origin: [x, y],
            reserved: [0; 2],
            foreground: [
                glyph.foreground.r,
                glyph.foreground.g,
                glyph.foreground.b,
                glyph.foreground.a,
            ],
        });
        staging = next;
        x += glyph.width;
        row = row.max(glyph.height);
    }
    let atlas_size = [width, y + row];
    let clipped = |glyph: &NativeRgbGlyphTile| -> [i32; 4] {
        if !scissor.drawable {
            return [0; 4];
        }
        [
            glyph.target_x.max(scissor.x as i32),
            glyph.target_y.max(scissor.y as i32),
            (glyph.target_x + glyph.width as i32).min((scissor.x + scissor.width) as i32),
            (glyph.target_y + glyph.height as i32).min((scissor.y + scissor.height) as i32),
        ]
    };
    let (mut left, mut top, mut right, mut bottom) = (size[0], size[1], 0u32, 0u32);
    for glyph in &tiles {
        let r = clipped(glyph);
        if r[0] >= r[2] || r[1] >= r[3] {
            continue;
        }
        left = left.min(r[0] as u32);
        top = top.min(r[1] as u32);
        right = right.max(r[2] as u32);
        bottom = bottom.max(r[3] as u32);
    }
    let visible = left < right && top < bottom;
    let first_column = if visible { left / 16 } else { 0 };
    let first_row = if visible { top / 16 } else { 0 };
    let columns = if visible {
        (right - 1) / 16 - first_column + 1
    } else {
        0
    };
    let rows = if visible {
        (bottom - 1) / 16 - first_row + 1
    } else {
        0
    };
    let mut cells = allocate((columns * rows) as usize)?;
    cells.resize((columns * rows) as usize, NativeRgbCell::default());
    for row in 0..rows {
        for column in 0..columns {
            let cell = &mut cells[(row * columns + column) as usize];
            let x = (first_column + column) * 16;
            let y = (first_row + row) * 16;
            cell.origin = [left.max(x), top.max(y)];
            cell.extent = [
                right.min(x + 16) - cell.origin[0],
                bottom.min(y + 16) - cell.origin[1],
            ];
        }
    }
    let visit = |glyph: &NativeRgbGlyphTile, action: &mut dyn FnMut(usize)| {
        let r = clipped(glyph);
        if r[0] >= r[2] || r[1] >= r[3] {
            return;
        }
        let x0 = r[0] as u32 / 16 - first_column;
        let y0 = r[1] as u32 / 16 - first_row;
        let x1 = (r[2] - 1) as u32 / 16 - first_column;
        let y1 = (r[3] - 1) as u32 / 16 - first_row;
        for row in y0..=y1 {
            for column in x0..=x1 {
                action((row * columns + column) as usize);
            }
        }
    };
    for glyph in &tiles {
        visit(glyph, &mut |index| cells[index].count += 1);
    }
    let storage = max_buffer_size.min(128 * 1024 * 1024);
    let mut reference_count = 0u64;
    for cell in &mut cells {
        cell.first = reference_count as u32;
        reference_count += u64::from(cell.count);
        if reference_count > storage / 4 {
            return Err(Error::Capacity);
        }
    }
    if cells.len() as u64 * 32 > storage {
        return Err(Error::Capacity);
    }
    let mut references = allocate(reference_count.max(1) as usize)?;
    references.resize(reference_count.max(1) as usize, 0);
    let mut cursors = allocate(cells.len())?;
    cursors.resize(cells.len(), 0u32);
    for (index, glyph) in tiles.iter().enumerate() {
        visit(glyph, &mut |cell| {
            references[(cells[cell].first + cursors[cell]) as usize] = index as u32;
            cursors[cell] += 1;
        });
    }
    cells.retain(|c| c.count != 0);
    let cell_count = cells.len() as u32;
    if cells.is_empty() {
        cells.try_reserve(1).map_err(|_| Error::Capacity)?;
        cells.push(NativeRgbCell::default());
    }
    let backdrop_origin = if visible {
        [left, top]
    } else {
        [scissor.x, scissor.y]
    };
    let backdrop_size = if visible {
        [right - left, bottom - top]
    } else {
        [1, 1]
    };
    let composite = NativeRgbCompositeFrame {
        extent: [size[0] as f32, size[1] as f32],
        render_origin: [0.0; 2],
        backdrop_origin,
        reserved: [0; 2],
    };
    Ok(NativeRgbGlyphPage {
        frame,
        policy,
        scissor,
        actual_target_size,
        tiles: tiles.into(),
        segments: segments.into(),
        records: records.into(),
        uniforms: uniforms.into(),
        instances: instances.into(),
        cells: cells.into(),
        references: references.into(),
        composite,
        atlas_size,
        backdrop_size,
        cell_count,
        coverage_bytes: staging,
        engine_flags,
        source_vertex_bytes,
    })
}
