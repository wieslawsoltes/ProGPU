use super::*;
use crate::{geometry::Affine, vector::PathGeometry};

/// Original independent invalidation generations. Content changes also change
/// `change`; descendant changes do not replace an ancestor's local content.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct VisualVersions {
    /// Any local or descendant presentation change.
    pub change: i64,
    /// Original local render-command generation.
    pub content: i64,
    /// Structural child insertion/removal/reordering generation.
    pub tree: i64,
}

/// Original retained rectangle or geometry clip, with its independent transform.
#[derive(Clone, Debug)]
pub enum VisualCompositeClip {
    /// Original x/y/width/height; far edges are computed by the real consumer.
    Rectangle {
        bounds: [f32; 4],
        transform: Matrix4,
    },
    /// Actual immutable geometry owner, never replaced by its bounds.
    Geometry {
        geometry: Arc<PathGeometry>,
        transform: Matrix4,
    },
}
impl PartialEq for VisualCompositeClip {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (
                Self::Rectangle {
                    bounds: a,
                    transform: x,
                },
                Self::Rectangle {
                    bounds: b,
                    transform: y,
                },
            ) => a == b && x == y,
            (
                Self::Geometry {
                    geometry: a,
                    transform: x,
                },
                Self::Geometry {
                    geometry: b,
                    transform: y,
                },
            ) => Arc::ptr_eq(a, b) && x == y,
            _ => false,
        }
    }
}

/// Cold visual metadata retained independently from local geometry and layout.
#[derive(Clone, Debug, Default)]
pub struct VisualCompositeState {
    /// Visual-local rectangular clip, distinct from the outer composite clips.
    pub clip_bounds: Option<[f32; 4]>,
    /// Outer original x/y/width/height clip.
    pub outer_clip_bounds: Option<[f32; 4]>,
    /// Ordered original outer clips, each with its own source transform.
    pub outer_composite_clips: Arc<[VisualCompositeClip]>,
    /// Actual immutable local clip generation.
    pub geometry_clip: Option<Arc<PathGeometry>>,
    /// Independent transformed local clip.
    pub local_composite_clip: Option<VisualCompositeClip>,
    /// Owned opacity-mask recording; alpha is not retained hit geometry.
    pub opacity_mask_picture: Option<Arc<Scene>>,
    /// Independent mask mapping rectangle.
    pub opacity_mask_bounds: Option<[f32; 4]>,
    /// Original uneffected subtree bounds; None selects layout size downstream.
    pub effect_content_bounds: Option<[f32; 4]>,
    /// Original optional scalar effect padding, including invalid raw metadata.
    pub effect_raster_padding: Option<f32>,
    /// Explicit actual source translation, finite when present.
    pub effect_source_translation: Option<[f32; 2]>,
    /// Optional layer-cache optimization flag.
    pub cache_as_layer: bool,
    /// Required source-cache contract; this is never optional direct replay.
    pub requires_layer_cache: bool,
    /// Explicit source ClearType policy, independent of ordinary paint policy.
    pub layer_cache_cleartype_policy: Option<bool>,
    /// Resolution multiplier, normalized by assignment as in the source setter.
    pub layer_cache_render_scale: Option<f32>,
    /// Snap only cached destination origins at their actual target boundary.
    pub layer_cache_snaps_to_device_pixels: bool,
}
impl PartialEq for VisualCompositeState {
    fn eq(&self, other: &Self) -> bool {
        self.clip_bounds == other.clip_bounds
            && self.outer_clip_bounds == other.outer_clip_bounds
            && self.outer_composite_clips == other.outer_composite_clips
            && same_arc(self.geometry_clip.as_ref(), other.geometry_clip.as_ref())
            && self.local_composite_clip == other.local_composite_clip
            && same_arc(
                self.opacity_mask_picture.as_ref(),
                other.opacity_mask_picture.as_ref(),
            )
            && self.opacity_mask_bounds == other.opacity_mask_bounds
            && self.effect_content_bounds == other.effect_content_bounds
            && self.effect_raster_padding == other.effect_raster_padding
            && self.effect_source_translation == other.effect_source_translation
            && self.cache_as_layer == other.cache_as_layer
            && self.requires_layer_cache == other.requires_layer_cache
            && self.layer_cache_cleartype_policy == other.layer_cache_cleartype_policy
            && self.layer_cache_render_scale == other.layer_cache_render_scale
            && self.layer_cache_snaps_to_device_pixels == other.layer_cache_snaps_to_device_pixels
    }
}
fn same_arc<T>(a: Option<&Arc<T>>, b: Option<&Arc<T>>) -> bool {
    match (a, b) {
        (None, None) => true,
        (Some(a), Some(b)) => Arc::ptr_eq(a, b),
        _ => false,
    }
}
impl VisualCompositeState {
    pub(super) fn normalized(mut self) -> Self {
        if let Some(scale) = self.layer_cache_render_scale {
            self.layer_cache_render_scale = Some(if scale.is_finite() {
                scale.max(0.0)
            } else {
                0.0
            });
        }
        self
    }
    /// Actual original cache default is one when no cold scale is stored.
    pub fn cache_render_scale(&self) -> f32 {
        self.layer_cache_render_scale.unwrap_or(1.0)
    }
}

