// Algorithm: Instanced analytic 2.5D artwork with rough stone cells, layered noise, textured foliage and fur, atmospheric extinction, and directional illumination. Original Suntrail artwork.
// Time complexity: O(V + F) for V visible sprites and F covered fragments; six vertices per sprite, at most 36 bounded shape layers per fragment; material noise uses three fixed octaves and stone cells use a 3x3 neighborhood, no scene-length loops or ray marching. An optional sky-cache miss adds O(P) work for P framebuffer pixels once per key; hits replace sky noise with one O(1) texel load per visible fragment.
// Space complexity: O(V) uploaded 48-byte sprite records in a fixed 2048-instance buffer; O(1) private fragment storage and a 288-byte frame uniform. Optional retained sky uses one physical-resolution RGBA32Float image capped at 96 MiB; replay performs one unfiltered 16-byte texel load per sky fragment. Other entries use zero texture samples.
// Coordinates are logical pixels, projected to physical framebuffer pixels by ProGPU.
// Coverage uses physical-pixel derivatives; premultiplied alpha, source-over composition.
// Fixed loops: stone neighborhood 9, tree branches 7 and canopy lobes 7, fern fronds 2 with 6 leaf pairs each, grass blades 6, petals 5, portal sparks 6, palm fronds 8, pine boughs 6, crystal prisms 3, local lights 3, conservative opaque rectangles at most 8. No ray marching or screen-space history.
// Lighting is an art-directed ellipsoid approximation, not a physically based ray tracer.
// Sphere, canopy and mountain coverage is evaluated before lighting. An optional
// exact-zero early return skips transparent lanes while retaining the same fwidth
// coverage value for visible lanes; worst-case loop bounds and precision are unchanged.

// Each instance is two triangles with flat material; all lanes of an interior quad
// select the same artwork. Helper invocations preserve derivatives at its edges.
// The compatibility loader removes only this diagnostic control for legacy Naga.
diagnostic(off, derivative_uniformity);

struct Frame { transform: mat4x4<f32>, scene: vec4<f32>, clip: vec4<f32>, lights: array<vec4<f32>,3>, occlusion: vec4<f32>, ground: array<vec4<f32>,8> };
@group(0) @binding(0) var<uniform> frame: Frame;
struct Sprite {
    @location(0) bounds: vec4<f32>,
    @location(1) color: vec4<f32>,
    @location(2) material: vec4<f32>,
};
struct Varying {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) @interpolate(flat) color: vec4<f32>,
    @location(2) @interpolate(flat) material: vec4<f32>,
    @location(3) @interpolate(flat) size: vec2<f32>,
    @location(4) local: vec2<f32>,
};
@vertex fn vs_main(sprite: Sprite, @builtin(vertex_index) vertex: u32) -> Varying {
    var corners = array<vec2<f32>, 6>(vec2(0.,0.),vec2(1.,0.),vec2(1.,1.),vec2(0.,0.),vec2(1.,1.),vec2(0.,1.));
    let uv = corners[vertex];
    let local = frame.transform * vec4(sprite.bounds.xy + uv * sprite.bounds.zw, 0., 1.);
    var o: Varying;
    o.position = vec4(local.x / frame.clip.z * 2. - 1., 1. - local.y / frame.clip.w * 2., 0., 1.);
    o.uv = uv; o.color = sprite.color; o.material = sprite.material;
    o.size = sprite.bounds.zw / max(frame.scene.w, .01); o.local = local.xy;
    return o;
}
fn hash(p: vec2<f32>) -> f32 {
    // Integer hash is stable on every WebGPU backend; no time-dependent seeds.
    let v = vec2<u32>(abs(p) * 71. + 19.);
    var x = v.x * 1664525u + v.y * 1013904223u + 1376312589u;
    x = (x ^ (x >> 16u)) * 2246822519u;
    return f32(x ^ (x >> 13u)) / 4294967295.;
}
// Continuous lattice noise: four integer-hash corners, cubic interpolation.
fn noise(p: vec2<f32>) -> f32 {
    let cell=floor(p); let f=fract(p); let w=f*f*(3.-2.*f);
    return mix(mix(hash(cell),hash(cell+vec2(1.,0.)),w.x),
        mix(hash(cell+vec2(0.,1.)),hash(cell+vec2(1.,1.)),w.x),w.y);
}
fn detail(p: vec2<f32>) -> f32 {
    let q=vec2(p.x*.80-p.y*.60,p.x*.60+p.y*.80);
    return noise(p)*.57+noise(q*2.13+17.)*.28+noise(q*4.47-9.)*.15;
}
fn stroke(p: vec2<f32>, a: vec2<f32>, b: vec2<f32>, width: f32) -> f32 {
    let ab=b-a; let t=clamp(dot(p-a,ab)/max(dot(ab,ab),.00001),0.,1.);
    return length(p-a-ab*t)-width;
}
fn merge_shape(a: f32,b: f32,k: f32) -> f32 {
    let h=clamp(.5+.5*(b-a)/k,0.,1.);
    return mix(b,a,h)-k*h*(1.-h);
}
// Returns distance to the nearest cell and separation from the second nearest.
fn stone_cells(p: vec2<f32>) -> vec2<f32> {
    let cell=floor(p); let f=fract(p); var first=9.; var second=9.;
    for(var y=-1;y<=1;y++) { for(var x=-1;x<=1;x++) {
        let offset=vec2(f32(x),f32(y)); let key=cell+offset;
        let point=offset+vec2(hash(key+41.),hash(key+137.))*.72+.14;
        let d=length(f-point);
        second=min(second,max(first,d)); first=min(first,d);
    }}
    return vec2(first,second-first);
}
fn coverage(d: f32) -> f32 { return clamp(.5 - d / max(fwidth(d), .0008), 0., 1.); }
fn ellipse(p: vec2<f32>, center: vec2<f32>, radius: vec2<f32>) -> f32 { return (length((p - center) / radius) - 1.) * min(radius.x, radius.y); }
fn rounded(p: vec2<f32>, c: vec2<f32>, b: vec2<f32>, r: f32) -> f32 {
    let q = abs(p - c) - b + r;
    return length(max(q, vec2(0.))) + min(max(q.x, q.y), 0.) - r;
}
fn over(back: vec4<f32>, front: vec4<f32>) -> vec4<f32> { return front + back * (1. - front.a); }
fn ink(rgb: vec3<f32>, alpha: f32) -> vec4<f32> { return vec4(rgb * alpha, alpha); }
fn paint(rgb: vec3<f32>, d: f32) -> vec4<f32> { return ink(rgb, coverage(d)); }
fn sphere(p: vec2<f32>, center: vec2<f32>, radius: vec2<f32>, rgb: vec3<f32>, gloss: f32) -> vec4<f32> {
    let q = (p - center) / radius;
    let d = (length(q) - 1.) * min(radius.x, radius.y);
    // Evaluate coverage before lighting. The derivative runs before any lane
    // returns, preserving edge helper quads; only exactly transparent lanes skip.
    let alpha=coverage(d);
    if(frame.occlusion.z>.5 && alpha==0.) { return vec4(0.); }
    let z = sqrt(max(0., 1. - dot(q,q)));
    let n = normalize(vec3(q * .8, max(z, .05)));
    let light = normalize(vec3(-.52, -.7, .85));
    let diffuse = max(0., dot(n, light));
    let rim = pow(1. - z, 3.) * .12;
    let spec = pow(max(0., dot(n, normalize(vec3(-.3,-.42,1.)))), 28.) * gloss;
    return ink(rgb * (.55 + .53 * diffuse) + vec3(1., .91, .68) * (spec + rim), alpha);
}
fn world() -> u32 { return u32(frame.scene.y+.5); }
fn foliage() -> vec3<f32> {
    switch world() {
        case 0u: { return vec3(.20,.31,.105); }
        case 1u: { return vec3(.31,.35,.17); }
        case 2u: { return vec3(.09,.32,.36); }
        case 3u: { return vec3(.13,.32,.25); }
        case 4u: { return vec3(.59,.245,.055); }
        case 5u: { return vec3(.23,.36,.37); }
        case 6u: { return vec3(.24,.19,.13); }
        default: { return vec3(.37,.43,.24); }
    }
}

