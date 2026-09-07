// Algorithm: Evaluate one signed-winding postfix program for each supersample row, carrying eight horizontal samples in two vec4<i32> values and packing the resulting predicate rows into two u32 masks per texel.
// Time complexity: O(G*N) vector operations per texel for sample-grid height G<=8 and bounded program length N.
// Space complexity: O(D) private vector stack storage for depth D<=16 and two u32 result words per texel.
const BOOLEAN_PROGRAM_FLAG: u32 = 0x80000000u;
const BOOLEAN_EMPTY_TOKEN: u32 = 0x40000000u;
const BOOLEAN_TOKEN_VALUE_MASK: u32 = 0x3fffffffu;
const BOOLEAN_LEAF_INDEX_MASK: u32 = 0x1fffffffu;
const MAX_BOOLEAN_STACK_DEPTH: u32 = 16u;

struct PathRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    fillRule: u32,
    _pad1: u32,
};

struct CoverageCombineUniforms {
    sourceOffsetWords: u32,
    sourceStrideWords: u32,
    sourceCount: u32,
    programIndex: u32,
    programCount: u32,
    destinationOffsetWords: u32,
    destinationRowWords: u32,
    width: u32,
    height: u32,
    sampleGrid: u32,
};

struct WindingRow {
    low: vec4<i32>,
    high: vec4<i32>,
};

@group(0) @binding(1) var<storage, read> pathRecords: array<PathRecord>;
@group(0) @binding(3) var<storage, read_write> coverageOutput: array<u32>;
@group(0) @binding(4) var<storage, read> coverageCombineUniforms: array<CoverageCombineUniforms>;

fn combine_winding_predicate_lanes(
    windingA: vec4<i32>,
    windingB: vec4<i32>,
    operation: u32) -> vec4<i32> {
    if (operation == 7u) {
        return windingA + windingB;
    }
    let insideA = windingA != vec4<i32>(0);
    let insideB = windingB != vec4<i32>(0);
    var inside = insideA;
    switch operation {
        case 1u: { inside = select(vec4<bool>(false), !insideB, insideA); }
        case 2u: { inside = select(vec4<bool>(false), insideB, insideA); }
        case 3u: { inside = select(insideB, vec4<bool>(true), insideA); }
        case 4u: { inside = insideA != insideB; }
        case 5u: { inside = select(vec4<bool>(false), !insideA, insideB); }
        default: {}
    }
    return select(vec4<i32>(0), vec4<i32>(1), inside);
}

fn combine_winding_rows(
    windingA: WindingRow,
    windingB: WindingRow,
    operation: u32) -> WindingRow {
    return WindingRow(
        combine_winding_predicate_lanes(
            windingA.low,
            windingB.low,
            operation),
        combine_winding_predicate_lanes(
            windingA.high,
            windingB.high,
            operation));
}

