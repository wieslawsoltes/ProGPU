//! Actual declaration owner, immutable snapshots and typed original factories.
use super::{RuntimeEffectChildren, RuntimeEffectError as E, RuntimeEffectUniforms};
use crate::{
    image::ColorFilter,
    image_filter::{FilterBlender, FilterShaderSource},
    vector::Matrix4,
};
use std::sync::Arc;

/// Original selected instance factory; source bodies are not compiled.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RuntimeEffectKind {
    /// Coordinate-to-color shader declaration.
    Shader,
    /// Color-to-color filter declaration.
    ColorFilter,
    /// Source/destination blender declaration.
    Blender,
}
/// Reflected child declaration. The original child setter accepts any of these
/// actual owners independently of the declaration's kind.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RuntimeChildKind {
    /// Shader child.
    Shader,
    /// Color-filter child.
    ColorFilter,
    /// Blender child.
    Blender,
}
/// All original reflected numeric spellings. Half values use 32-bit float
/// storage; three-component values and matrices receive no additional padding.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RuntimeUniformType {
    /// One 32-bit float.
    Float,
    /// One 32-bit half-language value.
    Half,
    /// One signed 32-bit integer.
    Int,
    /// Two floats.
    Float2,
    /// Two half-language values.
    Half2,
    /// Two signed integers.
    Int2,
    /// Three floats.
    Float3,
    /// Three half-language values.
    Half3,
    /// Three signed integers.
    Int3,
    /// Four floats.
    Float4,
    /// Four half-language values.
    Half4,
    /// Four signed integers.
    Int4,
    /// Four float matrix entries.
    Float2x2,
    /// Four half-language matrix entries.
    Half2x2,
    /// Nine float matrix entries.
    Float3x3,
    /// Nine half-language matrix entries.
    Half3x3,
    /// Sixteen float matrix entries.
    Float4x4,
    /// Sixteen half-language matrix entries.
    Half4x4,
}
impl RuntimeUniformType {
    /// Original byte count for a single array element.
    pub const fn element_size(self) -> usize {
        use RuntimeUniformType::*;
        match self {
            Float | Half | Int => 4,
            Float2 | Half2 | Int2 => 8,
            Float3 | Half3 | Int3 => 12,
            Float4 | Half4 | Int4 | Float2x2 | Half2x2 => 16,
            Float3x3 | Half3x3 => 36,
            Float4x4 | Half4x4 => 64,
        }
    }
}
/// Original packed uniform declaration in source order, including duplicates.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RuntimeUniformDescriptor {
    /// Original case-sensitive name.
    pub name: Arc<str>,
    /// Original unaligned packed byte offset.
    pub offset: usize,
    /// Complete byte count including declared array elements.
    pub size: usize,
    /// Exact reflected original type spelling.
    pub ty: RuntimeUniformType,
    /// Positive original array count; ordinary declarations use one.
    pub array_count: usize,
}
/// Original child declaration in source order, including duplicates.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RuntimeChildDescriptor {
    /// Original case-sensitive name.
    pub name: Arc<str>,
    /// Reflected kind, independent of actual assigned owner kind.
    pub kind: RuntimeChildKind,
}
/// Complete immutable original declaration result. This is reflection data,
/// source ownership and byte layout, not an executable shader IR.
#[derive(Debug)]
pub struct RuntimeEffectProgram {
    pub(super) source: Arc<str>,
    pub(super) kind: RuntimeEffectKind,
    pub(super) uniforms: Arc<[RuntimeUniformDescriptor]>,
    pub(super) children: Arc<[RuntimeChildDescriptor]>,
    pub(super) uniform_size: usize,
}
impl RuntimeEffectProgram {
    /// Actual unchanged source text, including comments and function bodies.
    pub fn source(&self) -> &str {
        &self.source
    }
    /// Selected original creation kind.
    pub fn kind(&self) -> RuntimeEffectKind {
        self.kind
    }
    /// Ordered original declarations, without name deduplication.
    pub fn uniforms(&self) -> &[RuntimeUniformDescriptor] {
        &self.uniforms
    }
    /// Ordered original child declarations, without name deduplication.
    pub fn children(&self) -> &[RuntimeChildDescriptor] {
        &self.children
    }
    /// Complete tightly packed original uniform buffer size.
    pub fn uniform_size(&self) -> usize {
        self.uniform_size
    }
    /// The original producer does not compile or execute SkSL function bodies.
    pub const fn supports_language_execution(&self) -> bool {
        false
    }
}