fn sky(uv: vec2<f32>) -> vec4<f32> {
    var top=vec3(.15,.32,.40); var horizon=vec3(.68,.78,.74);
    switch world() {
        case 1u: {top=vec3(.29,.42,.49);horizon=vec3(.93,.73,.43);}
        case 2u: {top=vec3(.018,.035,.070);horizon=vec3(.07,.19,.24);}
        case 3u: {top=vec3(.14,.30,.38);horizon=vec3(.66,.79,.75);}
        case 4u: {top=vec3(.28,.32,.40);horizon=vec3(.91,.66,.38);}
        case 5u: {top=vec3(.15,.26,.41);horizon=vec3(.72,.81,.87);}
        case 6u: {top=vec3(.055,.045,.08);horizon=vec3(.42,.15,.07);}
        case 7u: {top=vec3(.27,.30,.52);horizon=vec3(.98,.76,.56);}
        default: {}
    }
    if(frame.occlusion.y>.5){top*=.18;horizon*=.25;}
    let sun=length((uv-vec2(.76,.19))*vec2(1.8,1.));
    var col=mix(top,horizon,pow(uv.y,.66));
    if(world()!=2u && frame.occlusion.y<.5){col+=vec3(.95,.66,.32)*exp(-sun*5.5)*.27;}
    if(world()!=2u && world()!=6u && frame.occlusion.y<.5) { col=mix(col,vec3(1.,.94,.73),1.-smoothstep(.031,.035,sun)); }
    let cirrus=pow(detail(uv*vec2(9.,43.)+vec2(11.,4.)),4.);
    col=mix(col,vec3(.80,.85,.82),cirrus*.42*(1.-uv.y));
    col+=(noise(uv*vec2(13.,7.))-.5)*.013;
    return vec4(col,1.);
}

fn mountain(p: vec2<f32>, layer: f32, seed: f32) -> vec4<f32> {
    let broad=pow(max(0.,sin(p.x*3.14159265)),.65);
    var ridge=.92-broad*(.52+noise(vec2(p.x*6.,seed*29.))*.30);
    if(world()==1u){ridge=max(.25,.93-broad*.90)+noise(vec2(p.x*18.,seed))*.055;}
    if(world()==5u || world()==6u){ridge=.9-broad*.75+abs(sin(p.x*17.+seed))*.065;}
    let alpha=coverage(ridge-p.y);
    if(frame.occlusion.z>.5 && alpha==0.) { return vec4(0.); }
    let folds=noise(vec2(p.x*23.,p.y*3.)+seed*19.);
    var tint=mix(vec3(.44,.53,.47),vec3(.24,.37,.31),layer*.34);
    switch world() {
        case 1u: {tint=mix(vec3(.66,.48,.29),vec3(.45,.32,.22),layer*.32);}
        case 2u: {tint=mix(vec3(.10,.21,.25),vec3(.045,.12,.17),layer*.32);}
        case 3u: {tint=mix(vec3(.34,.51,.53),vec3(.18,.35,.37),layer*.32);}
        case 4u: {tint=mix(vec3(.54,.43,.34),vec3(.28,.30,.24),layer*.32);}
        case 5u: {tint=mix(vec3(.51,.65,.76),vec3(.24,.37,.49),layer*.32);}
        case 6u: {tint=mix(vec3(.28,.15,.14),vec3(.115,.09,.11),layer*.32);}
        case 7u: {tint=mix(vec3(.58,.52,.65),vec3(.36,.39,.50),layer*.32);}
        default: {}
    }
    if(world()==5u){tint=mix(tint,vec3(.83,.89,.90),1.-smoothstep(ridge+.02,ridge+.11+folds*.07,p.y));}
    tint*=.87+folds*.20;
    tint=mix(tint,vec3(.63,.67,.57),smoothstep(.4,1.2,p.y)*.22);
    return ink(tint,alpha);
}

fn cloud(p: vec2<f32>) -> vec4<f32> {
    var d=ellipse(p,vec2(.24,.65),vec2(.22,.18));
    d=merge_shape(d,ellipse(p,vec2(.40,.46),vec2(.21,.28)),.095);
    d=merge_shape(d,ellipse(p,vec2(.59,.53),vec2(.24,.23)),.095);
    d=merge_shape(d,ellipse(p,vec2(.76,.67),vec2(.20,.14)),.10);
    let density=detail(p*vec2(12.,8.));
    d+=(density-.5)*.028;
    let alpha=(1.-smoothstep(-.025,.022,d))*.82;
    let volume=smoothstep(.18,.81,p.y+(.5-density)*.13);
    var col=mix(vec3(.94,.93,.83),vec3(.64,.72,.70),volume*.67);
    if(world()==6u){col=mix(vec3(.22,.17,.18),vec3(.11,.10,.13),volume);}
    return ink(col,alpha);
}