/// Raw original `Scene.Visual` properties; this is not the stricter WinUI
/// composition wrapper. Finite-frame admission belongs to actual rendering.
#[derive(Clone, Debug, PartialEq)]
pub struct VisualProperties {
    /// Original two-dimensional offset.
    pub offset: [f32; 2],
    /// Layout size, independent of conservative local painted bounds.
    pub size: [f32; 2],
    /// Original visibility flag.
    pub visible: bool,
    /// Original unclamped scene alpha.
    pub opacity: f32,
    /// Full retained source matrix.
    pub transform: Matrix4,
    /// Three independent scale axes.
    pub scale: [f32; 3],
    /// Original Z rotation in radians.
    pub rotation: f32,
    /// Nonzero explicit center overrides size times render-transform origin.
    pub center_point: [f32; 3],
    /// Original default half-size origin.
    pub render_transform_origin: [f32; 2],
    /// Source-specific coordinate frame, never a paint reflection workaround.
    pub coordinate_frame: Matrix4,
    /// Explicit retained input identity, not inferred from raster bounds.
    pub hit_test_id: i32,
    /// Original templated visuals paint children before their own commands.
    pub has_template: bool,
    /// Conservative local render bounds; None disables local-content culling.
    pub local_render_bounds: Option<[f32; 4]>,
    /// Independent cold-state metadata.
    pub composite: VisualCompositeState,
}
impl Default for VisualProperties {
    fn default() -> Self {
        Self {
            offset: [0.0; 2],
            size: [0.0; 2],
            visible: true,
            opacity: 1.0,
            transform: Matrix4::IDENTITY,
            scale: [1.0; 3],
            rotation: 0.0,
            center_point: [0.0; 3],
            render_transform_origin: [0.5; 2],
            coordinate_frame: Matrix4::IDENTITY,
            hit_test_id: 0,
            has_template: false,
            local_render_bounds: None,
            composite: VisualCompositeState::default(),
        }
    }
}
impl VisualProperties {
    pub(super) fn normalized_cache_scale(mut self) -> Self {
        self.composite = self.composite.normalized();
        self
    }
    /// Exact original multiplication sequence; a zero center uses the relative
    /// anchor, including the original signed-zero comparisons.
    pub fn local_transform(&self) -> Matrix4 {
        self.local_transform_with_offset(self.offset)
    }
    /// Original overload used by source callers with an independently selected offset.
    pub fn local_transform_with_offset(&self, offset: [f32; 2]) -> Matrix4 {
        let anchor = if self.center_point != [0.0; 3] {
            self.center_point
        } else {
            [
                self.size[0] * self.render_transform_origin[0],
                self.size[1] * self.render_transform_origin[1],
                0.0,
            ]
        };
        let model = translation(anchor.map(|v| -v))
            * scale(self.scale)
            * rotation_z(self.rotation)
            * translation([offset[0] + anchor[0], offset[1] + anchor[1], anchor[2]]);
        self.transform * model
    }
}

