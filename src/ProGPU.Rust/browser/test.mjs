import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
const tools = path.resolve(process.env.PROGPU_RUST_BROWSER_TOOLS ?? 'artifacts/progpu-rust/browser-tools');
const { chromium } = createRequire(path.join(tools, 'package.json'))('playwright');
const output = path.resolve(process.env.PROGPU_RUST_BROWSER_RESULTS ?? 'artifacts/progpu-rust/browser-results');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true, channel: 'chromium', args: [
  '--enable-unsafe-webgpu', '--use-angle=swiftshader', '--disable-webgpu-shader-disk-cache',
] });
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
    page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
    await page.goto(process.env.PROGPU_RUST_BROWSER_URL ?? 'http://127.0.0.1:4174', { waitUntil: 'networkidle' });
    await page.evaluate(() => window.progpuReady);
    await waitFor(() => page.evaluate(async () => {
      const app = await window.progpuReady;
      return app.exclusive(() => app.renderer.metrics().frames >= 4);
    }));
    const report = await page.evaluate(async () => {
      const app = await window.progpuReady;
      return app.exclusive(async () => {
        const r = app.renderer;
        const check = (condition, message) => { if (!condition) throw new Error(message); };
        const shaders = await r.validate_shaders();
        check(shaders === 6, 'canonical shader inventory');
        let checkedPixels = 0;
        for (const [dpi, contrast] of [[1,-1],[1.25,-1],[2,-1], ...Array.from({length:13},(_,i)=>[1,i])]) {
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
        return { adapter: r.adapter_info(), shaders, checkedPixels, before, after, errors: [...app.errors] };
      });
    });
    assert.equal(report.before.draws, 1);
    assert.equal(report.before.pathRasters, 3);
    assert.equal(report.before.geometryBytes, 29760);
    assert.equal(report.checkedPixels, 65536);
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
    await page.screenshot({ path: path.join(output, `canvas-dpr${dpr}.png`) });
    reports.push({ dpr, ...report, resized: resized.metrics });
    await context.close();
  }
  const receipt = { browser: browser.version(), flags: ['--enable-unsafe-webgpu', '--use-angle=swiftshader'], reports };
  await writeFile(path.join(output,'results.json'),JSON.stringify(receipt,null,2)+'\n');
  console.log(JSON.stringify(receipt,null,2));
} finally { await browser.close(); }
