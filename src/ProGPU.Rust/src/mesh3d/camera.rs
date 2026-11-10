//! Owned original Viewport3D camera state and controller arithmetic.
//! Property changes are returned explicitly; native UI callbacks and pointer
//! capture stay with the host. Raw camera values are not silently sanitized.
use crate::{Error, vector::Matrix4};
use wide::f32x4 as V;

/// Common original ProjectionCamera properties, frozen by value when copied.
/// Setters return the source Changed decision, including NaN != NaN. Callers
/// can publish that decision to their own UI without retaining a callback in a
/// render packet. `set_view` publishes one decision for both changed vectors.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ProjectionCamera {
    transform: Matrix4,
    position: [f32; 3],
    look_direction: [f32; 3],
    up_direction: [f32; 3],
    near_plane_distance: f32,
    far_plane_distance: f32,
}
impl Default for ProjectionCamera {
    fn default() -> Self {
        Self {
            transform: Matrix4::IDENTITY,
            position: [0.0, 0.0, -10.0],
            look_direction: [0.0, 0.0, 1.0],
            up_direction: [0.0, 1.0, 0.0],
            near_plane_distance: 0.125,
            far_plane_distance: 1000.0,
        }
    }
}
macro_rules! property {
    ($getter:ident, $setter:ident, $field:ident, $type:ty) => {
        pub fn $getter(&self) -> $type {
            self.$field
        }
        pub fn $setter(&mut self, value: $type) -> bool {
            if self.$field == value {
                return false;
            }
            self.$field = value;
            true
        }
    };
}
impl ProjectionCamera {
    property!(transform, set_transform, transform, Matrix4);
    property!(position, set_position, position, [f32; 3]);
    property!(look_direction, set_look_direction, look_direction, [f32; 3]);
    property!(up_direction, set_up_direction, up_direction, [f32; 3]);
    property!(
        near_plane_distance,
        set_near_plane_distance,
        near_plane_distance,
        f32
    );
    property!(
        far_plane_distance,
        set_far_plane_distance,
        far_plane_distance,
        f32
    );

    pub fn look_at(&self) -> [f32; 3] {
        add(self.position, self.look_direction)
    }
    pub fn set_look_at(&mut self, value: [f32; 3]) -> bool {
        self.set_look_direction(sub(value, self.position))
    }
    /// Preserve the original all-or-nothing view replacement and one change.
    pub fn set_view(&mut self, position: [f32; 3], look_direction: [f32; 3]) -> bool {
        if self.position == position && self.look_direction == look_direction {
            return false;
        }
        self.position = position;
        self.look_direction = look_direction;
        true
    }
    /// Right-handed look-at followed by the source inverse-transform policy.
    /// A singular transform leaves the view unchanged. Nonfinite or degenerate
    /// input remains IEEE arithmetic; final renderer admission is independent.
    pub fn view_matrix(&self) -> Matrix4 {
        // Retain Position + LookDirection and the subsequent subtraction: the
        // source passes the constructed target to CreateLookAt, not a direction.
        let target = self.look_at();
        let backward = normalize(sub(self.position, target));
        let right = normalize(cross(self.up_direction, backward));
        let up = cross(backward, right);
        let view = Matrix4([
            [right[0], up[0], backward[0], 0.0],
            [right[1], up[1], backward[1], 0.0],
            [right[2], up[2], backward[2], 0.0],
            [
                -dot(right, self.position),
                -dot(up, self.position),
                -dot(backward, self.position),
                1.0,
            ],
        ]);
        if self.transform != Matrix4::IDENTITY
            && let Some(inverse) = inverse_camera_transform(self.transform)
        {
            inverse * view
        } else {
            view
        }
    }
}