/// Finite original composition-wrapper placement, independent from raw scene
/// fields. Relative Z offset is retained but original parent-size resolution
/// uses only X/Y. Quaternion inputs are normalized on assignment.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CompositionVisualProperties {
    /// Relative anchor in the effective size.
    pub anchor_point: [f32; 2],
    /// Three-dimensional transform center.
    pub center_point: [f32; 3],
    /// Source offset including retained Z.
    pub offset: [f32; 3],
    /// Quaternion components x/y/z/w.
    pub orientation: [f32; 4],
    /// Relative X/Y parent-size displacement; Z remains original metadata.
    pub relative_offset_adjustment: [f32; 3],
    /// Independent relative layout-size axes.
    pub relative_size_adjustment: [f32; 2],
    /// Axis rotation angle in radians.
    pub rotation_angle: f32,
    /// Normalized source axis.
    pub rotation_axis: [f32; 3],
    /// Independent three-dimensional scale.
    pub scale: [f32; 3],
    /// Nonnegative explicit layout size.
    pub size: [f32; 2],
    /// Full source transform, applied before resolved offset.
    pub transform: Matrix4,
    /// Original visibility.
    pub visible: bool,
    /// Finite composition alpha, clamped to [0,1] on assignment.
    pub opacity: f32,
}
impl Default for CompositionVisualProperties {
    fn default() -> Self {
        Self {
            anchor_point: [0.0; 2],
            center_point: [0.0; 3],
            offset: [0.0; 3],
            orientation: [0.0, 0.0, 0.0, 1.0],
            relative_offset_adjustment: [0.0; 3],
            relative_size_adjustment: [0.0; 2],
            rotation_angle: 0.0,
            rotation_axis: [0.0, 0.0, 1.0],
            scale: [1.0; 3],
            size: [0.0; 2],
            transform: Matrix4::IDENTITY,
            visible: true,
            opacity: 1.0,
        }
    }
}
impl CompositionVisualProperties {
    pub(super) fn normalized(mut self) -> Result<Self, Error> {
        let values = self
            .anchor_point
            .iter()
            .chain(self.center_point.iter())
            .chain(self.offset.iter())
            .chain(self.orientation.iter())
            .chain(self.relative_offset_adjustment.iter())
            .chain(self.relative_size_adjustment.iter())
            .chain(self.rotation_axis.iter())
            .chain(self.scale.iter())
            .chain(self.size.iter())
            .chain(self.transform.0.iter().flatten());
        if !values.copied().all(f32::is_finite)
            || !self.rotation_angle.is_finite()
            || !self.opacity.is_finite()
            || self.size.iter().any(|v| *v < 0.0)
        {
            return Err(Error::InvalidInput);
        }
        let axis_length = self.rotation_axis[0] * self.rotation_axis[0]
            + self.rotation_axis[1] * self.rotation_axis[1]
            + self.rotation_axis[2] * self.rotation_axis[2];
        let q = self.orientation;
        let q_length = q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3];
        if axis_length <= f32::from_bits(1) || q_length <= f32::from_bits(1) {
            return Err(Error::InvalidInput);
        }
        self.rotation_axis = self.rotation_axis.map(|v| v / axis_length.sqrt());
        self.orientation = self.orientation.map(|v| v / q_length.sqrt());
        self.opacity = self.opacity.clamp(0.0, 1.0);
        Ok(self)
    }
    /// Original wrapper arithmetic, preserving full matrix order and effective
    /// size independently of the inherited parent frame.
    pub fn resolve(self, parent_size: [f32; 2]) -> ([f32; 2], Matrix4) {
        let size = [0, 1]
            .map(|i| (self.size[i] + self.relative_size_adjustment[i] * parent_size[i]).max(0.0));
        let position = [
            self.offset[0] + self.relative_offset_adjustment[0] * parent_size[0]
                - self.anchor_point[0] * size[0],
            self.offset[1] + self.relative_offset_adjustment[1] * parent_size[1]
                - self.anchor_point[1] * size[1],
            self.offset[2] + 0.0 - 0.0,
        ];
        let half = self.rotation_angle * 0.5;
        let sine = half.sin();
        let axis = [
            self.rotation_axis[0] * sine,
            self.rotation_axis[1] * sine,
            self.rotation_axis[2] * sine,
            half.cos(),
        ];
        let transform = translation(self.center_point.map(|v| -v))
            * scale(self.scale)
            * quaternion(axis)
            * quaternion(self.orientation)
            * translation(self.center_point)
            * self.transform
            * translation(position);
        (size, transform)
    }
}

