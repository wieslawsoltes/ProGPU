"""Exact diagnostic-only source derivation; no product checkout mutation."""
from pathlib import Path
import hashlib,json
QUEUE_SHA='56cd5a8baf54e62d20e5010d7a706192aeef1e9f26f8bebaa2e7cd19220b0bdc'
PROBE_SHA='56b409ad5c9e9533afe52824d3132656a807f6b20eac2c98cb72423f7dd6d92e'
OBSERVER='''
unsafe fn progpu_diagnostic_state(gl: &glow::Context, label: &str) {
    static COUNT: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
    let ordinal = COUNT.fetch_add(1, Ordering::Relaxed);
    assert!(ordinal < 64, "diagnostic GL observation bound");
    if ordinal == 0 { eprintln!("GL_IDENTITY vendor={:?} renderer={:?} version={:?} shading={:?}", unsafe { gl.get_parameter_string(glow::VENDOR) }, unsafe { gl.get_parameter_string(glow::RENDERER) }, unsafe { gl.get_parameter_string(glow::VERSION) }, unsafe { gl.get_parameter_string(glow::SHADING_LANGUAGE_VERSION) }); }
    let mut rect = [0i32; 4];
    unsafe { gl.get_parameter_i32_slice(glow::SCISSOR_BOX, &mut rect); }
    eprintln!("GL_STATE ordinal={} label={} enabled={} rect={},{},{},{} samples={} sample_buffers={} draw_fbo={} read_fbo={}",
        ordinal, label, unsafe { gl.is_enabled(glow::SCISSOR_TEST) }, rect[0], rect[1], rect[2], rect[3],
        unsafe { gl.get_parameter_i32(glow::SAMPLES) }, unsafe { gl.get_parameter_i32(glow::SAMPLE_BUFFERS) },
        unsafe { gl.get_parameter_i32(glow::DRAW_FRAMEBUFFER_BINDING) }, unsafe { gl.get_parameter_i32(glow::READ_FRAMEBUFFER_BINDING) });
}
'''
def sha(raw):return hashlib.sha256(raw).hexdigest()
def replace_once(source,old,new):
    if source.count(old)!=1:raise ValueError('Exact source seam changed: '+old[:80])
    return source.replace(old,new,1)
def derive_queue(raw,variant):
    if sha(raw)!=QUEUE_SHA:raise ValueError('Reviewed queue identity changed')
    if variant not in ('disabled','full-scissor'):raise ValueError('Unknown variant')
    text=raw.decode()
    text=replace_once(text,'const DEBUG_ID: u32 = 0;','const DEBUG_ID: u32 = 0;\n'+OBSERVER)
    text=replace_once(text,'\n                let supports_full_instancing = self','\n                unsafe { progpu_diagnostic_state(gl, "before_draw"); }\n                let supports_full_instancing = self')
    start=text.index('            C::ResolveAttachment {');end=text.index('            C::InvalidateAttachments',start)
    block=text[start:end]
    old='                unsafe { gl.disable(glow::SCISSOR_TEST) };'
    replacement=('                unsafe { gl.disable(glow::SCISSOR_TEST) };' if variant=='disabled' else '                unsafe { gl.enable(glow::SCISSOR_TEST); gl.scissor(0, 0, size.width as i32, size.height as i32); };')
    block=replace_once(block,old,'                unsafe { progpu_diagnostic_state(gl, "before_resolve_policy"); }\n'+replacement+'\n                unsafe { progpu_diagnostic_state(gl, "after_resolve_policy"); }')
    block=replace_once(block,'                unsafe {\n                    gl.blit_framebuffer(','                unsafe { progpu_diagnostic_state(gl, "before_blit"); }\n                unsafe {\n                    gl.blit_framebuffer(')
    block=replace_once(block,'                unsafe { gl.bind_framebuffer(glow::READ_FRAMEBUFFER, None) };','                unsafe { progpu_diagnostic_state(gl, "after_blit"); }\n                unsafe { gl.bind_framebuffer(glow::READ_FRAMEBUFFER, None) };')
    return (text[:start]+block+text[end:]).encode()
def derive_probe(raw):
    if sha(raw)!=PROBE_SHA:raise ValueError('Reviewed probe identity changed')
    text=raw.decode();old='        const uint8_t* actual=pixels+y*ROW_BYTES+x*4;'
    return replace_once(text,old,old+'\n        printf("PIXEL case=%u x=%u y=%u rgba=%u,%u,%u,%u\\n",index,x,y,actual[0],actual[1],actual[2],actual[3]);').encode()
