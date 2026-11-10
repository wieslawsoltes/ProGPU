//! Original text alpha variants and styled/painted/legacy payload prefixes.
use super::*;
#[derive(Clone, Debug)]
pub struct NativeTextStylePage {
    scene: Arc<NativeScene>,
    scene_hash: u64,
    styles: Arc<[wire::TextStyle]>,
    command_styles: Arc<[u32]>,
}
impl NativeTextStylePage {
    pub fn compile(scene: Arc<NativeScene>, scene_hash: u64) -> Result<Self, Error> {
        let mut styles = allocate(32)?;
        styles.push(wire::TextStyle::default());
        let count = scene.envelope().header().command_count as usize;
        let mut indices = allocate(count)?;
        indices.resize(count, SCENE_NO_INDEX);
        let mut variants = variants(32)?;
        let mut cursor = NativeStateCursor::new(scene.clone(), [1.0, 1.0]);
        for (index, command) in scene.commands().enumerate() {
            let state = cursor.advance(command.record)?;
            if command.record.kind != SCENE_COMMAND_DRAW_GLYPH_RUN
                || command.record.flags & SCENE_GLYPH_STYLED == 0
            {
                continue;
            }
            let draw: wire::GlyphDraw = read(command.payload, 0)?;
            let key = Key(
                draw.style_resource_index,
                draw.style_index,
                state.opacity.to_bits(),
            );
            if let Some(&packed) = variants.get(&key) {
                indices[index] = packed;
                continue;
            }
            if styles.len() > SCENE_MAX_TEXT_STYLES as usize || styles.len() >= (1 << 24) {
                return Err(Error::Capacity);
            }
            let resource = scene
                .envelope()
                .resource(draw.style_resource_index)
                .ok_or(Error::InvalidInput)?;
            let mut style: wire::TextStyle = read(resource.payload, draw.style_index as usize)?;
            style.color.a *= state.opacity;
            let packed = styles.len() as u32;
            push(&mut styles, style)?;
            variants.try_reserve(1).map_err(|_| Error::Capacity)?;
            variants.insert(key, packed);
            indices[index] = packed;
        }
        Ok(Self {
            scene,
            scene_hash,
            styles: styles.into(),
            command_styles: indices.into(),
        })
    }
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn scene_hash(&self) -> u64 {
        self.scene_hash
    }
    pub fn styles(&self) -> &[wire::TextStyle] {
        &self.styles
    }
    pub fn command_style_indices(&self) -> &[u32] {
        &self.command_styles
    }
    pub fn command_style_index(&self, command: u32) -> Option<u32> {
        let index = *self.command_styles.get(command as usize)?;
        (index == SCENE_NO_INDEX || (index as usize) < self.styles.len()).then_some(index)
    }
}
/// Exact source glyph inventory prefix. Returns the retained raw positioned
/// records and count; this is not shaping or glyph geometry reconstruction.
pub fn native_glyph_payload(command: super::super::CommandView<'_>) -> Option<(&[u8], u32)> {
    let bytes = command.payload;
    if command.record.kind == SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN {
        let prefix = wire::PaintedGlyphDraw::WIRE_SIZE + wire::GlyphPaint::WIRE_SIZE;
        if bytes.len() < prefix {
            return None;
        }
        let draw = wire::PaintedGlyphDraw::read(bytes)?;
        if draw.struct_size != wire::PaintedGlyphDraw::WIRE_SIZE as u32
            || draw.glyph_count == 0
            || prefix as u64 + u64::from(draw.glyph_count) * wire::PositionedGlyph::WIRE_SIZE as u64
                != bytes.len() as u64
        {
            return None;
        }
        return Some((&bytes[prefix..], draw.glyph_count));
    }
    if command.record.flags & SCENE_GLYPH_STYLED == 0 {
        if bytes.is_empty() || bytes.len() % wire::PositionedGlyph::WIRE_SIZE != 0 {
            return None;
        }
        return Some((
            bytes,
            (bytes.len() / wire::PositionedGlyph::WIRE_SIZE) as u32,
        ));
    }
    let draw = wire::GlyphDraw::read(bytes)?;
    if draw.struct_size != wire::GlyphDraw::WIRE_SIZE as u32
        || draw.glyph_count == 0
        || wire::GlyphDraw::WIRE_SIZE as u64
            + u64::from(draw.glyph_count) * wire::PositionedGlyph::WIRE_SIZE as u64
            != bytes.len() as u64
    {
        return None;
    }
    Some((&bytes[wire::GlyphDraw::WIRE_SIZE..], draw.glyph_count))
}
