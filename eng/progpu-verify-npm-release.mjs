// Verify an immutable successful Build artifact; never execute its package code.
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import * as fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

export const repository = 'wieslawsoltes/ProGPU';
const workflowPath = '.github/workflows/build.yml';
const artifactName = 'progpu-npm-package';
const limit = 256 * 1024 * 1024;
const sha = value => createHash('sha256').update(value).digest('hex');
const id = value => { assert.match(String(value), /^[1-9][0-9]{0,14}$/); return String(value); };
const commit = value => assert.match(value, /^[a-f0-9]{40}$/);
const digest = value => assert.match(value, /^[a-f0-9]{64}$/);
const run = (command, args, options = {}) => execFileSync(command, args, {
  encoding: 'utf8', timeout: 60_000, maxBuffer: limit, stdio: ['pipe', 'pipe', 'pipe'], ...options
});

// Python's standard archive readers handle real npm PAX/GNU tar metadata and
// GitHub ZIP64 archives. Nothing is extracted from the package tarball. ZIP
// extraction admits exactly two nonempty regular files into a fresh directory.
const archiveReader = String.raw`
import hashlib, io, json, os, re, sys, tarfile, zipfile, zlib
LIMIT = 256 * 1024 * 1024
def require(ok, message):
    if not ok: raise ValueError(message)
def safe(name):
    return bool(re.fullmatch(r'[A-Za-z0-9_.+/-]+', name)) and not name.startswith('/') and all(p not in ('', '.', '..') for p in name.split('/'))
if sys.argv[1] == 'zip':
    with zipfile.ZipFile(sys.argv[2]) as archive:
        entries = archive.infolist()
        require(len(entries) == 2 and len(set(e.filename for e in entries)) == 2, 'Artifact must have exactly two unique files')
        require(all(not e.is_dir() and ((e.external_attr >> 16) & 0o170000) in (0, 0o100000) and not e.flag_bits & 1 for e in entries), 'Artifact has a nonregular/encrypted member')
        require(all(0 < e.file_size <= LIMIT for e in entries) and sum(e.file_size for e in entries) <= LIMIT, 'Artifact exceeds size bound')
        require('npm-artifact.json' in archive.namelist(), 'Missing npm-artifact.json')
        require(archive.getinfo('npm-artifact.json').file_size <= 1024 * 1024, 'Oversized artifact metadata')
        metadata = json.loads(archive.read('npm-artifact.json'))
        name = metadata.get('archive', '')
        require(isinstance(name, str) and bool(re.fullmatch(r'progpu-[A-Za-z0-9.+-]+\.tgz', name)), 'Unsafe archive filename')
        require(set(archive.namelist()) == {'npm-artifact.json', name}, 'Unexpected artifact member/path')
        for filename in ('npm-artifact.json', name):
            with open(os.path.join(sys.argv[3], filename), 'xb') as destination:
                destination.write(archive.read(filename))
        print(json.dumps(metadata))
else:
    files, total = {}, 0
    with open(sys.argv[2], 'rb') as source: compressed = source.read(LIMIT + 1)
    require(len(compressed) <= LIMIT, 'Package archive exceeds size bound')
    inflater = zlib.decompressobj(16 + zlib.MAX_WBITS)
    expanded = inflater.decompress(compressed, LIMIT + 1)
    require(len(expanded) <= LIMIT and inflater.eof and not inflater.unused_data, 'Oversized/truncated/concatenated gzip archive')
    with tarfile.open(fileobj=io.BytesIO(expanded), mode='r:') as archive:
        for entry in archive:
            require(len(files) < 1024 and entry.isfile() and not entry.issparse(), 'Package has a nonregular/sparse member or too many files')
            require(safe(entry.name) and entry.name.startswith('package/'), 'Unsafe package path')
            name = entry.name[len('package/'):]
            require(name not in files, 'Duplicate package path')
            total += entry.size
            require(0 < entry.size <= LIMIT and total <= LIMIT, 'Package exceeds size bound')
            if name in ('package.json', 'build-info.json'): require(entry.size <= 1024 * 1024, 'Oversized package metadata')
            data = archive.extractfile(entry).read()
            require(len(data) == entry.size, 'Truncated package member')
            files[name] = {'sha256': hashlib.sha256(data).hexdigest(), 'size': len(data)}
            if name in ('package.json', 'build-info.json'): files[name]['json'] = json.loads(data)
            if name == 'progpu-native.wasm': require(len(data) > 64 and data[:8] == b'\0asm\1\0\0\0', 'Invalid WebAssembly payload')
        require(not any(expanded[archive.offset:]), 'Unexpected data after package tar end')
    print(json.dumps(files))
`;

