// Authored pure lifecycle controls over the actual browser runtime functions.
// No DOM, .NET, navigator.gpu, real device, adapter or renderer is started.
// Execution, including this script, remains deferred to final validation.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const context = vm.createContext({
  TextDecoder,
  MessageChannel: class { port1 = {}; port2 = {}; },
  addEventListener() {}
});
vm.runInContext('globalThis.WorkerGlobalScope = class { static [Symbol.hasInstance]() { return true; } };', context);
const source = await readFile(new URL('../src/ProGPU.Browser/BrowserAssets/progpu-browser.js', import.meta.url), 'utf8');
const module = new vm.SourceTextModule(source +
  '\nexport { state, beginTextureLimitsInitialization, publishTextureLimitsDevice, getTextureLimitsGeneration, getTextureDimension2D };',
  { context });
await module.link(specifier => {
  const names = specifier.endsWith('/dotnet.js') ? ['dotnet'] :
    ['measurePhysicalCanvas', 'requestProGpuWebGpuDevice', 'updateProGpuVisualViewport'];
  return new vm.SyntheticModule(names, function () {
    for (const name of names) this.setExport(name, () => { throw new Error('Unexpected browser startup'); });
  }, { context });
});
await module.evaluate();
const { state, beginTextureLimitsInitialization: begin, publishTextureLimitsDevice: publish,
  getTextureLimitsGeneration: generation, getTextureDimension2D: maximum } = module.namespace;

function device(limit) {
  let lose;
  const lost = new Promise(resolve => { lose = resolve; });
  return { value: { limits: { maxTextureDimension2D: limit }, lost }, lose };
}
function install(entry) {
  const token = begin();
  state.device = entry.value;
  assert.equal(publish(entry.value, token), true);
  return token;
}

assert.equal(generation(), 0);
assert.equal(maximum(1), 0);
const first = device(6144);
const firstToken = install(first);
// Adapter, requested and caller diagnostic limits are not device limits.
state.adapter = { limits: { maxTextureDimension2D: 32768 } };
state.capabilities = { maxTextureDimension2D: 16384 };
assert.equal(generation(), firstToken);
assert.equal(maximum(firstToken), 6144);
for (const token of [0, -1, firstToken + 1, 1.5, NaN, Infinity, 9007199254740992])
  assert.equal(maximum(token), 0);

state.device = device(8192).value;
assert.equal(generation(), 0, 'A replacement object cannot reuse the old generation');
assert.equal(maximum(firstToken), 0);
state.device = first.value;
const pending = begin();
assert.equal(generation(), 0, 'A replacement attempt invalidates limits before initialization completes');
assert.equal(maximum(firstToken), 0);
assert.equal(publish(first.value, firstToken), false, 'A late old initializer cannot republish');

const second = device(8192);
state.device = second.value;
assert.equal(publish(second.value, pending), true);
assert.equal(maximum(pending), 8192);
first.lose({ reason: 'destroyed' });
await Promise.resolve();
assert.equal(maximum(pending), 8192, 'Loss from a previous generation cannot invalidate the replacement');
second.lose({ reason: 'unknown' });
await Promise.resolve();
assert.equal(generation(), 0);
assert.equal(maximum(pending), 0, 'Observed loss cannot retain a cached numeric limit');

const destroyed = device(4096);
const destroyedToken = install(destroyed);
destroyed.lose({ reason: 'destroyed' });
await Promise.resolve();
assert.equal(maximum(destroyedToken), 0, 'Intentional destruction revokes the capability too');

for (const invalid of [0, -1, 3.5, undefined, NaN, Infinity, 4294967296]) {
  const token = install(device(invalid));
  assert.equal(maximum(token), 0);
}
const unsignedMaximum = install(device(4294967295));
assert.equal(maximum(unsignedMaximum), 4294967295);

const workerToken = install(device(12288));
state.worker = {};
assert.equal(generation(), 0);
assert.equal(maximum(workerToken), 0, 'A stale main-realm device is not worker-device proof');
state.worker = null;

const reentrant = device(1);
const reentrantToken = install(reentrant);
Object.defineProperty(reentrant.value, 'limits', { get() {
  begin();
  return { maxTextureDimension2D: 8192 };
} });
assert.equal(maximum(reentrantToken), 0, 'Reentrant replacement must not publish stale outputs');

const throwing = device(1);
const throwingToken = install(throwing);
Object.defineProperty(throwing.value, 'limits', { get() { throw new Error('unavailable'); } });
assert.equal(maximum(throwingToken), 0);

state.deviceGeneration = Number.MAX_SAFE_INTEGER;
assert.throws(() => begin(), /generation is exhausted/);
assert.equal(generation(), 0, 'Generation exhaustion cannot revive the previous owner');
console.log('Browser texture-limit lifecycle controls passed; no GPU/runtime qualification.');