fn cliff(uv: vec2<f32>, size: vec2<f32>, seed: f32) -> vec4<f32> {
    let p=uv*size;
    let grain=detail(p*vec2(.033,.046)+seed*91.);
    let strata=noise(vec2(p.x*.016,p.y*.055+noise(vec2(p.x*.029,seed))*1.8));
    let warp=vec2(noise(p*.021),noise(p*.016+17.))*.16;
    var cellSize=vec2(81.,64.);
    if(world()==5u){cellSize=vec2(57.,151.);}
    if(world()==6u){cellSize=vec2(62.,140.);}
    if(world()==7u){cellSize=vec2(114.,47.);}
    let cell=stone_cells(p/cellSize+warp+seed*20.);
    var crack=1.-smoothstep(.018,.080,cell.y);
    if(world()==1u){crack*=.30;}
    var rock=mix(vec3(.19,.215,.22),vec3(.46,.46,.39),grain);
    switch world() {
        case 1u: {rock=mix(vec3(.47,.29,.15),vec3(.76,.58,.34),grain);}
        case 2u: {rock=mix(vec3(.075,.13,.17),vec3(.23,.32,.36),grain);}
        case 3u: {rock=mix(vec3(.17,.25,.25),vec3(.40,.48,.42),grain);}
        case 4u: {rock=mix(vec3(.24,.20,.16),vec3(.48,.39,.28),grain);}
        case 5u: {rock=mix(vec3(.14,.33,.47),vec3(.56,.73,.80),grain);}
        case 6u: {rock=mix(vec3(.085,.075,.08),vec3(.25,.205,.19),grain);}
        case 7u: {rock=mix(vec3(.44,.43,.46),vec3(.76,.73,.67),grain);}
        default: {}
    }
    rock*=.77+strata*.37;
    rock*=1.-crack*.22;
    // Height-derived normals bevel the fractured rock and retain physical-pixel AA.
    var height=smoothstep(.008,.17,cell.y)*5.+noise(p*.13)*.5;
    if(world()==1u){height=strata*3.+noise(p*vec2(.012,.20))*1.4;}
    if(world()==5u){height=smoothstep(.008,.17,cell.y)*3.+noise(p*vec2(.1,.007));}
    let gradient=vec2(dpdx(height)/max(length(dpdx(p)),.001),dpdy(height)/max(length(dpdy(p)),.001));
    let normal=normalize(vec3(-gradient,1.));
    let light=max(0.,dot(normal,normalize(vec3(-.45,-.65,.8))));
    rock*=.43+light*.82;
    rock+=(noise(p*.28)-.5)*.04+(hash(floor(p*1.4))-.5)*.019;
    rock+=vec3(.13,.115,.08)*smoothstep(.10,.31,cell.y)*(1.-cell.x)*.35;
    rock*=(.64+.36*smoothstep(0.,27.,p.x))*(.62+.38*smoothstep(0.,29.,size.x-p.x));
    rock*=mix(.61,1.,exp(-p.y/480.));
    let soilDepth=35.+noise(vec2(p.x*.035,seed))*22.;
    let rootWave=sin(p.x*.055+noise(p*.026)*5.+p.y*.045);
    let roots=(1.-smoothstep(.03,.12,abs(rootWave)))*(1.-smoothstep(24.,107.,p.y));
    var soil=mix(vec3(.21,.15,.09),vec3(.38,.29,.16),grain)-vec3(.07)*roots;
    if(world()==1u){soil=mix(vec3(.50,.34,.17),vec3(.74,.57,.32),grain);}
    if(world()==2u || world()==6u){soil=rock*.75;}
    if(world()==5u){soil=mix(vec3(.59,.72,.78),vec3(.87,.92,.93),grain);}
    if(world()==7u){soil=mix(vec3(.56,.54,.44),vec3(.77,.74,.58),grain);}
    rock=mix(rock,soil,1.-smoothstep(soilDepth-10.,soilDepth+10.,p.y));
    let mossDepth=12.+noise(vec2(p.x*.11,seed))*14.;
    let blade=noise(vec2(p.x*.35,p.y*.12));
    var grass=foliage()*(.62+grain*.70+blade*.22);
    if(world()==1u){grass=soil*1.13;}
    if(world()==2u || world()==6u){grass=rock*1.13;}
    if(world()==5u){grass=vec3(.87,.93,.95)*( .92+blade*.12);}
    if(world()==6u){rock+=vec3(.83,.15,.014)*pow(crack,4.)*smoothstep(45.,240.,p.y)*(.4+grain*.6);}
    rock=mix(rock,grass,1.-smoothstep(mossDepth-3.,mossDepth+3.,p.y));
    rock+=vec3(.19,.19,.075)*exp(-pow((p.y-6.)/4.,2.))*(.45+blade*.55);
    let top=2.+noise(vec2(p.x*.31,seed+7.))*4.;
    let side=max(1.8+noise(vec2(p.y*.14,seed))*2.-p.x, p.x-size.x+2.);
    let boundary=max(side,max(top-p.y,p.y-size.y+2.));
    return paint(rock,boundary);
}

fn canopy(p: vec2<f32>, center: vec2<f32>, radius: vec2<f32>, tint: vec3<f32>, leaf: f32, edgeNoise: f32) -> vec4<f32> {
    let q=(p-center)/radius;
    let d=(length(q)-1.)*min(radius.x,radius.y)+(edgeNoise-.5)*.034;
    let alpha=coverage(d);
    if(frame.occlusion.z>.5 && alpha==0.) { return vec4(0.); }
    let z=sqrt(max(.025,1.-dot(q,q)*.83));
    let normal=normalize(vec3(q*vec2(.63,.75),z));
    let lit=max(0.,dot(normal,normalize(vec3(-.52,-.72,.72))));
    let scattering=pow(1.-z,2.)*max(0.,-.3-q.x-q.y)*.17;
    var color=tint*(.28+lit*.95)*(.84+leaf*.30);
    color+=vec3(.28,.29,.09)*scattering;
    return ink(color,alpha);
}
fn tree(p0: vec2<f32>, seed: f32) -> vec4<f32> {
    var p=p0; p.x+=sin(frame.scene.x*.8+seed*8.)*.003*(1.-p.y);
    let curve=.50+sin(p.y*8.+seed*3.)*.022;
    let trunk=abs(p.x-curve)-(.013+p.y*.022);
    let bark=noise(vec2(p.x*180.,p.y*11.)+seed*29.);
    let barkCol=mix(vec3(.14,.115,.075),vec3(.33,.27,.16),bark)*(.77+p.x*.30);
    var c=paint(barkCol,max(trunk,max(.24-p.y,p.y-.98)));
    for(var i=0;i<7;i++) {
        let t=f32(i)/7.; let side=select(-1.,1.,i%2==0);
        let a=vec2(.50,.68-t*.30);
        let b=vec2(.50+side*(.14+t*.11),.42-t*.21);
        c=over(c,paint(barkCol,stroke(p,a,b,.012-t*.006)));
    }
    let coarse=detail(p*vec2(19.,23.)+seed*43.);
    let leafMass=noise(p*vec2(89.,101.)+seed*71.);
    // Overlapping rough canopy volumes supply occlusion and varied branch silhouettes.
    // Their continuous shared microtexture prevents the lacquered-ball appearance.
    for(var i=0;i<7;i++) {
        let a=f32(i)*2.39996+seed*2.;
        let r=select(.19,.105,i>3);
        let center=vec2(.50+cos(a)*r,.31+sin(a)*r*.79);
        let radius=vec2(.22+hash(vec2(f32(i),seed*71.))*.045,.175);
        let tint=foliage()*(.84+f32(i)*.044);
        c=over(c,canopy(p,center,radius,tint,leafMass,coarse));
    }
    return c;
}

