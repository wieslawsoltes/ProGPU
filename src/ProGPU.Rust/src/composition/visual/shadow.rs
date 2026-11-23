//! Independent source alpha for the original retained drop-shadow effect.
use super::*;

/// Original WinUI shadow source selection. Default's content inheritance is an
/// explicit owner contract, not inferred from source pixels or receiving bounds.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CompositionDropShadowSourcePolicy {
    #[default]
    Default,
    InheritFromVisualContent,
}

/// Immutable original opacity-mask producer. The effect owner's color capture
/// stays independent and is composited above the shadow when DrawSource is true.
#[derive(Clone, Debug)]
pub enum ShadowOpacitySource {
    /// A complete frozen original visual subtree. Its root transform and outer
    /// visual state are excluded during this separate source capture.
    Visual(Arc<VisualSnapshot>),
    /// Actual retained source commands, with the effect frame's source rebasing.
    Picture(Arc<Scene>),
    /// Original PrepareSceneBrush source and original receiving rectangle.
    Brush {
        brush: Arc<CompositionBrushSnapshot>,
        bounds: [f32; 4],
    },
}
impl PartialEq for ShadowOpacitySource {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (Self::Visual(a), Self::Visual(b)) => Arc::ptr_eq(a, b),
            (Self::Picture(a), Self::Picture(b)) => Arc::ptr_eq(a, b),
            (
                Self::Brush {
                    brush: a,
                    bounds: x,
                },
                Self::Brush {
                    brush: b,
                    bounds: y,
                },
            ) => Arc::ptr_eq(a, b) && x == y,
            _ => false,
        }
    }
}
impl ShadowOpacitySource {
    /// Freeze the original WinUI mask selection. None means inherited visual
    /// content; a null noninheriting mask produces the original opaque rectangle.
    /// A CompositionMaskBrush has no PrepareSceneBrush result on this path and
    /// therefore produces an empty mask while retaining its actual dependencies.
    pub fn from_composition_brush(
        mask: Option<&CompositionBrush>,
        policy: CompositionDropShadowSourcePolicy,
        default_inherits_content: bool,
        bounds: [f32; 4],
    ) -> Result<Option<Arc<Self>>, Error> {
        if bounds.iter().any(|v| !v.is_finite()) || bounds[2] < 0.0 || bounds[3] < 0.0 {
            return Err(Error::InvalidInput);
        }
        if mask.is_none()
            && (policy == CompositionDropShadowSourcePolicy::InheritFromVisualContent
                || default_inherits_content)
        {
            return Ok(None);
        }
        let brush = if let Some(mask) = mask {
            mask.resolve(bounds)?
        } else {
            CompositionBrushValue::Color([255; 4]).resolve(bounds)?
        };
        Ok(Some(Arc::new(Self::Brush {
            brush: Arc::new(brush),
            bounds,
        })))
    }
    pub(crate) fn recording(&self) -> Result<Option<Arc<Scene>>, Error> {
        match self {
            Self::Visual(_) => Ok(None),
            Self::Picture(scene) => Ok(Some(scene.clone())),
            Self::Brush { brush, bounds } => {
                if bounds.iter().any(|v| !v.is_finite()) || bounds[2] < 0.0 || bounds[3] < 0.0 {
                    return Err(Error::InvalidInput);
                }
                let mut scene = Scene::default();
                if bounds[2] > 0.0
                    && bounds[3] > 0.0
                    && !matches!(brush.as_ref(), CompositionBrushSnapshot::Mask { .. })
                {
                    brush.record(&mut scene, *bounds)?;
                }
                Ok(Some(Arc::new(scene)))
            }
        }
    }
    pub(crate) fn cacheable(&self) -> bool {
        match self {
            Self::Picture(scene) => scene.visual_page_cacheable(),
            Self::Brush { .. } => true,
            Self::Visual(visual) => {
                let mut cacheable = true;
                visual.visit_pages(&mut |v| {
                    cacheable &= v.page().can_cache_incremental_page()
                        && v.page().scene().visual_page_cacheable();
                });
                cacheable
            }
        }
    }
}
