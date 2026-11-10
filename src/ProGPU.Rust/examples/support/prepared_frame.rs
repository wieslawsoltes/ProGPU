//! Keep the complete visual snapshot/capture owner alive alongside ordinary scenes.
use progpu::renderer::{PreparedScene, PreparedVisual, VisualMetrics};
pub enum PreparedFrame {
    Scene(Box<PreparedScene>),
    Visual(Box<PreparedVisual>),
}
impl From<PreparedScene> for PreparedFrame {
    fn from(value: PreparedScene) -> Self {
        Self::Scene(Box::new(value))
    }
}
impl From<PreparedVisual> for PreparedFrame {
    fn from(value: PreparedVisual) -> Self {
        Self::Visual(Box::new(value))
    }
}
impl std::ops::Deref for PreparedFrame {
    type Target = PreparedScene;
    fn deref(&self) -> &Self::Target {
        match self {
            Self::Scene(scene) => scene,
            Self::Visual(visual) => visual.scene(),
        }
    }
}
impl PreparedFrame {
    pub fn visual_metrics(&self) -> Option<VisualMetrics> {
        match self {
            Self::Visual(visual) => Some(visual.metrics()),
            Self::Scene(_) => None,
        }
    }
}