export function inspectArchive(filename) {
  return JSON.parse(run('python3', ['-c', archiveReader, 'tar', filename]));
}

export function validateRun(build, workflow, artifact, requestedRunId) {
  assert.equal(build.repository?.full_name, repository, 'Wrong Build repository');
  assert.equal(build.head_repository?.full_name, repository, 'Wrong source repository; build merged main instead');
  assert.equal(id(build.id), id(requestedRunId), 'Wrong Build run');
  id(build.run_attempt);
  commit(build.head_sha);
  assert.equal(build.status, 'completed', 'Build is incomplete');
  assert.equal(build.conclusion, 'success', 'The entire Build must have succeeded');
  assert.ok(['push', 'pull_request', 'workflow_dispatch'].includes(build.event), 'Unexpected Build event');
  assert.equal(workflow.name, 'Build', 'Wrong workflow name');
  assert.equal(workflow.path, workflowPath, 'Wrong workflow path');
  assert.equal(build.path, workflowPath, 'Wrong run workflow path');
  assert.equal(id(build.workflow_id), id(workflow.id), 'Wrong workflow ID');
  assert.equal(artifact.name, artifactName, 'Wrong artifact name');
  id(artifact.id);
  assert.equal(artifact.expired, false, 'Artifact expired');
  assert.ok(Date.parse(artifact.expires_at) > Date.now(), 'Artifact expiry is invalid/past');
  assert.match(artifact.digest, /^sha256:[a-f0-9]{64}$/, 'Missing artifact SHA256 digest');
  assert.equal(id(artifact.workflow_run?.id), id(build.id), 'Artifact belongs to another run');
  assert.equal(artifact.workflow_run.head_sha, build.head_sha, 'Artifact source differs from Build');
  assert.equal(id(artifact.workflow_run.repository_id), id(build.repository.id), 'Wrong artifact repository');
  assert.equal(id(artifact.workflow_run.head_repository_id), id(build.head_repository.id), 'Wrong artifact source repository');
}

export function validateMergedSource(sourceCommit, repo) {
  commit(sourceCommit);
  run('git', ['merge-base', '--is-ancestor', sourceCommit, 'origin/main'], {cwd: repo});
}