pub(super) fn translation(v: [f32; 3]) -> Matrix4 {
    let mut matrix = Matrix4::IDENTITY;
    matrix.0[3][..3].copy_from_slice(&v);
    matrix
}
pub(super) fn scale(v: [f32; 3]) -> Matrix4 {
    let mut matrix = Matrix4::IDENTITY;
    for i in 0..3 {
        matrix.0[i][i] = v[i];
    }
    matrix
}
pub(super) fn rotation_z(angle: f32) -> Matrix4 {
    let c = angle.cos();
    let s = angle.sin();
    Matrix4([
        [c, s, 0.0, 0.0],
        [-s, c, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ])
}
fn quaternion([x, y, z, w]: [f32; 4]) -> Matrix4 {
    let xx = x * x;
    let yy = y * y;
    let zz = z * z;
    let xy = x * y;
    let wz = z * w;
    let xz = z * x;
    let wy = y * w;
    let yz = y * z;
    let wx = x * w;
    Matrix4([
        [1.0 - 2.0 * (yy + zz), 2.0 * (xy + wz), 2.0 * (xz - wy), 0.0],
        [2.0 * (xy - wz), 1.0 - 2.0 * (zz + xx), 2.0 * (yz + wx), 0.0],
        [2.0 * (xz + wy), 2.0 * (yz - wx), 1.0 - 2.0 * (yy + xx), 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ])
}

/// Original row-vector 3-by-2 transform helpers keep shape/brush multiplication
/// in the six-coefficient source arithmetic, not an expanded 4x4 shortcut.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CompositionTransform {
    /// Original center.
    pub center: [f32; 2],
    /// Original independent scale axes.
    pub scale: [f32; 2],
    /// Rotation in radians.
    pub rotation: f32,
    /// Original source matrix.
    pub matrix: Affine,
    /// Final source offset.
    pub offset: [f32; 2],
}
impl Default for CompositionTransform {
    fn default() -> Self {
        Self {
            center: [0.0; 2],
            scale: [1.0; 2],
            rotation: 0.0,
            matrix: Affine::IDENTITY,
            offset: [0.0; 2],
        }
    }
}
impl CompositionTransform {
    pub(super) fn validate(self) -> Result<(), Error> {
        if self
            .center
            .iter()
            .chain(self.scale.iter())
            .chain(self.matrix.0.iter())
            .chain(self.offset.iter())
            .copied()
            .all(f32::is_finite)
            && self.rotation.is_finite()
        {
            Ok(())
        } else {
            Err(Error::InvalidInput)
        }
    }
    /// Original local shape transform composed with the parent's six coefficients.
    pub fn resolve(self, parent: Affine) -> Affine {
        multiply(self.local_transform(), parent)
    }
    /// Original six-step local expression, without an extra identity multiply.
    pub fn local_transform(self) -> Affine {
        let c = self.rotation.cos();
        let s = self.rotation.sin();
        let mut result = multiply(
            Affine([1.0, 0.0, 0.0, 1.0, -self.center[0], -self.center[1]]),
            Affine([self.scale[0], 0.0, 0.0, self.scale[1], 0.0, 0.0]),
        );
        result = multiply(result, Affine([c, s, -s, c, 0.0, 0.0]));
        result = multiply(
            result,
            Affine([1.0, 0.0, 0.0, 1.0, self.center[0], self.center[1]]),
        );
        result = multiply(result, self.matrix);
        result = multiply(
            result,
            Affine([1.0, 0.0, 0.0, 1.0, self.offset[0], self.offset[1]]),
        );
        result
    }
}
pub(super) fn multiply(Affine(a): Affine, Affine(b): Affine) -> Affine {
    Affine([
        a[0] * b[0] + a[1] * b[2],
        a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2],
        a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4],
        a[4] * b[1] + a[5] * b[3] + b[5],
    ])
}
pub(super) fn inverse(Affine(a): Affine) -> Option<Affine> {
    let determinant = a[0] * a[3] - a[2] * a[1];
    if determinant.abs() < f32::from_bits(1) {
        return None;
    }
    let inverse = 1.0 / determinant;
    Some(Affine([
        a[3] * inverse,
        -a[1] * inverse,
        -a[2] * inverse,
        a[0] * inverse,
        (a[2] * a[5] - a[4] * a[3]) * inverse,
        (a[4] * a[1] - a[0] * a[5]) * inverse,
    ]))
}