/// Original degree-valued perspective camera, default field of view 45.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PerspectiveCamera {
    projection: ProjectionCamera,
    field_of_view: f32,
}
impl Default for PerspectiveCamera {
    fn default() -> Self {
        Self {
            projection: Default::default(),
            field_of_view: 45.0,
        }
    }
}
impl PerspectiveCamera {
    pub fn new(projection: ProjectionCamera) -> Self {
        Self {
            projection,
            ..Default::default()
        }
    }
    pub fn projection(&self) -> &ProjectionCamera {
        &self.projection
    }
    pub fn projection_mut(&mut self) -> &mut ProjectionCamera {
        &mut self.projection
    }
    property!(field_of_view, set_field_of_view, field_of_view, f32);

    /// Original `degrees * PI / 180` order and right-handed 0..1-depth
    /// perspective. Preserve the original comparison-based domain checks:
    /// NaN survives those comparisons, aspect is not clamped, and positive
    /// infinite far distance selects the infinite-far projection.
    pub fn projection_matrix(&self, aspect_ratio: f32) -> Result<Matrix4, CameraProjectionError> {
        let radians = self.field_of_view * std::f32::consts::PI / 180.0;
        let near = self.projection.near_plane_distance;
        let far = self.projection.far_plane_distance;
        if radians <= 0.0 || radians >= std::f32::consts::PI {
            return Err(CameraProjectionError::FieldOfView);
        }
        if near <= 0.0 {
            return Err(CameraProjectionError::NearPlane);
        }
        if far <= 0.0 {
            return Err(CameraProjectionError::FarPlane);
        }
        if near >= far {
            return Err(CameraProjectionError::PlaneOrder);
        }
        let height = 1.0 / (radians * 0.5).tan();
        let width = height / aspect_ratio;
        let range = if far == f32::INFINITY {
            -1.0
        } else {
            far / (near - far)
        };
        Ok(Matrix4([
            [width, 0.0, 0.0, 0.0],
            [0.0, height, 0.0, 0.0],
            [0.0, 0.0, range, -1.0],
            [0.0, 0.0, range * near, 0.0],
        ]))
    }
    pub fn view_matrix(&self) -> Matrix4 {
        self.projection.view_matrix()
    }
}

/// Original width-valued orthographic camera, default width two.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct OrthographicCamera {
    projection: ProjectionCamera,
    width: f32,
}
impl Default for OrthographicCamera {
    fn default() -> Self {
        Self {
            projection: Default::default(),
            width: 2.0,
        }
    }
}
impl OrthographicCamera {
    pub fn new(projection: ProjectionCamera) -> Self {
        Self {
            projection,
            ..Default::default()
        }
    }
    pub fn projection(&self) -> &ProjectionCamera {
        &self.projection
    }
    pub fn projection_mut(&mut self) -> &mut ProjectionCamera {
        &mut self.projection
    }
    property!(width, set_width, width, f32);
    /// Retain raw width/aspect/plane division. Unlike perspective, the original
    /// orthographic producer does not reject zero extents or reversed planes.
    pub fn projection_matrix(&self, aspect_ratio: f32) -> Matrix4 {
        let height = self.width / aspect_ratio;
        let near = self.projection.near_plane_distance;
        let far = self.projection.far_plane_distance;
        let depth = 1.0 / (near - far);
        Matrix4([
            [2.0 / self.width, 0.0, 0.0, 0.0],
            [0.0, 2.0 / height, 0.0, 0.0],
            [0.0, 0.0, depth, 0.0],
            [0.0, 0.0, depth * near, 1.0],
        ])
    }
    pub fn view_matrix(&self) -> Matrix4 {
        self.projection.view_matrix()
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CameraProjectionError {
    FieldOfView,
    NearPlane,
    FarPlane,
    PlaneOrder,
}
impl std::fmt::Display for CameraProjectionError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::FieldOfView => "perspective field of view must be between zero and 180 degrees",
            Self::NearPlane => "perspective near distance must be positive",
            Self::FarPlane => "perspective far distance must be positive",
            Self::PlaneOrder => "perspective near distance must be less than far distance",
        })
    }
}
impl std::error::Error for CameraProjectionError {}
impl From<CameraProjectionError> for Error {
    fn from(_: CameraProjectionError) -> Self {
        Self::InvalidInput
    }
}