fn bush(p: vec2<f32>) -> vec4<f32> {
    let leaf=noise(p*97.); let edge=detail(p*17.);
    var c=canopy(p,vec2(.24,.70),vec2(.23,.25),foliage()*.80,leaf,edge);
    c=over(c,canopy(p,vec2(.68,.65),vec2(.29,.28),foliage(),leaf,edge));
    return over(c,canopy(p,vec2(.45,.54),vec2(.27,.34),foliage()*1.10,leaf,edge));
}

fn flower(p: vec2<f32>, seed: f32) -> vec4<f32> {
    let sway=sin(frame.scene.x*2.+seed*20.)*.06;
    var q=p; q.x-=sway*(1.-q.y);
    var c=paint(foliage()*.65,rounded(q,vec2(.5,.68),vec2(.025,.30),.02));
    c=over(c,sphere(q,vec2(.34,.66),vec2(.18,.065),foliage(),0.));
    let petal=mix(vec3(1.,.76,.25),vec3(1.,.88,.80),step(.6,seed));
    for(var i=0;i<5;i++) {
        let a=f32(i)*1.256637;
        c=over(c,sphere(q,vec2(.5,.29)+vec2(cos(a),sin(a))*.16,vec2(.12,.10),petal,.12));
    }
    return over(c,sphere(q,vec2(.5,.29),vec2(.105,.08),vec3(.50,.25,.08),.25));
}
fn wooden_crate(p: vec2<f32>) -> vec4<f32> {
    let edge=rounded(p,vec2(.5),vec2(.46),.028);
    let grain=noise(p*vec2(82.,5.)+noise(p*vec2(13.,3.))*4.);
    let fine=sin(p.x*237.+noise(p*19.)*3.)*.025;
    let joint=1.-smoothstep(.0,.025,abs(fract(p.x*4.)-.5));
    var col=mix(vec3(.30,.19,.085),vec3(.56,.38,.18),grain)-vec3(joint*.07)+fine;
    col*=.95-p.y*.22;
    let inset=rounded(p,vec2(.5),vec2(.335),.018);
    col*=1.-coverage(inset)*.20;
    let diagonal=abs(p.x-p.y);
    col=mix(col,col*1.21,1.-smoothstep(.049,.065,diagonal));
    col+=vec3(.10,.08,.04)*(1.-smoothstep(.04,.072,p.y));
    var c=paint(col,edge);
    c=over(c,sphere(p,vec2(.13,.13),vec2(.021),vec3(.23,.24,.21),.18));
    c=over(c,sphere(p,vec2(.87,.87),vec2(.021),vec3(.23,.24,.21),.18));
    return c;
}

