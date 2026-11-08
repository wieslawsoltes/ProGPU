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
        const shaders = await r.validate_shaders();
        check(shaders === 6, 'canonical shader inventory');
        let checkedPixels = 0;
        for (const [dpi, contrast] of [[1,-1],[1.25,-1],[2,-1],[1,-2],[1.25,-2],[2,-2], ...Array.from({length:13},(_,i)=>[1,i])]) {
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
        return { adapter: r.adapter_info(), outlineBytes: decoded.segments.length, compositeBytes: expanded.segments.length, cffBytes: cffSegments.length, variationChecks, shaders, checkedPixels, before, after, errors: [...app.errors] };
      });
    });
    assert.equal(report.before.draws, 1);
    assert.equal(report.before.pathRasters, 3);
    assert.equal(report.before.geometryBytes, 29760);
    assert.equal(report.checkedPixels, 77824);
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
