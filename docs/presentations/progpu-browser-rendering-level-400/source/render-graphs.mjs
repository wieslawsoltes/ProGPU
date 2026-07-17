import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { Graphviz } from '@hpcc-js/wasm';

const sourceDir = path.dirname(fileURLToPath(import.meta.url));
const assets = path.join(sourceDir, 'assets');
const execFileAsync = promisify(execFile);
const graphviz = await Graphviz.load();

for (const name of ['architecture', 'workers', 'retained', 'aot']) {
  const dot = await readFile(`${assets}/${name}.dot`, 'utf8');
  const svg = graphviz.layout(dot, 'svg', 'dot');
  const svgPath = `${assets}/${name}.svg`;
  const pngPath = `${assets}/${name}.png`;
  await writeFile(svgPath, svg, 'utf8');
  await execFileAsync('sips', ['-s', 'format', 'png', svgPath, '--out', pngPath]);
}
