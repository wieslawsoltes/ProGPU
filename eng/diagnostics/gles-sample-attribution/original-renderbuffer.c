/* Standalone authored GLES diagnosis. No wgpu, window/input or qualification. */
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <X11/Xlib.h>
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { WIDTH=16, HEIGHT=12, PIXELS=WIDTH*HEIGHT };
static void check_gl(const char *where) {
    GLenum error=glGetError();
    if(error!=GL_NO_ERROR) { fprintf(stderr,"GL error %s: 0x%x\n",where,error);exit(2); }
}
static void state(unsigned index,const char *phase) {
    GLint box[4],samples=0,sample_buffers=0,read=0,draw=0,viewport[4];
    glGetIntegerv(GL_SCISSOR_BOX,box);glGetIntegerv(GL_SAMPLES,&samples);
    glGetIntegerv(GL_SAMPLE_BUFFERS,&sample_buffers);glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING,&read);
    glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING,&draw);glGetIntegerv(GL_VIEWPORT,viewport);
    printf("state case=%u phase=%s scissor=%d box=%d,%d,%d,%d samples=%d sample_buffers=%d read=%d draw=%d viewport=%d,%d,%d,%d read_status=%x draw_status=%x\n",
        index,phase,glIsEnabled(GL_SCISSOR_TEST),box[0],box[1],box[2],box[3],samples,sample_buffers,
        read,draw,viewport[0],viewport[1],viewport[2],viewport[3],
        glCheckFramebufferStatus(GL_READ_FRAMEBUFFER),glCheckFramebufferStatus(GL_DRAW_FRAMEBUFFER));
    check_gl(phase);fflush(stdout);
}
static GLuint shader(GLenum kind,const char *source) {
    GLuint value=glCreateShader(kind);assert(value);glShaderSource(value,1,&source,NULL);glCompileShader(value);
    GLint ok=0;glGetShaderiv(value,GL_COMPILE_STATUS,&ok);
    if(!ok) { char log[8192];glGetShaderInfoLog(value,sizeof(log),NULL,log);fprintf(stderr,"%s\n",log);exit(2); }
    return value;
}
int main(int argc,char **argv) {
    assert(argc==3 && (!strcmp(argv[1],"disable") || !strcmp(argv[1],"full-box")));
    assert(getenv("RUNNER_ENVIRONMENT") && !strcmp(getenv("RUNNER_ENVIRONMENT"),"github-hosted"));
    assert(getenv("GITHUB_REPOSITORY") && !strcmp(getenv("GITHUB_REPOSITORY"),"wieslawsoltes/ProGPU"));
    Display *x=XOpenDisplay(NULL);assert(x);
    EGLDisplay display=eglGetDisplay((EGLNativeDisplayType)x);assert(display!=EGL_NO_DISPLAY);
    EGLint major=0,minor=0;assert(eglInitialize(display,&major,&minor));assert(eglBindAPI(EGL_OPENGL_ES_API));
    EGLint config_attributes[]={EGL_SURFACE_TYPE,EGL_PBUFFER_BIT,EGL_RENDERABLE_TYPE,EGL_OPENGL_ES3_BIT_KHR,EGL_RED_SIZE,8,EGL_GREEN_SIZE,8,EGL_BLUE_SIZE,8,EGL_ALPHA_SIZE,8,EGL_NONE};
    EGLConfig config;EGLint count=0;assert(eglChooseConfig(display,config_attributes,&config,1,&count)&&count==1);
    EGLint surface_attributes[]={EGL_WIDTH,WIDTH,EGL_HEIGHT,HEIGHT,EGL_NONE};
    EGLSurface surface=eglCreatePbufferSurface(display,config,surface_attributes);assert(surface!=EGL_NO_SURFACE);
    EGLint context_attributes[]={EGL_CONTEXT_CLIENT_VERSION,3,EGL_NONE};
    EGLContext context=eglCreateContext(display,config,EGL_NO_CONTEXT,context_attributes);assert(context!=EGL_NO_CONTEXT);
    assert(eglMakeCurrent(display,surface,surface,context));
    printf("EGL=%d.%d vendor=%s version=%s\nGL vendor=%s renderer=%s version=%s shading=%s\n",major,minor,eglQueryString(display,EGL_VENDOR),eglQueryString(display,EGL_VERSION),glGetString(GL_VENDOR),glGetString(GL_RENDERER),glGetString(GL_VERSION),glGetString(GL_SHADING_LANGUAGE_VERSION));
    GLint maximum=0;glGetIntegerv(GL_MAX_SAMPLES,&maximum);assert(maximum>=4);
    const char *vs="#version 300 es\nvoid main(){vec2 p[3]=vec2[3](vec2(-1.,-1.),vec2(3.,-1.),vec2(-1.,3.));gl_Position=vec4(p[gl_VertexID],0.,1.);}";
    const char *fs="#version 300 es\nprecision highp float;out vec4 color;void main(){color=vec4(0.,1.,0.,1.);}";
    GLuint vertex=shader(GL_VERTEX_SHADER,vs),fragment=shader(GL_FRAGMENT_SHADER,fs),program=glCreateProgram();assert(program);
    glAttachShader(program,vertex);glAttachShader(program,fragment);glLinkProgram(program);
    GLint linked=0;glGetProgramiv(program,GL_LINK_STATUS,&linked);assert(linked);glUseProgram(program);
    GLuint vao=0,renderbuffer=0,source=0,destination=0,texture=0;
    glGenVertexArrays(1,&vao);glBindVertexArray(vao);
    glGenRenderbuffers(1,&renderbuffer);glBindRenderbuffer(GL_RENDERBUFFER,renderbuffer);
    glRenderbufferStorageMultisample(GL_RENDERBUFFER,4,GL_RGBA8,WIDTH,HEIGHT);
    GLint actual_samples=0,format=0;glGetRenderbufferParameteriv(GL_RENDERBUFFER,GL_RENDERBUFFER_SAMPLES,&actual_samples);
    glGetRenderbufferParameteriv(GL_RENDERBUFFER,GL_RENDERBUFFER_INTERNAL_FORMAT,&format);assert(actual_samples==4 && format==GL_RGBA8);
    glGenFramebuffers(1,&source);glBindFramebuffer(GL_FRAMEBUFFER,source);
    glFramebufferRenderbuffer(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_RENDERBUFFER,renderbuffer);assert(glCheckFramebufferStatus(GL_FRAMEBUFFER)==GL_FRAMEBUFFER_COMPLETE);
    glGenTextures(1,&texture);glBindTexture(GL_TEXTURE_2D,texture);glTexStorage2D(GL_TEXTURE_2D,1,GL_RGBA8,WIDTH,HEIGHT);
    glGenFramebuffers(1,&destination);glBindFramebuffer(GL_FRAMEBUFFER,destination);
    glFramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,texture,0);assert(glCheckFramebufferStatus(GL_FRAMEBUFFER)==GL_FRAMEBUFFER_COMPLETE);
    glDisable(GL_BLEND);glDisable(GL_DEPTH_TEST);glDisable(GL_STENCIL_TEST);glDisable(GL_CULL_FACE);
    glDisable(GL_SAMPLE_ALPHA_TO_COVERAGE);glDisable(GL_SAMPLE_COVERAGE);glDisable(GL_DITHER);
    glColorMask(GL_TRUE,GL_TRUE,GL_TRUE,GL_TRUE);glViewport(0,0,WIDTH,HEIGHT);glPixelStorei(GL_PACK_ALIGNMENT,1);
    glDisable(GL_SCISSOR_TEST);glClearColor(0,0,1,1);glClear(GL_COLOR_BUFFER_BIT);glFinish();
    unsigned total=0;
    for(unsigned index=0;index<3;index++) {
        const int left=index==1?2:0,top=index==1?4:0,width=index==1?8:WIDTH,height=4,draw=index<2;
        glBindFramebuffer(GL_FRAMEBUFFER,source);glDisable(GL_SCISSOR_TEST);
        glClearColor(index==1?0:1,0,index==1?1:0,1);glClear(GL_COLOR_BUFFER_BIT);
        /* Top-left logical oracle maps explicitly to bottom-left GL coordinates. */
        glScissor(0,0,WIDTH,HEIGHT);glEnable(GL_SCISSOR_TEST);
        if(draw) { glScissor(left,HEIGHT-top-height,width,height);state(index,"before-draw");glDrawArrays(GL_TRIANGLES,0,3); }
        state(index,"after-draw");
        if(!strcmp(argv[1],"disable"))glDisable(GL_SCISSOR_TEST);
        else { glScissor(0,0,WIDTH,HEIGHT);glEnable(GL_SCISSOR_TEST); }
        glBindFramebuffer(GL_READ_FRAMEBUFFER,source);glReadBuffer(GL_COLOR_ATTACHMENT0);glBindFramebuffer(GL_DRAW_FRAMEBUFFER,destination);
        state(index,"before-resolve");glBlitFramebuffer(0,0,WIDTH,HEIGHT,0,0,WIDTH,HEIGHT,GL_COLOR_BUFFER_BIT,GL_NEAREST);
        state(index,"after-resolve");glBindFramebuffer(GL_READ_FRAMEBUFFER,destination);glReadBuffer(GL_COLOR_ATTACHMENT0);
        unsigned char raw[PIXELS*4];glReadPixels(0,0,WIDTH,HEIGHT,GL_RGBA,GL_UNSIGNED_BYTE,raw);check_gl("readback");
        char path[4096];assert(snprintf(path,sizeof(path),"%s/case-%u-bottom-left.rgba",argv[2],index)<(int)sizeof(path));
        FILE *file=fopen(path,"wb");assert(file);assert(fwrite(raw,1,sizeof(raw),file)==sizeof(raw));assert(!fclose(file));
        unsigned mismatches=0;
        for(int y=0;y<HEIGHT;y++)for(int xx=0;xx<WIDTH;xx++) {
            const unsigned char *actual=raw+((HEIGHT-1-y)*WIDTH+xx)*4;
            int green=draw && xx>=left && xx<left+width && y>=top && y<top+height;
            unsigned char expected[4]={!green && index!=1?255:0,green?255:0,!green && index==1?255:0,255};
            if(memcmp(actual,expected,4)) { mismatches++;printf("mismatch case=%u xy=%d,%d actual=%u,%u,%u,%u expected=%u,%u,%u,%u\n",index,xx,y,actual[0],actual[1],actual[2],actual[3],expected[0],expected[1],expected[2],expected[3]); }
        }
        total+=mismatches;printf("case%u samples=4 pixels=192 mismatches=%u\n",index,mismatches);fflush(stdout);
    }
    glBindFramebuffer(GL_FRAMEBUFFER,0);glDeleteFramebuffers(1,&source);glDeleteFramebuffers(1,&destination);glDeleteRenderbuffers(1,&renderbuffer);glDeleteTextures(1,&texture);
    glUseProgram(0);glDeleteProgram(program);glDeleteShader(vertex);glDeleteShader(fragment);glDeleteVertexArrays(1,&vao);check_gl("cleanup");
    assert(eglMakeCurrent(display,EGL_NO_SURFACE,EGL_NO_SURFACE,EGL_NO_CONTEXT));assert(eglDestroyContext(display,context));assert(eglDestroySurface(display,surface));assert(eglTerminate(display));assert(XCloseDisplay(x)==0);
    printf("%s standalone GLES samples=4 pixels=576 mismatches=%u mode=%s; authored diagnosis only\n",total?"FAIL":"PASS",total,argv[1]);return total?1:0;
}
