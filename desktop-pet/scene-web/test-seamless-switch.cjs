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
  const messages = [], timers = new Map(), images = [], held = new Map();
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
    document: { getElementById: id => id === 'canvas' ? canvas : video, addEventListener() {} },
    ResizeObserver: class { observe() {} disconnect() {} },
    Blob, DataView, Uint8Array, AbortController, DOMException, performance: { now: () => now },
    setTimeout: (fn, ms) => { const id = nextTimer++; timers.set(id, { fn, ms }); return id; },
    clearTimeout: id => timers.delete(id),
    fetch: async url => {
      const gate = held.get(url);
      if (gate) await gate.promise;
      return { ok: true, arrayBuffer: async () => png(url.includes('idle') ? 1 : url.includes('working') ? 2 : 3) };
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
    idle: action('idle'), working: action('working'), 'walk-left': action('left'),
    video: { loop: { type: 'video', file: 'clip.webm' }, repeat: true }
  } } });
  return {
    send, messages, images, timers, canvas, video,
    diagnostics: () => sandbox.window.sceneDiagnostics(), pixels: () => pixels,
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

async function settled() { for (let i = 0; i < 20; i++) await Promise.resolve(); }
async function play(h, action, id = action) { h.send({ type: 'play', action, requestId: id, repeat: true }); await settled(); }

test('switch holds the last PNG until the replacement first frame, without completing the old run', async () => {
  const h = harness(); await play(h, 'idle');
  assert.equal(h.pixels(), 1);
  const before = h.diagnostics(), release = h.hold('working.png');
  h.send({ type: 'switch', action: 'working', requestId: 'work', repeat: true }); await settled();
  assert.equal(h.pixels(), 1);
  assert.equal(h.diagnostics().displayedAction, 'idle');
  assert.equal(h.diagnostics().action, 'working');
  assert.equal(h.diagnostics().retainedFrameWhileLoading, true);
  assert.equal(h.diagnostics().blankClears, before.blankClears);
  assert.equal(h.diagnostics().preservedTransitions, 1);
  assert.equal(h.images[0].closed, true, 'a retained canvas must not pin old bitmaps');
  release(); await settled();
  assert.equal(h.pixels(), 2);
  assert.equal(h.diagnostics().displayedAction, 'working');
  assert.equal(h.diagnostics().retainedFrameWhileLoading, false);
  assert.equal(h.messages.filter(x => x.type === 'completed').length, 0);
  assert.equal(h.messages.filter(x => x.type === 'playing' && x.requestId === 'work').length, 1);
  h.send({ type: 'dispose' });
});

test('superseded pending decodes never redraw, acknowledge, or complete the current state', async () => {
  const h = harness(); await play(h, 'idle');
  const release = h.hold('working.png');
  h.send({ type: 'switch', action: 'working', requestId: 'work', repeat: true }); await settled();
  h.send({ type: 'switch', action: 'walk-left', requestId: 'left', repeat: true }); await settled();
  assert.equal(h.pixels(), 3);
  release(); await settled();
  assert.equal(h.pixels(), 3);
  assert.equal(h.diagnostics().requestId, 'left');
  assert.equal(h.messages.some(x => ['playing', 'completed', 'error'].includes(x.type) && x.requestId === 'work'), false);
  assert.equal(h.images.filter(image => image.label === 2).every(image => image.closed), true,
    'a cancelled fetch must either avoid decoding or close its late bitmap');
  h.send({ type: 'stop', requestId: 'work', immediate: true });
  assert.equal(h.diagnostics().playing, true, 'a stale stop must not cancel the replacement');
  h.send({ type: 'stop', requestId: 'left', immediate: true });
  assert.equal(h.pixels(), null);
  assert.equal(h.diagnostics().playing, false);
  assert.equal(h.images.every(image => image.closed), true);
});

test('pause still allows a replacement first frame without running its clock', async () => {
  const h = harness(); await play(h, 'idle');
  h.send({ type: 'pause', paused: true });
  h.send({ type: 'switch', action: 'walk-left', requestId: 'left', repeat: true }); await settled();
  assert.equal(h.pixels(), 3);
  assert.equal(h.diagnostics().paused, true);
  assert.equal(h.diagnostics().timerActive, false);
  h.send({ type: 'pause', paused: false });
  assert.equal(h.diagnostics().timerActive, true);
  h.send({ type: 'dispose' });
  assert.equal(h.timers.size, 0);
});

test('video switching snapshots its visible frame before detaching the source', async () => {
  const h = harness(); await play(h, 'video');
  assert.equal(h.video.style.display, 'block');
  const release = h.hold('working.png');
  h.send({ type: 'switch', action: 'working', requestId: 'work', repeat: true }); await settled();
  assert.equal(h.pixels(), 'video-frame');
  assert.equal(h.video.src, undefined);
  assert.equal(h.video.paused, true);
  assert.equal(h.canvas.style.display, 'block');
  assert.equal(h.diagnostics().retainedFrameWhileLoading, true);
  release(); await settled();
  assert.equal(h.pixels(), 2);
  h.send({ type: 'dispose' });
});

test('dispose during a replacement decode releases its late bitmap and does not resurrect playback', async () => {
  const h = harness(); await play(h, 'idle');
  const release = h.hold('working.png');
  h.send({ type: 'switch', action: 'working', requestId: 'work', repeat: true }); await settled();
  h.send({ type: 'dispose' }); release(); await settled();
  assert.equal(h.pixels(), null);
  assert.equal(h.diagnostics().playing, false);
  assert.equal(h.images.every(image => image.closed), true);
  assert.equal(h.messages.some(x => x.type === 'playing' && x.requestId === 'work'), false);
});

test('invalid request ids cannot replace a valid animation', async () => {
  const h = harness(); await play(h, 'idle');
  h.send({ type: 'switch', action: 'working', requestId: '', repeat: true }); await settled();
  assert.equal(h.diagnostics().requestId, 'idle');
  assert.equal(h.pixels(), 1);
  assert.equal(h.messages.at(-1).type, 'error');
  h.send({ type: 'dispose' });
});

test('natural switch completion retains the final frame while the default state resumes', async () => {
  const h = harness();
  h.send({ type: 'switch', action: 'walk-left', requestId: 'preview', repeat: false }); await settled();
  const before = h.diagnostics();
  h.tick(125); await settled();
  assert.equal(h.diagnostics().playing, false);
  assert.equal(h.pixels(), 3);
  assert.equal(h.diagnostics().blankClears, before.blankClears);
  assert.equal(h.diagnostics().displayedAction, 'walk-left');
  assert.equal(h.images.every(image => image.closed), true, 'completion keeps canvas pixels without decoded image resources');
  assert.equal(h.messages.filter(x => x.type === 'completed' && x.requestId === 'preview').length, 1);
  const release = h.hold('idle.png');
  h.send({ type: 'switch', action: 'idle', requestId: 'default', repeat: true }); await settled();
  assert.equal(h.pixels(), 3);
  assert.equal(h.diagnostics().retainedFrameWhileLoading, true);
  release(); await settled();
  assert.equal(h.pixels(), 1);
  assert.equal(h.diagnostics().blankClears, before.blankClears);
  h.send({ type: 'dispose' });
});

test('ordinary play completion clears while explicit stop and dispose clear retained switch completion', async () => {
  const ordinary = harness();
  ordinary.send({ type: 'play', action: 'idle', requestId: 'ordinary', repeat: false }); await settled();
  ordinary.tick(125); await settled();
  assert.equal(ordinary.pixels(), null);
  assert.equal(ordinary.diagnostics().hasFrame, false);
  for (const type of ['stop', 'dispose']) {
    const h = harness();
    h.send({ type: 'switch', action: 'idle', requestId: 'preview', repeat: false }); await settled();
    h.tick(125); await settled();
    assert.equal(h.pixels(), 1);
    h.send({ type, requestId: 'preview', immediate: true });
    assert.equal(h.pixels(), null);
    assert.equal(h.diagnostics().hasFrame, false);
  }
  const graceful = harness();
  graceful.send({ type: 'switch', action: 'idle', requestId: 'loop', repeat: true }); await settled();
  graceful.send({ type: 'stop', requestId: 'loop', immediate: false });
  graceful.tick(125); await settled();
  assert.equal(graceful.pixels(), null, 'an explicit graceful stop must not retain the final frame');
});
