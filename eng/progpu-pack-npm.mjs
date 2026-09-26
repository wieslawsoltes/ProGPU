import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const build = path.resolve(process.argv[2] ?? path.join(repo, 'artifacts/progpu-native/build-browser'));
const output = path.resolve(process.argv[3] ?? path.join(repo, 'artifacts/npm'));
const source = path.join(repo, 'src/ProGPU.Native/browser/npm');
const run = (command, args, cwd = repo) => execFileSync(command, args, {
  cwd, encoding: 'utf8', maxBuffer: 8 * 1024 * 1024
}).trim();
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const sourceCommit = run('git', ['rev-parse', 'HEAD']);
assert.match(sourceCommit, /^[a-f0-9]{40}$/);
assert.equal(process.env.PROGPU_QUALIFIED_COMMIT ?? sourceCommit, sourceCommit,
  'Package must be built from the exact qualified checkout.');
const compiler = await fs.realpath(run('which', ['emcc']));
const toolchain = path.dirname(compiler);
const version = (await fs.readFile(path.join(toolchain, 'emscripten-version.txt'), 'utf8'))
  .trim().replaceAll('"', '');
assert.equal(version, '4.0.18', 'Use the same pinned Emscripten version as browser CI.');
assert.equal(process.env.EMCC_LOCAL_PORTS ?? '', '', 'Unverified local port overrides cannot be packaged.');
const port = path.join(run('em-config', ['PORTS']), 'emdawnwebgpu', 'emdawnwebgpu_pkg');
await fs.mkdir(output, { recursive: true });
// Every invocation gets fresh staging. Never reuse a previous package payload.
const stage = await fs.mkdtemp(path.join(output, 'stage-'));
const copy = async (from, to) => {
  const stat = await fs.lstat(from);
  assert.ok(stat.isFile() && stat.size > 0, `Expected nonempty regular file: ${from}`);
  const destination = path.join(stage, to);
  await fs.mkdir(path.dirname(destination), { recursive: true });
  await fs.copyFile(from, destination, fs.constants.COPYFILE_EXCL);
};
for (const name of ['package.json', 'index.js', 'scene.js', 'index.d.ts', 'README.md', 'THIRD-PARTY-NOTICES.md']) {
  await copy(path.join(source, name), name);
}
await copy(path.join(repo, 'LICENSE'), 'LICENSE');
for (const name of ['progpu-native.mjs', 'progpu-native.wasm']) {
  await copy(path.join(build, name), name);
}
const wasm = await fs.readFile(path.join(stage, 'progpu-native.wasm'));
assert.ok(wasm.length > 64 && wasm.subarray(0, 8).equals(Buffer.from([0,97,115,109,1,0,0,0])),
  'Expected the actual compiled WebAssembly module.');

const licenses = {};
const addLicense = async (root, relative, label) => {
  const name = `licenses/${label}/${relative}`;
  await copy(path.join(root, relative), name);
  licenses[name] = hash(await fs.readFile(path.join(stage, name)));
};
await addLicense(toolchain, 'LICENSE', 'emscripten');
for (const relative of [
  'system/lib/compiler-rt/LICENSE.TXT', 'system/lib/libc/musl/COPYRIGHT',
  'system/lib/libcxx/LICENSE.TXT', 'system/lib/libcxxabi/LICENSE.TXT',
  'system/lib/libunwind/LICENSE.TXT', 'system/lib/llvm-libc/LICENSE.TXT',
  'system/lib/mimalloc/LICENSE'
]) await addLicense(toolchain, relative, 'emscripten');
let portNotices = 0;
async function collectPortNotices(directory, relative = '') {
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const child = path.join(relative, entry.name);
    if (entry.isDirectory()) await collectPortNotices(path.join(port, child), child);
    else if (entry.isFile() && /^(LICENSE|COPYRIGHT|COPYING|NOTICE)([._-].*)?$/i.test(entry.name)) {
      await addLicense(port, child, 'emdawnwebgpu');
      portNotices++;
    }
  }
}
await collectPortNotices(port);
assert.ok(portNotices > 0, 'The actual Emdawnwebgpu port must retain its original notices.');
const info = {
  sourceCommit,
  sourceDirty: run('git', ['status', '--porcelain']).length !== 0,
  emscripten: version,
  emdawnPortSha256: hash(await fs.readFile(path.join(toolchain, 'tools/ports/emdawnwebgpu.py'))),
  buildRunId: process.env.GITHUB_RUN_ID ?? null,
  buildRunAttempt: process.env.GITHUB_RUN_ATTEMPT ?? null,
  licenses
};
await fs.writeFile(path.join(stage, 'build-info.json'), JSON.stringify(info, null, 2) + '\n', { flag: 'wx' });
const manifest = JSON.parse(await fs.readFile(path.join(stage, 'package.json'), 'utf8'));
assert.equal(manifest.type, 'module');
assert.ok(!manifest.private && !manifest.scripts, 'Runtime packages must have no install/publish lifecycle scripts.');
const [packed] = JSON.parse(run('npm', ['pack', '--json', '--ignore-scripts'], stage));
const required = ['package.json', 'index.js', 'scene.js', 'index.d.ts', 'progpu-native.mjs', 'progpu-native.wasm',
  'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'build-info.json', ...Object.keys(licenses)];
assert.deepEqual(packed.files.map(file => file.path).sort(), required.sort(),
  'The archive must contain the complete runtime and notices, and nothing else.');
const archive = path.join(output, packed.filename);
await fs.copyFile(path.join(stage, packed.filename), archive, fs.constants.COPYFILE_EXCL);
const artifact = {
  schemaVersion: 1, name: manifest.name, version: manifest.version,
  archive: packed.filename, sha256: hash(await fs.readFile(archive)),
  integrity: packed.integrity, ...info
};
await fs.writeFile(path.join(output, 'npm-artifact.json'), JSON.stringify(artifact, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ archive, name: artifact.name, version: artifact.version,
  sha256: artifact.sha256, sourceCommit, fileCount: packed.files.length }, null, 2));
