// Algorithm: Convert analytic path winding or bounded postfix predicates into supersampled R8 coverage, with phased mask combination for driver-sensitive overlaps.
// Time complexity: O(A*(S+N)) per texel for A supersamples, S segment visits, and N bounded postfix instructions.
// Space complexity: O(D) private expression-stack storage for D<=16; split mask programs retain two u32 words per leaf texel.
// The managed PathAtlas retains this bounded inline evaluator for compatibility.
// ProGPU.Native recognizes the signed program flag before dispatch and uses the
// staged leaf/evaluate/pack pipelines instead.
fn combine_winding_predicate_lanes(
    windingA: vec4<i32>,
    windingB: vec4<i32>,
    pathOpKind: u32) -> vec4<i32> {
    let insideA = windingA != vec4<i32>(0);
    let insideB = windingB != vec4<i32>(0);
    var inside = insideA;
    switch pathOpKind {
        case 1u: { inside = insideA && !insideB; }
        case 2u: { inside = insideA && insideB; }
        case 3u: { inside = insideA || insideB; }
        case 4u: { inside = insideA != insideB; }
        case 5u: { inside = insideB && !insideA; }
        default: {}
    }
    return select(vec4<i32>(0), vec4<i32>(1), inside);
}

fn combine_winding_predicates(
    windingA: WindingRow,
    windingB: WindingRow,
    pathOpKind: u32) -> WindingRow {
    return WindingRow(
        combine_winding_predicate_lanes(
            windingA.low,
            windingB.low,
            pathOpKind),
        combine_winding_predicate_lanes(
            windingA.high,
            windingB.high,
            pathOpKind));
}

fn signed_winding_program_row_coverage_mask(
    pixelX: f32,
    sampleY: f32,
    sampleGrid: u32,
    scaleX: f32,
    pathIndex: u32,
    programIndex: u32,
    encodedProgramCount: u32) -> u32 {
    var stack: array<WindingRow, 16>;
    var stackCount = 0u;
    let programCount = encodedProgramCount & BOOLEAN_TOKEN_VALUE_MASK;
    for (var instructionIndex = 0u;
         instructionIndex < programCount;
         instructionIndex = instructionIndex + 1u) {
        let token = pathRecords[programIndex + instructionIndex].startSegment;
        if ((token & BOOLEAN_PROGRAM_FLAG) != 0u) {
            let operation = token & BOOLEAN_LEAF_INDEX_MASK;
            if (operation == 8u) {
                if (stackCount < 1u) {
                    return 0u;
                }
                stack[stackCount - 1u].low =
                    -stack[stackCount - 1u].low;
                stack[stackCount - 1u].high =
                    -stack[stackCount - 1u].high;
            } else {
                if (stackCount < 2u) {
                    return 0u;
                }
                let windingB = stack[stackCount - 1u];
                let windingA = stack[stackCount - 2u];
                stackCount = stackCount - 1u;
                if (operation == 7u) {
                    stack[stackCount - 1u] = WindingRow(
                        windingA.low + windingB.low,
                        windingA.high + windingB.high);
                } else {
                    stack[stackCount - 1u] = combine_winding_predicates(
                        windingA,
                        windingB,
                        operation);
                }
            }
        } else {
            if (stackCount >= MAX_BOOLEAN_STACK_DEPTH) {
                return 0u;
            }
            var winding = WindingRow(vec4<i32>(0), vec4<i32>(0));
            if (token != BOOLEAN_EMPTY_TOKEN) {
                let record = pathRecords[
                    pathIndex + (token & BOOLEAN_LEAF_INDEX_MASK)];
                winding = row_winding(
                    pixelX,
                    sampleY,
                    sampleGrid,
                    scaleX,
                    record);
                if ((token & WINDING_LEAF_TOKEN_FLAG) == 0u) {
                    winding = winding_fill_predicate(
                        winding,
                        record.fillRule);
                }
            }
            stack[stackCount] = winding;
            stackCount = stackCount + 1u;
        }
    }

    if (stackCount != 1u) {
        return 0u;
    }
    return winding_row_coverage_mask(stack[0], sampleGrid, 1u);
}

