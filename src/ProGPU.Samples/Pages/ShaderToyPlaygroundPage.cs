using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Transpiler;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

namespace ProGPU.Samples
{
    public class ShaderToyPlaygroundPageGrid : Grid, IAnimatedElement
    {
        private readonly ShaderToyControl _toyControl;
        private readonly RichEditBox _editor;
        private readonly RichTextBlock _consoleText;
        private readonly TextBlock _statsText;
        private readonly Button _playBtn;
        private readonly Button _pauseBtn;

        private bool _isCodeDirty;
        private DateTime _lastCodeChangeTime;

        public const string Preset1_CosmicWaves = @"// Rainbow Plasma / Cosmic Waves
fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = fragCoord / inputs.iResolution.xy;
    let t = inputs.iTime * 1.5;
    
    let r = 0.5 + 0.5 * sin(uv.x * 10.0 + t + sin(uv.y * 5.0 + t));
    let g = 0.5 + 0.5 * sin(uv.y * 10.0 - t + cos(uv.x * 5.0 + t));
    let b = 0.5 + 0.5 * sin((uv.x + uv.y) * 5.0 + t + sin(t));
    var col = vec3<f32>(r, g, b);
    
    // Pulse circle at mouse position if left-clicked
    let mouse = inputs.iMouse;
    if (mouse.z > 0.0) {
        let dist = distance(fragCoord, mouse.xy);
        let circle = smoothstep(60.0, 58.0, dist);
        col = mix(col, vec3<f32>(1.0, 1.0, 1.0), circle * 0.8);
    }
    
    return vec4<f32>(col, 1.0);
}";

        public const string Preset2_StarNest = @"// Star Nest (Cosmic Space Folding)
fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = (fragCoord - 0.5 * inputs.iResolution.xy) / inputs.iResolution.y;
    var dir = vec3<f32>(uv * 0.8, 1.0);
    let time = inputs.iTime * 0.05;
    
    // Rotate camera based on mouse or time
    var s = sin(time * 0.3);
    var c = cos(time * 0.3);
    if (inputs.iMouse.z > 0.0) {
        let mouseNorm = inputs.iMouse.xy / inputs.iResolution.xy;
        s = sin(mouseNorm.x * 3.14);
        c = cos(mouseNorm.x * 3.14);
    }
    
    dir = vec3<f32>(dir.x * c - dir.z * s, dir.y, dir.x * s + dir.z * c);
    
    var startPos = vec3<f32>(1.0, 0.5, 0.5);
    startPos += vec3<f32>(time * 2.0, time, -2.0);
    
    // Volumetric rendering loop
    var s_val = 0.1;
    var fade = 0.5;
    var v = vec3<f32>(0.0);
    
    for (var r: i32 = 0; r < 12; r = r + 1) {
        var p = startPos + f32(r) * dir * s_val;
        // Float floor-modulo replacement for WebGPU portability
        p = abs(vec3<f32>(0.85) - (p - floor(p / 1.7) * 1.7));
        
        var pa = 0.0;
        var a = 0.0;
        for (var i: i32 = 0; i < 10; i = i + 1) {
            p = abs(p) / dot(p, p) - vec3<f32>(0.53);
            let len = length(p);
            a = a + abs(len - pa);
            pa = len;
        }
        
        let dm = max(0.0, 0.85 - a * a * 0.001);
        var a_val = a * a * a;
        v = v + vec3<f32>(dm, dm, dm) * fade;
        v = v + vec3<f32>(s_val, s_val * s_val, s_val * s_val * s_val) * a_val * fade * 0.0003;
        fade = fade * 0.86;
    }
    
    let intensity = length(v);
    var col = mix(vec3<f32>(intensity * 0.01), v * 0.1, 0.5);
    col = col * 0.35;
    
    return vec4<f32>(col, 1.0);
}";

        public const string Preset3_RaymarchedTorus = @"// Spinning Raymarched Torus SDF
fn sdTorus(p: vec3<f32>, t: vec2<f32>) -> f32 {
    let q = vec2<f32>(length(p.xz) - t.x, p.y);
    return length(q) - t.y;
}

fn map(p: vec3<f32>) -> f32 {
    let t = inputs.iTime * 1.0;
    let c = cos(t);
    let s = sin(t);
    var rp = p;
    rp = vec3<f32>(rp.x * c - rp.y * s, rp.x * s + rp.y * c, rp.z);
    rp = vec3<f32>(rp.x, rp.y * c - rp.z * s, rp.y * s + rp.z * c);
    return sdTorus(rp, vec2<f32>(1.5, 0.5));
}

fn getNormal(p: vec3<f32>) -> vec3<f32> {
    let eps = 0.001;
    let h = vec2<f32>(eps, 0.0);
    return normalize(vec3<f32>(
        map(p + h.xyy) - map(p - h.xyy),
        map(p + h.yxy) - map(p - h.yxy),
        map(p + h.yyx) - map(p - h.yyx)
    ));
}

fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = (fragCoord - 0.5 * inputs.iResolution.xy) / inputs.iResolution.y;
    
    let ro = vec3<f32>(0.0, 0.0, -4.0);
    let rd = normalize(vec3<f32>(uv, 1.0));
    
    var t = 0.0;
    var d = 0.0;
    var hit = false;
    for (var i: i32 = 0; i < 80; i = i + 1) {
        let p = ro + rd * t;
        d = map(p);
        if (d < 0.001) {
            hit = true;
            break;
        }
        t = t + d;
        if (t > 10.0) {
            break;
        }
    }
    
    var col = vec3<f32>(0.1, 0.12, 0.15);
    if (hit) {
        let p = ro + rd * t;
        let n = getNormal(p);
        let lightDir = normalize(vec3<f32>(1.0, 2.0, -3.0));
        
        let diff = max(0.0, dot(n, lightDir));
        let viewDir = normalize(ro - p);
        let reflectDir = reflect(-lightDir, n);
        let spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
        
        let baseColor = 0.5 + 0.5 * cos(inputs.iTime + p.xyx + vec3<f32>(0.0, 2.0, 4.0));
        col = baseColor * (diff + 0.1) + vec3<f32>(0.5) * spec;
    }
    
    return vec4<f32>(col, 1.0);
}";

        public const string Preset4_RaymarchingPrimitives = @"// Raymarching - Primitives
// Ported from original GLSL shader: https://www.shadertoy.com/view/Xds3zN
// Created by inigo quilez - iq/2013
// License Creative Commons Attribution-NonCommercial-ShareAlike 3.0 Unported License.

float dot2(in vec2 v) { return dot(v,v); }
float dot2(in vec3 v) { return dot(v,v); }
float ndot(in vec2 a, in vec2 b) { return a.x*b.x - a.y*b.y; }

float sdSphere( vec3 p, float s )
{
  return length(p)-s;
}

float sdBox( vec3 p, vec3 b )
{
  vec3 q = abs(p) - b;
  return length(max(q,0.0)) + min(max(q.x,max(q.y,q.z)),0.0);
}

float sdRoundBox( vec3 p, vec3 b, float r )
{
  vec3 q = abs(p) - b;
  return length(max(q,0.0)) + min(max(q.x,max(q.y,q.z)),0.0) - r;
}

float sdBoxFrame( vec3 p, vec3 b, float e )
{
  p = abs(p)-b;
  vec3 q = abs(p+e)-e;
  return min(min(
      length(max(vec3(p.x,q.y,q.z),0.0))+min(max(p.x,max(q.y,q.z)),0.0),
      length(max(vec3(q.x,p.y,q.z),0.0))+min(max(q.x,max(p.y,q.z)),0.0)),
      length(max(vec3(q.x,q.y,p.z),0.0))+min(max(q.x,max(q.y,p.z)),0.0));
}

float sdEllipsoid( vec3 p, vec3 r )
{
  float k0 = length(p/r);
  float k1 = length(p/(r*r));
  return k0*(k0-1.0)/k1;
}

float sdTorus( vec3 p, vec2 t )
{
  vec2 q = vec2(length(p.xz)-t.x,p.y);
  return length(q)-t.y;
}

float sdCappedTorus(in vec3 p, in vec2 sc, in float ra, in float rb)
{
  p.x = abs(p.x);
  float k = (sc.y*p.x>sc.x*p.y) ? dot(p.xy,sc) : length(p.xy);
  return sqrt( dot(p,p) + ra*ra - 2.0*ra*k ) - rb;
}

float sdHexPrism( vec3 p, vec2 h )
{
  const vec3 k = vec3(-0.8660254, 0.5, 0.57735);
  p = abs(p);
  p.xy -= 2.0*min(dot(k.xy, p.xy), 0.0)*k.xy;
  vec2 d = vec2(
       length(p.xy-vec2(clamp(p.x,-k.z*h.x,k.z*h.x), h.x))*sign(p.y-h.x),
       p.z-h.y );
  return min(max(d.x,d.y),0.0) + length(max(d,0.0));
}

float sdOctahedronPrism( vec3 p, float r, float h )
{
  const vec3 k = vec3(-0.9238795325, 0.3826834323, 0.4142135623);
  p = abs(p);
  p.xy -= 2.0*min(dot(vec2( k.x, k.y),p.xy),0.0)*vec2( k.x,k.y);
  p.xy -= 2.0*min(dot(vec2(-k.x, k.y),p.xy),0.0)*vec2(-k.x,k.y);
  p.xy -= vec2(clamp(p.x, -k.z*r, k.z*r), r);
  vec2 d = vec2( length(p.xy)*sign(p.y), p.z-h );
  return min(max(d.x,d.y),0.0) + length(max(d,0.0));
}

float sdCapsule( vec3 p, vec3 a, vec3 b, float r )
{
  vec3 pa = p - a, ba = b - a;
  float h = clamp( dot(pa,ba)/dot(ba,ba), 0.0, 1.0 );
  return length( pa - ba*h ) - r;
}

float sdRoundCone( vec3 p, float r1, float r2, float h )
{
  vec2 q = vec2( length(p.xz), p.y );
  float b = (r1-r2)/h;
  float a = sqrt(1.0-b*b);
  float k = dot(q,vec2(-b,a));
  if( k < 0.0 ) return length(q) - r1;
  if( k > a*h ) return length(q-vec2(0.0,h)) - r2;
  return dot(q, vec2(a,b) ) - r1;
}