/// Closed inventory of the original built-in concrete camera producers.
/// Custom host cameras can continue supplying explicit view/projection matrices
/// to the viewport draw; they are not relabeled as one of these source types.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Camera {
    Perspective(PerspectiveCamera),
    Orthographic(OrthographicCamera),
}
impl Default for Camera {
    fn default() -> Self {
        Self::Perspective(Default::default())
    }
}
impl From<PerspectiveCamera> for Camera {
    fn from(value: PerspectiveCamera) -> Self {
        Self::Perspective(value)
    }
}
impl From<OrthographicCamera> for Camera {
    fn from(value: OrthographicCamera) -> Self {
        Self::Orthographic(value)
    }
}
impl Camera {
    pub fn projection(&self) -> &ProjectionCamera {
        match self {
            Self::Perspective(c) => c.projection(),
            Self::Orthographic(c) => c.projection(),
        }
    }
    pub fn projection_mut(&mut self) -> &mut ProjectionCamera {
        match self {
            Self::Perspective(c) => c.projection_mut(),
            Self::Orthographic(c) => c.projection_mut(),
        }
    }
    pub fn view_matrix(&self) -> Matrix4 {
        self.projection().view_matrix()
    }
    pub fn projection_matrix(&self, aspect_ratio: f32) -> Result<Matrix4, CameraProjectionError> {
        match self {
            Self::Perspective(c) => c.projection_matrix(aspect_ratio),
            Self::Orthographic(c) => Ok(c.projection_matrix(aspect_ratio)),
        }
    }
    /// Original viewport uses logical Size.X / Size.Y, independently of the
    /// truncated physical attachment extent and selected display sample count.
    pub fn matrices(
        &self,
        viewport_size: [f32; 2],
    ) -> Result<CameraMatrices, CameraProjectionError> {
        let projection = self.projection_matrix(viewport_size[0] / viewport_size[1])?;
        Ok(CameraMatrices {
            projection,
            view: self.view_matrix(),
        })
    }
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CameraMatrices {
    pub projection: Matrix4,
    pub view: Matrix4,
}

/// Source key meanings after the host resolves Left/A, Right/D, Up/W, Down/S,
/// PageUp/Q and PageDown/E. Focus, enablement and selection gestures are separate.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CameraNavigationKey {
    Left,
    Right,
    Up,
    Down,
    ZoomIn,
    ZoomOut,
}
/// Actual property changes and viewport invalidation requests. Width changes
/// occur outside ApplyCameraState and each invalidate; its atomic SetView is
/// inside the source update guard and the final explicit invalidate occurs once.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CameraControlUpdate {
    pub camera_changes: u32,
    pub viewport_invalidations: u32,
}
/// Original orbit/pan/zoom state. The host calls `camera_changed` on external
/// property changes or camera-owner replacement, then feeds selected gestures.
/// There are no callbacks, allocations, platform input queries or GPU work here.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CameraNavigation {
    theta: f32,
    phi: f32,
    radius: f32,
    target: [f32; 3],
    initialized: bool,
}
impl Default for CameraNavigation {
    fn default() -> Self {
        Self {
            theta: 0.0,
            phi: 0.5,
            radius: 10.0,
            target: [0.0; 3],
            initialized: false,
        }
    }
}
impl CameraNavigation {
    pub fn theta(&self) -> f32 {
        self.theta
    }
    pub fn phi(&self) -> f32 {
        self.phi
    }
    pub fn radius(&self) -> f32 {
        self.radius
    }
    pub fn target(&self) -> [f32; 3] {
        self.target
    }
    pub fn is_initialized(&self) -> bool {
        self.initialized
    }
    pub fn camera_changed(&mut self) {
        self.initialized = false;
    }
    pub fn initialize(&mut self, camera: &ProjectionCamera) {
        let direction = neg(camera.look_direction());
        self.radius = dot(direction, direction).sqrt();
        if self.radius < 0.1 {
            self.radius = 0.1;
        }
        self.theta = direction[0].atan2(direction[2]);
        let horizontal = (direction[0] * direction[0] + direction[2] * direction[2]).sqrt();
        self.phi = clamp_phi(horizontal.atan2(direction[1]));
        self.target = camera.look_at();
        self.initialized = true;
    }
    fn ensure_initialized(&mut self, camera: &Camera) {
        if !self.initialized {
            self.initialize(camera.projection());
        }
    }
    fn apply(&self, camera: &mut Camera) -> CameraControlUpdate {
        let sin_phi = self.phi.sin();
        let cos_phi = self.phi.cos();
        let sin_theta = self.theta.sin();
        let cos_theta = self.theta.cos();
        let offset = [
            self.radius * sin_phi * sin_theta,
            self.radius * cos_phi,
            self.radius * sin_phi * cos_theta,
        ];
        let changed = camera
            .projection_mut()
            .set_view(add(self.target, offset), neg(offset));
        CameraControlUpdate {
            camera_changes: u32::from(changed),
            viewport_invalidations: 1,
        }
    }
    /// Delta is the original pointer position minus its previous selected point.
    pub fn orbit(&mut self, camera: &mut Camera, delta: [f32; 2]) -> CameraControlUpdate {
        self.ensure_initialized(camera);
        self.theta -= delta[0] * 0.01;
        self.phi -= delta[1] * 0.01;
        self.phi = clamp_phi(self.phi);
        self.apply(camera)
    }
    pub fn pan(&mut self, camera: &mut Camera, delta: [f32; 2]) -> CameraControlUpdate {
        self.ensure_initialized(camera);
        let (right, up) = axes(camera.projection());
        let speed = self.radius * 0.0015;
        self.target = sub(self.target, scale(right, delta[0] * speed));
        self.target = add(self.target, scale(up, delta[1] * speed));
        self.apply(camera)
    }
    /// Preserve sign-only wheel policy: zero and negative deltas both zoom out.
    pub fn wheel(&mut self, camera: &mut Camera, delta: f32) -> CameraControlUpdate {
        self.ensure_initialized(camera);
        self.zoom(camera, if delta > 0.0 { 0.9 } else { 1.1 })
    }
    pub fn key(
        &mut self,
        camera: &mut Camera,
        key: CameraNavigationKey,
        shift: bool,
    ) -> CameraControlUpdate {
        self.ensure_initialized(camera);
        // The original computes these axes for every recognized key, including zoom.
        let (right, up) = axes(camera.projection());
        let speed = self.radius * 0.03;
        let pan = shift || matches!(camera, Camera::Orthographic(_));
        match key {
            CameraNavigationKey::Left if pan => self.target = sub(self.target, scale(right, speed)),
            CameraNavigationKey::Right if pan => {
                self.target = add(self.target, scale(right, speed))
            }
            CameraNavigationKey::Up if pan => self.target = add(self.target, scale(up, speed)),
            CameraNavigationKey::Down if pan => self.target = sub(self.target, scale(up, speed)),
            CameraNavigationKey::Left => self.theta -= 0.05,
            CameraNavigationKey::Right => self.theta += 0.05,
            CameraNavigationKey::Up => self.phi = clamp_phi(self.phi - 0.05),
            CameraNavigationKey::Down => self.phi = clamp_phi(self.phi + 0.05),
            CameraNavigationKey::ZoomIn => return self.zoom(camera, 0.9),
            CameraNavigationKey::ZoomOut => return self.zoom(camera, 1.1),
        }
        self.apply(camera)
    }
    fn zoom(&mut self, camera: &mut Camera, factor: f32) -> CameraControlUpdate {
        let mut changes = 0;
        if let Camera::Orthographic(orthographic) = camera {
            changes += u32::from(orthographic.set_width(orthographic.width() * factor));
            changes += u32::from(orthographic.set_width(orthographic.width().clamp(0.1, 1000.0)));
            if changes != 0 {
                self.initialized = false;
            }
        } else {
            self.radius *= factor;
            self.radius = self.radius.clamp(1.5, 100.0);
        }
        let mut update = self.apply(camera);
        update.camera_changes += changes;
        update.viewport_invalidations += changes;
        update
    }
}

