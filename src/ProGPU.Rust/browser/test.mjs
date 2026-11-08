import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
const tools = path.resolve(process.env.PROGPU_RUST_BROWSER_TOOLS ?? 'artifacts/progpu-rust/browser-tools');
const { chromium } = createRequire(path.join(tools, 'package.json'))('playwright');
const { PNG } = createRequire(path.join(tools, 'package.json'))('pngjs');
const output = path.resolve(process.env.PROGPU_RUST_BROWSER_RESULTS ?? 'artifacts/progpu-rust/browser-results');
await mkdir(output, { recursive: true });
// A failed rerun must not leave a previous successful receipt or screenshot.
await Promise.all(['results.json', 'canvas-dpr1.png', 'canvas-dpr2.png'].map(
  name => rm(path.join(output, name), { force: true })));
const flags = ['--enable-unsafe-webgpu', '--use-angle=swiftshader', '--disable-webgpu-shader-disk-cache'];
if (process.platform === 'linux') {
  // WebGPU and the Linux canvas compositor must share the Vulkan software host.
  // Adapter creation alone succeeds without this, but canvas presentation loses
  // the device because Chromium cannot allocate its shared image backing.
  flags.push('--enable-features=Vulkan', '--use-vulkan=swiftshader', '--disable-vulkan-surface');
}
const headless = process.env.PROGPU_RUST_BROWSER_HEADED !== '1';
const browser = await chromium.launch({ headless, channel: 'chromium', args: flags });
const reports = [];
// Playwright's waitForFunction treats a returned Promise as truthy. Await each
// serialized WASM query explicitly so a false result cannot pass the gate.
async function waitFor(check) {
  const deadline=Date.now()+30000;
  while(Date.now()<deadline) {
    const result=await check();
    if(result) return result;
    await new Promise(resolve=>setTimeout(resolve,50));
  }
  throw new Error('Timed out waiting for retained browser frame');
}
try {
  for (const dpr of [1, 2]) {
    const context = await browser.newContext({ viewport: { width: 960, height: 680 }, deviceScaleFactor: dpr });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(String(e)));
    page.on('console', m => {
      if (m.type() === 'error') errors.push(m.text());
      if (m.type() === 'info' || m.type() === 'error') console.log(`[DPR ${dpr}] ${m.text()}`);
    });
    await page.goto(process.env.PROGPU_RUST_BROWSER_URL ?? 'http://127.0.0.1:4174', { waitUntil: 'networkidle' });
    await page.evaluate(() => window.progpuReady);
    console.log(`[DPR ${dpr}] browser renderer initialized`);
    await waitFor(() => page.evaluate(async () => {
      const app = await window.progpuReady;
      return app.exclusive(() => app.renderer.metrics().frames >= 4);
    }));
    const report = await page.evaluate(async () => {
      const app = await window.progpuReady;
      return app.exclusive(async () => {
        const r = app.renderer;
        const check = (condition, message) => { if (!condition) throw new Error(message); };
        // Authored three-point contour: off/on/off, original signed deltas and
        // instructions. Expected records are independent explicit native controls.
        const head=new Uint8Array(54); new DataView(head.buffer).setInt16(50,1);
        const maxp=new Uint8Array(6); new DataView(maxp.buffer).setUint16(4,1);
        const glyf=new Uint8Array(31), gv=new DataView(glyf.buffer);
        gv.setInt16(0,1); gv.setUint16(10,2); gv.setUint16(12,2);
        glyf.set([0xb0,1,0,1,0],14);
        for(let i=19;i<31;i+=2) gv.setInt16(i,-513);
        const loca=new Uint8Array(8); new DataView(loca.buffer).setUint32(4,glyf.length);
        const tables=[['head',head],['maxp',maxp],['loca',loca],['glyf',glyf]];
        const font=new Uint8Array(200), fv=new DataView(font.buffer);
        fv.setUint32(0,0x10000); fv.setUint16(4,tables.length);
        let offset=12+tables.length*16;
        for(let i=0;i<tables.length;++i) {
          const [tag,bytes]=tables[i], record=12+i*16;
          font.set(new TextEncoder().encode(tag),record);
          fv.setUint32(record+8,offset); fv.setUint32(record+12,bytes.length);
          font.set(bytes,offset); offset=(offset+bytes.length+3)&~3;
        }
        const decoded=app.decode_simple_glyph(font,0);
        check(JSON.stringify([...decoded.points])===JSON.stringify([-513,-513,0,-1026,-1026,1,-1539,-1539,0]),'WASM original outline points');
        check(JSON.stringify([...decoded.ends])==='[2]','WASM contour endpoints');
        check(JSON.stringify([...decoded.instructions])==='[176,1]','WASM retained instructions');
        const expectedSegments=new ArrayBuffer(96), sv=new DataView(expectedSegments);
        for(let i=0;i<2;++i) {
          const values=[-1026,-1026,i===0?-513:-1539,i===0?-513:-1539,-1026,-1026,0,0];
          values.forEach((value,j)=>sv.setFloat32(i*48+j*4,value,true));
          sv.setUint32(i*48+32,1,true);
        }
        check(decoded.segments.length===96 && decoded.segments.every((b,i)=>b===new Uint8Array(expectedSegments)[i]),'WASM exact quadratic path bytes');
        let rejected=false;
        try { app.decode_simple_glyph(font.slice(0,offset-4),0); } catch { rejected=true; }
        check(rejected,'WASM truncated glyph rejection');
        // Half-scale component with scaled offsets (3,5), rounded ties-to-even.
        // This executes production recursive expansion, not a JS geometry path.
        new DataView(maxp.buffer).setUint16(4,2);
        const compound=new Uint8Array(20), cv=new DataView(compound.buffer);
        cv.setInt16(0,-1); cv.setUint16(10,0x80f);
        cv.setInt16(14,3); cv.setInt16(16,5); cv.setInt16(18,8192);
        const allGlyphs=new Uint8Array(glyf.length+compound.length);
        allGlyphs.set(glyf); allGlyphs.set(compound,glyf.length);
        const locations=new Uint8Array(12), lv=new DataView(locations.buffer);
        lv.setUint32(4,glyf.length); lv.setUint32(8,allGlyphs.length);
        const compoundFont=new Uint8Array(256), cfv=new DataView(compoundFont.buffer);
        cfv.setUint32(0,0x10000); cfv.setUint16(4,4);
        let compoundOffset=76;
        for(const [i,[tag,data]] of [['head',head],['maxp',maxp],['loca',locations],['glyf',allGlyphs]].entries()) {
          const record=12+i*16;
          compoundFont.set(new TextEncoder().encode(tag),record);
          cfv.setUint32(record+8,compoundOffset); cfv.setUint32(record+12,data.length);
          compoundFont.set(data,compoundOffset); compoundOffset=(compoundOffset+data.length+3)&~3;
        }
        const expanded=app.decode_glyph_outline(compoundFont,1);
        check(JSON.stringify([...expanded.points])==='[-254.5,-254.5,-511,-511,-767.5,-767.5]','WASM composite original points');
        const expectedExpanded=expectedSegments.slice(0), ev=new DataView(expectedExpanded);
        for(let i=0;i<2;++i) for(let j=0;j<6;++j) ev.setFloat32(i*48+j*4,sv.getFloat32(i*48+j*4,true)*.5+2,true);
        check(expanded.segments.length===96 && expanded.segments.every((b,i)=>b===new Uint8Array(expectedExpanded)[i]),'WASM composite exact path bytes');
        rejected=false;
        try { app.decode_glyph_outline(compoundFont.slice(0,compoundOffset-4),1); } catch { rejected=true; }
        check(rejected,'WASM truncated composite rejection');
        const cff=new Uint8Array([1,0,4,4,0,0,0,1,1,1,3,156,17,0,0,0,0,
          0,1,1,1,12,139,139,21,239,139,139,239,39,139,5,14]);
        const cffFont=new Uint8Array(28+cff.length), cffv=new DataView(cffFont.buffer);
        cffv.setUint32(0,0x4f54544f); cffv.setUint16(4,1);
        cffv.setUint32(12,0x43464620); cffv.setUint32(20,28); cffv.setUint32(24,cff.length);
        cffFont.set(cff,28);
        const cffSegments=app.decode_cff_outline(cffFont,0);
        const expectedCff=new Uint8Array(192), cfsv=new DataView(expectedCff.buffer);
        [[0,0,100,0],[100,0,100,100],[100,100,0,100],[0,100,0,0]].forEach((line,i)=>
          line.forEach((value,j)=>cfsv.setFloat32(i*48+j*4,value,true)));
        check(cffSegments.length===192 && cffSegments.every((b,i)=>b===expectedCff[i]),'WASM CFF original closed path bytes');
        rejected=false;
        try { app.decode_cff_outline(cffFont.slice(0,-1),0); } catch { rejected=true; }
        check(rejected,'WASM truncated CFF rejection');
        const variableFont=new Uint8Array(110), vv=new DataView(variableFont.buffer);
        vv.setUint32(0,0x10000); vv.setUint16(4,2);
        vv.setUint32(12,0x66766172); vv.setUint32(20,44); vv.setUint32(24,36);
        vv.setUint32(28,0x61766172); vv.setUint32(36,80); vv.setUint32(40,30);
        vv.setUint32(44,0x10000); vv.setUint16(48,16); vv.setUint16(52,1); vv.setUint16(54,20);
        vv.setUint32(60,0x77676874); vv.setInt32(64,-65536); vv.setInt32(72,65536);
        vv.setUint32(80,0x10000); vv.setUint16(86,1); vv.setUint16(88,5);
        [[-16384,-16384],[-4,-2],[0,0],[4,2],[16384,16384]].forEach((pair,i)=>
          pair.forEach((value,j)=>vv.setInt16(90+i*4+j*2,value)));
        let variationChecks=0;
        for (const [fixed,expected] of [[-12,-2],[-4,0],[4,0],[12,2]]) {
          for (const strict of [false,true]) {
            check(app.normalize_font_variation(variableFont,0,fixed,fixed/65536,strict)===expected,'WASM variation mapped ties');
            variationChecks++;
          }
        }
        vv.setInt16(100,1);
        check(app.normalize_font_variation(variableFont,0,0,0,false)===1,'WASM legacy avar admission');
        rejected=false;
        try { app.normalize_font_variation(variableFont,0,0,0,true); } catch { rejected=true; }
        check(rejected,'WASM strict avar rejection');
        variationChecks+=2;
        const itemStore=new Uint8Array(44), iv=new DataView(itemStore.buffer);
        iv.setUint16(0,1); iv.setUint32(2,16); iv.setUint16(6,2);
        iv.setUint32(8,26); iv.setUint32(12,36);
        iv.setUint16(16,1); iv.setUint16(18,1); iv.setInt16(22,8192); iv.setInt16(24,16384);
        iv.setUint16(26,1); iv.setUint16(28,1); iv.setUint16(30,1); iv.setInt16(34,100);
        iv.setUint16(40,1);
        const itemValues=app.item_variation_values(itemStore,new Int16Array([4096]));
        check(itemValues.length===3 && itemValues[0]===0.5 && itemValues[1]===50 && itemValues[2]===0.5,'WASM item variation scalars/deltas');
        rejected=false;
        try { app.item_variation_values(itemStore.slice(0,-1),new Int16Array([4096])); } catch { rejected=true; }
        check(rejected,'WASM unselected item subtable truncation rejection');
        variationChecks+=2;
        const metricsFont=new Uint8Array(219), mv=new DataView(metricsFont.buffer);
        mv.setUint32(0,0x10000);mv.setUint16(4,3);
        [[0x66766172,60,36],[0x68656164,96,54],[0x48564152,150,69]].forEach(([tag,off,len],i)=>{
          mv.setUint32(12+i*16,tag);mv.setUint32(20+i*16,off);mv.setUint32(24+i*16,len);
        });
        metricsFont.set(variableFont.subarray(44,80),60);metricsFont.set(head,96);mv.setUint16(114,1000);
        mv.setUint32(150,0x10000);mv.setUint32(154,20);mv.setUint32(158,64);mv.setUint32(162,64);mv.setUint32(166,0xffffffff);
        metricsFont.set(itemStore,170);metricsFont.set([0,0,0,1,0],214);
        const metricsCoordinates=new Int16Array([4096]);
        const metricValues=app.horizontal_font_metrics(metricsFont,65535,metricsCoordinates,true);
        check(JSON.stringify([...metricValues])==='[1,1,550,550,50,1,50,1]','WASM owned HVAR mapping and base fallback');
        check(app.design_font_advance(metricsFont,0,metricsCoordinates)===550,'WASM direct HVAR advance');
        const noBearing=metricsFont.slice();new DataView(noBearing.buffer).setUint32(162,0);
        const noBearingValues=app.horizontal_font_metrics(noBearing,0,metricsCoordinates,true);
        check(noBearingValues[6]===0 && noBearingValues[7]===0,'WASM absent bearing mapping');
        const disabledHvar=metricsFont.slice();new DataView(disabledHvar.buffer).setUint32(154,0);
        check(JSON.stringify([...app.horizontal_font_metrics(disabledHvar,0,metricsCoordinates,false)])==='[0,0,500,500,0,0,0,0]','WASM legacy zero-store HVAR');
        rejected=false;try {app.horizontal_font_metrics(disabledHvar,0,metricsCoordinates,true);}catch{rejected=true;}
        check(rejected,'WASM strict HVAR rejects zero store');
        const badHvar=metricsFont.slice();new DataView(badHvar.buffer).setUint32(154,0xffffffff);
        check(app.design_font_advance(badHvar,0,new Int16Array())===500,'WASM empty request precedes variation validation');
        rejected=false;try {app.design_font_advance(badHvar,0,metricsCoordinates);}catch{rejected=true;}
        check(rejected,'WASM active malformed HVAR rejection');
        rejected=false;try {app.horizontal_font_metrics(metricsFont,0,new Int16Array(),true);}catch{rejected=true;}
        check(rejected,'WASM prepared HVAR coordinate capacity');
        const horizontalChecks=8;
        // Neutral VVAR is strict and separate from static VORG origin policy.
        const verticalHeader=new Uint8Array(36), vh=new DataView(verticalHeader.buffer);
        vh.setUint16(34,1);
        const verticalMetric=new Uint8Array([4,176,0,0]);
        const verticalOrigin=new Uint8Array([0,1,0,0,3,132,0,0]); // 900, no records
        const verticalMaxp=new Uint8Array([0,0,0,0,0,8]);
        const vvar=new Uint8Array(73), vr=new DataView(vvar.buffer);
        vr.setUint32(0,0x10000);vr.setUint32(4,24);vr.setUint32(8,68);vr.setUint32(12,68);vr.setUint32(20,68);
        vvar.set(itemStore,24);vvar.set([0,0,0,1,0],68);
        const verticalTables=[['maxp',verticalMaxp],['fvar',variableFont.slice(44,80)],['vhea',verticalHeader],['vmtx',verticalMetric],['VORG',verticalOrigin],['VVAR',vvar]];
        const makeVertical=(tables)=>{
          const bytes=new Uint8Array(12+tables.length*16+tables.reduce((n,[,v])=>n+v.length,0)),view=new DataView(bytes.buffer);
          view.setUint32(0,0x10000);view.setUint16(4,tables.length);let at=12+tables.length*16;
          tables.forEach(([tag,data],i)=>{bytes.set(new TextEncoder().encode(tag),12+i*16);view.setUint32(20+i*16,at);view.setUint32(24+i*16,data.length);bytes.set(data,at);at+=data.length;});
          return bytes;
        };
        const verticalFont=makeVertical(verticalTables);
        check(JSON.stringify([...app.vertical_font_metrics(verticalFont,7,metricsCoordinates)])==='[1200,1,900,1,1,1,50,50,0,50,1,0,1]','WASM owned VVAR deltas and independent static VORG');
        check(JSON.stringify([...app.vertical_font_metrics(makeVertical(verticalTables.slice(0,-1)),7,metricsCoordinates)])==='[1200,1,900,1,0,0,0,0,0,0,0,0,0]','WASM absent VVAR remains distinct');
        let verticalChecks=2;
        for (const [bytes,glyph,coords] of [
          [verticalFont,8,metricsCoordinates],
          [verticalFont,0,new Int16Array()],
          [verticalFont,0,new Int16Array([16385])],
          [makeVertical([...verticalTables,['VVAR',vvar]]),0,metricsCoordinates],
        ]) {rejected=false;try{app.vertical_font_metrics(bytes,glyph,coords);}catch{rejected=true;}check(rejected,'WASM strict VVAR request/directory rejection');verticalChecks++;}
        for (const mutate of [
          v=>v.setUint32(8,0), // implicit advance map lacks all eight rows
          v=>v.setUint8(69,0x40), // reserved map format bits
          v=>v.setUint8(68,1), // generic format one is not VVAR admission
          v=>v.setInt16(48,16385), // region endpoint outside normalized range
        ]) {const changed=vvar.slice();mutate(new DataView(changed.buffer));const tables=verticalTables.slice();tables[5]=['VVAR',changed];rejected=false;try{app.vertical_font_metrics(makeVertical(tables),0,metricsCoordinates);}catch{rejected=true;}check(rejected,'WASM whole VVAR validation');verticalChecks++;}
        const simpleFont=new Uint8Array(80), simpleView=new DataView(simpleFont.buffer);
        simpleView.setUint32(0,0x10000);simpleView.setUint16(4,1);simpleView.setUint32(12,0x636d6170);simpleView.setUint32(20,28);simpleView.setUint32(24,52);
        simpleView.setUint16(30,1);simpleView.setUint16(32,3);simpleView.setUint16(34,10);simpleView.setUint32(36,12);simpleView.setUint16(40,12);simpleView.setUint32(44,40);simpleView.setUint32(52,2);
        [[56,0x41,3],[68,0x1f600,7]].forEach(([at,code,glyph])=>{simpleView.setUint32(at,code);simpleView.setUint32(at+4,code);simpleView.setUint32(at+8,glyph);});
        const simpleText=new Uint16Array([0x41,0xd83d,0xde00,0xad,10,0x85,0xd800]);
        const simpleRun=app.simple_font_run(simpleFont,simpleText,2,6);
        check(JSON.stringify([...simpleRun.text])===JSON.stringify([...simpleText]) && JSON.stringify([...simpleRun.clusters])==='[0,1,1,2,3,4,5]' && JSON.stringify([...simpleRun.glyphs])==='[3,7,6,2,2,0]','WASM owned UTF-16 run and source retention');
        const metricPairs=new Uint32Array([250,300,350,400,450,500,999,999,999,999,550,600]);
        const simpleScale=new Float64Array([10,1]);
        const horizontal=app.simple_font_advances(simpleText,simpleRun.clusters,simpleRun.glyphs,metricPairs,1000,simpleScale,false);
        check(JSON.stringify([...horizontal.advances])==='[2,4,4,0,0,6,-1234567,-1234567]' && JSON.stringify([...horizontal.state])==='[2,2,2,1,1,2,165,165]','WASM exact simple advance rounding, scratch and tails');
        const sideways=app.simple_font_advances(simpleText,simpleRun.clusters,simpleRun.glyphs,metricPairs,1000,simpleScale,true);
        check(JSON.stringify([...sideways.advances])==='[3,4,5,0,0,6,-1234567,-1234567]','WASM simple height selection');
        const noCmap=new Uint8Array(12);new DataView(noCmap.buffer).setUint32(0,0x10000);
        check(JSON.stringify([...app.simple_font_run(noCmap,new Uint16Array([0,0xad,0x85]),9,0).glyphs])==='[9,9,9]','WASM missing cmap control mapping and soft-hyphen fallback');
        rejected=false;try{app.simple_font_run(noCmap,new Uint16Array([0,0x41]),9,0);}catch{rejected=true;}check(rejected,'WASM late missing cmap rejection');
        let simpleTextChecks=5;
        for (const [text,expected] of [[[0x41,10],2],[[10,0x41],0]]) {
          const result=app.simple_font_advances(new Uint16Array(text),new Uint16Array([0,0]),new Uint16Array([3]),new Uint32Array([250,300]),1000,simpleScale,false);
          check(result.advances[0]===expected,'WASM original first cluster control ownership');simpleTextChecks++;
        }
        for (const [metrics,units,scales] of [[metricPairs,1000,new Float64Array([NaN,1])],[new Uint32Array([0xffffffff,300,350,400,450,500,999,999,999,999,550,600]),1,simpleScale]]) {
          rejected=false;try{app.simple_font_advances(simpleText,simpleRun.clusters,simpleRun.glyphs,metrics,units,scales,false);}catch{rejected=true;}check(rejected,'WASM finite and overflow preflight');simpleTextChecks++;
        }
        rejected=false;try{app.simple_font_run(simpleFont,new Uint16Array(65537).fill(0x41),0,0);}catch{rejected=true;}check(rejected,'WASM complete simple run capacity boundary');simpleTextChecks++;
        const unicode8=app.decode_unicode_utf8(new TextEncoder().encode('  A\u0301 ا😀'));
        const unicode16=app.decode_unicode_utf16(new Uint16Array([0x20,0x20,0x41,0x301,0x20,0x627,0xd83d,0xde00]));
        const records8=new Uint32Array(unicode8.scalars.buffer,unicode8.scalars.byteOffset,unicode8.scalars.byteLength/4);
        const records16=new Uint32Array(unicode16.scalars.buffer,unicode16.scalars.byteOffset,unicode16.scalars.byteLength/4);
        const latinTag=0x6c61746e,arabicTag=0x61726162,defaultTag=0x44464c54;
        check(JSON.stringify([...records8])===JSON.stringify([0x20,0,1,defaultTag,0x20,1,1,defaultTag,0x41,2,1,latinTag,0x301,3,0x00e60002,defaultTag,0x20,5,1,defaultTag,0x627,6,2,arabicTag,0x1f600,8,4,defaultTag]),'WASM strict UTF-8 original scalar records');
        check(JSON.stringify([...records16])===JSON.stringify([0x20,0,1,defaultTag,0x20,1,1,defaultTag,0x41,2,1,latinTag,0x301,3,0x00e60001,defaultTag,0x20,4,1,defaultTag,0x627,5,1,arabicTag,0x1f600,6,2,defaultTag]),'WASM strict UTF-16 source offsets');
        check(JSON.stringify([...new Uint32Array(unicode8.runs.buffer,unicode8.runs.byteOffset,unicode8.runs.byteLength/4)])===JSON.stringify([0,5,0,6,latinTag,5,2,6,6,arabicTag]),'WASM original leading/trailing Common script attachment');
        check(JSON.stringify([...new Uint32Array(unicode16.runs.buffer,unicode16.runs.byteOffset,unicode16.runs.byteLength/4)])===JSON.stringify([0,5,0,5,latinTag,5,2,5,3,arabicTag]),'WASM script run UTF-16 ranges');
        let unicodeChecks=4;
        for(const bytes of [[0x41,0xc0,0x80],[0x41,0xed,0xa0,0x80],[0xf4,0x90,0x80,0x80],[0xe2,0x82]]){rejected=false;try{app.decode_unicode_utf8(new Uint8Array(bytes));}catch{rejected=true;}check(rejected,'WASM invalid UTF-8 atomic rejection');unicodeChecks++;}
        for(const words of [[0x41,0xd800],[0xdc00],[0xdbff,0x41]]){rejected=false;try{app.decode_unicode_utf16(new Uint16Array(words));}catch{rejected=true;}check(rejected,'WASM strict unpaired-surrogate rejection');unicodeChecks++;}
        const properties=app.unicode_properties(new Uint32Array([0x3042,0xe81,0x301,0x28,0xd800,0x1d7d0,0xff08]));
        check(properties[0]===0x6b616e61 && properties[10]===0x6c616f20 && properties[21]===230,'WASM script aliases and combining class');unicodeChecks++;
        check(properties[35]===0x29 && properties[36]===0x28 && properties[37]===1 && properties[38]===0x29 && properties[39]===1 && properties[66]===0xfe35,'WASM original directional/bracket mappings');unicodeChecks++;
        check(properties[40]===defaultTag && properties[43]===29 && properties[44]===0xffffffff && properties[54]===2,'WASM invalid scalar category and mathematical decimal digit');unicodeChecks++;
        const cff2Table=new Uint8Array(86), c2=new DataView(cff2Table.buffer);
        cff2Table.set([2,0,5,0,19]);
        [[5,28,17],[11,44,12],[18,54,24]].forEach(([at,value,op])=>{c2.setUint8(at,29);c2.setUint32(at+1,value);c2.setUint8(at+5,op);});
        c2.setUint8(17,36);
        cff2Table.set([0,0,0,1,1,1,10,139,139,21,239,159,140,16,139,5],28);
        cff2Table.set([0,0,0,1,1,1,4,139,139,18],44);
        cff2Table.set([0,30,0,1,0,0,0,12,0,1,0,0,0,22,0,1,0,1,0,0,32,0,64,0,0,0,0,0,0,1,0,0],54);
        const cff2Font=new Uint8Array(60+86+54+36), c2f=new DataView(cff2Font.buffer);
        c2f.setUint32(0,0x4f54544f);c2f.setUint16(4,3);
        [[0x43464632,60,86],[0x68656164,146,54],[0x66766172,200,36]].forEach(([tag,off,len],i)=>{
          c2f.setUint32(12+i*16,tag);c2f.setUint32(20+i*16,off);c2f.setUint32(24+i*16,len);
        });
        cff2Font.set(cff2Table,60);c2f.setUint16(164,1000);
        c2f.setUint16(204,16);c2f.setUint16(208,1);c2f.setUint16(210,20);
        const cff2Segments=app.decode_cff2_outline(cff2Font,0,new Int16Array([8192]));
        const cff2Expected=new Uint8Array(96), c2e=new DataView(cff2Expected.buffer);
        c2e.setFloat32(8,120,true);c2e.setFloat32(48,120,true);
        check(cff2Segments.length===96 && cff2Segments.every((b,i)=>b===cff2Expected[i]),'WASM CFF2 original blended bytes');
        rejected=false;
        try { app.decode_cff2_outline(cff2Font,0,new Int16Array()); } catch { rejected=true; }
        check(rejected,'WASM CFF2 missing coordinates rejection');
        const sameValues=(actual,expected)=>actual.length===expected.length && actual.every((v,i)=>v===expected[i]);
        check(sameValues(app.decode_variation_runs(new Uint8Array([3,2,1,0,7]),0,true),[5,0,1,1,8]),'WASM packed point duplicates');
        check(sameValues(app.decode_variation_runs(new Uint8Array([0xc2]),3,false),[1,0,0,0,0]),'WASM packed zero-run precedence');
        check(sameValues(app.decode_variation_runs(new Uint8Array([0]),0,true),[1,1]),'WASM all-point marker');
        check(sameValues(app.decode_variation_runs(new Uint8Array([128,0]),0,true),[2,0]),'WASM explicit empty point set');
        const ip=new Float32Array([0,0,10,5,20,10]),ix=new Float32Array([2,99,6]),iy=new Float32Array([4,98,8]),it=new Uint8Array([1,0,255]);
        check(sameValues(app.interpolate_gvar_deltas(ip,new Uint16Array([2]),ix,iy,it),[2,4,6,4,6,8]),'WASM contour interpolation');
        rejected=false;
        try { app.interpolate_gvar_deltas(ip,new Uint16Array([1,0]),ix,iy,it); } catch { rejected=true; }
        check(rejected,'WASM complete contour rejection');
        const gf=new Uint8Array(176),gvarView=new DataView(gf.buffer);
        gvarView.setUint32(0,0x10000);gvarView.setUint16(4,3);
        [[0x67766172,60,54],[0x6d617870,114,6],[0x66766172,120,56]].forEach(([tag,off,len],i)=>{
          gvarView.setUint32(12+i*16,tag);gvarView.setUint32(20+i*16,off);gvarView.setUint32(24+i*16,len);
        });
        gvarView.setUint32(60,0x10000);gvarView.setUint16(64,2);gvarView.setUint16(66,1);gvarView.setUint32(68,28);
        gvarView.setUint16(72,1);gvarView.setUint16(74,1);gvarView.setUint32(76,32);gvarView.setUint32(84,22);
        gvarView.setInt16(88,7153);gvarView.setInt16(90,-9973);gvarView.setUint16(92,1);gvarView.setUint16(94,20);
        gvarView.setUint16(96,2);gvarView.setUint16(98,0xe000);
        [7153,-9973,0,-16384,16384,0].forEach((v,i)=>gvarView.setInt16(100+i*2,v));gf.set([128,128],112);
        gvarView.setUint16(118,1);gvarView.setUint16(124,16);gvarView.setUint16(128,2);gvarView.setUint16(130,20);
        check(sameValues(app.decode_gvar_metadata(gf,0),[2,1,0,2,0xe000,0,-16384,7153,-9973,16384,0]),'WASM original gvar tuple metadata');
        rejected=false;
        try { app.decode_gvar_metadata(gf.slice(0,113),0); } catch { rejected=true; }
        check(rejected,'WASM truncated gvar rejection');
        const payloadFont = new Uint8Array(190), pv = new DataView(payloadFont.buffer);
        payloadFont.set(gf.subarray(0,112));
        payloadFont.set([4,3,0,0,2,1,3,1,1,6,10,3,2,2,8,12],112);
        payloadFont.set(gf.subarray(114),128);
        pv.setUint32(24,68); pv.setUint32(36,128); pv.setUint32(52,134);
        pv.setUint32(84,36); pv.setUint16(96,16);
        const coordinates = new Int16Array([7153,-9973]);
        const originalPoints = new Int32Array([0,0,5,5,10,10]);
        check(sameValues(app.apply_gvar_simple(payloadFont,0,coordinates,new Uint16Array([2]),originalPoints),[2,4,9,11,16,18]),'WASM sparse gvar duplicates and IUP');
        check(sameValues(app.gvar_component_metrics(payloadFont,0,coordinates,3),[2,4,0,0,6,8,10,0]),'WASM component offsets and phantom deltas');
        check(sameValues(app.apply_gvar_simple(payloadFont,0,new Int16Array([0,0]),new Uint16Array([2]),originalPoints),originalPoints),'WASM inactive tuple');
        const badPayload=payloadFont.slice();badPayload[112]=7;
        rejected=false;
        try { app.apply_gvar_simple(badPayload,0,new Int16Array([0,0]),new Uint16Array([2]),originalPoints); } catch { rejected=true; }
        check(rejected,'WASM inactive malformed payload rejection');
        const gvarChecks=12;
        const shaders = await r.validate_shaders();
        check(shaders === 6, 'canonical shader inventory');
        let checkedPixels = 0;
        for (const [dpi, contrast] of [[1,-1],[1.25,-1],[2,-1],[1,-2],[1.25,-2],[2,-2],[1,-3],[1.25,-3],[2,-3],[1,-4],[1.25,-4],[2,-4], ...Array.from({length:13},(_,i)=>[1,i])]) {
          console.info(`validating GPU pixels: dpi=${dpi} contrast=${contrast}`);
          const [cold, warm] = await r.validation_pixels(dpi, contrast);
          check(cold.length === 64*64*4 && warm.length === cold.length, 'readback extent');
          for (let i=0; i<cold.length; ++i) check(cold[i]===warm[i], `warm generation mismatch ${dpi}/${contrast}/${i}`);
          for (let y=0; y<64; ++y) for (let x=0; x<64; ++x) {
            let expected = [0,0,0,0];
            if (contrast >= 0) {
              const overlap = (v,lo,hi) => Math.max(0,Math.min(v+1,hi)-Math.max(v,lo));
              const coverage = Math.round(overlap(x,4.25,20.75)*overlap(y,4.5,20.5)*255)/255;
              expected.fill(255-Math.round(Math.pow(1-coverage,1/(1+contrast/10))*255));
            } else {
              const coverage = (tx,ty) => {
                let count=0;
                for(let sy=0;sy<8;++sy) for(let sx=0;sx<8;++sx) {
                  const px=(x+(sx+.5)/8)/dpi-tx, py=(y+(sy+.5)/8)/dpi-ty;
                  count += px>=4.25 && px<20.75 && py>=4.5 && py<20.5;
                }
                return Math.round(count*255/64)/255;
              };
              const blend=(color,coverage)=> {
                const a=color[3]*coverage;
                for(let c=0;c<3;++c) expected[c]=Math.round(color[c]*a*255+expected[c]*(1-a));
                expected[3]=Math.round(a*255+expected[3]*(1-a));
              };
              blend([1,0,0,.6],coverage(0,0));
              const px=(x+.5)/dpi,py=(y+.5)/dpi;
              blend([0,0,1,.4],Number(px>=12&&px<32&&py>=8&&py<28));
              blend([0,1,0,.7],coverage(12,-2));
            }
            for(let c=0;c<4;++c) check(Math.abs(cold[(y*64+x)*4+c]-expected[c]) <= (contrast>=0?1:2),
              `pixel dpi=${dpi} contrast=${contrast} (${x},${y})/${c}: ${cold[(y*64+x)*4+c]} != ${expected[c]}`);
            ++checkedPixels;
          }
        }
        const before = r.metrics();
        for(let i=0;i<10;++i) check(r.draw(), 'warm canvas presentation');
        const after = r.metrics();
        check(after.frames===before.frames+10, 'presentation count');
        for(const key of ['geometryBytes','uniformBytes','draws','pathRasters','atlasBytes']) check(after[key]===before[key],`warm ${key}`);
        await r.resize(0,0,1);
        check(r.draw()===false,'zero extent suspension');
        const size=app.dimensions();
        await r.resize(size.width,size.height,size.dpi);
        check(r.draw(),'resume presentation');
        return { adapter: r.adapter_info(), outlineBytes: decoded.segments.length, compositeBytes: expanded.segments.length, cffBytes: cffSegments.length, cff2Bytes: cff2Segments.length, gvarChecks, horizontalChecks, verticalChecks, simpleTextChecks, unicodeChecks, variationChecks, shaders, checkedPixels, before, after, errors: [...app.errors] };
      });
    });
    assert.equal(report.before.draws, 1);
    assert.equal(report.before.pathRasters, 3);
    assert.equal(report.before.geometryBytes, 29760);
    assert.equal(report.checkedPixels, 102400);
    assert.deepEqual(report.errors, []);
    await page.setViewportSize({ width: 800, height: 600 });
    await waitFor(() => page.evaluate(async () => {
      const app = await window.progpuReady;
      return app.exclusive(() => {
        const m=app.renderer.metrics(), size=app.dimensions();
        return innerWidth===800 && innerHeight===600 && m.width===800*devicePixelRatio
          && m.height===size.height && m.dpi===size.dpi;
      });
    }));
    const resized = await page.evaluate(async () => {
      const app=await window.progpuReady;
      return app.exclusive(()=>({metrics:app.renderer.metrics(),size:app.dimensions(),errors:[...app.errors]}));
    });
    assert.equal(resized.metrics.height,resized.size.height);
    assert.equal(resized.metrics.dpi,dpr);
    assert.deepEqual(resized.errors,[]);
    assert.deepEqual(errors,[]);
    const entry={ dpr, ...report, resized: resized.metrics };
    reports.push(entry);
    console.log(`[DPR ${dpr}] resize verified; capturing final presented frame`);
    await page.evaluate(async () => {
      const app=await window.progpuReady;
      await app.pause();
    });
    await waitFor(() => page.evaluate(async () => {
      const app=await window.progpuReady;
      return app.exclusive(() => app.renderer.capture_ready());
    }));
    entry.captureCompleted=true;
    await page.evaluate(async () => {
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    });
    const bounds = await page.locator('canvas').boundingBox();
    assert.ok(bounds);
    const screenshot = PNG.sync.read(await page.screenshot({ path: path.join(output, `canvas-dpr${dpr}.png`) }));
    // The compositor screenshot is a separate required gate. Offscreen readback
    // can pass while a browser compositor presents an entirely black canvas.
    const pixel = (x,y) => {
      const offset=(Math.floor((bounds.y+y)*dpr)*screenshot.width+Math.floor((bounds.x+x)*dpr))*4;
      return [...screenshot.data.subarray(offset,offset+4)];
    };
    const background=pixel(8,8), rectangle=pixel(40,40), ellipse=pixel(128,49), curve=pixel(52,503);
    entry.presentationPixels={background,rectangle,ellipse,curve};
    assert.ok(background[2]>background[0] && background[2]>5,`canvas clear: ${background}`);
    for(const color of [rectangle,ellipse]) assert.ok(color[2]>color[0]+60 && color[2]>color[1]+60,`canvas analytic shape: ${color}`);
    assert.ok(curve[0]>curve[2]+60 && curve[0]>curve[1]+60,`canvas computed path: ${curve}`);
    assert.deepEqual(errors,[]);
    await context.close();
  }
  const receipt = { status: 'passed', browser: browser.version(), platform: process.platform, architecture: process.arch, headless, flags, reports };
  await writeFile(path.join(output,'results.json'),JSON.stringify(receipt,null,2)+'\n');
  console.log(JSON.stringify(receipt,null,2));
} catch (error) {
  await writeFile(path.join(output,'results.json'),JSON.stringify({ status: 'failed',
    browser: browser.version(), platform: process.platform, architecture: process.arch,
    headless, flags, reports, error: String(error) },null,2)+'\n');
  throw error;
} finally { await browser.close(); }