float sdRoundCone(vec3 p, vec3 a, vec3 b, float r1, float r2)
{
  vec3  ba = b - a;
  float l2 = dot(ba,ba);
  float rr = r1 - r2;
  float a2 = l2 - rr*rr;
  float il2 = 1.0/l2;
  
  vec3 pa = p - a;
  float y = dot(pa,ba);
  float z = y - l2;
  float x2 = dot2(pa*l2 - ba*y);
  float y2 = y*y*l2;
  float z2 = z*z*l2;

  float k = sign(rr)*rr*rr*x2;
  if( sign(z)*a2*z2 > k ) return  sqrt(x2 + z2)*il2 - r2;
  if( sign(y)*a2*y2 < k ) return  sqrt(x2 + y2)*il2 - r1;
  return (sqrt(x2*a2*il2)+y*rr)*il2 - r1;
}

float sdTriPrism( vec3 p, vec2 h )
{
  const float k = sqrt(3.0);
  h.x *= 0.5*k;
  p.xy /= h.x;
  p.x = abs(p.x) - 1.0;
  p.y = p.y + 1.0/k;
  if( p.x+k*p.y>0.0 ) p.xy = vec2(p.x-k*p.y,-k*p.x-p.y)/2.0;
  p.x -= clamp( p.x, -2.0, 0.0 );
  float d1 = length(p.xy)*sign(-p.y)*h.x;
  float d2 = abs(p.z) - h.y;
  return length(max(vec2(d1,d2),0.0)) + min(max(d1,d2),0.0);
}

float sdCylinder( vec3 p, vec2 h )
{
  vec2 d = abs(vec2(length(p.xz),p.y)) - h;
  return min(max(d.x,d.y),0.0) + length(max(d,0.0));
}

float sdCylinder(vec3 p, vec3 a, vec3 b, float r)
{
  vec3  ba = b - a;
  vec3  pa = p - a;
  float baba = dot(ba,ba);
  float paba = dot(pa,ba);
  float x = length(pa*baba-ba*paba) - r*baba;
  float y = abs(paba-baba*0.5) - baba*0.5;
  float x2 = x*x;
  float y2 = y*y*baba;
  float d = (max(x,y)<0.0)?-min(x2,y2):(((x>0.0)?x2:0.0)+((y>0.0)?y2:0.0));
  return sign(d)*sqrt(abs(d))/baba;
}

float sdCone( vec3 p, vec2 c, float h )
{
  vec2 q = h*vec2(c.x,-c.y)/c.y;
  vec2 w = vec2( length(p.xz), p.y );
  vec2 a = w - q*clamp( dot(w,q)/dot(q,q), 0.0, 1.0 );
  vec2 b = w - q*vec2( clamp(w.x/q.x, 0.0, 1.0), 1.0 );
  float k = sign(q.y);
  float d = min(dot(a,a),dot(b,b));
  float s = max( k*(w.x*q.y-w.y*q.x),k*(w.y-q.y) );
  return sqrt(d)*sign(s);
}

float sdCappedCone( vec3 p, float h, float r1, float r2 )
{
  vec2 q = vec2( length(p.xz), p.y );
  vec2 k1 = vec2(r2,h);
  vec2 k2 = vec2(r2-r1,2.0*h);
  vec2 ca = vec2(q.x-min(q.x,(q.y<0.0)?r1:r2), abs(q.y)-h);
  vec2 cb = q - k1 + k2*clamp( dot(k1-q,k2)/dot(k2,k2), 0.0, 1.0 );
  float s = (cb.x<0.0 && ca.y<0.0)?-1.0:1.0;
  return s*sqrt(min(dot(ca,ca),dot(cb,cb)));
}

float sdCappedCone(vec3 p, vec3 a, vec3 b, float ra, float rb)
{
  float rba = rb-ra;
  float baba = dot(b-a,b-a);
  float papa = dot(p-a,p-a);
  float paba = dot(p-a,b-a)/baba;
  float x = sqrt( papa - paba*paba*baba );
  float cax = max(0.0, x - ((paba<0.5)?ra:rb));
  float cay = abs(paba-0.5)-0.5;
  float k = rba*rba + baba;
  float f = clamp( (rba*(x-ra)+paba*baba)/k, 0.0, 1.0 );
  float cbx = x - ra - f*rba;
  float cby = paba - f;
  float s = (cbx<0.0 && cay<0.0)?-1.0:1.0;
  return s*sqrt(min(cax*cax + cay*cay*baba, cbx*cbx + cby*cby*baba) );
}

float sdSolidAngle(vec3 p, vec2 c, float r)
{
  vec2 q = vec2( length(p.xz), p.y );
  float l = length(q) - r;
  float m = length(q - c*clamp(dot(q,c),0.0,r) );
  return max(l,m*sign(c.y*q.x-c.x*q.y));
}

float sdOctahedron( vec3 p, float s )
{
  p = abs(p);
  return (p.x+p.y+p.z-s)*0.57735027;
}

