//! Original child-first source rebinding without flattening intermediate clamps.
use super::*;
use crate::scene::LayerBlend;
use std::collections::HashMap;
/// One original GPU operation. Matrix/luma use Texture drawing; the remaining
/// operations use their canonical compute shaders and preserve premultiplied inputs.
#[derive(Clone, Debug)]
pub enum ColorFilterOperation {
    /// Original matrix/luma image draw, keeping source alpha interpretation.
    Matrix(ImageColorMatrix),
    /// Original table compute operation.
    Table(ColorTables),
    /// Original constant foreground plus original ImageBlend compute.
    Blend {
        /// Straight RGBA8 foreground factory color.
        color: [u8; 4],
        /// Original semantic blend mode.
        mode: LayerBlend,
    },
    /// Original HSLA/high-contrast compute operation.
    Nonlinear(NonlinearColorUniforms),
    /// Original overdraw compute palette.
    Overdraw(OverdrawColorUniforms),
    /// Original premultiplied arithmetic blend: k=[0,weight,1-weight,0].
    Lerp {
        /// Original unclamped interior weight.
        weight: f32,
    },
}
/// Frozen source dependencies and operation; result index is this step's index+1.
#[derive(Clone, Debug)]
pub struct ColorFilterStep {
    /// Input result indices; zero denotes the actual source generation. Unary
    /// operations repeat their input in the second lane.
    pub inputs: [u32; 2],
    /// Original operation and immutable parameters.
    pub operation: ColorFilterOperation,
}
/// Bounded immutable dependency program. Same node/source pairs share one result;
/// Compose binds its outer node to the actual completed inner result.
#[derive(Clone, Debug)]
pub struct ColorFilterPlan {
    steps: Vec<ColorFilterStep>,
    output: u32,
    owner: ColorFilter,
}
impl ColorFilterPlan {
    /// Retain original nodes and schedule child-before-parent, with a checked
    /// caller-selected work budget. Construction performs no GPU/pixel work.
    pub fn new(filter: &ColorFilter, maximum_steps: usize) -> Result<Self, Error> {
        if maximum_steps == 0 || maximum_steps > u32::MAX as usize {
            return Err(Error::Capacity);
        }
        fn visit(
            filter: &ColorFilter,
            input: u32,
            steps: &mut Vec<ColorFilterStep>,
            memo: &mut HashMap<(usize, u32), u32>,
            maximum: usize,
        ) -> Result<u32, Error> {
            let key = (filter.identity(), input);
            if let Some(&found) = memo.get(&key) {
                return Ok(found);
            }
            let (inputs, operation) = match filter.kind() {
                ColorFilterKind::Compose { outer, inner } => {
                    let inner = visit(inner, input, steps, memo, maximum)?;
                    let output = visit(outer, inner, steps, memo, maximum)?;
                    memo.insert(key, output);
                    return Ok(output);
                }
                ColorFilterKind::Lerp {
                    weight,
                    filter0,
                    filter1,
                } => {
                    let a = visit(filter0, input, steps, memo, maximum)?;
                    let b = visit(filter1, input, steps, memo, maximum)?;
                    if a == b {
                        memo.insert(key, a);
                        return Ok(a);
                    }
                    ([a, b], ColorFilterOperation::Lerp { weight: *weight })
                }
                ColorFilterKind::Matrix(matrix) => {
                    ([input; 2], ColorFilterOperation::Matrix(*matrix))
                }
                ColorFilterKind::Luma => (
                    [input; 2],
                    ColorFilterOperation::Matrix(ImageColorMatrix {
                        luminance_to_alpha: true,
                        ..Default::default()
                    }),
                ),
                ColorFilterKind::Table(t) => ([input; 2], ColorFilterOperation::Table(t.clone())),
                ColorFilterKind::Blend { color, mode } => (
                    [input; 2],
                    ColorFilterOperation::Blend {
                        color: *color,
                        mode: *mode,
                    },
                ),
                ColorFilterKind::Hsla(m) => (
                    [input; 2],
                    ColorFilterOperation::Nonlinear(NonlinearColorUniforms::hsla(*m)),
                ),
                ColorFilterKind::HighContrast(c) => {
                    ([input; 2], ColorFilterOperation::Nonlinear(c.uniforms()))
                }
                ColorFilterKind::Overdraw(colors) => (
                    [input; 2],
                    ColorFilterOperation::Overdraw(OverdrawColorUniforms {
                        colors: colors.map(|c| c.map(|v| f32::from(v) / 255.0)),
                    }),
                ),
            };
            if steps.len() == maximum {
                return Err(Error::Capacity);
            }
            steps.try_reserve(1).map_err(|_| Error::Capacity)?;
            steps.push(ColorFilterStep { inputs, operation });
            let output = steps.len() as u32;
            memo.insert(key, output);
            Ok(output)
        }
        let mut steps = Vec::new();
        let mut memo = HashMap::new();
        let output = visit(filter, 0, &mut steps, &mut memo, maximum_steps)?;
        Ok(Self {
            steps,
            output,
            owner: filter.clone(),
        })
    }
    /// Complete ordered immutable dependencies and operations.
    pub fn steps(&self) -> &[ColorFilterStep] {
        &self.steps
    }
    /// Final result index, retaining the original source-zero convention.
    pub fn output(&self) -> u32 {
        self.output
    }
    /// Actual retained root generation; never a flattened synthetic matrix.
    pub fn source(&self) -> &ColorFilter {
        &self.owner
    }
}