fn coin(p0: vec2<f32>, phase: f32, relic: f32) -> vec4<f32> {
    var p=p0;
    p.y-=sin(frame.scene.x*2.6+phase)*.07;
    let w=.30*max(.25,abs(cos(frame.scene.x*2.+phase)));
    let col=select(vec3(1.,.68,.12),vec3(.42,1.,.87),relic>.5);
    var c=ink(col,exp(-dot((p-vec2(.5))*3.,(p-vec2(.5))*3.))*.10);
    c=over(c,sphere(p,vec2(.5),vec2(w,.36),col,.85));
    let q=(p-vec2(.5))/vec2(w,.36);
    let ring=abs(length(q)-.69)-.045;
    c=over(c,paint(col*.62,ring*.2));
    c=over(c,paint(vec3(1.,.95,.58),rounded(p,vec2(.5),vec2(w*.11,.15),.014)));
    return c;
}
fn fur_surface(p: vec2<f32>, center: vec2<f32>, radius: vec2<f32>, color: vec3<f32>) -> vec4<f32> {
    var c=sphere(p,center,radius,color,.018);
    let q=(p-center)/radius;
    let direction=atan2(q.y,q.x)*9.;
    let fibers=sin(direction+length(q)*53.+noise(p*37.)*7.);
    let undercoat=noise(p*103.);
    c=vec4(c.rgb*(.94+undercoat*.08+fibers*.015),c.a);
    return c;
}
fn courier(p0: vec2<f32>, facing: f32, stride: f32) -> vec4<f32> {
    var p=p0; if(facing<0.){p.x=1.-p.x;}
    let step=sin(frame.scene.x*15.)*max(0.,stride)*.055;
    let bob=abs(cos(frame.scene.x*15.))*max(0.,stride)*.015;
    p.y+=bob;
    let fur=vec3(.76,.35,.095); let cream=vec3(.88,.79,.59);
    var c=fur_surface(p,vec2(.24,.61),vec2(.19,.14),fur);
    c=over(c,fur_surface(p,vec2(.115,.58),vec2(.085,.09),cream));
    c=over(c,sphere(p,vec2(.43,.59),vec2(.135,.17),vec3(.13,.32,.30),.2));
    c=over(c,sphere(p,vec2(.48,.79+step),vec2(.095,.08),vec3(.23,.18,.14),.2));
    c=over(c,sphere(p,vec2(.67,.79-step),vec2(.10,.08),vec3(.26,.20,.15),.2));
    c=over(c,fur_surface(p,vec2(.57,.60),vec2(.145,.19),fur));
    c=over(c,fur_surface(p,vec2(.62,.62),vec2(.084,.12),cream));
    c=over(c,fur_surface(p,vec2(.45,.255),vec2(.07,.16),fur));
    c=over(c,fur_surface(p,vec2(.68,.24),vec2(.07,.15),fur));
    c=over(c,sphere(p,vec2(.45,.255),vec2(.032,.095),vec3(.37,.20,.18),0.));
    c=over(c,sphere(p,vec2(.68,.24),vec2(.032,.095),vec3(.37,.20,.18),0.));
    c=over(c,fur_surface(p,vec2(.56,.39),vec2(.205,.165),fur));
    c=over(c,fur_surface(p,vec2(.65,.45),vec2(.15,.095),cream));
    c=over(c,sphere(p,vec2(.52,.365),vec2(.028,.040),vec3(.10,.12,.14),.5));
    c=over(c,sphere(p,vec2(.68,.365),vec2(.025,.039),vec3(.10,.12,.14),.5));
    c=over(c,sphere(p,vec2(.52,.348),vec2(.012,.017),vec3(1.),.1));
    c=over(c,sphere(p,vec2(.68,.348),vec2(.011,.016),vec3(1.),.1));
    c=over(c,sphere(p,vec2(.765,.426),vec2(.036,.027),vec3(.12,.13,.15),.7));
    // Teal scarf and wind-swept tail form the recognizable silhouette.
    c=over(c,paint(vec3(.12,.34,.31),rounded(p,vec2(.565,.526),vec2(.14,.031),.025)));
    c=over(c,sphere(p,vec2(.38,.53+sin(frame.scene.x*8.)*.02),vec2(.12,.04),vec3(.14,.39,.35),.1));
    c=over(c,fur_surface(p,vec2(.73,.62-step),vec2(.055,.085),fur));
    return c;
}
fn beetle(p0: vec2<f32>, facing: f32) -> vec4<f32> {
    var p=p0;if(facing<0.){p.x=1.-p.x;}
    var c=vec4(0.);
    for(var i=0;i<3;i++) {
        let x=.25+f32(i)*.18;
        let walk=sin(frame.scene.x*13.+f32(i)*1.8)*.028;
        c=over(c,paint(vec3(.10,.085,.065),stroke(p,vec2(x,.57),vec2(x-.07,.88+walk),.018)));
        c=over(c,paint(vec3(.14,.12,.08),stroke(p,vec2(x+.02,.60),vec2(x+.13,.87-walk),.020)));
    }
    let shell=vec3(.30,.115,.065)*( .93+noise(p*72.)*.12);
    c=over(c,sphere(p,vec2(.44,.50),vec2(.35,.31),shell,.24));
    c=over(c,paint(vec3(.115,.095,.05),stroke(p,vec2(.46,.21),vec2(.48,.73),.006)));
    c=over(c,sphere(p,vec2(.72,.62),vec2(.16,.17),vec3(.115,.13,.085),.11));
    c=over(c,paint(vec3(.13,.13,.075),stroke(p,vec2(.75,.50),vec2(.83,.30),.008)));
    c=over(c,paint(vec3(.13,.13,.075),stroke(p,vec2(.81,.54),vec2(.95,.40),.008)));
    c=over(c,sphere(p,vec2(.77,.575),vec2(.021,.027),vec3(.027,.036,.03),.40));
    c=over(c,sphere(p,vec2(.84,.62),vec2(.018,.023),vec3(.027,.036,.03),.40));
    return c;
}

fn lantern(p: vec2<f32>, lit: f32) -> vec4<f32> {
    var c=paint(vec3(.30,.25,.18),rounded(p,vec2(.35,.62),vec2(.038,.34),.015));
    c=over(c,paint(vec3(.52,.37,.19),rounded(p,vec2(.53,.20),vec2(.22,.027),.014)));
    let glow=select(vec3(.96,.57,.13),vec3(.48,1.,.69),lit>.5);
    c=over(c,ink(glow,exp(-dot((p-vec2(.64,.33))*5.,(p-vec2(.64,.33))*5.))*.28));
    c=over(c,paint(vec3(.26,.25,.20),rounded(p,vec2(.64,.35),vec2(.16,.18),.025)));
    c=over(c,sphere(p,vec2(.64,.35),vec2(.117,.135),glow,.55));
    return c;
}
fn portal(p: vec2<f32>) -> vec4<f32> {
    let q=(p-vec2(.5,.50))/vec2(.36,.45);
    let outer=length(q)-1.;
    var c=ink(vec3(1.,.65,.16),exp(-dot(q,q)*1.8)*.3);
    let ring=abs(outer)-.105;
    let grain=detail(p*39.);
    c=over(c,paint(mix(vec3(.24,.255,.22),vec3(.60,.55,.38),grain)*(.75+.25*(1.-p.x)),ring*.25));
    c=over(c,paint(vec3(1.,.89,.38),(abs(outer+.045)-.016)*.25));
    let inside=coverage((length(q)-.79)*.25);
    let swirl=sin(length(q)*21.-frame.scene.x*2.5+atan2(q.y,q.x)*3.+detail(q*4.+frame.scene.x*.1)*4.);
    c=over(c,ink(mix(vec3(.16,.49,.43),vec3(.89,.87,.47),swirl*.25+.4),inside*.68));
    c=over(c,paint(vec3(.47,.36,.22),rounded(p,vec2(.5,.95),vec2(.42,.042),.018)));
    for(var i=0;i<6;i++) {
        let a=f32(i)*1.0472+frame.scene.x*.55;
        c=over(c,sphere(p,vec2(.5,.5)+vec2(cos(a)*.31,sin(a)*.39),vec2(.014),vec3(1.,.96,.66),.3));
    }
    return c;
}
fn ledge(p: vec2<f32>, size: vec2<f32>) -> vec4<f32> {
    let q=p*size; let n=detail(q*.078);
    let d=rounded(q,size*.5,size*.5-vec2(1.),4.)+(n-.5)*1.7;
    var col=mix(vec3(.23,.24,.19),vec3(.47,.44,.31),n)*(.94-p.y*.29);
    var cap=foliage()*(.83+n*.47);
    switch world() {
        case 1u: {col=mix(vec3(.40,.27,.15),vec3(.68,.49,.28),n);cap=col*1.2;}
        case 2u: {col=mix(vec3(.10,.22,.27),vec3(.24,.39,.43),n);cap=vec3(.23,.49,.48);}
        case 5u: {col=mix(vec3(.20,.40,.55),vec3(.49,.65,.75),n);cap=vec3(.87,.93,.94);}
        case 6u: {col=mix(vec3(.10,.08,.09),vec3(.32,.23,.16),n);cap=col*1.25;}
        case 7u: {col=mix(vec3(.47,.46,.45),vec3(.77,.73,.64),n);cap=vec3(.80,.80,.65);}
        default: {}
    }
    col=mix(col,cap,1.-smoothstep(.17,.42,p.y));
    col+=vec3(.10,.105,.04)*exp(-pow((p.y-.11)/.055,2.));
    return paint(col,d);
}