float sdPyramid( vec3 p, float h )
{
  float m2 = h*h + 0.25;
  p.xz = abs(p.xz);
  p.xz = (p.z>p.x) ? p.zx : p.xz;
  p.xz -= 0.5;
  vec3 q = vec3( p.z, h*p.y - 0.5*p.x, h*p.x + 0.5*p.y );
  float s = max(-q.x,0.0);
  float t = clamp( (q.y-0.5*p.z)/(m2+0.25), 0.0, 1.0 );
  float a = m2*(q.x+s)*(q.x+s) + q.y*q.y;
  float b = m2*(q.x+0.5*t)*(q.x+0.5*t) + (q.y-m2*t)*(q.y-m2*t);
  float d2 = min(q.y,-q.x*m2-q.y*0.5)>0.0 ? 0.0 : min(a,b);
  return sqrt( (d2+q.z*q.z)/m2 ) * sign(max(q.z,-p.y));
}

float sdRhombus(vec3 p, float la, float lb, float h, float ra)
{
  p = abs(p);
  vec2 b = vec2(la,lb);
  float f = clamp( (ndot(b,b-2.0*p.xz))/dot(b,b), -1.0, 1.0 );
  vec2 q = vec2(length(p.xz-0.5*b*vec2(1.0-f,1.0+f))*sign(p.x*b.y+p.z*b.x-b.x*b.y)-ra, p.y-h);
  return min(max(q.x,q.y),0.0) + length(max(q,0.0));
}

vec2 opU( vec2 d1, vec2 d2 )
{
  return (d1.x<d2.x) ? d1 : d2;
}

vec2 map( in vec3 pos )
{
    vec2 res = vec2( 1e10, 0.0 );

    res = opU( res, vec2( sdSphere(    pos-vec3(-2.0,0.25, 0.0), 0.25 ), 26.9 ) );

    // Row 0
    res = opU( res, vec2( sdBoxFrame(  pos-vec3( 0.0,0.25, 0.0), vec3(0.3,0.25,0.2), 0.025 ), 16.9 ) );
    res = opU( res, vec2( sdTorus(     pos-vec3( 0.0,0.30, 1.0), vec2(0.25,0.05) ), 25.0 ) );
    res = opU( res, vec2( sdCone(      pos-vec3( 0.0,0.45,-1.0), vec2(0.6,0.8), 0.45 ), 55.0 ) );
    res = opU( res, vec2( sdCappedCone(pos-vec3( 0.0,0.25,-2.0), 0.25, 0.25, 0.1 ), 13.67 ) );
    res = opU( res, vec2( sdSolidAngle(pos-vec3( 0.0,0.00,-3.0), vec2(3.0,4.0)/5.0, 0.4 ), 49.13 ) );

    // Row 1
    res = opU( res, vec2( sdCappedTorus(pos-vec3( 1.0,0.30, 1.0), vec2(0.866025,-0.5), 0.25, 0.05 ), 8.5 ) );
    res = opU( res, vec2( sdBox(       pos-vec3( 1.0,0.25, 0.0), vec3(0.3,0.25,0.1) ), 3.0 ) );
    res = opU( res, vec2( sdCapsule(   pos-vec3( 1.0,0.00,-1.0), vec3(-0.1,0.1,-0.1), vec3(0.2,0.4,0.2), 0.1 ), 31.9 ) );
    res = opU( res, vec2( sdCylinder(  pos-vec3( 1.0,0.25,-2.0), vec2(0.15,0.25) ), 8.0 ) );
    res = opU( res, vec2( sdHexPrism(  pos-vec3( 1.0,0.20,-3.0), vec2(0.2,0.05) ), 18.4 ) );

    // Row 2
    res = opU( res, vec2( sdPyramid(   pos-vec3(-1.0,-0.6,-3.0), 1.0 ), 13.56 ) );
    res = opU( res, vec2( sdOctahedron(pos-vec3(-1.0,0.35,-2.0), 0.35 ), 23.56 ) );
    res = opU( res, vec2( sdTriPrism(  pos-vec3(-1.0,0.15,-1.0), vec2(0.3,0.05) ), 43.5 ) );
    res = opU( res, vec2( sdEllipsoid( pos-vec3(-1.0,0.25, 0.0), vec3(0.2,0.25,0.05) ), 43.17 ) );
    res = opU( res, vec2( sdRhombus(   pos-vec3(-1.0,0.34, 1.0), 0.15, 0.25, 0.04, 0.08 ), 17.0 ) );

    // Row 3
    res = opU( res, vec2( sdOctahedronPrism(pos-vec3( 2.0,0.20,-3.0), 0.2, 0.05 ), 51.8 ) );
    res = opU( res, vec2( sdCylinder(  pos-vec3( 2.0,0.15,-2.0), vec3(0.1,-0.1,0.0), vec3(-0.2,0.35,0.1), 0.08 ), 32.1 ) );
    res = opU( res, vec2( sdCappedCone(pos-vec3( 2.0,0.10,-1.0), vec3(0.1,0.0,0.0), vec3(-0.2,0.4,0.1), 0.15, 0.05 ), 46.1 ) );
    res = opU( res, vec2( sdRoundCone( pos-vec3( 2.0,0.15, 1.0), 0.2, 0.1, 0.3 ), 37.0 ) );
    res = opU( res, vec2( sdRoundCone( pos-vec3( 2.0,0.15, 0.0), vec3(0.1,0.0,0.0), vec3(-0.1,0.35,0.1), 0.15, 0.05 ), 51.7 ) );

    return res;
}

