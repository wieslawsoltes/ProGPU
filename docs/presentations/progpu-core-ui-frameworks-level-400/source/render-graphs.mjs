import { readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { Graphviz } from '@hpcc-js/wasm';

const sourceDir = path.dirname(fileURLToPath(import.meta.url));
const sourceAssets = path.join(sourceDir, 'assets');
const generatedAssets = path.resolve(
  process.env.PROGPU_PRESENTATION_ASSETS
    ?? path.join(os.tmpdir(), 'progpu-core-ui-frameworks-level-400-assets'),
);
const execFileAsync = promisify(execFile);
const graphviz = await Graphviz.load();

await mkdir(generatedAssets, { recursive: true });
for (const name of ['architecture', 'compositor', 'geometry', 'frameworks']) {
  const dot = await readFile(path.join(sourceAssets, `${name}.dot`), 'utf8');
  const svg = graphviz.layout(dot, 'svg', 'dot');
  const svgPath = path.join(generatedAssets, `${name}.svg`);
  const pngPath = path.join(generatedAssets, `${name}.png`);
  await writeFile(svgPath, svg, 'utf8');
  await execFileAsync('sips', ['-s', 'format', 'png', svgPath, '--out', pngPath]);
}

console.log(generatedAssets);
