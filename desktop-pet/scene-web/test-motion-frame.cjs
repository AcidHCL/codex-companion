// Deterministic renderer lifecycle checks. The media decoder and clock are
// controlled so that a pending decode cannot hide a one-frame blank or race.
const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');

const source = fs.readFileSync(path.join(__dirname, '../assets/scene-runtime/scene.js'), 'utf8')
  .replace("import {decodeApng} from './apng.js';", '');

function harness() {
  const messages = [], timers = new Map(), images = [], held = new Map(), documentListeners = new Map();
  let listener, nextTimer = 1, pixels = null, now = 0;
  const context2d = {
    clearRect() { pixels = null; },
    drawImage(image) { assert.equal(image.closed, undefined, 'must not draw a released bitmap'); pixels = image.label; }
  };
  const canvas = { width: 512, height: 512, style: {}, getContext: () => context2d,
    getBoundingClientRect: () => ({ width: 512, height: 512 }) };
  const events = new Map();
  const video = { label: 'video-frame', style: {}, currentTime: 0, paused: true, muted: true,
    readyState: 0, videoWidth: 2, videoHeight: 2, duration: 1,
    addEventListener(name, callback) { if (!events.has(name)) events.set(name, new Set()); events.get(name).add(callback); },
    removeEventListener(name, callback) { events.get(name)?.delete(callback); },
    pause() { this.paused = true; }, play() { this.paused = false; return Promise.resolve(); },
    removeAttribute(name) { delete this[name]; },
    load() {
      if (!this.src) { this.readyState = 0; return; }
      queueMicrotask(() => {
        this.readyState = 2;
        for (const name of ['loadedmetadata', 'loadeddata']) for (const callback of [...(events.get(name) ?? [])]) callback();
      });
    }
  };
  function png(label) {
    const bytes = new Uint8Array(33), view = new DataView(bytes.buffer);
    bytes.set([137, 80, 78, 71, 13, 10, 26, 10]);
    view.setUint32(8, 13); view.setUint32(12, 0x49484452);
    view.setUint32(16, 2); view.setUint32(20, 2); bytes[28] = label;
    return bytes.buffer;
  }
  const sandbox = {
    window: { devicePixelRatio: 1, chrome: { webview: { postMessage: x => messages.push(x), addEventListener: (_, fn) => listener = fn } } },
    document: { hidden: false, getElementById: id => id === 'canvas' ? canvas : video,
      addEventListener(name, listener) { documentListeners.set(name, listener); } },
    ResizeObserver: class { observe() {} disconnect() {} },
    Blob, DataView, Uint8Array, AbortController, DOMException, performance: { now: () => now },
    setTimeout: (fn, ms) => { const id = nextTimer++; timers.set(id, { fn, ms }); return id; },
    clearTimeout: id => timers.delete(id),
    fetch: async url => {
      const gate = held.get(url);
      if (gate) await gate.promise;
      return { ok: true, arrayBuffer: async () => png(url.includes('jump') ? 10 + Number(url.match(/frame-(\d)/)?.[1] ?? 0) : url.includes('idle') ? 1 : url.includes('working') ? 2 : 3) };
    },
    createImageBitmap: async blob => {
      const bytes = new Uint8Array(await blob.arrayBuffer());
      const image = { width: 2, height: 2, label: bytes[28], close() { this.closed = true; } };
      images.push(image); return image;
    }
  };
  vm.runInNewContext(source, sandbox, { filename: 'scene.js' });
  const send = data => listener({ data });
  const action = name => ({ loop: { type: 'frames', frames: [name + '.png'], fps: 8 }, repeat: true });
  send({ type: 'load', requestId: 'load', pack: { actions: {
    idle: action('idle'), working: action('working'), 'walk-left': action('left'), land: action('land'),
    'jump-right': { loop: { type: 'frames', frames: Array.from({length: 6}, (_, i) => 'jump/frame-' + i + '.png'), fps: 10 }, repeat: false },
    video: { loop: { type: 'video', file: 'clip.webm' }, repeat: true }
  } } });
  return {
    send, messages, images, timers, canvas, video,
    diagnostics: () => sandbox.window.sceneDiagnostics(), pixels: () => pixels,
    occlude(hidden) { sandbox.document.hidden = hidden; documentListeners.get('visibilitychange')?.(); },
    tick(ms) {
      now += ms;
      const callbacks = [...timers.values()]; timers.clear();
      for (const entry of callbacks) entry.fn();
    },
    hold(file) {
      let release; const promise = new Promise(resolve => release = resolve);
      held.set('/media/' + file, { promise }); return release;
    }
  };
}

async function settled() { for (let i = 0; i < 100; i++) await Promise.resolve(); }
async function play(h, action, id = action) { h.send({ type: 'play', action, requestId: id, repeat: true }); await settled(); }

test('external motion set before decode selects the requested first frame and holds a one-shot', async () => {
  const h = harness();
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 3 });
  await play(h, 'jump-right');
  assert.equal(h.pixels(), 13);
  assert.equal(h.diagnostics().frame, 3);
  assert.equal(h.diagnostics().motionControlled, true);
  assert.equal(h.diagnostics().timerActive, false);
  h.tick(10000); await settled();
  assert.equal(h.pixels(), 13);
  assert.equal(h.diagnostics().playing, true);
  assert.equal(h.messages.some(x => x.type === 'completed'), false);
  h.send({ type: 'dispose' });
});