/// Shared actual effect identity. Clones retain the owner; separately parsing
/// identical source creates a distinct binding domain.
#[derive(Clone, Debug)]
pub struct RuntimeEffect(pub(super) Arc<RuntimeEffectProgram>);
impl RuntimeEffect {
    /// Original declaration parser, preserving selected kind and source text.
    pub fn create(source: impl Into<Arc<str>>, kind: RuntimeEffectKind) -> Result<Self, E> {
        Ok(Self(Arc::new(super::parser::parse(source.into(), kind)?)))
    }
    /// Original shader declaration factory.
    pub fn create_shader(source: impl Into<Arc<str>>) -> Result<Self, E> {
        Self::create(source, RuntimeEffectKind::Shader)
    }
    /// Original color-filter declaration factory.
    pub fn create_color_filter(source: impl Into<Arc<str>>) -> Result<Self, E> {
        Self::create(source, RuntimeEffectKind::ColorFilter)
    }
    /// Original blender declaration factory.
    pub fn create_blender(source: impl Into<Arc<str>>) -> Result<Self, E> {
        Self::create(source, RuntimeEffectKind::Blender)
    }
    /// Create the original shader builder with zero uniforms and null children.
    pub fn build_shader(source: impl Into<Arc<str>>) -> Result<RuntimeShaderBuilder, E> {
        RuntimeShaderBuilder::new(Self::create_shader(source)?)
    }
    /// Create the original color-filter builder.
    pub fn build_color_filter(source: impl Into<Arc<str>>) -> Result<RuntimeColorFilterBuilder, E> {
        RuntimeColorFilterBuilder::new(Self::create_color_filter(source)?)
    }
    /// Create the original blender builder.
    pub fn build_blender(source: impl Into<Arc<str>>) -> Result<RuntimeBlenderBuilder, E> {
        RuntimeBlenderBuilder::new(Self::create_blender(source)?)
    }
    /// Actual immutable reflection owner.
    pub fn program(&self) -> &RuntimeEffectProgram {
        &self.0
    }
    /// Ordered original uniform names; duplicates remain visible.
    pub fn uniforms(&self) -> impl ExactSizeIterator<Item = &str> {
        self.0.uniforms.iter().map(|v| v.name.as_ref())
    }
    /// Ordered original child names; duplicates remain visible.
    pub fn children(&self) -> impl ExactSizeIterator<Item = &str> {
        self.0.children.iter().map(|v| v.name.as_ref())
    }
    /// Original tightly packed byte count.
    pub fn uniform_size(&self) -> usize {
        self.0.uniform_size
    }
    /// Actual source identity, independent of source-text equality.
    pub fn same_owner(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    /// Snapshot matching mutable bindings atomically and preserve the exact
    /// original nine-component shader local matrix. Non-shader instances use
    /// identity, matching their factories rather than accepting ignored frames.
    pub fn instantiate(
        &self,
        uniforms: &RuntimeEffectUniforms,
        children: &RuntimeEffectChildren,
        local_matrix: RuntimeLocalMatrix,
    ) -> Result<RuntimeEffectInstance, E> {
        uniforms.ensure_effect(self)?;
        children.ensure_effect(self)?;
        let local_matrix = if self.0.kind != RuntimeEffectKind::Shader || local_matrix.is_identity()
        {
            RuntimeLocalMatrix::IDENTITY
        } else {
            local_matrix
        };
        Ok(RuntimeEffectInstance(Arc::new(Instance {
            effect: self.clone(),
            uniforms: uniforms.snapshot(),
            children: children.snapshot(),
            local_matrix,
        })))
    }
    /// Original zero-uniform, null-child shader factory.
    pub fn to_shader(&self) -> Result<FilterShaderSource, E> {
        let builder = RuntimeEffectBuilder::new(self.clone())?;
        self.to_shader_with(
            &builder.uniforms,
            &builder.children,
            RuntimeLocalMatrix::IDENTITY,
        )
    }
    /// Original shader factory with supplied uniform bytes and null children.
    pub fn to_shader_with_uniforms(
        &self,
        uniforms: &RuntimeEffectUniforms,
    ) -> Result<FilterShaderSource, E> {
        self.to_shader_with(
            uniforms,
            &RuntimeEffectChildren::new(self.clone())?,
            RuntimeLocalMatrix::IDENTITY,
        )
    }
    /// Retain actual shader source/uniform/child owners without claiming execution.
    pub fn to_shader_with(
        &self,
        uniforms: &RuntimeEffectUniforms,
        children: &RuntimeEffectChildren,
        local_matrix: RuntimeLocalMatrix,
    ) -> Result<FilterShaderSource, E> {
        self.ensure_kind(RuntimeEffectKind::Shader)?;
        Ok(FilterShaderSource::Runtime(self.instantiate(
            uniforms,
            children,
            local_matrix,
        )?))
    }
    /// Original zero-uniform, null-child color-filter factory.
    pub fn to_color_filter(&self) -> Result<ColorFilter, E> {
        let builder = RuntimeEffectBuilder::new(self.clone())?;
        self.to_color_filter_with(&builder.uniforms, &builder.children)
    }
    /// Original color-filter factory with null children.
    pub fn to_color_filter_with_uniforms(
        &self,
        uniforms: &RuntimeEffectUniforms,
    ) -> Result<ColorFilter, E> {
        self.to_color_filter_with(uniforms, &RuntimeEffectChildren::new(self.clone())?)
    }
    /// Retain the original runtime color-filter owner and its distinct image-pass
    /// identity / single-color Clear behavior.
    pub fn to_color_filter_with(
        &self,
        uniforms: &RuntimeEffectUniforms,
        children: &RuntimeEffectChildren,
    ) -> Result<ColorFilter, E> {
        self.ensure_kind(RuntimeEffectKind::ColorFilter)?;
        Ok(ColorFilter::from_runtime_instance(self.instantiate(
            uniforms,
            children,
            RuntimeLocalMatrix::IDENTITY,
        )?))
    }
    /// Original zero-uniform, null-child blender factory.
    pub fn to_blender(&self) -> Result<FilterBlender, E> {
        let builder = RuntimeEffectBuilder::new(self.clone())?;
        self.to_blender_with(&builder.uniforms, &builder.children)
    }
    /// Original blender factory with null children.
    pub fn to_blender_with_uniforms(
        &self,
        uniforms: &RuntimeEffectUniforms,
    ) -> Result<FilterBlender, E> {
        self.to_blender_with(uniforms, &RuntimeEffectChildren::new(self.clone())?)
    }
    /// Retain the actual runtime blender; execution does not select a blend mode.
    pub fn to_blender_with(
        &self,
        uniforms: &RuntimeEffectUniforms,
        children: &RuntimeEffectChildren,
    ) -> Result<FilterBlender, E> {
        self.ensure_kind(RuntimeEffectKind::Blender)?;
        Ok(FilterBlender::Runtime(self.instantiate(
            uniforms,
            children,
            RuntimeLocalMatrix::IDENTITY,
        )?))
    }
    fn ensure_kind(&self, expected: RuntimeEffectKind) -> Result<(), E> {
        if self.0.kind == expected {
            Ok(())
        } else {
            Err(E::Kind {
                actual: self.0.kind,
                expected,
            })
        }
    }
}

/// Original SKMatrix.Values order, retaining perspective and exact coefficient
/// bits. Reflection storage does not impose rendering-frame admission.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RuntimeLocalMatrix(pub [f32; 9]);
impl RuntimeLocalMatrix {
    /// Original scale/skew/translation/perspective identity.
    pub const IDENTITY: Self = Self([1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0]);
    /// Original numeric identity comparison, including equality of signed zero.
    pub fn is_identity(self) -> bool {
        self == Self::IDENTITY
    }
    /// Exact original row-vector 4-by-4 embedding, without perspective loss.
    pub fn to_matrix4(self) -> Matrix4 {
        let v = self.0;
        Matrix4([
            [v[0], v[3], 0.0, v[6]],
            [v[1], v[4], 0.0, v[7]],
            [0.0, 0.0, 1.0, 0.0],
            [v[2], v[5], 0.0, v[8]],
        ])
    }
}
impl Default for RuntimeLocalMatrix {
    fn default() -> Self {
        Self::IDENTITY
    }
}

/// Original actual child owner; null is represented by Option at the binding
/// slot, while the declared child kind remains independent metadata.
#[derive(Clone, Debug)]
pub enum RuntimeEffectChild {
    /// Actual immutable shader source generation.
    Shader(Arc<FilterShaderSource>),
    /// Actual immutable color-filter generation.
    ColorFilter(ColorFilter),
    /// Actual immutable fixed/arithmetic/runtime blender.
    Blender(FilterBlender),
}
impl RuntimeEffectChild {
    /// Original typed shader view; other actual kinds return None.
    pub fn shader(&self) -> Option<&FilterShaderSource> {
        match self {
            Self::Shader(value) => Some(value),
            _ => None,
        }
    }
    /// Original typed color-filter view; other actual kinds return None.
    pub fn color_filter(&self) -> Option<&ColorFilter> {
        match self {
            Self::ColorFilter(value) => Some(value),
            _ => None,
        }
    }
    /// Original typed blender view; other actual kinds return None.
    pub fn blender(&self) -> Option<&FilterBlender> {
        match self {
            Self::Blender(value) => Some(value),
            _ => None,
        }
    }
    /// Actual assigned kind, which the original setter does not coerce.
    pub fn kind(&self) -> RuntimeChildKind {
        match self {
            Self::Shader(_) => RuntimeChildKind::Shader,
            Self::ColorFilter(_) => RuntimeChildKind::ColorFilter,
            Self::Blender(_) => RuntimeChildKind::Blender,
        }
    }
}
impl From<FilterShaderSource> for RuntimeEffectChild {
    fn from(value: FilterShaderSource) -> Self {
        Self::Shader(Arc::new(value))
    }
}
impl From<Arc<FilterShaderSource>> for RuntimeEffectChild {
    fn from(value: Arc<FilterShaderSource>) -> Self {
        Self::Shader(value)
    }
}
impl From<ColorFilter> for RuntimeEffectChild {
    fn from(value: ColorFilter) -> Self {
        Self::ColorFilter(value)
    }
}
impl From<FilterBlender> for RuntimeEffectChild {
    fn from(value: FilterBlender) -> Self {
        Self::Blender(value)
    }
}

#[derive(Debug)]
struct Instance {
    effect: RuntimeEffect,
    uniforms: Arc<[u8]>,
    children: Arc<[Option<RuntimeEffectChild>]>,
    local_matrix: RuntimeLocalMatrix,
}
/// Immutable original instance, retaining source, exact uniform bytes, ordered
/// null/typed children and local transform beyond all builder mutations.
#[derive(Clone, Debug)]
pub struct RuntimeEffectInstance(Arc<Instance>);
impl RuntimeEffectInstance {
    /// Actual immutable declaration owner.
    pub fn effect(&self) -> &RuntimeEffect {
        &self.0.effect
    }
    /// Actual original program and reflected declaration order.
    pub fn program(&self) -> &RuntimeEffectProgram {
        self.0.effect.program()
    }
    /// Exact frozen native-endian uniform bytes.
    pub fn uniform_data(&self) -> &[u8] {
        &self.0.uniforms
    }
    /// Complete ordered original child slots, including null assignments.
    pub fn children(&self) -> &[Option<RuntimeEffectChild>] {
        &self.0.children
    }
    /// Exact original shader-local matrix; other kinds have identity.
    pub fn local_matrix(&self) -> RuntimeLocalMatrix {
        self.0.local_matrix
    }
    /// Actual immutable instance owner, independent of source or binding equality.
    pub fn same_owner(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    pub(crate) fn identity(&self) -> usize {
        Arc::as_ptr(&self.0) as usize
    }
}

/// Original mutable builder; Rust Drop retires only its own references. Existing
/// instances retain their independent immutable generations.
#[derive(Clone, Debug)]
pub struct RuntimeEffectBuilder {
    /// Actual immutable effect owner.
    pub effect: RuntimeEffect,
    /// Original zero-initialized, copy-on-write byte storage.
    pub uniforms: RuntimeEffectUniforms,
    /// Original ordered nullable child bindings.
    pub children: RuntimeEffectChildren,
}
impl RuntimeEffectBuilder {
    /// Allocate original zero uniforms and null children atomically.
    pub fn new(effect: RuntimeEffect) -> Result<Self, E> {
        Ok(Self {
            uniforms: RuntimeEffectUniforms::new(effect.clone())?,
            children: RuntimeEffectChildren::new(effect.clone())?,
            effect,
        })
    }
}
/// Typed shader builder retaining the original build-time kind check.
#[derive(Clone, Debug)]
pub struct RuntimeShaderBuilder(pub RuntimeEffectBuilder);
impl std::ops::Deref for RuntimeShaderBuilder {
    type Target = RuntimeEffectBuilder;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}
impl std::ops::DerefMut for RuntimeShaderBuilder {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}
impl RuntimeShaderBuilder {
    /// Retain the selected effect; kind admission occurs when building.
    pub fn new(effect: RuntimeEffect) -> Result<Self, E> {
        Ok(Self(RuntimeEffectBuilder::new(effect)?))
    }
    /// Snapshot the original identity-local shader instance.
    pub fn build(&self) -> Result<FilterShaderSource, E> {
        self.build_with_matrix(RuntimeLocalMatrix::IDENTITY)
    }
    /// Snapshot the original complete shader-local transform.
    pub fn build_with_matrix(&self, matrix: RuntimeLocalMatrix) -> Result<FilterShaderSource, E> {
        self.0
            .effect
            .to_shader_with(&self.0.uniforms, &self.0.children, matrix)
    }
}
/// Typed color-filter builder retaining the original build-time kind check.
#[derive(Clone, Debug)]
pub struct RuntimeColorFilterBuilder(pub RuntimeEffectBuilder);
impl std::ops::Deref for RuntimeColorFilterBuilder {
    type Target = RuntimeEffectBuilder;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}
impl std::ops::DerefMut for RuntimeColorFilterBuilder {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}
impl RuntimeColorFilterBuilder {
    /// Retain the selected effect; kind admission occurs when building.
    pub fn new(effect: RuntimeEffect) -> Result<Self, E> {
        Ok(Self(RuntimeEffectBuilder::new(effect)?))
    }
    /// Snapshot the original color-filter instance.
    pub fn build(&self) -> Result<ColorFilter, E> {
        self.0
            .effect
            .to_color_filter_with(&self.0.uniforms, &self.0.children)
    }
}
/// Typed blender builder retaining the original build-time kind check.
#[derive(Clone, Debug)]
pub struct RuntimeBlenderBuilder(pub RuntimeEffectBuilder);
impl std::ops::Deref for RuntimeBlenderBuilder {
    type Target = RuntimeEffectBuilder;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}
impl std::ops::DerefMut for RuntimeBlenderBuilder {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}
impl RuntimeBlenderBuilder {
    /// Retain the selected effect; kind admission occurs when building.
    pub fn new(effect: RuntimeEffect) -> Result<Self, E> {
        Ok(Self(RuntimeEffectBuilder::new(effect)?))
    }
    /// Snapshot the original runtime-blender instance.
    pub fn build(&self) -> Result<FilterBlender, E> {
        self.0
            .effect
            .to_blender_with(&self.0.uniforms, &self.0.children)
    }
}
