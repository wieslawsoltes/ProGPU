// Offline metadata/archive adversarial fixtures. The fake wasm marker is not a
// renderer binary and none of these tests qualify browser rendering or publish.
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import * as fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {test} from 'node:test';
import {inspectArchive, repository, validateMergedSource, validatePackage, validateRun, verifyDownloadedArtifact}
  from './progpu-verify-npm-release.mjs';

const sourceCommit = 'a'.repeat(40);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const build = {id: 123, run_attempt: 2, workflow_id: 456, path: '.github/workflows/build.yml',
  head_sha: sourceCommit, status: 'completed', conclusion: 'success', event: 'pull_request',
  repository: {id: 17, full_name: repository}, head_repository: {id: 17, full_name: repository}};
const workflow = {id: 456, path: '.github/workflows/build.yml', name: 'Build'};
const artifact = {id: 789, name: 'progpu-npm-package', expired: false, expires_at: '2100-01-01T00:00:00Z',
  digest: `sha256:${'c'.repeat(64)}`, workflow_run: {id: 123, head_sha: sourceCommit, repository_id: 17, head_repository_id: 17}};
const manifest = {name: 'progpu-renderer', version: '0.1.0-preview.1', type: 'module', main: './index.js', types: './index.d.ts',
  exports: {'.': {types: './index.d.ts', import: './index.js'}, './native': './progpu-native.mjs',
    './progpu-native.wasm': './progpu-native.wasm', './package.json': './package.json'},
  publishConfig: {access: 'public', registry: 'https://registry.npmjs.org/', tag: 'next'}};
const noticeNames = ['LICENSE', 'system/lib/compiler-rt/LICENSE.TXT', 'system/lib/libc/musl/COPYRIGHT',
  'system/lib/libcxx/LICENSE.TXT', 'system/lib/libcxxabi/LICENSE.TXT', 'system/lib/libunwind/LICENSE.TXT',
  'system/lib/llvm-libc/LICENSE.TXT', 'system/lib/mimalloc/LICENSE'].map(name => `licenses/emscripten/${name}`)
  .concat(['licenses/emdawnwebgpu/LICENSE']);
