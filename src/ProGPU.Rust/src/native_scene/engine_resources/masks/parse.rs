//! Pointer-free owned views of the already semantically admitted mask resource.
use super::*;
struct Reader<'a> {
    bytes: &'a [u8],
    offset: usize,
}
impl<'a> Reader<'a> {
    fn take(&mut self, size: usize) -> Result<&'a [u8], Error> {
        let end = self.offset.checked_add(size).ok_or(Error::Capacity)?;
        let result = self
            .bytes
            .get(self.offset..end)
            .ok_or(Error::InvalidInput)?;
        self.offset = end;
        Ok(result)
    }
    fn records<T: WireRecord>(&mut self, count: u32) -> Result<Arc<[T]>, Error> {
        let bytes = self.take(
            (count as usize)
                .checked_mul(T::WIRE_SIZE)
                .ok_or(Error::Capacity)?,
        )?;
        let mut result = allocate(count as usize)?;
        for chunk in bytes.chunks_exact(T::WIRE_SIZE) {
            result.push(T::read(chunk).ok_or(Error::InvalidInput)?);
        }
        Ok(result.into())
    }
    fn end(&self) -> Result<(), Error> {
        if self.offset != self.bytes.len() {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
}
fn read<T: WireRecord>(bytes: &[u8]) -> Result<T, Error> {
    T::read(bytes).ok_or(Error::InvalidInput)
}
fn owned(bytes: &[u8]) -> Result<Arc<[u8]>, Error> {
    let mut result = allocate(bytes.len())?;
    result.extend_from_slice(bytes);
    Ok(result.into())
}
fn picture(source: wire::LayerPictureMask, streams: &[u8]) -> Result<MaskNode, Error> {
    let start = source.stream_offset as usize;
    let end = start
        .checked_add(source.stream_size as usize)
        .ok_or(Error::Capacity)?;
    let bytes = owned(streams.get(start..end).ok_or(Error::InvalidInput)?)?;
    let scene = NativeScene::new(bytes).map_err(|_| Error::InvalidInput)?;
    Ok(MaskNode::Picture(source, Arc::new(scene)))
}
fn stops(
    mut brush: wire::Brush,
    source: &Arc<[wire::GradientStop]>,
) -> Result<(wire::Brush, Arc<[wire::GradientStop]>), Error> {
    let count = crate::native_scene::semantic::stored_stop_count(&brush);
    let offset = brush.stop_offset as usize;
    if count == 0 {
        return Ok((brush, source.clone()));
    }
    let selected = source
        .get(offset..offset.checked_add(count as usize).ok_or(Error::Capacity)?)
        .ok_or(Error::InvalidInput)?;
    let mut result = allocate(selected.len())?;
    result.extend_from_slice(selected);
    brush.stop_offset = 0;
    Ok((brush, result.into()))
}
pub(super) fn resource(scene: &Arc<NativeScene>, index: u32) -> Result<MaskNode, Error> {
    let resource = scene
        .envelope()
        .resource(index)
        .ok_or(Error::InvalidInput)?;
    if resource.record.kind != SCENE_RESOURCE_LAYER_MASK {
        return Err(Error::InvalidInput);
    }
    let kind = u32::read(resource.payload.get(4..).ok_or(Error::InvalidInput)?)
        .ok_or(Error::InvalidInput)?;
    let mut r = Reader {
        bytes: resource.auxiliary,
        offset: 0,
    };
    let result = match kind {
        SCENE_LAYER_MASK_ROUNDED_RECTANGLE => MaskNode::Analytic(read(resource.payload)?),
        SCENE_LAYER_MASK_COVERAGE_BITMAP => {
            r.offset = r.bytes.len();
            MaskNode::Coverage(read(resource.payload)?, owned(resource.auxiliary)?)
        }
        SCENE_LAYER_MASK_ANALYTIC_CHAIN => MaskNode::Chain(read(resource.payload)?),
        SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN => {
            let header: wire::LayerVectorMask = read(resource.payload)?;
            MaskNode::Vector(VectorMask {
                paths: r.records(header.path_count)?,
                segments: r.records(header.segment_count)?,
                nodes: r.records(header.boolean_node_count)?,
                header,
            })
        }
        SCENE_LAYER_MASK_BRUSH => {
            let header: wire::LayerBrushMask = read(resource.payload)?;
            MaskNode::Brush(BrushMask {
                stops: r.records(header.gradient_stop_count)?,
                header,
            })
        }
        SCENE_LAYER_MASK_GEOMETRY => {
            let header: wire::LayerGeometryMask = read(resource.payload)?;
            MaskNode::Geometry(GeometryMask {
                primitives: r.records(header.primitive_count)?,
                stops: r.records(header.gradient_stop_count)?,
                header,
            })
        }
        SCENE_LAYER_MASK_PICTURE => {
            r.offset = r.bytes.len();
            picture(read(resource.payload)?, resource.auxiliary)?
        }
        SCENE_LAYER_MASK_COMPOSITE => {
            let mut raw = [0u8; wire::LayerCompositeMask::WIRE_SIZE];
            let destination = raw
                .get_mut(..resource.payload.len())
                .ok_or(Error::InvalidInput)?;
            destination.copy_from_slice(resource.payload);
            let header: wire::LayerCompositeMask = read(&raw)?;
            let brushes = r.records::<wire::LayerBrushMask>(header.brush_mask_count)?;
            let geometries = r.records::<wire::LayerGeometryMask>(header.geometry_mask_count)?;
            let primitives =
                r.records::<wire::GeometryPrimitive>(header.geometry_primitive_count)?;
            let pictures = r.records::<wire::LayerPictureMask>(header.picture_mask_count)?;
            let streams = r.take(header.picture_stream_bytes as usize)?;
            let paths = r.records::<wire::ClipPath>(header.path_count)?;
            let segments = r.records::<wire::PathSegment>(header.segment_count)?;
            let nodes = r.records::<wire::PathBooleanNode>(header.boolean_node_count)?;
            let gradient_stops = r.records::<wire::GradientStop>(header.gradient_stop_count)?;
            let mut children = allocate(header.component_count as usize)?;
            // Original composition order is vector, pictures, geometry, then brush.
            if header.path_count != 0 {
                children.push(MaskNode::Vector(VectorMask {
                    header: wire::LayerVectorMask {
                        struct_size: wire::LayerVectorMask::WIRE_SIZE as u32,
                        kind: SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN,
                        path_count: header.path_count,
                        segment_count: header.segment_count,
                        opacity: 1.0,
                        boolean_node_count: header.boolean_node_count,
                        ..Default::default()
                    },
                    paths,
                    segments,
                    nodes,
                }));
            }
            for source in pictures.iter() {
                children.push(picture(*source, streams)?);
            }
            for source in geometries.iter() {
                let mut source = *source;
                let (brush, stops) = stops(source.brush, &gradient_stops)?;
                source.brush = brush;
                children.push(MaskNode::Geometry(GeometryMask {
                    header: source,
                    primitives: primitives.clone(),
                    stops,
                }));
            }
            for source in brushes.iter() {
                let mut source = *source;
                let (brush, stops) = stops(source.brush, &gradient_stops)?;
                source.brush = brush;
                children.push(MaskNode::Brush(BrushMask {
                    header: source,
                    stops,
                }));
            }
            if children.len() != header.component_count as usize {
                return Err(Error::InvalidInput);
            }
            MaskNode::Composite {
                opacity: header.opacity,
                children: children.into(),
            }
        }
        _ => return Err(Error::InvalidInput),
    };
    r.end()?;
    Ok(result)
}