vec2 iBox( in vec3 ro, in vec3 rd, in vec3 rad ) 
{
    vec3 m = 1.0/rd;
    vec3 n = m*ro;
    vec3 k = abs(m)*rad;
    vec3 t1 = -n - k;
    vec3 t2 = -n + k;
    return vec2( max(max(t1.x,t1.y),t1.z), min(min(t2.x,t2.y),t2.z) );
}

float calcAO( in vec3 pos, in vec3 nor )
{
    float occ = 0.0;
    float sca = 1.0;
    for( int i=0; i<5; i++ )
    {
        float h = 0.01 + 0.12*float(i)/4.0;
        float d = map( pos + h*nor ).x;
        occ += (h-d) * sca;
        sca *= 0.95;
        if( occ>0.35 ) break;
    }
    return clamp( 1.0 - 3.0*occ, 0.0, 1.0 ) * (0.5+0.5*nor.y);
}

float checkersGradBox( in vec2 p, in vec2 dpdx, in vec2 dpdy )
{
    vec2 w = abs(dpdx)+abs(dpdy) + 0.001;
    vec2 i = 2.0*(abs(fract((p-0.5*w)*0.5)-0.5)-abs(fract((p+0.5*w)*0.5)-0.5))/w;
    return 0.5 - 0.5*i.x*i.y;                  
}

float calcSoftshadow( in vec3 ro, in vec3 rd, in float mint, in float tmax )
{
    float tp = (0.8-ro.y)/rd.y; if( tp>0.0 ) tmax = min( tmax, tp );

    float res = 1.0;
    float t = mint;
    for( int i=0; i<24; i++ )
    {
        float h = map( ro + rd*t ).x;
        float s = clamp( 8.0*h/t, 0.0, 1.0 );
        res = min( res, s*s*(3.0-2.0*s) );
        t += clamp( h, 0.02, 0.20 );
        if( res<0.004 || t>tmax ) break;
    }
    return clamp( res, 0.0, 1.0 );
}

vec3 calcNormal( in vec3 pos )
{
    vec2 e = vec2(1.0,-1.0)*0.5773*0.0005;
    return normalize( e.xyy*map( pos + e.xyy ).x + 
                      e.yyx*map( pos + e.yyx ).x + 
                      e.yxy*map( pos + e.yxy ).x + 
                      e.xxx*map( pos + e.xxx ).x );
}

vec2 raycast( in vec3 ro, in vec3 rd )
{
    vec2 res = vec2(-1.0,-1.0);
    float tmin = 1.0;
    float tmax = 20.0;

    float tp1 = (0.0-ro.y)/rd.y; if( tp1>0.0 ) { tmax = min( tmax, tp1 ); res = vec2( tp1, 1.0 ); }

    vec2 tb = iBox( ro-vec3(0.0,0.4,-0.5), rd, vec3(2.5,0.41,3.0) );
    if( tb.x<tb.y && tb.y>0.0 && tb.x<tmax )
    {
        tmin = max(tb.x,tmin);
        tmax = min(tb.y,tmax);

        float t = tmin;
        for( int i=0; i<70 && t<tmax; i++ )
        {
            vec2 h = map( ro + rd*t );
            if( abs(h.x)<(0.0001*t) )
            { 
                res = vec2(t,h.y); 
                break;
            }
            t += h.x;
        }
    }
    return res;
}

vec3 render( in vec3 ro, in vec3 rd, in vec3 rdx, in vec3 rdy )
{ 
    vec3 col = vec3(0.7, 0.7, 0.9) - max(rd.y,0.0)*0.3;
    
    vec2 res = raycast(ro,rd);
    float t = res.x;
    float m = res.y;
    if( m>-0.5 )
    {
        vec3 pos = ro + t*rd;
        vec3 nor = (m<1.5) ? vec3(0.0,1.0,0.0) : calcNormal(pos);
        vec3 ref = reflect( rd, nor );

        col = 0.2 + 0.2*sin( 2.0*m + vec3(0.0,1.0,2.0) );
        float ks = 1.0;
        
        if( m<1.5 )
        {
            vec3 dpdx = ro.y*(rd/rd.y - rdx/rdx.y);
            vec3 dpdy = ro.y*(rd/rd.y - rdy/rdy.y);
            
            float f = checkersGradBox( 3.0*pos.xz, 3.0*dpdx.xz, 3.0*dpdy.xz );
            col = 0.15 + f*vec3(0.05);
            ks = 0.4;
        }

        float occ = calcAO( pos, nor );
        
        vec3 lin = vec3(0.0);

        {
            vec3  lig = normalize( vec3(-0.5, 0.4, -0.6) );
            vec3  hal = normalize( lig-rd );
            float dif = clamp( dot( nor, lig ), 0.0, 1.0 );
            dif *= calcSoftshadow( pos, lig, 0.02, 2.5 );
            float spe = pow( clamp( dot( nor, hal ), 0.0, 1.0 ), 16.0);
            spe *= dif;
            spe *= 0.04+0.96*pow(clamp(1.0-dot(hal,lig),0.0,1.0),5.0);
            lin += col * 2.20 * dif * vec3(1.30,1.00,0.70);
            lin +=      5.00 * spe * vec3(1.30,1.00,0.70) * ks;
        }

        {
            float dif = sqrt(clamp( 0.5 + 0.5*nor.y, 0.0, 1.0 ));
            dif *= occ;
            float spe = smoothstep( -0.2, 0.2, ref.y );
            spe *= dif;
            spe *= 0.04+0.96*pow(clamp(1.0+dot(nor,rd),0.0,1.0),5.0);
            spe *= calcSoftshadow( pos, ref, 0.02, 2.5 );
            lin += col * 0.60 * dif * vec3(0.40,0.60,1.00);
            lin +=      2.00 * spe * vec3(0.40,0.60,1.00) * ks;
        }

        {
            float dif = clamp( dot( nor, normalize(vec3(0.5,0.0,0.6)) ), 0.0, 1.0 ) * clamp( 1.0-pos.y,0.0,1.0);
            dif *= occ;
            lin += col * 0.55 * dif * vec3(0.25,0.25,0.25);
        }

        {
            float dif = pow(clamp(1.0+dot(nor,rd),0.0,1.0),2.0);
            dif *= occ;
            lin += col * 0.25 * dif * vec3(1.0,1.0,1.0);
        }

        col = lin;

        col = mix( col, vec3(0.7,0.7,0.9), 1.0-exp( -0.0001*t*t*t ) );
    }

    return vec3( clamp(col,0.0,1.0) );
}