const archiveFixture = String.raw`
import base64, io, json, sys, tarfile, zipfile
request = json.load(sys.stdin)
if request['kind'] == 'tar':
    with tarfile.open(request['path'], 'w:gz', format=tarfile.PAX_FORMAT) as archive:
        for entry in request['entries']:
            info = tarfile.TarInfo(entry['name'])
            data = base64.b64decode(entry['data'])
            info.size = len(data)
            if entry.get('type') == 'symlink': info.type, info.linkname, info.size = tarfile.SYMTYPE, '/tmp/escape', 0
            if entry.get('type') == 'directory': info.type, info.size = tarfile.DIRTYPE, 0
            if entry.get('pax'): info.pax_headers = entry['pax']
            archive.addfile(info, io.BytesIO(data))
else:
    with zipfile.ZipFile(request['path'], 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        for entry in request['entries']:
            info = zipfile.ZipInfo(entry['name'])
            info.external_attr = (0o120777 if entry.get('type') == 'symlink' else 0o100644) << 16
            archive.writestr(info, base64.b64decode(entry['data']))
`;
async function workspace(t) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'progpu-npm-release-test-'));
  t.after(() => fs.rm(directory, {recursive: true})); // Only this newly owned fixture.
  return directory;
}
function writeArchive(kind, filename, entries) {
  execFileSync('python3', ['-c', archiveFixture], {
    input: JSON.stringify({kind, path: filename, entries}), timeout: 10_000, stdio: ['pipe', 'pipe', 'pipe']
  });
}
const entry = (name, bytes, extra = {}) => ({name, data: Buffer.from(bytes).toString('base64'), ...extra});
async function fixture(directory, options = {}) {
  const licenses = Object.fromEntries(noticeNames.map(name => [name, hash(Buffer.from(`notice ${name}`))]));
  const info = {sourceCommit, sourceDirty: false, emscripten: '4.0.18', emdawnPortSha256: 'b'.repeat(64),
    buildRunId: '123', buildRunAttempt: '2', licenses};
  const wasmMarker = Buffer.concat([Buffer.from([0, 97, 115, 109, 1, 0, 0, 0]), Buffer.alloc(64)]);
  const entries = [entry('package/package.json', JSON.stringify(manifest)),
    ...['index.js', 'scene.js', 'index.d.ts', 'progpu-native.mjs', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md']
      .map(name => entry(`package/${name}`, `fixture ${name}`)),
    entry('package/progpu-native.wasm', wasmMarker), entry('package/build-info.json', JSON.stringify(info)),
    ...noticeNames.map(name => entry(`package/${name}`, `notice ${name}`))];
  options.entries?.(entries);
  let archive = path.join(directory, 'fixture.tgz');
  let packed;
  if (options.npmPack) {
    const source = path.join(directory, 'package-source');
    for (const item of entries) {
      const destination = path.join(source, item.name.slice('package/'.length));
      await fs.mkdir(path.dirname(destination), {recursive: true});
      await fs.writeFile(destination, Buffer.from(item.data, 'base64'), {flag: 'wx'});
    }
    // Never read user/global npm credentials or contact a registry for this
    // metadata-only pack fixture. The fake Wasm remains explicitly unqualified.
    const userConfig = path.join(directory, 'user.npmrc');
    const globalConfig = path.join(directory, 'global.npmrc');
    await fs.writeFile(userConfig, '', {flag: 'wx'});
    await fs.writeFile(globalConfig, '', {flag: 'wx'});
    const result = JSON.parse(execFileSync('npm', ['pack', '--json', '--ignore-scripts', '--offline',
      '--userconfig', userConfig, '--globalconfig', globalConfig, '--cache', path.join(directory, 'npm-cache')], {
      cwd: source, encoding: 'utf8', timeout: 60_000, maxBuffer: 1024 * 1024,
      env: {PATH: process.env.PATH, SYSTEMROOT: process.env.SYSTEMROOT},
      stdio: ['ignore', 'pipe', 'pipe']
    }));
    assert.equal(result.length, 1);
    [packed] = result;
    assert.equal(packed.name, manifest.name);
    assert.equal(packed.version, manifest.version);
    assert.equal(path.basename(packed.filename), packed.filename);
    archive = path.join(source, packed.filename);
  } else {
    writeArchive('tar', archive, entries);
  }
  const bytes = await fs.readFile(archive);
  const metadata = {schemaVersion: 1, name: 'progpu-renderer', version: manifest.version,
    archive: packed?.filename ?? `progpu-renderer-${manifest.version}.tgz`, sha256: hash(bytes),
    integrity: `sha512-${createHash('sha512').update(bytes).digest('base64')}`, ...info};
  return {archive, bytes, metadata};
}

test('exact successful Build metadata is admitted', () => validateRun(build, workflow, artifact, '123'));
for (const [name, mutate] of [
  ['wrong repository', b => b.repository.full_name = 'foreign/ProGPU'],
  ['fork source', b => b.head_repository.full_name = 'foreign/ProGPU'],
  ['wrong run', b => b.id = 124], ['wrong workflow ID', b => b.workflow_id = 1],
  ['wrong workflow path', b => b.path = '.github/workflows/release.yml'],
  ['running Build', b => b.status = 'in_progress'], ['cancelled Build', b => b.conclusion = 'cancelled'],
  ['failed Build', b => b.conclusion = 'failure'], ['neutral Build', b => b.conclusion = 'neutral'],
  ['privileged PR event', b => b.event = 'pull_request_target'], ['invalid source SHA', b => b.head_sha = '../main'],
  ['invalid attempt', b => b.run_attempt = 0]
]) test(`reject ${name}`, () => {
  const changed = structuredClone(build); mutate(changed);
  assert.throws(() => validateRun(changed, workflow, artifact, '123'));
});
for (const [name, mutate] of [
  ['expired artifact', a => a.expired = true], ['past expiry', a => a.expires_at = '2000-01-01'],
  ['missing digest', a => delete a.digest], ['wrong artifact name', a => a.name = 'other'],
  ['artifact run mismatch', a => a.workflow_run.id = 124],
  ['artifact source mismatch', a => a.workflow_run.head_sha = 'd'.repeat(40)],
  ['artifact repo mismatch', a => a.workflow_run.repository_id = 18],
  ['artifact source repo mismatch', a => a.workflow_run.head_repository_id = 18]
]) test(`reject ${name}`, () => {
  const changed = structuredClone(artifact); mutate(changed);
  assert.throws(() => validateRun(build, workflow, changed, '123'));
});

test('real ZIP/tgz metadata fixture round trip, exact inventory and license hashes', async t => {
  const directory = await workspace(t);
  const f = await fixture(directory);
  const files = inspectArchive(f.archive);
  validatePackage(f.metadata, files, f.bytes, build);
  const zip = path.join(directory, 'artifact.zip');
  writeArchive('zip', zip, [entry('npm-artifact.json', JSON.stringify(f.metadata)), entry(f.metadata.archive, f.bytes)]);
  const a = {...artifact, digest: `sha256:${hash(await fs.readFile(zip))}`};
  const result = await verifyDownloadedArtifact(zip, path.join(directory, 'out'), build, workflow, a, '123');
  assert.equal(hash(await fs.readFile(result.archivePath)), f.metadata.sha256);
  await assert.rejects(verifyDownloadedArtifact(zip, path.join(directory, 'out'), build, workflow, a, '123'), /EEXIST/);
  await assert.rejects(verifyDownloadedArtifact(zip, path.join(directory, 'bad'), build, workflow, artifact, '123'), /digest mismatch/);
});

test('actual offline npm pack renderer filename passes immutable archive verification', async t => {
  const directory = await workspace(t);
  const sourceManifest = JSON.parse(await fs.readFile(new URL('../src/ProGPU.Native/browser/npm/package.json', import.meta.url), 'utf8'));
  assert.equal(sourceManifest.name, manifest.name);
  assert.equal(sourceManifest.version, manifest.version);
  const f = await fixture(directory, {npmPack: true});
  assert.equal(f.metadata.archive, 'progpu-renderer-0.1.0-preview.1.tgz');
  validatePackage(f.metadata, inspectArchive(f.archive), f.bytes, build);
  const zip = path.join(directory, 'artifact.zip');
  writeArchive('zip', zip, [entry('npm-artifact.json', JSON.stringify(f.metadata)), entry(f.metadata.archive, f.bytes)]);
  const a = {...artifact, digest: `sha256:${hash(await fs.readFile(zip))}`};
  const verified = await verifyDownloadedArtifact(zip, path.join(directory, 'out'), build, workflow, a, '123');
  assert.equal(hash(await fs.readFile(verified.archivePath)), f.metadata.sha256);
});

test('package metadata and inner manifest/build-info fail closed', async t => {
  const directory = await workspace(t);
  const f = await fixture(directory);
  const files = inspectArchive(f.archive);
  for (const mutate of [
    m => m.schemaVersion = 2, m => m.name = 'other', m => m.version = '1.0.0',
    m => m.name = 'progpu', m => m.name = '@wieslawsoltes/progpu', m => m.name = '@foreign/progpu',
    m => m.archive = `progpu-${m.version}.tgz`,
    m => m.archive = `wieslawsoltes-progpu-${m.version}.tgz`,
    m => m.archive = `foreign-progpu-${m.version}.tgz`,
    m => m.archive = `@wieslawsoltes/progpu-${m.version}.tgz`,
    m => m.version = '0.1.0-preview.01', m => m.version = '0.1.0-preview.1\ninjected=1',
    m => m.archive = '../evil.tgz', m => m.sha256 = '0'.repeat(64), m => m.integrity = 'sha512-invalid',
    m => m.sourceDirty = true, m => m.sourceCommit = 'e'.repeat(40), m => m.buildRunId = '124',
    m => m.buildRunAttempt = '1', m => m.emscripten = 'latest', m => m.emdawnPortSha256 = '',
    m => delete m.licenses['licenses/emdawnwebgpu/LICENSE'],
    m => m.licenses['licenses/emscripten/LICENSE'] = '0'.repeat(64)
  ]) {
    const changed = structuredClone(f.metadata); mutate(changed);
    assert.throws(() => validatePackage(changed, files, f.bytes, build));
  }
  for (const mutate of [
    f => delete f['progpu-native.mjs'], f => delete f['progpu-native.wasm'], f => delete f['scene.js'],
    f => f['unexpected.js'] = {sha256: '0'.repeat(64)},
    f => f['package.json'].json.version = '0.1.0-preview.2',
    f => f['package.json'].json.name = 'progpu',
    f => f['package.json'].json.name = '@wieslawsoltes/progpu',
    f => f['package.json'].json.name = '@foreign/progpu',
    f => f['package.json'].json.scripts = {}, f => f['package.json'].json.scripts = {install: 'evil'},
    f => f['package.json'].json.publishConfig.tag = 'latest',
    f => f['package.json'].json.publishConfig.registry = 'https://foreign.invalid/',
    f => f['build-info.json'].json.sourceCommit = 'e'.repeat(40),
    f => f['build-info.json'].json.sourceDirty = true, f => f['build-info.json'].json.buildRunAttempt = '1'
  ]) {
    const changed = structuredClone(files); mutate(changed);
    assert.throws(() => validatePackage(f.metadata, changed, f.bytes, build));
  }
});

for (const [name, mutate] of [
  ['traversal', entries => entries.push(entry('package/../escape', 'bad'))],
  ['absolute path', entries => entries.push(entry('/package/escape', 'bad'))],
  ['duplicate member', entries => entries.push(entries[0])],
  ['symlink', entries => entries.push(entry('package/link', '', {type: 'symlink'}))],
  ['directory', entries => entries.push(entry('package/directory', '', {type: 'directory'}))],
  ['PAX traversal', entries => entries.push(entry('package/safe', 'bad', {pax: {path: '../escape'}}))],
  ['empty required file', entries => entries[1] = entry('package/index.js', '')],
  ['invalid wasm', entries => entries[8] = entry('package/progpu-native.wasm', 'not wasm')]
]) test(`reject tar ${name}`, async t => {
  const directory = await workspace(t);
  const f = await fixture(directory, {entries: mutate});
  assert.throws(() => inspectArchive(f.archive));
});

test('reject truncated and concatenated gzip archives', async t => {
  const directory = await workspace(t);
  const f = await fixture(directory);
  await fs.writeFile(f.archive, f.bytes.subarray(0, f.bytes.length - 8));
  assert.throws(() => inspectArchive(f.archive));
  await fs.writeFile(f.archive, Buffer.concat([f.bytes, f.bytes]));
  assert.throws(() => inspectArchive(f.archive));
});

test('reject wrong/duplicate/traversing/nonregular ZIP contents even with matching digest', async t => {
  const directory = await workspace(t);
  const f = await fixture(directory);
  const normal = [entry('npm-artifact.json', JSON.stringify(f.metadata)), entry(f.metadata.archive, f.bytes)];
  for (const [index, entries] of [
    [...normal, entry('extra', 'bad')], [normal[0], normal[0]],
    [normal[0], entry(`../${f.metadata.archive}`, f.bytes)],
    [normal[0], entry(f.metadata.archive, f.bytes, {type: 'symlink'})],
    [entry('npm-artifact.json', JSON.stringify({...f.metadata, archive: `progpu-${manifest.version}.tgz`})), entry(`progpu-${manifest.version}.tgz`, f.bytes)],
    [entry('npm-artifact.json', JSON.stringify({...f.metadata, archive: `wieslawsoltes-progpu-${manifest.version}.tgz`})), entry(`wieslawsoltes-progpu-${manifest.version}.tgz`, f.bytes)],
    [entry('npm-artifact.json', JSON.stringify({...f.metadata, archive: `foreign-progpu-${manifest.version}.tgz`})), entry(`foreign-progpu-${manifest.version}.tgz`, f.bytes)],
    [entry('npm-artifact.json', JSON.stringify({...f.metadata, archive: '../escape.tgz'})), normal[1]]
  ].entries()) {
    const zip = path.join(directory, `invalid-${index}.zip`);
    writeArchive('zip', zip, entries);
    const a = {...artifact, digest: `sha256:${hash(await fs.readFile(zip))}`};
    await assert.rejects(verifyDownloadedArtifact(zip, path.join(directory, `out-${index}`), build, workflow, a, '123'));
    await assert.rejects(fs.stat(path.join(directory, 'escape.tgz')), /ENOENT/);
  }
});

test('real git ancestry rejects a source not merged into origin/main', async t => {
  const directory = await workspace(t);
  const git = args => execFileSync('git', args, {cwd: directory, encoding: 'utf8', timeout: 10_000}).trim();
  git(['init', '-q']); git(['config', 'user.name', 'Offline fixture']); git(['config', 'user.email', 'fixture@example.invalid']);
  git(['commit', '-q', '--allow-empty', '-m', 'base']);
  const ancestor = git(['rev-parse', 'HEAD']);
  git(['update-ref', 'refs/remotes/origin/main', ancestor]);
  git(['commit', '-q', '--allow-empty', '-m', 'not merged']);
  const unmerged = git(['rev-parse', 'HEAD']);
  validateMergedSource(ancestor, directory);
  assert.throws(() => validateMergedSource(unmerged, directory));
  assert.throws(() => validateMergedSource('HEAD; echo injected', directory));
});
