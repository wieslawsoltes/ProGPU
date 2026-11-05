// Algorithm: Record the actual production binary-scan visit sequence per lane.
// Time complexity: O(P+4) per lane for its original P horizontal pixel visits.
// Space complexity: O(1) private state and 143 uint words per lane in output.
struct GlyphRecord { traceBase: u32 };
struct GlyphUniforms { width: u32 };
@group(0) @binding(0) var<storage, read_write> trace: array<u32>;
var<private> uniforms: GlyphUniforms;
var<private> cursor: u32;

fn render_scan_visit(record: GlyphRecord, index: u32, level: f32, vertical: bool, kind: u32) {
    if cursor < 70u {
        trace[record.traceBase + 1u + cursor * 2u] = kind;
        trace[record.traceBase + 2u + cursor * 2u] = index;
    }
    cursor++;
}

@compute @workgroup_size(256)
fn cs_trace(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let caseBase = group.x * (2u + 256u * 143u);
    uniforms.width = trace[caseBase];
    let vertical = trace[caseBase + 1u] != 0u;
    let record = GlyphRecord(caseBase + 2u + lane * 143u);
    cursor = 0u;
    render_scan_visits(record, 0.0, vertical, lane);
    trace[record.traceBase] = cursor;
}
