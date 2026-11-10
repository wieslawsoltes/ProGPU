//! Native recorded source-input capture, independent of raster/culling bounds.
use super::*;
use crate::native_scene::semantic::{image_payload, values};
mod dash;
mod math;
mod shapes;
mod tree;
mod triangles;
mod walk;
const EXACT_LIMIT: usize = 1 << 24;
fn p2(x: f32, y: f32) -> wire::Point {
    wire::Point { x, y }
}
fn f4(x: f32, y: f32, z: f32, w: f32) -> wire::Float4 {
    wire::Float4 { x, y, z, w }
}
fn finite_point(p: wire::Point) -> bool {
    p.x.is_finite() && p.y.is_finite()
}
// std::min/max retain the first equal value, including signed zero.
fn minimum(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
fn maximum(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
fn push<T>(values: &mut Vec<T>, value: T) -> BuildResult<()> {
    reserve_append(values, 1)?;
    values.push(value);
    Ok(())
}
fn record<T: WireRecord>(bytes: &[u8], index: usize) -> BuildResult<T> {
    index
        .checked_mul(T::WIRE_SIZE)
        .and_then(|start| bytes.get(start..))
        .and_then(T::read)
        .ok_or(B::UnsupportedHitTest)
}
#[derive(Clone, Copy, Default)]
struct ClipEntry {
    scope: u32,
    frame: u32,
    start: Option<u32>,
}
#[derive(Clone, Copy, Default)]
struct VectorClip {
    loaded: bool,
    rectangle: bool,
    frame: u32,
    start: u32,
    count: u32,
    rule: u32,
    min: wire::Point,
    max: wire::Point,
}
struct Capture<'a> {
    builder: &'a NativeSceneBuilder,
    source_geometry: bool,
    input_flags: u32,
    primitives: Vec<wire::HitTestPrimitive>,
    segments: Vec<wire::PathSegment>,
    dash_points: Vec<wire::Point>,
    dash_intervals: Vec<f64>,
    layer_clips: Vec<wire::ImageRect>,
    layer_clip_scope: u32,
    input_frames: Vec<wire::Affine2d>,
    input_frame: u32,
    clip_starts: Vec<ClipEntry>,
    vector_clips: Vec<VectorClip>,
    owner: Option<i32>,
    query: u32,
}
impl NativeSceneBuilder {
    /// Capture the original supported input families from actual retained
    /// builder metadata. No raster readback, serialization round trip, inferred
    /// source glyph bounds or broad-phase replacement of precise geometry.
    pub fn add_recorded_hit_test_index(
        &mut self,
        opacity_mode: NativeSceneHitTestOpacityMode,
    ) -> BuildResult<u32> {
        if self.stack_depth != 0 {
            return Err(B::UnbalancedStack);
        }
        let source_geometry = opacity_mode == NativeSceneHitTestOpacityMode::SourceGeometry;
        let mut capture = Capture {
            builder: self,
            source_geometry,
            input_flags: SCENE_STATE_CLIP_RECT
                | SCENE_STATE_CLIP_PIXEL_CENTERS
                | if source_geometry {
                    SCENE_STATE_GUIDELINE_SET | SCENE_STATE_MASK
                } else {
                    0
                },
            primitives: Vec::new(),
            segments: Vec::new(),
            dash_points: Vec::new(),
            dash_intervals: Vec::new(),
            layer_clips: Vec::new(),
            layer_clip_scope: 0,
            input_frames: Vec::new(),
            input_frame: 0,
            clip_starts: Vec::new(),
            vector_clips: Vec::new(),
            owner: None,
            query: 0,
        };
        push(&mut capture.layer_clips, wire::ImageRect::default())?;
        reserve_total(&mut capture.clip_starts, self.resources.len() + 1)?;
        capture
            .clip_starts
            .resize(self.resources.len() + 1, ClipEntry::default());
        capture.walk()?;
        let (nodes, indices) = tree::build(&capture.primitives)?;
        let Capture {
            primitives,
            segments,
            ..
        } = capture;
        self.add_hit_test_index(&primitives, &nodes, &indices, &segments)
    }
}
impl Capture<'_> {
    fn map_rectangle(&self, rectangle: wire::ImageRect) -> BuildResult<wire::ImageRect> {
        if self.input_frame == 0 {
            return Ok(rectangle);
        }
        let t = self.input_frames[self.input_frame as usize];
        if !((t.m12 == 0.0 && t.m21 == 0.0) || (t.m11 == 0.0 && t.m22 == 0.0))
            || rectangle.width < 0.0
            || rectangle.height < 0.0
        {
            return Err(B::UnsupportedHitTest);
        }
        let mut bounds = wire::HitTestPrimitive::default();
        if !math::place(
            &mut bounds,
            p2(rectangle.x, rectangle.y),
            p2(
                rectangle.x + rectangle.width,
                rectangle.y + rectangle.height,
            ),
            t,
        ) {
            return Err(B::UnsupportedHitTest);
        }
        let rectangle = wire::ImageRect {
            x: bounds.bounds_min.x,
            y: bounds.bounds_min.y,
            width: bounds.bounds_max.x - bounds.bounds_min.x,
            height: bounds.bounds_max.y - bounds.bounds_min.y,
        };
        if !rectangle.width.is_finite() || !rectangle.height.is_finite() {
            return Err(B::UnsupportedHitTest);
        }
        Ok(rectangle)
    }
    fn state(&self, index: u32) -> BuildResult<wire::State> {
        let mut state = if index == NO_INDEX {
            NativeSceneBuilder::identity_state()
        } else {
            record(
                &self
                    .builder
                    .resources
                    .get(index as usize)
                    .ok_or(B::UnsupportedHitTest)?
                    .payload,
                0,
            )?
        };
        if state.flags & !self.input_flags != 0 {
            return Err(B::UnsupportedHitTest);
        }
        if self.input_frame != 0 {
            state.transform = math::input_frame(
                state.transform,
                self.input_frames[self.input_frame as usize],
            )
            .ok_or(B::UnsupportedHitTest)?;
            if state.flags & SCENE_STATE_CLIP_RECT != 0 {
                state.clip_rect = self.map_rectangle(state.clip_rect)?;
            }
        }
        Ok(state)
    }
    fn vector_clip(&mut self, index: u32) -> BuildResult<VectorClip> {
        let builder = self.builder;
        let resource = builder
            .resources
            .get(index as usize)
            .ok_or(B::UnsupportedHitTest)?;
        if self.vector_clips.is_empty() {
            reserve_total(&mut self.vector_clips, builder.resources.len())?;
            self.vector_clips
                .resize(builder.resources.len(), VectorClip::default());
        }
        let cached = self.vector_clips[index as usize];
        if cached.loaded && cached.frame == self.input_frame {
            return Ok(cached);
        }
        if !resource.source_geometry_clip
            || resource.record.kind != SCENE_RESOURCE_LAYER_MASK
            || resource.payload.len() != wire::LayerVectorMask::WIRE_SIZE
        {
            return Err(B::UnsupportedHitTest);
        }
        let mask: wire::LayerVectorMask = record(&resource.payload, 0)?;
        if mask.kind != SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN
            || mask.opacity != 1.0
            || mask.path_count != 1
            || mask.boolean_node_count != 0
        {
            return Err(B::UnsupportedHitTest);
        }
        let path: wire::ClipPath = record(&resource.auxiliary, 0)?;
        if path.boolean_node_count != 0
            || path.operation != CLIP_INTERSECT
            || path.segment_count == 0
            || path.segment_count > EXACT_LIMIT as u64
            || self.segments.len() > EXACT_LIMIT - path.segment_count as usize
        {
            return Err(B::UnsupportedHitTest);
        }
        let mut transform = path.transform;
        if self.input_frame != 0 {
            transform = math::input_frame(transform, self.input_frames[self.input_frame as usize])
                .ok_or(B::UnsupportedHitTest)?;
        }
        let mut bounds = wire::HitTestPrimitive::default();
        if !math::place(
            &mut bounds,
            p2(path.min_x, path.min_y),
            p2(path.max_x, path.max_y),
            transform,
        ) {
            return Err(B::UnsupportedHitTest);
        }
        let mut cached = VectorClip {
            loaded: false,
            rectangle: false,
            frame: self.input_frame,
            start: self.segments.len() as u32,
            count: path.segment_count as u32,
            rule: if path.fill_rule == FILL_RULE_EVEN_ODD {
                0
            } else {
                1
            },
            min: bounds.bounds_min,
            max: bounds.bounds_max,
        };
        let data = resource
            .auxiliary
            .get(wire::ClipPath::WIRE_SIZE..)
            .ok_or(B::UnsupportedHitTest)?;
        for j in 0..path.segment_count {
            let mut segment: wire::PathSegment = record(data, (path.segment_offset + j) as usize)?;
            if !matches!(
                segment.kind,
                PATH_SEGMENT_LINE | PATH_SEGMENT_QUADRATIC | PATH_SEGMENT_CUBIC
            ) {
                return Err(B::UnsupportedHitTest);
            }
            let mut x = [segment.p0.x, segment.p1.x, segment.p2.x, segment.p3.x];
            let mut y = [segment.p0.y, segment.p1.y, segment.p2.y, segment.p3.y];
            if !math::coordinates(&mut x, &mut y, transform) {
                return Err(B::UnsupportedHitTest);
            }
            segment.p0 = p2(x[0], y[0]);
            segment.p1 = p2(x[1], y[1]);
            segment.p2 = p2(x[2], y[2]);
            segment.p3 = p2(x[3], y[3]);
            push(&mut self.segments, segment)?;
        }
        cached.rectangle = math::rectangular(
            &self.segments[cached.start as usize..cached.start as usize + cached.count as usize],
            cached.min,
            cached.max,
        );
        cached.loaded = true;
        self.vector_clips[index as usize] = cached;
        Ok(cached)
    }
    fn append(
        &mut self,
        mut primitive: wire::HitTestPrimitive,
        state: &wire::State,
        state_index: u32,
    ) -> BuildResult<()> {
        if self.primitives.len() >= EXACT_LIMIT {
            return Err(B::UnsupportedHitTest);
        }
        primitive.id = self.owner.ok_or(B::UnsupportedHitTest)?;
        primitive.z_index = self.primitives.len() as f32;
        primitive.flags = HIT_TEST_VISIBLE | HIT_TEST_VISIBLE_TO_INPUT | self.query;
        let mut vector = if state.flags & SCENE_STATE_MASK != 0 {
            Some(self.vector_clip(state.mask_resource_index)?)
        } else {
            None
        };
        if let Some(clip) = vector {
            primitive.bounds_min = p2(
                maximum(primitive.bounds_min.x, clip.min.x),
                maximum(primitive.bounds_min.y, clip.min.y),
            );
            primitive.bounds_max = p2(
                minimum(primitive.bounds_max.x, clip.max.x),
                minimum(primitive.bounds_max.y, clip.max.y),
            );
            if primitive.bounds_min.x > primitive.bounds_max.x
                || primitive.bounds_min.y > primitive.bounds_max.y
            {
                return Ok(());
            }
            primitive.clip_start_segment = clip.start;
            primitive.clip_segment_count = clip.count;
            primitive.clip_fill_rule = clip.rule;
            primitive.clip_flags = 1;
        }
        let state_clip = state.flags & SCENE_STATE_CLIP_RECT != 0;
        if state_clip || self.layer_clip_scope != 0 {
            let mut clip = if !state_clip {
                self.layer_clips[self.layer_clip_scope as usize]
            } else if self.layer_clip_scope == 0 {
                state.clip_rect
            } else {
                math::intersect(
                    state.clip_rect,
                    self.layer_clips[self.layer_clip_scope as usize],
                )
            };
            if let Some(v) = vector.filter(|v| v.rectangle) {
                clip = math::intersect(
                    clip,
                    wire::ImageRect {
                        x: v.min.x,
                        y: v.min.y,
                        width: v.max.x - v.min.x,
                        height: v.max.y - v.min.y,
                    },
                );
                vector = None;
            }
            if clip.width <= 0.0 || clip.height <= 0.0 {
                return Ok(());
            }
            let right = clip.x + clip.width;
            let bottom = clip.y + clip.height;
            if !right.is_finite() || !bottom.is_finite() {
                return Err(B::UnsupportedHitTest);
            }
            if let Some(v) = vector {
                if clip.x > v.min.x || clip.y > v.min.y || right < v.max.x || bottom < v.max.y {
                    return Err(B::UnsupportedHitTest);
                }
                return push(&mut self.primitives, primitive);
            }
            primitive.bounds_min = p2(
                maximum(primitive.bounds_min.x, clip.x),
                maximum(primitive.bounds_min.y, clip.y),
            );
            primitive.bounds_max = p2(
                minimum(primitive.bounds_max.x, right),
                minimum(primitive.bounds_max.y, bottom),
            );
            if primitive.bounds_min.x > primitive.bounds_max.x
                || primitive.bounds_min.y > primitive.bounds_max.y
            {
                return Ok(());
            }
            let index = if state_index == NO_INDEX {
                self.builder.resources.len()
            } else {
                state_index as usize
            };
            let cached = &mut self.clip_starts[index];
            if cached.scope != self.layer_clip_scope || cached.frame != self.input_frame {
                cached.scope = self.layer_clip_scope;
                cached.frame = self.input_frame;
                cached.start = None;
            }
            if cached.start.is_none() {
                if self.segments.len() > EXACT_LIMIT - 4 {
                    return Err(B::UnsupportedHitTest);
                }
                cached.start = Some(self.segments.len() as u32);
                let points = [
                    p2(clip.x, clip.y),
                    p2(right, clip.y),
                    p2(right, bottom),
                    p2(clip.x, bottom),
                ];
                for i in 0..4 {
                    push(
                        &mut self.segments,
                        wire::PathSegment {
                            kind: PATH_SEGMENT_LINE,
                            p0: points[i],
                            p1: points[(i + 1) % 4],
                            ..Default::default()
                        },
                    )?;
                }
            }
            primitive.clip_start_segment = cached.start.ok_or(B::UnsupportedHitTest)?;
            primitive.clip_segment_count = 4;
            primitive.clip_flags = 1;
            primitive.clip_fill_rule = 1;
        }
        push(&mut self.primitives, primitive)
    }
}