mat3 setCamera( in vec3 ro, in vec3 ta, float cr )
{
    vec3 cw = normalize(ta-ro);
    vec3 cp = vec3(sin(cr), cos(cr),0.0);
    vec3 cu = normalize( cross(cw,cp) );
    vec3 cv =          ( cross(cu,cw) );
    return mat3( cu, cv, cw );
}

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec2 mo = iMouse.xy/iResolution.xy;
    float time = 32.0 + iTime*1.5;
    
    vec3 ta = vec3( 0.5, -0.5, -0.6 );
    vec3 ro = ta + vec3( 4.5*cos(0.1*time + 7.0*mo.x), 1.3 + 2.0*mo.y, 4.5*sin(0.1*time + 7.0*mo.x) );
    
    mat3 ca = setCamera( ro, ta, 0.0 );
    
    vec3 tot = vec3(0.0);
    
    vec2 p = (2.0*fragCoord-iResolution.xy)/iResolution.y;

    vec3 rd = ca * normalize( vec3(p,2.5) );

    vec2 px = (2.0*(fragCoord+vec2(1.0,0.0))-iResolution.xy)/iResolution.y;
    vec2 py = (2.0*(fragCoord+vec2(0.0,1.0))-iResolution.xy)/iResolution.y;
    vec3 rdx = ca * normalize( vec3(px,2.5) );
    vec3 rdy = ca * normalize( vec3(py,2.5) );
    
    vec3 col = render( ro, rd, rdx, rdy );

    col = pow( col, vec3(0.4545) );

    tot += col;
    
    fragColor = vec4( tot, 1.0 );
}";

        public const string Preset5_StarNestGlsl = @"// Star Nest by Kali
#define iterations 15
#define formuparam 0.53

#define volsteps 12
#define stepsize 0.1

#define zoom   0.800
#define tile   0.850
#define speed  0.010 