fn thorns(p: vec2<f32>, size: vec2<f32>) -> vec4<f32> {
    let x=fract(p.x*size.x/22.);
    let d=abs(x-.5)-p.y*.48;
    return paint(mix(vec3(.92,.77,.61),vec3(.37,.29,.34),p.y),max(d,.05-p.y));
}
fn mushroom(p: vec2<f32>) -> vec4<f32> {
    var c=sphere(p,vec2(.5,.69),vec2(.11,.27),vec3(.69,.62,.43),.035);
    var cap=sphere(p,vec2(.5,.36),vec2(.43,.30),vec3(.54,.24,.095),.05);
    cap*=coverage(p.y-.57);
    c=over(c,cap);
    c=over(c,sphere(p,vec2(.35,.31),vec2(.055,.04),vec3(1.,.86,.56),.1));
    c=over(c,sphere(p,vec2(.62,.26),vec2(.07,.048),vec3(1.,.86,.56),.1));
    return c;
}
fn ruin(p: vec2<f32>) -> vec4<f32> {
    let outer=rounded(p,vec2(.5,.65),vec2(.36,.34),.21);
    let opening=rounded(p,vec2(.5,.74),vec2(.18,.32),.16);
    let grain=detail(p*vec2(25.,32.));
    let d=max(outer,-opening)+(grain-.5)*.014;
    let masonry=noise(vec2(p.x*9.,floor(p.y*12.)));
    var col=mix(vec3(.29,.33,.28),vec3(.54,.54,.41),grain)*(.80+.2*masonry);
    col=mix(col,foliage()*.65,smoothstep(.55,.77,noise(p*8.))*.65);
    return paint(col,d);
}

fn shafts(p: vec2<f32>) -> vec4<f32> {
    let diagonal=p.x-.76+p.y*.31;
    let first=exp(-pow((diagonal+.04)/.031,2.));
    let second=exp(-pow((diagonal-.13)/.048,2.));
    let third=exp(-pow((diagonal+.19)/.025,2.));
    let density=.6+.4*noise(p*vec2(4.,7.)+vec2(frame.scene.x*.012,0.));
    let alpha=(first+second*.7+third*.5)*density*.039*(1.-p.y*.62);
    return ink(vec3(.98,.82,.49),alpha*select(1.,.12,world()==2u || world()==6u || frame.occlusion.y>.5));
}
fn fern(p: vec2<f32>, seed: f32) -> vec4<f32> {
    var c=vec4(0.);
    // Two arching fronds. Leaflets follow the local tangent instead of horizontal bars.
    for(var frond=0;frond<2;frond++) {
        let side=select(-1.,1.,frond==1); let root=vec2(.49,.96);
        var previous=root;
        for(var i=0;i<6;i++) {
            let t=f32(i+1)/6.;
            let a=root+vec2(side*(t*.29+t*t*.065),-t*.78+t*t*.18);
            c=over(c,paint(foliage()*.76,stroke(p,previous,a,.0045)));
            let tangent=normalize(a-previous);let cross=vec2(-tangent.y,tangent.x);
            let span=.13*(1.-t*.69);
            for(var j=0;j<2;j++) {
                let sign=select(-1.,1.,j==1);
                let axis=normalize(cross*sign+tangent*.64);
                let center=a+axis*span*.48; let delta=p-center;
                let local=vec2(dot(delta,axis),dot(delta,vec2(-axis.y,axis.x)));
                c=over(c,paint(foliage()*(.84+t*.32),ellipse(local,vec2(0.),vec2(span*.62,.014*(1.-t*.5)))));
            }
            previous=a;
        }
    }
    return c;
}
fn grass(p: vec2<f32>, seed: f32) -> vec4<f32> {
    var c=vec4(0.);
    for(var i=0;i<6;i++) {
        let t=f32(i); let h=hash(vec2(t,seed*111.));
        let root=vec2(.12+t*.145,.94);
        let tip=vec2(root.x+(h-.5)*.26+sin(frame.scene.x*.9+seed*7.+t)*.025,.20+h*.45);
        let q=p-root;let axis=tip-root;let along=clamp(dot(q,axis)/dot(axis,axis),0.,1.);
        let d=stroke(p,root,tip,.014*(1.-along)+.0015);
        c=over(c,paint(foliage()*(.85+h*.55),d));
    }
    return c;
}