fn split_signed_program_row_winding(
    uniforms: CoverageCombineUniforms,
    x: u32,
    y: u32,
    sampleY: u32) -> WindingRow {
    var stack: array<WindingRow, 16>;
    var stackCount = 0u;
    let programCount = uniforms.programCount & BOOLEAN_TOKEN_VALUE_MASK;
    for (var instructionIndex = 0u;
         instructionIndex < programCount;
         instructionIndex = instructionIndex + 1u) {
        let token = pathRecords[
            uniforms.programIndex + instructionIndex].startSegment;
        if ((token & BOOLEAN_PROGRAM_FLAG) != 0u) {
            let operation = token & BOOLEAN_LEAF_INDEX_MASK;
            if (operation == 8u) {
                if (stackCount < 1u) {
                    return WindingRow(vec4<i32>(0), vec4<i32>(0));
                }
                stack[stackCount - 1u].low =
                    -stack[stackCount - 1u].low;
                stack[stackCount - 1u].high =
                    -stack[stackCount - 1u].high;
            } else {
                if (stackCount < 2u) {
                    return WindingRow(vec4<i32>(0), vec4<i32>(0));
                }
                let windingB = stack[stackCount - 1u];
                let windingA = stack[stackCount - 2u];
                stackCount = stackCount - 1u;
                stack[stackCount - 1u] = combine_winding_rows(
                    windingA,
                    windingB,
                    operation);
            }
        } else {
            if (stackCount >= MAX_BOOLEAN_STACK_DEPTH) {
                return WindingRow(vec4<i32>(0), vec4<i32>(0));
            }
            var winding = WindingRow(vec4<i32>(0), vec4<i32>(0));
            if (token != BOOLEAN_EMPTY_TOKEN) {
                let leafIndex = token & BOOLEAN_LEAF_INDEX_MASK;
                if (leafIndex >= uniforms.sourceCount) {
                    return WindingRow(vec4<i32>(0), vec4<i32>(0));
                }
                let sourceIndex = uniforms.sourceOffsetWords +
                    leafIndex * uniforms.sourceStrideWords +
                    (y * uniforms.width + x) * 64u +
                    sampleY * 8u;
                winding = WindingRow(
                    vec4<i32>(
                        bitcast<i32>(coverageOutput[sourceIndex + 0u]),
                        bitcast<i32>(coverageOutput[sourceIndex + 1u]),
                        bitcast<i32>(coverageOutput[sourceIndex + 2u]),
                        bitcast<i32>(coverageOutput[sourceIndex + 3u])),
                    vec4<i32>(
                        bitcast<i32>(coverageOutput[sourceIndex + 4u]),
                        bitcast<i32>(coverageOutput[sourceIndex + 5u]),
                        bitcast<i32>(coverageOutput[sourceIndex + 6u]),
                        bitcast<i32>(coverageOutput[sourceIndex + 7u])));
            }
            stack[stackCount] = winding;
            stackCount = stackCount + 1u;
        }
    }
    if (stackCount != 1u) {
        return WindingRow(vec4<i32>(0), vec4<i32>(0));
    }
    return stack[0];
}

fn winding_row_coverage_mask(
    winding: WindingRow,
    sampleGrid: u32) -> u32 {
    var mask = 0u;
    mask = mask | select(0u, 1u << 0u, winding.low.x != 0);
    mask = mask | select(0u, 1u << 1u, winding.low.y != 0);
    mask = mask | select(0u, 1u << 2u, winding.low.z != 0);
    mask = mask | select(0u, 1u << 3u, winding.low.w != 0);
    mask = mask | select(0u, 1u << 4u, winding.high.x != 0);
    mask = mask | select(0u, 1u << 5u, winding.high.y != 0);
    mask = mask | select(0u, 1u << 6u, winding.high.z != 0);
    mask = mask | select(0u, 1u << 7u, winding.high.w != 0);
    return mask & ((1u << sampleGrid) - 1u);
}

fn split_signed_result_offset(
    uniforms: CoverageCombineUniforms) -> u32 {
    return uniforms.sourceOffsetWords +
        uniforms.sourceStrideWords * uniforms.sourceCount;
}

@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let uniforms = coverageCombineUniforms[global_id.z];
    let x = global_id.x;
    let y = global_id.y;
    let sampleGrid = clamp(uniforms.sampleGrid, 1u, 8u);
    if (x >= uniforms.width || y >= uniforms.height) {
        return;
    }
    var samples = vec2<u32>(0u);
    for (var sampleY = 0u;
         sampleY < sampleGrid;
         sampleY = sampleY + 1u) {
        let rowMask = winding_row_coverage_mask(
            split_signed_program_row_winding(
                uniforms,
                x,
                y,
                sampleY),
            sampleGrid);
        if (sampleY < 4u) {
            samples.x = samples.x | (rowMask << (sampleY * 8u));
        } else {
            samples.y = samples.y | (rowMask << ((sampleY - 4u) * 8u));
        }
    }
    let resultIndex = split_signed_result_offset(uniforms) +
        (y * uniforms.width + x) * 2u;
    coverageOutput[resultIndex] = samples.x;
    coverageOutput[resultIndex + 1u] = samples.y;
}
