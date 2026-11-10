//! Original immutable full-image filter graphs and exact source-bound planning.
//! Original parameters, owners and crop policies remain distinct from execution.
use crate::Error;
use std::sync::Arc;
mod color_space;
mod graph;
mod parameters;
mod plan;
mod transform;
pub use color_space::{ImageFilterColorSpace, TransferFunction, TransferFunctionKind};
pub use graph::{
    FilterArithmetic, FilterBlender, FilterColorChannel, FilterConvolution, FilterImageData,
    FilterImageSource, FilterLight, FilterLightSource, FilterMagnifier, FilterPictureSource,
    FilterSampling, FilterShaderSource, FilterShadow, FilterTileMode, ImageFilter, ImageFilterKind,
    PictureColorEntry,
};
pub use parameters::{
    DisplacementMapParams, ImageLightingInput, ImageLightingParams, MagnifierParams,
    MatrixConvolutionParams, MorphologyParams,
};
pub use plan::{
    FilterPlanRequest, ImageFilterOperation, ImageFilterPlan, ImageFilterResources, ImageFilterStep,
};
pub use transform::{FilterRect, FilterTransform, magnifier_zoom};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{
    ImageFilterComputeEngine, ImageFilterComputeMetrics, ImageFilterComputeRequest,
    PreparedImageFilterCompute,
};