// Original world landmarks: faceted minerals, palm fronds, snow boughs, and eroded pillars.
fn crystal(p: vec2<f32>, seed: f32) -> vec4<f32> {
    var c=vec4(0.);
    for(var i=0;i<3;i++) {
        let fi=f32(i);let center=.26+fi*.23;let h=.40+hash(vec2(fi,seed*91.))*.44;
        let q=vec2(p.x-center+(p.y-.9)*(fi-1.)*.14,p.y);
        let width=.105; let top=.92-h;
        let tip=top+abs(q.x)*1.7;
        let d=max(abs(q.x)-width,max(tip-q.y,q.y-.94));
        var col=mix(vec3(.035,.20,.29),vec3(.20,.67,.69),step(0.,q.x));
        if(world()==6u){col=mix(vec3(.10,.065,.065),vec3(.36,.14,.055),step(0.,q.x));}
        col*=.83+noise(p*53.)*.19;
        col+=vec3(.20,.37,.33)*(1.-smoothstep(.0,.007,abs(q.x)));
        col+=vec3(.17,.40,.43)*pow(max(0.,1.-(q.y-top)/h),3.);
        c=over(c,paint(col,d));
    }
    return c;
}
fn palm(p: vec2<f32>, seed: f32) -> vec4<f32> {
    let curve=.47+pow(1.-p.y,2.)*.12;
    let rings=sin(p.y*123.+seed)*.055;
    var c=paint(vec3(.30,.245,.15)*( .82+rings+noise(p*61.)*.23),
        max(abs(p.x-curve)-(.013+p.y*.018),max(.25-p.y,p.y-.98)));
    for(var i=0;i<8;i++) {
        let a=f32(i)*.73+.20;let tip=vec2(.56+cos(a)*.41,.30+sin(a)*.17);
        let root=vec2(.56,.29);let axis=tip-root;let t=clamp(dot(p-root,axis)/dot(axis,axis),0.,1.);
        let arch=vec2(root.x+axis.x*t,root.y+axis.y*t-sin(t*3.14159)*.10);
        let serration=.75+.25*abs(sin(t*54.));
        let d=length(p-arch)-sin(t*3.14159)*.042*serration;
        c=over(c,paint(foliage()*(.70+f32(i)*.055),d));
    }
    return c;
}
fn pine(p: vec2<f32>, seed: f32) -> vec4<f32> {
    var c=paint(vec3(.20,.185,.14),rounded(p,vec2(.5,.63),vec2(.018,.35),.009));
    let grain=noise(p*95.+seed);
    for(var i=0;i<6;i++) {
        let t=f32(i)/6.; let top=.09+t*.58;let width=.10+t*.28;
        let edge=abs(p.x-.5)-clamp((p.y-top)*1.27,0.,width);
        let bottom=top+.24;
        let d=max(edge,max(top-p.y,p.y-bottom))+(grain-.5)*.012;
        var color=foliage()*(.55+grain*.29+(.5-p.x)*.6);
        let snow=1.-smoothstep(.009,.041,p.y-top-abs(p.x-.5)*.78);
        if(world()==5u){color=mix(color,vec3(.76,.87,.91)*( .92+grain*.12),snow);}
        c=over(c,paint(color,d));
    }
    return c;
}
fn spire(p: vec2<f32>, seed: f32) -> vec4<f32> {
    let q=p-vec2(.5,.52);let taper=.18+(p.y*.14);
    let d=max(abs(q.x)-taper,max(.09+abs(q.x)*.55-p.y,p.y-.98));
    let grain=detail(p*vec2(22.,31.)+seed*19.);
    var col=mix(vec3(.105,.09,.10),vec3(.28,.24,.21),grain);
    if(world()==7u){col=mix(vec3(.53,.52,.55),vec3(.87,.83,.71),grain);}
    col*=select(.68,1.13,p.x<.49);
    let flute=pow(abs(sin(p.x*49.+noise(p*8.)*.2)),14.);
    col*=1.-flute*.20;
    return paint(col,d+(grain-.5)*.017);
}
fn water(p: vec2<f32>) -> vec4<f32> {
    let t=frame.scene.x;let scale=vec2(frame.clip.z/max(frame.scene.w,.01)*.011,14.);
    let n=detail(p*scale+vec2(t*.13,-t*.07));
    let ripple=pow(max(0.,sin(p.y*130.+n*8.+t)),12.);
    var col=mix(vec3(.08,.22,.27),vec3(.32,.49,.48),n)+vec3(.20,.23,.19)*ripple*.4;
    if(world()==6u){col=mix(vec3(.16,.04,.023),vec3(.70,.19,.02),n)+vec3(1.,.46,.035)*ripple*.5;}
    // Horizontal distant sea only. Never extend a bright vertical band under an island.
    return ink(col,smoothstep(.0,.13,p.y)*.85);
}
fn cavern(p: vec2<f32>, size: vec2<f32>) -> vec4<f32> {
    let x=p.x*size.x+frame.scene.z*.3;
    let edge=.42+noise(vec2(x*.005,11.))*.35+pow(abs(sin(x*.019)),12.)*.18;
    let grain=detail(vec2(x*.027,p.y*21.));
    let col=mix(vec3(.026,.055,.078),vec3(.10,.16,.19),grain)*( .65+p.y*.35);
    return paint(col,p.y-edge);
}

// Glazed copper conduit: rounded rim, recessed opening, cylindrical highlight and
// deterministic oxidation. One analytic cylinder plus three bounded shape layers.
fn pipe_art(p: vec2<f32>) -> vec4<f32> {
    let body=rounded(p,vec2(.5,.57),vec2(.36,.42),.025);
    let cylinder=sqrt(max(0.,1.-pow((p.x-.5)/.38,2.)));
    let patina=detail(p*vec2(17.,9.));
    var metal=mix(vec3(.08,.23,.19),vec3(.25,.51,.36),cylinder);
    metal=mix(metal,vec3(.34,.25,.11),smoothstep(.55,.82,patina)*.55);
    metal+=vec3(.65,.73,.44)*pow(max(0.,1.-abs(p.x-.32)*8.),12.)*.45;
    var c=paint(metal,body);
    c=over(c,paint(metal*1.17,rounded(p,vec2(.5,.16),vec2(.46,.105),.025)));
    c=over(c,paint(vec3(.018,.05,.04),ellipse(p,vec2(.5,.08),vec2(.39,.044))));
    c=over(c,paint(vec3(.49,.61,.32),abs(p.y-.25)-.007));
    return c;
}

// Original bounded clockwork hazards. Phase comes from the simulation, so visual
// flame activation/drop warnings and collision use the same deterministic clock.
fn saw_art(p: vec2<f32>) -> vec4<f32> {
    let q=p-.5; let r=length(q);
    let angle=atan2(q.y,q.x)+frame.scene.x*3.;
    let teeth=.38+.07*smoothstep(-.4,.5,sin(angle*14.));
    let steel=mix(vec3(.20,.26,.28),vec3(.76,.80,.70),.5+.5*sin(angle*2.));
    var c=paint(steel,r-teeth);
    c=over(c,paint(vec3(.11,.16,.16),abs(r-.28)-.025));
    c=over(c,sphere(p,vec2(.5),vec2(.10),vec3(.53,.31,.09),.6));
    return c;
}
fn flame_jet(p: vec2<f32>, phase: f32, emitting: f32) -> vec4<f32> {
    var c=paint(vec3(.23,.24,.20),rounded(p,vec2(.5,.93),vec2(.47,.06),.03));
    let warning=smoothstep(.16,.30,phase)*(1.-step(.64,phase));
    if(emitting>.5) {
        let sway=sin(p.y*15.-frame.scene.x*14.)*.08*(1.-p.y);
        let edge=abs(p.x-.5-sway)-(.055+p.y*.29);
        let d=max(edge,.025-p.y);
        let heat=clamp((.35-abs(p.x-.5))*3.,0.,1.);
        c=over(c,paint(mix(vec3(1.,.19,.015),vec3(1.,.93,.43),heat),d));
    }
    c=over(c,paint(vec3(1.,.36+.4*warning,.04)*(.25+.75*warning),ellipse(p,vec2(.5,.91),vec2(.31,.023))));
    return c;
}
fn crusher_art(p: vec2<f32>, phase: f32) -> vec4<f32> {
    let d=rounded(p,vec2(.5),vec2(.44,.47),.045);
    let grain=detail(p*vec2(14.,18.));
    var c=paint(mix(vec3(.17,.20,.23),vec3(.41,.43,.39),grain)*(.6+.4*(1.-p.x)),d);
    let edge=min(min(p.x,1.-p.x),min(p.y,1.-p.y));
    c=over(c,paint(vec3(.53,.44,.25),max(d,abs(edge-.10)-.018)));
    let warning=select(.2,1.,phase>.40 && phase<.75);
    c=over(c,paint(vec3(1.,.37,.08)*warning,ellipse(p,vec2(.34,.39),vec2(.08,.045))));
    c=over(c,paint(vec3(1.,.37,.08)*warning,ellipse(p,vec2(.66,.39),vec2(.08,.045))));
    return c;
}