fn clamp_phi(value: f32) -> f32 {
    value.clamp(0.01, std::f32::consts::PI - 0.01)
}
fn vector(value: [f32; 3]) -> V {
    V::from([value[0], value[1], value[2], 0.0])
}
fn components(value: V) -> [f32; 3] {
    let [x, y, z, _] = value.to_array();
    [x, y, z]
}
fn add(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    components(vector(a) + vector(b))
}
fn sub(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    components(vector(a) - vector(b))
}
fn neg(value: [f32; 3]) -> [f32; 3] {
    components(-vector(value))
}
fn scale(value: [f32; 3], factor: f32) -> [f32; 3] {
    components(vector(value) * V::splat(factor))
}
fn dot(a: [f32; 3], b: [f32; 3]) -> f32 {
    let p = (vector(a) * vector(b)).to_array();
    p[0] + p[1] + p[2]
}
fn normalize(value: [f32; 3]) -> [f32; 3] {
    components(vector(value) / V::splat(dot(value, value).sqrt()))
}
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    components(
        V::from([a[1], a[2], a[0], 0.0]) * V::from([b[2], b[0], b[1], 0.0])
            - V::from([a[2], a[0], a[1], 0.0]) * V::from([b[1], b[2], b[0], 0.0]),
    )
}
fn axes(camera: &ProjectionCamera) -> ([f32; 3], [f32; 3]) {
    let forward = normalize(camera.look_direction());
    let right = normalize(cross(forward, camera.up_direction()));
    (right, normalize(cross(right, forward)))
}