export function validatePackage(metadata, files, archiveBytes, build) {
  assert.equal(metadata.schemaVersion, 1, 'Unknown artifact schema');
  assert.equal(metadata.name, 'progpu', 'Wrong npm package');
  assert.match(metadata.version, /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*$/, 'A fixed prerelease SemVer is required');
  assert.ok(metadata.version.length <= 128, 'Oversized version');
  assert.equal(metadata.archive, `progpu-${metadata.version}.tgz`, 'Archive name/version mismatch');
  digest(metadata.sha256);
  assert.equal(sha(archiveBytes), metadata.sha256, 'Package archive SHA256 mismatch');
  assert.equal(metadata.integrity, `sha512-${createHash('sha512').update(archiveBytes).digest('base64')}`, 'Package integrity mismatch');
  commit(metadata.sourceCommit);
  assert.equal(metadata.sourceCommit, build.head_sha, 'Package source differs from Build');
  assert.equal(metadata.sourceDirty, false, 'Dirty source cannot be published');
  assert.equal(id(metadata.buildRunId), id(build.id), 'Package Build run mismatch');
  assert.equal(id(metadata.buildRunAttempt), id(build.run_attempt), 'Package Build attempt mismatch');
  assert.equal(metadata.emscripten, '4.0.18', 'Unexpected Emscripten version');
  digest(metadata.emdawnPortSha256);
  assert.ok(metadata.licenses && typeof metadata.licenses === 'object' && !Array.isArray(metadata.licenses), 'Missing license inventory');
  const licenses = Object.keys(metadata.licenses);
  assert.ok(licenses.some(name => name.startsWith('licenses/emdawnwebgpu/')), 'Missing Emdawnwebgpu notices');
  for (const suffix of ['LICENSE', 'system/lib/compiler-rt/LICENSE.TXT', 'system/lib/libc/musl/COPYRIGHT',
    'system/lib/libcxx/LICENSE.TXT', 'system/lib/libcxxabi/LICENSE.TXT', 'system/lib/libunwind/LICENSE.TXT',
    'system/lib/llvm-libc/LICENSE.TXT', 'system/lib/mimalloc/LICENSE']) {
    assert.ok(licenses.includes(`licenses/emscripten/${suffix}`), `Missing toolchain notice: ${suffix}`);
  }
  for (const name of licenses) {
    assert.match(name, /^licenses\/(emscripten|emdawnwebgpu)\/[A-Za-z0-9_.+/-]+$/);
    assert.ok(!name.split('/').some(part => ['', '.', '..'].includes(part)), 'Unsafe license path');
    digest(metadata.licenses[name]);
    assert.equal(files[name]?.sha256, metadata.licenses[name], `License hash mismatch: ${name}`);
  }
  const required = ['package.json', 'index.js', 'scene.js', 'index.d.ts', 'progpu-native.mjs', 'progpu-native.wasm',
    'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'build-info.json', ...licenses];
  assert.deepEqual(Object.keys(files).sort(), required.sort(), 'Unexpected/incomplete package inventory');
  const manifest = files['package.json'].json;
  assert.equal(manifest.name, metadata.name);
  assert.equal(manifest.version, metadata.version, 'Inner/outer package version mismatch');
  assert.equal(manifest.type, 'module');
  assert.ok(!Object.hasOwn(manifest, 'scripts') && !manifest.private && !manifest.bin, 'Package must not have lifecycle scripts, bin, or private selection');
  assert.equal(manifest.main, './index.js');
  assert.equal(manifest.types, './index.d.ts');
  assert.deepEqual(manifest.exports, {'.': {types: './index.d.ts', import: './index.js'},
    './native': './progpu-native.mjs', './progpu-native.wasm': './progpu-native.wasm', './package.json': './package.json'});
  assert.deepEqual(manifest.publishConfig, {access: 'public', registry: 'https://registry.npmjs.org/', tag: 'next'});
  assert.deepEqual(files['build-info.json'].json, {
    sourceCommit: metadata.sourceCommit, sourceDirty: false, emscripten: metadata.emscripten,
    emdawnPortSha256: metadata.emdawnPortSha256, buildRunId: metadata.buildRunId,
    buildRunAttempt: metadata.buildRunAttempt, licenses: metadata.licenses
  }, 'Inner build-info differs from artifact metadata');
}

export async function verifyDownloadedArtifact(zipFile, output, build, workflow, artifact, requestedRunId) {
  validateRun(build, workflow, artifact, requestedRunId);
  const zip = await fs.readFile(zipFile);
  assert.ok(zip.length > 0 && zip.length <= limit, 'Artifact ZIP exceeds size bound');
  assert.equal(`sha256:${sha(zip)}`, artifact.digest, 'GitHub artifact ZIP digest mismatch');
  await fs.mkdir(output); // Fail rather than overwrite a prior staging directory.
  const metadata = JSON.parse(run('python3', ['-c', archiveReader, 'zip', zipFile, output]));
  const archivePath = path.join(output, metadata.archive);
  const archiveBytes = await fs.readFile(archivePath);
  const files = inspectArchive(archivePath);
  validatePackage(metadata, files, archiveBytes, build);
  return {metadata, files, archivePath};
}

async function main() {
  const args = process.argv.slice(2);
  assert.equal(args.length, 4, 'Usage: node eng/progpu-verify-npm-release.mjs --run-id ID --output NEW_DIRECTORY');
  assert.equal(args[0], '--run-id');
  assert.equal(args[2], '--output');
  const runId = id(args[1]);
  assert.equal(process.env.GITHUB_REPOSITORY, repository, 'Release must run in the canonical repository');
  assert.equal(process.env.GITHUB_REF, 'refs/heads/main', 'Release must be invoked from main');
  assert.equal(process.env.GITHUB_EVENT_NAME, 'workflow_dispatch', 'Release must be explicitly dispatched');
  const output = path.resolve(args[3]);
  const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const api = endpoint => JSON.parse(run('gh', ['api', `repos/${repository}/${endpoint}`], {cwd: repo}));
  const build = api(`actions/runs/${runId}`);
  const workflow = api('actions/workflows/build.yml');
  const artifacts = [];
  for (let page = 1; ; page++) {
    assert.ok(page <= 100, 'Artifact listing exceeded page bound');
    const batch = api(`actions/runs/${runId}/artifacts?per_page=100&page=${page}`);
    artifacts.push(...batch.artifacts.filter(item => item.name === artifactName));
    if (page * 100 >= batch.total_count) break;
  }
  assert.equal(artifacts.length, 1, 'Expected exactly one npm artifact from this Build run');
  const artifact = artifacts[0];
  validateRun(build, workflow, artifact, runId);
  // A successful PR Build is eligible only after that exact source is merged.
  validateMergedSource(build.head_sha, repo);
  for (const [variable, actual] of [['PROGPU_EXPECTED_ARTIFACT_ID', String(artifact.id)],
    ['PROGPU_EXPECTED_ATTEMPT', String(build.run_attempt)]]) {
    if (process.env[variable] !== undefined)
      assert.equal(process.env[variable], actual, `Verified selection changed: ${variable}`);
  }
  const download = run('gh', ['api', `repos/${repository}/actions/artifacts/${id(artifact.id)}/zip`], {encoding: null, cwd: repo});
  assert.ok(download.length <= limit, 'Artifact ZIP exceeds size bound');
  const zipPath = `${output}.zip`;
  await fs.writeFile(zipPath, download, {flag: 'wx'});
  const result = await verifyDownloadedArtifact(zipPath, output, build, workflow, artifact, runId);
  if (process.env.PROGPU_EXPECTED_ARCHIVE_SHA256 !== undefined)
    assert.equal(result.metadata.sha256, process.env.PROGPU_EXPECTED_ARCHIVE_SHA256, 'Verified package changed');
  // Bind hand-authored package assets to the merged source, not just its label.
  for (const name of ['package.json', 'index.js', 'scene.js', 'index.d.ts', 'README.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE']) {
    const sourcePath = name === 'LICENSE' ? 'LICENSE' : `src/ProGPU.Native/browser/npm/${name}`;
    const sourceBytes = run('git', ['show', `${build.head_sha}:${sourcePath}`], {encoding: null, cwd: repo});
    assert.equal(result.files[name].sha256, sha(sourceBytes), `Archive differs from merged source: ${name}`);
  }
  const latest = api(`actions/runs/${runId}`);
  validateRun(latest, workflow, artifact, runId);
  assert.equal(latest.run_attempt, build.run_attempt, 'Build was rerun during verification');
  const verified = {repository, workflow: workflowPath, runId, attempt: id(build.run_attempt),
    artifactId: id(artifact.id), artifactDigest: artifact.digest, sourceCommit: build.head_sha,
    archive: result.metadata.archive, sha256: result.metadata.sha256, version: result.metadata.version, tag: 'next'};
  await fs.writeFile(path.join(output, 'release-verification.json'), JSON.stringify(verified, null, 2) + '\n', {flag: 'wx'});
  if (process.env.GITHUB_OUTPUT) {
    // Only validated single-line identifiers are emitted, never raw API strings.
    await fs.appendFile(process.env.GITHUB_OUTPUT,
      `artifact_id=${verified.artifactId}\nattempt=${verified.attempt}\narchive_sha256=${verified.sha256}\narchive_name=${verified.archive}\nsource_commit=${verified.sourceCommit}\nversion=${verified.version}\n`);
  }
  console.log(JSON.stringify(verified, null, 2));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => { console.error(`npm release verification failed: ${error.message}`); process.exitCode = 1; });
}