#define brightness 0.0015
#define darkmatter 0.300
#define distfading 0.730
#define saturation 0.850

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
	//get coords and direction
	vec2 uv=fragCoord.xy/iResolution.xy-.5;
	uv.y*=iResolution.y/iResolution.x;
	vec3 dir=vec3(uv*zoom,1.);
	float time=iTime*speed+.25;

	//mouse rotation
	float s=sin(time*0.3), c=cos(time*0.3);
	if (iMouse.z > 0.0) {
		float mouseNorm = iMouse.x / iResolution.x;
		s = sin(mouseNorm * 3.14);
		c = cos(mouseNorm * 3.14);
	}
	
	vec3 from=vec3(1.,.5,0.5);
	from+=vec3(time*2.,time,-2.);
	
	//volumetric rendering
	float s_val=0.1,fade=1.;
	vec3 v=vec3(0.);
	for (int r=0; r<volsteps; r++) {
		vec3 p=from+s_val*dir*s_val;
		p = abs(vec3(tile)-mod(p,vec3(tile*2.))); // modulation
		float pa,a=pa=0.;
		for (int i=0; i<iterations; i++) {
			p=abs(p)/dot(p,p)-vec3(formuparam); // the magic formula
			a+=abs(length(p)-pa); // absolute sum of average change
			pa=length(p);
		}
		float dm=max(0.,darkmatter-a*a*.001); //dark matter
		a*=a*a; // add contrast
		if (r>6) fade*=1.-dm; // dark matter signup with distance
		v+=fade;
		v+=vec3(s_val,s_val*s_val,s_val*s_val*s_val)*a*brightness*fade; // coloring based on distance
		fade*=distfading; // distance fading
		s_val+=stepsize;
	}
	v=mix(vec3(length(v)),v,saturation); //color adjust
	fragColor = vec4(v*.01,1.);	
}";

        public ShaderToyPlaygroundPageGrid()
        {
            Margin = new Thickness(12);

            // Columns: Left (Code & Controls) / Right (Canvas & Console)
            ColumnDefinitions.Add(new GridLength(460, GridUnitType.Absolute));
            ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

            // ----------------------------------------------------
            // LEFT COLUMN: Controls & Code Editor
            // ----------------------------------------------------
            var leftGrid = new Grid();
            leftGrid.RowDefinitions.Add(new GridLength(45, GridUnitType.Absolute)); // Header toolbar
            leftGrid.RowDefinitions.Add(new GridLength(40, GridUnitType.Absolute)); // Actions row
            leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));    // Editor

            // Row 0: Presets dropdown ComboBox
            var toolbarStack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            var presetLabel = new TextBlock
            {
                Text = "Preset: ",
                Font = AppState._font,
                FontSize = 12f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            toolbarStack.AddChild(presetLabel);

            var presetCombo = new ComboBox { Font = AppState._font, Width = 180f };
            var wavesItem = new ComboBoxItem("Cosmic Waves");
            var starNestItem = new ComboBoxItem("Star Nest Journey");
            var torusItem = new ComboBoxItem("Raymarched Torus");
            var primitivesItem = new ComboBoxItem("Raymarching Primitives (GLSL)");
            var starNestGlslItem = new ComboBoxItem("Star Nest (Original GLSL)");
            presetCombo.Items.Add(wavesItem);
            presetCombo.Items.Add(starNestItem);
            presetCombo.Items.Add(torusItem);
            presetCombo.Items.Add(primitivesItem);
            presetCombo.Items.Add(starNestGlslItem);
            presetCombo.SelectedItem = wavesItem;
            toolbarStack.AddChild(presetCombo);

            // Run Button
            var runBtn = new Button
            {
                Width = 100f,
                Height = 28f,
                CornerRadius = 4f,
                Margin = new Thickness(12, 0, 0, 0),
                Background = new ThemeResourceBrush("SystemAccentColor")
            };
            runBtn.Content = new TextBlock
            {
                Text = "▶ Run",
                Font = AppState._font,
                FontSize = 11f,
                Foreground = new SolidColorBrush(Vector4.One),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            runBtn.Click += (s, e) => CompileNow();
            toolbarStack.AddChild(runBtn);

            leftGrid.AddChild(toolbarStack);
            Grid.SetRow(toolbarStack, 0);

            // Row 1: Playing / Timeline Actions
            var actionStack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            
            _playBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 6, 0) };
            _playBtn.Content = new TextBlock { Text = "Play", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _playBtn.Click += (s, e) => SetPlaying(true);
            actionStack.AddChild(_playBtn);

            _pauseBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 6, 0) };
            _pauseBtn.Content = new TextBlock { Text = "Pause", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _pauseBtn.Click += (s, e) => SetPlaying(false);
            actionStack.AddChild(_pauseBtn);

            var resetBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 12, 0) };
            resetBtn.Content = new TextBlock { Text = "Reset", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            resetBtn.Click += (s, e) => _toyControl.Reset();
            actionStack.AddChild(resetBtn);

            var transpileBtn = new Button { Width = 110f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 12, 0) };
            transpileBtn.Content = new TextBlock { Text = "Transpile GLSL", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            transpileBtn.Click += (s, e) =>
            {
                try
                {
                    string input = _editor.Text;
                    string translated = ShaderToyTranspiler.Translate(input);
                    _editor.Text = translated;
                    
                    _consoleText.Inlines.Clear();
                    _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.2f, 1.0f)) });
                    _consoleText.Inlines.Add(new Bold(new Run("Transpilation succeeded!\n")) { Foreground = new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.2f, 1.0f)) });
                    _consoleText.Inlines.Add(new Run("GLSL code successfully translated to WGSL in editor."));
                    _consoleText.Invalidate();
                    
                    CompileNow();
                }
                catch (Exception ex)
                {
                    _consoleText.Inlines.Clear();
                    _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1.0f)) });
                    _consoleText.Inlines.Add(new Bold(new Run("Transpilation failed!\n")) { Foreground = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1.0f)) });
                    _consoleText.Inlines.Add(new Run(ex.Message) { Foreground = new SolidColorBrush(new Vector4(0.85f, 0.7f, 0.7f, 1.0f)) });
                    _consoleText.Invalidate();
                }
            };
            actionStack.AddChild(transpileBtn);

            // Stats Text block
            _statsText = new TextBlock
            {
                Text = "Time: 0.0s | Frame: 0",
                Font = AppState._fontCourier,
                FontSize = 11f,
                Foreground = new ThemeResourceBrush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center
            };
            actionStack.AddChild(_statsText);

            leftGrid.AddChild(actionStack);
            Grid.SetRow(actionStack, 1);

            // Row 2: Code Editor (RichEditBox)
            var editorBorder = new Border
            {
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 6f,
                Padding = new Thickness(4)
            };

            _editor = new RichEditBox
            {
                Font = AppState._fontCourier,
                FontSize = 12f,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            
            // Listen to edits and debounce compilation
            _editor.TextChanged += (s, e) =>
            {
                _isCodeDirty = true;
                _lastCodeChangeTime = DateTime.UtcNow;
            };



            editorBorder.Child = _editor;
            leftGrid.AddChild(editorBorder);
            Grid.SetRow(editorBorder, 2);

            AddChild(leftGrid);
            Grid.SetColumn(leftGrid, 0);

            // ----------------------------------------------------
            // RIGHT COLUMN: Live Canvas & Console Log
            // ----------------------------------------------------
            var rightGrid = new Grid { Margin = new Thickness(12, 0, 0, 0) };
            rightGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));    // ShaderToy control
            rightGrid.RowDefinitions.Add(new GridLength(130, GridUnitType.Absolute)); // Compile output console

            // Row 0: ShaderToy render canvas Card
            var canvasCard = new Border
            {
                Background = new ThemeResourceBrush("CardBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(0)
            };

            _toyControl = new ShaderToyControl();
            _toyControl.CompilationFailed += HandleCompilationFailed;
            _toyControl.CompilationSucceeded += HandleCompilationSucceeded;
            canvasCard.Child = _toyControl;
            rightGrid.AddChild(canvasCard);
            Grid.SetRow(canvasCard, 0);

            // Row 1: Monospaced Console Log Card
            var consoleCard = new Border
            {
                Background = new SolidColorBrush(new Vector4(0.08f, 0.08f, 0.09f, 1f)), // Dark terminal color
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 6f,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 10, 0, 0)
            };

            var consoleScroll = new ScrollViewer { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            _consoleText = new RichTextBlock
            {
                Font = AppState._fontCourier,
                FontSize = 11f,
                Foreground = new SolidColorBrush(new Vector4(0.7f, 0.7f, 0.75f, 1f))
            };
            
            _consoleText.Inlines.Add(new Run("[System] ShaderToy Playground ready. Welcome!\n"));
            _consoleText.Inlines.Add(new Run("[System] Type WGSL code and click Run (or press Ctrl+Enter)."));

            consoleScroll.Content = _consoleText;
            consoleCard.Child = consoleScroll;
            rightGrid.AddChild(consoleCard);
            Grid.SetRow(consoleCard, 1);

            AddChild(rightGrid);
            Grid.SetColumn(rightGrid, 1);

            // Handle dropdown Preset changes
            presetCombo.SelectionChanged += (s, e) =>
            {
                if (presetCombo.SelectedItem != null)
                {
                    string code = presetCombo.SelectedItem.Text switch
                    {
                        "Cosmic Waves" => Preset1_CosmicWaves,
                        "Star Nest Journey" => Preset2_StarNest,
                        "Raymarched Torus" => Preset3_RaymarchedTorus,
                        "Raymarching Primitives (GLSL)" => Preset4_RaymarchingPrimitives,
                        "Star Nest (Original GLSL)" => Preset5_StarNestGlsl,
                        _ => Preset1_CosmicWaves
                    };

                    _editor.Text = code;
                    _toyControl.Reset();
                    CompileNow();
                }
            };

            // Set initial state
            _editor.Text = Preset1_CosmicWaves;
            _toyControl.ShaderSource = Preset1_CosmicWaves;
            SetPlaying(true);
        }

        private void SetPlaying(bool play)
        {
            _toyControl.IsPlaying = play;
            _playBtn.IsEnabled = !play;
            _pauseBtn.IsEnabled = play;
        }

        private void CompileNow()
        {
            _isCodeDirty = false;
            
            _consoleText.Inlines.Clear();
            _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] Compiling shader module...\n"));
            _consoleText.Invalidate();

            _toyControl.ShaderSource = _editor.Text;
        }

        private void HandleCompilationSucceeded()
        {
            _consoleText.Inlines.Clear();
            _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.2f, 1.0f)) });
            _consoleText.Inlines.Add(new Bold(new Run("Compilation succeeded!\n")) { Foreground = new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.2f, 1.0f)) });
            _consoleText.Inlines.Add(new Run("GPU Pipeline recompiled and hot-swapped smoothly. Zero-copy render target active."));
            _consoleText.Invalidate();
        }

        private void HandleCompilationFailed(string error)
        {
            _consoleText.Inlines.Clear();
            _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1.0f)) });
            _consoleText.Inlines.Add(new Bold(new Run("Compilation failed!\n")) { Foreground = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1.0f)) });
            
            // Output compiler diagnostic error
            _consoleText.Inlines.Add(new Run(error) { Foreground = new SolidColorBrush(new Vector4(0.85f, 0.7f, 0.7f, 1.0f)) });
            _consoleText.Invalidate();
        }

        public void Update(float delta)
        {
            // 1. Accumulate and update visual statistics
            _statsText.Text = $"Time: {_toyControl.Time:F1}s | Frame: {_toyControl.Frame:F0} | FPS: {(1.0f / delta):F0}";

            // 2. Debounced auto-compilation
            if (_isCodeDirty && (DateTime.UtcNow - _lastCodeChangeTime).TotalMilliseconds > 800)
            {
                CompileNow();
            }
        }
    }

    public static class ShaderToyPlaygroundPage
    {
        public static FrameworkElement Create()
        {
            return new ShaderToyPlaygroundPageGrid();
        }
    }
}