test('motion frame updates draw immediately, clamp to the decoded range, and release to normal completion', async () => {
  const h = harness(); await play(h, 'jump-right');
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 2 });
  assert.equal(h.pixels(), 12);
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 299 });
  assert.equal(h.pixels(), 15);
  assert.equal(h.diagnostics().frame, 5);
  h.send({ type: 'motionFrame', action: null, frame: 0 });
  assert.equal(h.diagnostics().motionControlled, false);
  assert.equal(h.diagnostics().timerActive, true);
  h.tick(100); await settled();
  assert.equal(h.messages.filter(x => x.type === 'completed').length, 1);
  assert.equal(h.diagnostics().playing, false);
  h.send({ type: 'dispose' });
});

test('pending controls for another action leave an idle/emotion timeline running', async () => {
  const h = harness(); await play(h, 'idle');
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 4 });
  assert.equal(h.diagnostics().motionControlled, false);
  assert.equal(h.diagnostics().timerActive, true);
  h.tick(125); await settled();
  assert.equal(h.diagnostics().cycles, 1);
  assert.equal(h.pixels(), 1);
  h.send({ type: 'switch', action: 'jump-right', requestId: 'jump', repeat: false }); await settled();
  assert.equal(h.pixels(), 14);
  h.send({ type: 'switch', action: 'working', requestId: 'work', repeat: true }); await settled();
  assert.equal(h.pixels(), 2);
  assert.equal(h.diagnostics().timerActive, true);
  assert.equal(h.diagnostics().motionControlled, false);
  h.send({ type: 'dispose' });
});

test('a pending decode uses the latest motion frame without clearing the previous drawing', async () => {
  const h = harness(); await play(h, 'idle');
  const release = h.hold('jump/frame-0.png');
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 0 });
  h.send({ type: 'switch', action: 'jump-right', requestId: 'jump', repeat: false }); await settled();
  assert.equal(h.pixels(), 1);
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 4 });
  release(); await settled();
  assert.equal(h.pixels(), 14);
  assert.equal(h.diagnostics().frame, 4);
  assert.equal(h.diagnostics().timerActive, false);
  h.send({ type: 'dispose' });
});

test('a superseded controlled decode cannot draw over a replacement emotion', async () => {
  const h = harness(); await play(h, 'idle');
  const release = h.hold('jump/frame-0.png');
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 2 });
  h.send({ type: 'switch', action: 'jump-right', requestId: 'jump', repeat: false }); await settled();
  h.send({ type: 'switch', action: 'working', requestId: 'work', repeat: false }); await settled();
  release(); await settled();
  assert.equal(h.pixels(), 2);
  assert.equal(h.diagnostics().requestId, 'work');
  assert.equal(h.messages.some(x => x.type === 'playing' && x.requestId === 'jump'), false);
  h.send({ type: 'dispose' });
});

test('pause/resume preserves external control and graceful stop cannot hang on a held frame', async () => {
  const h = harness();
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 5 });
  await play(h, 'jump-right');
  h.send({ type: 'pause', paused: true });
  h.send({ type: 'pause', paused: false });
  assert.equal(h.diagnostics().timerActive, false);
  assert.equal(h.pixels(), 15);
  h.send({ type: 'stop', requestId: 'jump-right', immediate: false });
  assert.equal(h.diagnostics().timerActive, true);
  h.tick(100); await settled();
  assert.equal(h.diagnostics().playing, false);
  assert.equal(h.pixels(), null);
  h.send({ type: 'dispose' });
});

test('invalid motion controls cannot disturb the active timeline or pixels', async () => {
  const h = harness(); await play(h, 'idle');
  for (const frame of [-1, 720, NaN, 1.5]) h.send({ type: 'motionFrame', action: 'idle', frame });
  assert.equal(h.pixels(), 1);
  assert.equal(h.diagnostics().timerActive, true);
  assert.equal(h.diagnostics().motionAction, null);
  assert.equal(h.messages.filter(x => x.type === 'error').length, 4);
  h.send({ type: 'dispose' });
});

test('browser occlusion cannot stall a timed landing after an externally driven jump', async () => {
  const h = harness();
  h.send({ type: 'motionFrame', action: 'jump-right', frame: 5 });
  await play(h, 'jump-right');
  h.occlude(true);
  assert.equal(h.diagnostics().paused, false, 'document visibility cannot independently change the host pause policy');
  h.send({ type: 'motionFrame', action: null, frame: 0 });
  h.send({ type: 'switch', action: 'land', requestId: 'landing', repeat: false }); await settled();
  assert.equal(h.diagnostics().timerActive, true);
  h.tick(125); await settled();
  assert.equal(h.messages.filter(x => x.type === 'completed' && x.requestId === 'landing').length, 1);
  assert.equal(h.diagnostics().playing, false);
  h.send({ type: 'switch', action: 'idle', requestId: 'resumed', repeat: true }); await settled();
  assert.equal(h.diagnostics().timerActive, true);
  h.tick(125); await settled();
  assert.equal(h.diagnostics().cycles, 1, 'ordinary idle continues even when occluded');
  h.send({ type: 'dispose' });
});

test('only an explicit host resume restarts a paused landing, regardless of visibility events', async () => {
  const h = harness();
  h.send({ type: 'switch', action: 'land', requestId: 'landing', repeat: false }); await settled();
  h.send({ type: 'pause', paused: true });
  h.occlude(true); h.occlude(false);
  h.tick(2000); await settled();
  assert.equal(h.diagnostics().paused, true);
  assert.equal(h.diagnostics().timerActive, false);
  assert.equal(h.diagnostics().playing, true);
  assert.equal(h.messages.some(x => x.type === 'completed'), false);
  h.send({ type: 'pause', paused: false });
  assert.equal(h.diagnostics().timerActive, true);
  h.tick(125); await settled();
  assert.equal(h.diagnostics().playing, false);
  assert.equal(h.messages.filter(x => x.type === 'completed' && x.requestId === 'landing').length, 1);
  h.send({ type: 'dispose' });
});