// Source Matrix4x4.Invert reports failure for a singular determinant. Unlike the
// image-filter admission helper, this camera operation does not add finite-input
// or finite-result rejection: IEEE NaN/overflow remains in the resulting view.
// Independent cofactor lanes are SIMD; determinant reductions stay ordered.
fn inverse_camera_transform(matrix: Matrix4) -> Option<Matrix4> {
    let m = matrix.0;
    let mut cofactors = [[0.0; 4]; 4];
    for (row, output) in cofactors.iter_mut().enumerate() {
        let rows: [usize; 3] = std::array::from_fn(|i| if i < row { i } else { i + 1 });
        let minors: [[f32; 9]; 4] = std::array::from_fn(|column| {
            let mut values = [0.0; 9];
            let mut index = 0;
            for r in rows {
                for c in 0..4 {
                    if c != column {
                        values[index] = m[r][c];
                        index += 1;
                    }
                }
            }
            values
        });
        let lane = |i| V::from(minors.map(|values| values[i]));
        let values = lane(0) * (lane(4) * lane(8) - lane(5) * lane(7))
            - lane(1) * (lane(3) * lane(8) - lane(5) * lane(6))
            + lane(2) * (lane(3) * lane(7) - lane(4) * lane(6));
        *output = (values
            * V::from(std::array::from_fn(|c| {
                if (row + c) % 2 == 0 { 1.0 } else { -1.0 }
            })))
        .to_array();
    }
    let terms = (V::from(m[0]) * V::from(cofactors[0])).to_array();
    let determinant = terms[0] + terms[1] + terms[2] + terms[3];
    if determinant.abs() < f32::from_bits(1) {
        return None;
    }
    let reciprocal = V::splat(1.0 / determinant);
    Some(Matrix4(std::array::from_fn(|r| {
        (V::from(std::array::from_fn(|c| cofactors[c][r])) * reciprocal).to_array()
    })))
}