fn path_coverage_byte(x: u32, y: u32, uniforms: PathUniforms) -> u32 {
    let pathIndex = uniforms.pathIndex;
    let record = pathRecords[pathIndex];

    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);

    var coveredSamples = 0u;
    let sampleGrid = clamp(uniforms.sampleGrid, 1u, 8u);
    let sampleWeight = 1.0 / f32(sampleGrid * sampleGrid);
    for (var sampleY = 0u; sampleY < sampleGrid; sampleY = sampleY + 1u) {
        let samplePositionY = py + (f32(sampleY) + 0.5) / f32(sampleGrid);
        let samplePathY = samplePositionY / uniforms.scaleY;
        var combinedMask = 0u;
        if ((uniforms.pathOpKind & BOOLEAN_PROGRAM_FLAG) != 0u) {
            if ((uniforms.pathOpKind &
                    SIGNED_WINDING_PROGRAM_FLAG) != 0u) {
                combinedMask = signed_winding_program_row_coverage_mask(
                    px,
                    samplePathY,
                    sampleGrid,
                    uniforms.scaleX,
                    pathIndex,
                    uniforms.pathIndexB,
                    uniforms.pathOpKind);
            } else {
                combinedMask = boolean_program_row_coverage_mask(
                    px,
                    samplePathY,
                    sampleGrid,
                    uniforms.scaleX,
                    pathIndex,
                    uniforms.pathIndexB,
                    uniforms.pathOpKind);
            }
        } else {
            let maskA = row_coverage_mask(
                px,
                samplePathY,
                sampleGrid,
                uniforms.scaleX,
                record);
            combinedMask = maskA;
            if (uniforms.pathOpKind != 0u) {
                let recordB = pathRecords[uniforms.pathIndexB];
                let maskB = row_coverage_mask(
                    px,
                    samplePathY,
                    sampleGrid,
                    uniforms.scaleX,
                    recordB);
                combinedMask = combine_coverage_masks(
                    maskA,
                    maskB,
                    uniforms.pathOpKind);
            }
        }
        let validMask = (1u << sampleGrid) - 1u;
        coveredSamples = coveredSamples + countOneBits(combinedMask & validMask);
    }
    return min(255u, u32(round(f32(coveredSamples) * sampleWeight * 255.0)));
}

fn path_sample_mask(x: u32, y: u32, uniforms: PathUniforms) -> vec2<u32> {
    let record = pathRecords[uniforms.pathIndex];
    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);
    let sampleGrid = clamp(uniforms.sampleGrid, 1u, 8u);
    var samples = vec2<u32>(0u);
    for (var sampleY = 0u; sampleY < sampleGrid; sampleY = sampleY + 1u) {
        let samplePositionY = py + (f32(sampleY) + 0.5) / f32(sampleGrid);
        let samplePathY = samplePositionY / uniforms.scaleY;
        let rowMask = row_coverage_mask(
            px,
            samplePathY,
            sampleGrid,
            uniforms.scaleX,
            record);
        if (sampleY < 4u) {
            samples.x = samples.x | (rowMask << (sampleY * 8u));
        } else {
            samples.y = samples.y | (rowMask << ((sampleY - 4u) * 8u));
        }
    }
    return samples;
}

// Four adjacent pixels are packed by one invocation so the storage buffer has
// the exact byte layout required by WebGPU copyBufferToTexture for R8Unorm.
@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let uniforms = pathUniforms[global_id.z];
    let wordX = global_id.x;
    let y = global_id.y;
    let firstX = wordX * 4u;
    if (firstX >= uniforms.width || y >= uniforms.height) {
        return;
    }

    var packed = 0u;
    for (var lane = 0u; lane < 4u; lane = lane + 1u) {
        let x = firstX + lane;
        if (x < uniforms.width) {
            packed = packed | (path_coverage_byte(x, y, uniforms) << (lane * 8u));
        }
    }

    coverageOutput[uniforms.outputOffsetWords + y * uniforms.outputRowWords + wordX] = packed;
}

