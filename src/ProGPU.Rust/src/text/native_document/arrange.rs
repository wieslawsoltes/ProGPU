//! Original ordered double width, margin-collapse, row measurement and placement.
use super::*;
use rows::Rows;
#[derive(Clone, Copy, Default)]
struct State {
    height: f64,
    content_height: f64,
    before: f64,
    after: f64,
    through: bool,
}
#[derive(Clone, Copy, Default)]
struct Cursor {
    y: f64,
    pending: f64,
    trailing: f64,
    content: bool,
}
pub(super) fn widths(
    source: NativeDocumentStructure<'_>,
    width: f64,
    layout: &Rows<'_>,
    output: &mut [NativeDocumentBox],
) -> Result<()> {
    let blocks = source.blocks;
    let mut ancestors = [0u32; 128];
    let mut depth = 0;
    for (i, b) in blocks.iter().enumerate() {
        while depth != 0 && i == blocks[ancestors[depth - 1] as usize].subtree_end as usize {
            depth -= 1;
        }
        if depth >= ancestors.len() {
            return invalid();
        }
        let parent = if depth == 0 {
            ROOT
        } else {
            ancestors[depth - 1]
        };
        if b.parent_index != parent
            || b.subtree_end as usize <= i
            || b.subtree_end as usize > blocks.len()
            || parent != ROOT && b.subtree_end > blocks[parent as usize].subtree_end
            || !pair(b.margins[0], b.margins[2])
            || !pair(b.margins[1], b.margins[3])
            || !pair(b.insets[0], b.insets[2])
            || !pair(b.insets[1], b.insets[3])
        {
            return invalid();
        }
        let mut parent_box = if parent == ROOT {
            NativeDocumentBox {
                width,
                ..Default::default()
            }
        } else {
            output[parent as usize]
        };
        if let Some(c) = layout.cell(i) {
            let r = source.rows[c.row_index as usize];
            let column = r.column_start + c.column_start;
            parent_box.x += layout.column_x[column as usize]
                + r.cell_spacing * (f64::from(c.column_start) + 0.5);
            parent_box.width = layout.track_width(column, c.column_count)
                + r.cell_spacing * f64::from(c.column_count - 1);
        }
        let left = b.margins[0] + b.insets[0];
        let right = b.margins[2] + b.insets[2];
        let used = left + right;
        let x = parent_box.x + left;
        if !used.is_finite() || !x.is_finite() {
            return invalid();
        }
        output[i] = NativeDocumentBox {
            x,
            y: 0.0,
            width: max(0.0, parent_box.width - used),
            height: 0.0,
        };
        if let Some(r) = layout.row(i) {
            output[i].width = layout.track_width(r.column_start, r.column_count)
                + r.cell_spacing * f64::from(r.column_count);
        }
        if b.subtree_end as usize != i + 1 {
            ancestors[depth] = i as u32;
            depth += 1;
        }
    }
    Ok(())
}
fn measure(
    input: NativeDocumentInput<'_>,
    layout: &Rows<'_>,
    paragraphs: &[Option<&NativeDocumentPositionedParagraph>],
    states: &mut [State],
) -> Result<()> {
    let blocks = input.structure.blocks;
    let mut cursor = 0usize;
    for (i, b) in blocks.iter().enumerate() {
        if b.subtree_end as usize != i + 1 && b.line_count != 0
            || b.line_start as usize != cursor
            || b.line_count as usize > input.lines.len() - cursor
        {
            return invalid();
        }
        cursor += b.line_count as usize;
    }
    if cursor != input.lines.len() {
        return invalid();
    }
    for l in input.lines {
        if !pair(l.width, l.height) || l.height == 0.0 {
            return invalid();
        }
    }
    for (i, o) in input.objects.iter().enumerate() {
        let index = o.block_index as usize;
        if index >= blocks.len()
            || i != 0 && input.objects[i - 1].block_index >= o.block_index
            || blocks[index].subtree_end as usize != index + 1
            || blocks[index].line_count != 0
            || layout.row(index).is_some()
            || !pair(o.width, o.height)
        {
            return invalid();
        }
    }
    let mut object_cursor = input.objects.len();
    for index in (0..blocks.len()).rev() {
        let b = blocks[index];
        let mut s = State {
            before: b.margins[1],
            after: b.margins[3],
            ..Default::default()
        };
        if let Some(r) = layout.row(index) {
            let mut tallest = 0.0;
            let mut child = index + 1;
            while child < b.subtree_end as usize {
                let c = states[child];
                tallest = max(tallest, c.before + c.height + c.after);
                child = blocks[child].subtree_end as usize;
            }
            s.content_height = tallest + r.cell_spacing;
            s.height = b.insets[1] + s.content_height + b.insets[3];
            s.through = false;
            if !s.height.is_finite() {
                return invalid();
            }
            states[index] = s;
            continue;
        }
        let has_object =
            object_cursor != 0 && input.objects[object_cursor - 1].block_index as usize == index;
        let mut content_height = 0.0;
        let mut pending = 0.0;
        if has_object {
            object_cursor -= 1;
            content_height = input.objects[object_cursor].height;
        }
        let mut content = b.line_count != 0 || has_object;
        let collapse_edges = layout.cell(index).is_none();
        if let Some(p) = paragraphs.get(index).copied().flatten() {
            content_height = p.height;
        } else {
            for j in 0..b.line_count {
                content_height += input.lines[(b.line_start + j) as usize].height;
            }
        }
        let mut child = index + 1;
        while child < b.subtree_end as usize {
            let c = states[child];
            pending = max(pending, c.before);
            if c.through {
                pending = max(pending, c.after);
                child = blocks[child].subtree_end as usize;
                continue;
            }
            if !content && b.insets[1] == 0.0 && collapse_edges {
                s.before = max(s.before, pending);
            } else {
                content_height += pending;
            }
            content_height += c.height;
            content = true;
            pending = c.after;
            child = blocks[child].subtree_end as usize;
        }
        if !content && b.insets[1] == 0.0 && collapse_edges {
            s.before = max(s.before, pending);
            pending = 0.0;
        }
        if b.insets[3] == 0.0 && collapse_edges {
            s.after = max(s.after, pending);
        } else {
            content_height += pending;
        }
        s.content_height = content_height;
        s.height = b.insets[1] + content_height + b.insets[3];
        s.through = !content && b.insets[1] == 0.0 && b.insets[3] == 0.0 && collapse_edges;
        if s.through {
            s.before = max(s.before, s.after);
            s.after = s.before;
        }
        if !s.height.is_finite() {
            return invalid();
        }
        states[index] = s;
    }
    Ok(())
}
fn place(
    input: NativeDocumentInput<'_>,
    layout: &Rows<'_>,
    paragraphs: &[Option<&NativeDocumentPositionedParagraph>],
    states: &[State],
    boxes: &mut [NativeDocumentBox],
    positions: &mut [NativeDocumentPosition],
) -> Result<NativeDocumentResult> {
    let blocks = input.structure.blocks;
    let mut cursors = filled(blocks.len(), Cursor::default())?;
    let mut forest = Cursor::default();
    let mut object_cursor = 0;
    let mut extent_width = input.width;
    let mut content_width = 0.0;
    for (i, b) in blocks.iter().enumerate() {
        let s = states[i];
        let mut p = if b.parent_index == ROOT {
            forest
        } else {
            cursors[b.parent_index as usize]
        };
        let mut y = p.y;
        let cell = layout.cell(i);
        if let Some(c) = cell {
            y = boxes[b.parent_index as usize].y
                + input.structure.rows[c.row_index as usize].cell_spacing * 0.5
                + s.before;
        } else {
            p.pending = max(p.pending, s.before);
            let top_collapse = b.parent_index != ROOT
                && blocks[b.parent_index as usize].insets[1] == 0.0
                && layout.cell(b.parent_index as usize).is_none();
            if s.through {
                p.pending = max(p.pending, s.after);
            } else {
                if p.content || !top_collapse {
                    y += p.pending;
                }
                p.y = y + s.height;
                p.pending = s.after;
                p.content = true;
            }
        }
        boxes[i].y = y + b.insets[1];
        boxes[i].height = if let Some(c) = cell {
            max(
                0.0,
                states[b.parent_index as usize].content_height
                    - input.structure.rows[c.row_index as usize].cell_spacing
                    - s.before
                    - s.after
                    - b.insets[1]
                    - b.insets[3],
            )
        } else {
            s.content_height
        };
        cursors[i].y = boxes[i].y;
        cursors[i].trailing = p.trailing + b.insets[2] + b.margins[2];
        extent_width = max(
            extent_width,
            boxes[i].x + boxes[i].width + cursors[i].trailing,
        );
        content_width = max(
            content_width,
            boxes[i].x
                + cursors[i].trailing
                + if layout.row(i).is_some() {
                    boxes[i].width
                } else {
                    0.0
                },
        );
        if object_cursor < input.objects.len()
            && input.objects[object_cursor].block_index as usize == i
        {
            let right = boxes[i].x + input.objects[object_cursor].width + cursors[i].trailing;
            object_cursor += 1;
            extent_width = max(extent_width, right);
            content_width = max(content_width, right);
        }
        let mut line_y = boxes[i].y;
        let paragraph = paragraphs.get(i).copied().flatten();
        if let Some(paragraph) = paragraph {
            extent_width = max(
                extent_width,
                boxes[i].x + paragraph.width + cursors[i].trailing,
            );
            content_width = max(
                content_width,
                boxes[i].x + paragraph.width + cursors[i].trailing,
            );
        }
        for j in 0..b.line_count {
            let index = (b.line_start + j) as usize;
            positions[index] = if paragraph.is_some() {
                NativeDocumentPosition {
                    x: boxes[i].x + input.local_positions[index].x,
                    y: boxes[i].y + input.local_positions[index].y,
                }
            } else {
                let position = NativeDocumentPosition {
                    x: boxes[i].x,
                    y: line_y,
                };
                line_y += input.lines[index].height;
                position
            };
            if !pair(positions[index].x, positions[index].y) {
                return invalid();
            }
            extent_width = max(
                extent_width,
                positions[index].x + input.lines[index].width + cursors[i].trailing,
            );
            content_width = max(
                content_width,
                positions[index].x + input.lines[index].width + cursors[i].trailing,
            );
        }
        if !boxes[i].y.is_finite() || !p.y.is_finite() || !line_y.is_finite() {
            return invalid();
        }
        if b.parent_index == ROOT {
            forest = p;
        } else {
            cursors[b.parent_index as usize] = p;
        }
    }
    let height = forest.y + forest.pending;
    if !height.is_finite() || !extent_width.is_finite() {
        return invalid();
    }
    Ok(NativeDocumentResult {
        block_count: blocks.len() as u32,
        line_count: input.lines.len() as u32,
        width: extent_width,
        height,
        content_width,
    })
}
pub(super) fn arrange(
    input: NativeDocumentInput<'_>,
) -> Result<(
    Vec<NativeDocumentBox>,
    Vec<NativeDocumentPosition>,
    NativeDocumentResult,
)> {
    input.admit()?;
    let blocks = input.structure.blocks;
    let mut boxes = filled(blocks.len(), NativeDocumentBox::default())?;
    let mut states = filled(blocks.len(), State::default())?;
    let mut placed = filled(input.lines.len(), NativeDocumentPosition::default())?;
    let layout = Rows::new(input.structure)?;
    let mut paragraphs = filled(
        if input.paragraphs.is_empty() {
            0
        } else {
            blocks.len()
        },
        None,
    )?;
    widths(input.structure, input.width, &layout, &mut boxes)?;
    for (i, p) in input.paragraphs.iter().enumerate() {
        let index = p.block_index as usize;
        if index >= blocks.len()
            || i != 0 && input.paragraphs[i - 1].block_index >= p.block_index
            || !pair(p.width, p.height)
            || p.height == 0.0
        {
            return invalid();
        }
        let b = blocks[index];
        if b.subtree_end as usize != index + 1
            || b.line_count == 0
            || b.line_start as usize > input.lines.len()
            || b.line_count as usize > input.lines.len() - b.line_start as usize
            || layout.row(index).is_some()
        {
            return invalid();
        }
        paragraphs[index] = Some(p);
    }
    measure(input, &layout, &paragraphs, &mut states)?;
    if !input.paragraphs.is_empty() {
        for (i, b) in blocks.iter().enumerate() {
            if b.line_start as usize > input.lines.len()
                || b.line_count as usize > input.lines.len() - b.line_start as usize
            {
                return invalid();
            }
            for j in 0..b.line_count {
                let k = (b.line_start + j) as usize;
                let p = input.local_positions[k];
                let descriptor = paragraphs[i];
                if !pair(p.x, p.y)
                    || match descriptor {
                        None => p.x != 0.0 || p.y != 0.0,
                        Some(d) => {
                            p.x + input.lines[k].width > d.width
                                || p.y + input.lines[k].height > d.height
                        }
                    }
                {
                    return invalid();
                }
            }
        }
    }
    let result = place(
        input,
        &layout,
        &paragraphs,
        &states,
        &mut boxes,
        &mut placed,
    )?;
    Ok((boxes, placed, result))
}