fn shade(v: Varying, kind: u32) -> vec4<f32> {
    let p=v.uv;
    // Opaque terrain is drawn later. Skip only fragments deep inside its interior;
    // all edge helper quads and every gap still execute the original artwork.
    let backdrop=kind==0u || kind==14u || kind==15u || kind==16u || kind==27u || kind==28u ||
        ((kind==2u || kind==23u || kind==24u || kind==25u || kind==26u) && v.material.z>.5);
    if(backdrop) {
        for(var i=0u;i<u32(frame.occlusion.x);i++) {
            let rect=frame.ground[i];
            if(all(v.local>rect.xy) && all(v.local<rect.zw)){return vec4(0.);}
        }
    }
    var c=vec4(0.);
    switch kind {
        case 0u: {c=sky(p);}
        case 1u: {c=cliff(p,v.size,v.material.y);}
        case 2u: {
            c=tree(p,v.material.y);
            if(v.material.z>.5) {
                var haze=vec3(.44,.56,.51);
                if(world()==4u){haze=vec3(.58,.44,.32);}
                c=vec4(mix(c.rgb,haze*c.a,.42),c.a);
            }
        }
        case 3u: {c=bush(p);}
        case 4u: {c=flower(p,v.material.y);}
        case 5u: {c=wooden_crate(p);}
        case 6u: {c=coin(p,v.material.y,v.material.z);}
        case 7u: {c=courier(p,v.material.y,v.material.z);}
        case 8u: {c=beetle(p,v.material.y);}
        case 9u: {c=lantern(p,v.material.y);}
        case 10u: {c=portal(p);}
        case 11u: {c=ledge(p,v.size);}
        case 12u: {c=thorns(p,v.size);}
        case 13u: {
            let r=length((p-.5)*2.);
            var col=vec3(.54,.46,.31); var alpha=exp(-r*r*5.)*.32*(1.-smoothstep(.65,1.,r));
            if(v.material.y>.5){col=vec3(1.,.68,.22);alpha=exp(-r*r*7.)*.50+exp(-r*r*130.)*.7;}
            if(v.material.y>2.5){col=vec3(.90,.96,1.);alpha=coverage(r-.4);}
            if(v.material.y>3.5){col=vec3(.66,.29,.055);alpha=coverage(ellipse(p,vec2(.5),vec2(.34,.15)));}
            c=ink(col,min(1.,alpha));
        }
        case 14u: {c=cloud(p);}
        case 15u: {c=mountain(p,v.material.y,v.material.z);}
        case 16u: {c=ruin(p);}
        case 17u: {c=mushroom(p);}
        case 19u: {c=ink(vec3(.05,.12,.10),pow(max(0.,1.-length((p-.5)*2.)),1.3)*.7);}
        case 20u: {c=shafts(p);}
        case 21u: {c=fern(p,v.material.y);}
        case 22u: {c=grass(p,v.material.y);}
        case 23u: {c=crystal(p,v.material.y);}
        case 24u: {c=palm(p,v.material.y);}
        case 25u: {c=pine(p,v.material.y);}
        case 26u: {c=spire(p,v.material.y);}
        case 27u: {c=water(p);}
        case 28u: {c=cavern(p,v.size);}
        case 29u: {c=pipe_art(p);}
        case 30u: {c=saw_art(p);}
        case 31u: {c=flame_jet(p,v.material.y,v.material.z);}
        case 32u: {c=crusher_art(p,v.material.y);}
        default: {}
    }
    // Three bounded world-space emitters; no per-sprite lights or native crossings.
    if(kind==1u || kind==3u || kind==5u || kind==7u || kind==8u || kind==11u || kind==23u) {
        var illumination=vec3(0.);
        for(var i=0;i<3;i++) {
            let light=frame.lights[i]; let delta=(v.local-light.xy)/light.z;
            let falloff=max(0.,1.-dot(delta,delta));
            illumination+=vec3(.70,.43,.14)*falloff*falloff*light.w;
        }
        c=vec4(c.rgb+illumination*c.a,c.a);
    }
    if(any(v.local<frame.clip.xy)||any(v.local>frame.clip.zw)){discard;}
    let screen=v.local/frame.clip.zw;
    let vignette=1.-dot((screen-.5)*vec2(.45,.25),(screen-.5)*vec2(.45,.25));
    return vec4(c.rgb*v.color.rgb*v.color.a*vignette,c.a*v.color.a);
}

// Constant entry points let the backend eliminate unrelated material branches.
// Identical functions, derivatives and compositing; no lower-detail mobile variant.
@fragment fn fs_main(v: Varying) -> @location(0) vec4<f32> { return shade(v,u32(v.material.x+.5)); }
@fragment fn fs_sky(v: Varying) -> @location(0) vec4<f32> { return shade(v,0u); }
@fragment fn fs_cliff(v: Varying) -> @location(0) vec4<f32> { return shade(v,1u); }
@fragment fn fs_mountain(v: Varying) -> @location(0) vec4<f32> { return shade(v,15u); }
@fragment fn fs_tree(v: Varying) -> @location(0) vec4<f32> { return shade(v,2u); }
@fragment fn fs_shafts(v: Varying) -> @location(0) vec4<f32> { return shade(v,20u); }

// Bake uses fs_sky with occlusion disabled. Replay restores current occlusion and
// loads the identical physical pixel: no filtering, quantization, or temporal reuse
// of changing effects. World, room, dimensions, DPI and tint invalidate the image.
@group(1) @binding(0) var retained_sky: texture_2d<f32>;
@fragment fn fs_sky_cached(v: Varying) -> @location(0) vec4<f32> {
    for(var i=0u;i<u32(frame.occlusion.x);i++) {
        let rect=frame.ground[i];
        if(all(v.local>rect.xy) && all(v.local<rect.zw)){return vec4(0.);}
    }
    if(any(v.local<frame.clip.xy)||any(v.local>frame.clip.zw)){discard;}
    let extent=vec2<i32>(textureDimensions(retained_sky));
    let texel=clamp(vec2<i32>(v.uv*vec2<f32>(extent)),vec2<i32>(0),extent-1);
    return textureLoad(retained_sky,texel,0);
}