@compute @workgroup_size(16, 16)
fn cs_split_leaf(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let uniforms = pathUniforms[global_id.z];
    let x = global_id.x;
    let y = global_id.y;
    if (x >= uniforms.width || y >= uniforms.height) {
        return;
    }
    let samples = path_sample_mask(x, y, uniforms);
    let outputIndex = uniforms.outputOffsetWords +
        y * uniforms.outputRowWords + x * 2u;
    coverageOutput[outputIndex] = samples.x;
    coverageOutput[outputIndex + 1u] = samples.y;
}

fn combine_sample_masks(
    maskA: vec2<u32>,
    maskB: vec2<u32>,
    operation: u32) -> vec2<u32> {
    return vec2<u32>(
        combine_coverage_masks(maskA.x, maskB.x, operation),
        combine_coverage_masks(maskA.y, maskB.y, operation));
}

fn split_boolean_program_sample_mask(
    uniforms: CoverageCombineUniforms,
    x: u32,
    y: u32) -> vec2<u32> {
    var stack: array<vec2<u32>, 16>;
    var stackCount = 0u;
    let programCount = uniforms.programCount & BOOLEAN_TOKEN_VALUE_MASK;
    for (var instructionIndex = 0u;
         instructionIndex < programCount;
         instructionIndex = instructionIndex + 1u) {
        let token = pathRecords[
            uniforms.programIndex + instructionIndex].startSegment;
        if ((token & BOOLEAN_PROGRAM_FLAG) != 0u) {
            if (stackCount < 2u) {
                return vec2<u32>(0u);
            }
            let maskB = stack[stackCount - 1u];
            let maskA = stack[stackCount - 2u];
            stackCount = stackCount - 1u;
            stack[stackCount - 1u] = combine_sample_masks(
                maskA,
                maskB,
                token & BOOLEAN_TOKEN_VALUE_MASK);
        } else {
            if (stackCount >= MAX_BOOLEAN_STACK_DEPTH) {
                return vec2<u32>(0u);
            }
            var samples = vec2<u32>(0u);
            if (token != BOOLEAN_EMPTY_TOKEN) {
                if (token >= uniforms.sourceCount) {
                    return vec2<u32>(0u);
                }
                let sourceIndex = uniforms.sourceOffsetWords +
                    token * uniforms.sourceStrideWords +
                    (y * uniforms.width + x) * 2u;
                samples = vec2<u32>(
                    coverageOutput[sourceIndex],
                    coverageOutput[sourceIndex + 1u]);
            }
            stack[stackCount] = samples;
            stackCount = stackCount + 1u;
        }
    }
    return select(vec2<u32>(0u), stack[0], stackCount == 1u);
}

@compute @workgroup_size(16, 16)
fn cs_split_boolean_combine(
    @builtin(global_invocation_id) global_id: vec3<u32>) {
    let uniforms = coverageCombineUniforms[global_id.z];
    let wordX = global_id.x;
    let y = global_id.y;
    if (wordX * 4u >= uniforms.width || y >= uniforms.height) {
        return;
    }
    var packed = 0u;
    let sampleGrid = clamp(uniforms.sampleGrid, 1u, 8u);
    let sampleWeight = 1.0 / f32(sampleGrid * sampleGrid);
    for (var lane = 0u; lane < 4u; lane = lane + 1u) {
        let x = wordX * 4u + lane;
        if (x < uniforms.width) {
            let samples = split_boolean_program_sample_mask(
                uniforms,
                x,
                y);
            let coveredSamples =
                countOneBits(samples.x) + countOneBits(samples.y);
            let coverage = min(
                255u,
                u32(round(
                    f32(coveredSamples) * sampleWeight * 255.0)));
            packed = packed | (coverage << (lane * 8u));
        }
    }
    coverageOutput[
        uniforms.destinationOffsetWords +
        y * uniforms.destinationRowWords + wordX] = packed;
}
