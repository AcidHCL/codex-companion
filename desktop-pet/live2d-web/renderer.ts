import { CubismPose } from './vendor/Framework/src/effect/cubismpose';
import { CubismFramework, LogLevel, Option } from './vendor/Framework/src/live2dcubismframework';
import { CubismMoc } from './vendor/Framework/src/model/cubismmoc';
import { CubismModel } from './vendor/Framework/src/model/cubismmodel';
import { CubismRenderer_WebGL } from './vendor/Framework/src/rendering/cubismrenderer_webgl';
import { CubismShaderManager_WebGL } from './vendor/Framework/src/rendering/cubismshader_webgl';
import { CubismMatrix44 } from './vendor/Framework/src/math/cubismmatrix44';
import { StandardIdle } from './standard-idle';

type HostMessage = { type?: string; url?: string; requestId?: string; paused?: boolean; enabled?: boolean };
type Bounds = { left: number; right: number; bottom: number; top: number };
type Loaded = { moc: CubismMoc; model: CubismModel; renderer: CubismRenderer_WebGL;
  textures: WebGLTexture[]; bounds: Bounds; url: string; requestId: string; idle: StandardIdle };
declare global {
  interface Window {
    chrome?: { webview?: { postMessage: (value: unknown) => void;
      addEventListener: (name: string, listener: (event: { data: unknown }) => void) => void } };
    petDiagnostics: () => Record<string, unknown>;
  }
}

const ORIGIN = 'https://aipet.invalid';
const SHADERS = `${ORIGIN}/runtime/shaders/`;
const MAX_FILE = 128 * 1024 * 1024;
const canvas = document.getElementById('pet') as HTMLCanvasElement;
// The host passes only its normalized process-wide language in the fixed
// local entry URL. Resource paths and the host message protocol are unchanged.
const english = new URLSearchParams(location.search).get('lang') === 'en-US';
const text = (chinese: string, en: string) => english ? en : chinese;
document.documentElement.lang = english ? 'en-US' : 'zh-CN';
document.title = text('AI 桌宠 Live2D', 'AI Pet Live2D');
canvas.setAttribute('aria-label', text('Live2D 角色', 'Live2D character'));
let gl: WebGLRenderingContext;
let current: Loaded | null = null;
let loading: AbortController | null = null;
let generation = 0;
let paused = false;
let motionEnabled = true;
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
let initialized = false;
let disposed = false;
let contextLost = false;
let frames = 0;
let pendingFrame = 0;
let lastFrameAt: number | null = null;
let animationFailed = false;
let dirty = true;
let lastRequestId = '';
const post = (value: Record<string, unknown>) => window.chrome?.webview?.postMessage(value);
const errorMessage = (error: unknown) => error instanceof Error ? error.message.slice(0, 500) : text('Live2D 加载失败。', 'Live2D failed to load.');
const fail = (error: unknown, requestId = '') => post({ type: 'error', requestId, message: errorMessage(error) });

function checkedModelUrl(raw: unknown): URL {
  if (typeof raw !== 'string' || raw.length > 4096) throw new Error(text('模型路径无效。', 'Invalid model path.'));
  const url = new URL(raw);
  if (url.origin !== ORIGIN || !url.pathname.startsWith('/model/') || url.search || url.hash ||
      url.username || url.password || !url.pathname.toLowerCase().endsWith('.model3.json'))
    throw new Error(text('只允许加载本机映射目录中的 .model3.json。', 'Only .model3.json files in the mapped local directory can be loaded.'));
  checkedPath(url);
  return url;
}
function checkedPath(url: URL): void {
  const path = decodeURIComponent(url.pathname);
  if (!path.startsWith('/model/') || /[\\\x00-\x1f:]/.test(path) ||
      path.split('/').some(part => part === '.' || part === '..'))
    throw new Error(text('模型资源路径越出允许目录。', 'The model resource path is outside the allowed directory.'));
}
function resourceUrl(base: URL, value: unknown, extension: RegExp): URL {
  if (typeof value !== 'string' || !value || value.length > 4096 ||
      /^[\/\\]/.test(value) || /^[a-z][a-z0-9+.-]*:/i.test(value) || /[\\?#\x00-\x1f]/.test(value))
    throw new Error(text('模型资源必须使用本地相对路径。', 'Model resources must use local relative paths.'));
  const decoded = decodeURIComponent(value);
  if (decoded.split('/').some(part => part === '..') || /[\\\x00-\x1f:]/.test(decoded))
    throw new Error(text('模型资源路径包含不允许的上级目录。', 'The model resource path contains a forbidden parent directory.'));
  const url = new URL(value, base);
  checkedPath(url);
  if (url.origin !== ORIGIN || !extension.test(url.pathname)) throw new Error(text('模型资源类型不受支持。', 'Unsupported model resource type.'));
  return url;
}
async function readFile(url: URL, signal: AbortSignal, maxSize = MAX_FILE): Promise<ArrayBuffer> {
  const response = await fetch(url.href, { signal, cache: 'no-store', credentials: 'omit', redirect: 'error' });
  if (!response.ok) throw new Error(text(`无法读取模型资源（HTTP ${response.status}）。`, `Could not read model resource (HTTP ${response.status}).`));
  const stated = Number(response.headers.get('content-length'));
  if (stated > maxSize) throw new Error(text('模型资源文件过大。', 'The model resource file is too large.'));
  const reader = response.body?.getReader();
  if (!reader) throw new Error(text('模型资源内容为空。', 'The model resource is empty.'));
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const part = await reader.read();
      if (part.done) break;
      size += part.value.byteLength;
      if (size > maxSize) { await reader.cancel(); throw new Error(text('模型资源文件过大。', 'The model resource file is too large.')); }
      chunks.push(part.value);
    }
  } finally { reader.releaseLock(); }
  if (size === 0) throw new Error(text('模型资源内容为空。', 'The model resource is empty.'));
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  return bytes.buffer;
}
function disposeModel(model: Loaded | null): void {
  if (!model) return;
  model.idle.release();
  model.renderer.release();
  for (const texture of model.textures) gl.deleteTexture(texture);
  model.moc.deleteModel(model.model);
  model.moc.release();
}
function clear(): void {
  if (!gl || contextLost) return;
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  gl.viewport(0, 0, canvas.width, canvas.height);
  gl.colorMask(true, true, true, true);
  gl.clearColor(0, 0, 0, 0);
  gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
}
function modelBounds(model: CubismModel): Bounds {
  let left = Infinity, right = -Infinity, bottom = Infinity, top = -Infinity;
  for (let i = 0; i < model.getDrawableCount(); i++) {
    if (model.getDrawableOpacity(i) <= 0) continue;
    const vertices = model.getDrawableVertices(i);
    for (let j = 0; j < vertices.length; j += 2) {
      const x = vertices[j], y = vertices[j + 1];
      if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
      left = Math.min(left, x); right = Math.max(right, x);
      bottom = Math.min(bottom, y); top = Math.max(top, y);
    }
  }
  if (![left, right, bottom, top].every(Number.isFinite) || right <= left || top <= bottom)
    throw new Error(text('模型缺少可显示的网格。', 'The model has no visible mesh.'));
  return { left, right, bottom, top };
}
function resizeCanvas(): void {
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  const width = Math.max(1, Math.min(2048, Math.round(canvas.clientWidth * ratio)));
  const height = Math.max(1, Math.min(2048, Math.round(canvas.clientHeight * ratio)));
  if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; dirty = true; }
}
function render(): void {
  if (!initialized || disposed || paused || contextLost) return;
  resizeCanvas();
  clear();
  if (current) {
    const b = current.bounds;
    const scale = Math.min(canvas.width / (b.right - b.left), canvas.height / (b.top - b.bottom)) * 0.94;
    const sx = 2 * scale / canvas.width, sy = 2 * scale / canvas.height;
    const mvp = new CubismMatrix44();
    mvp.setMatrix(new Float32Array([sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, 1, 0,
      -(b.left + b.right) * sx / 2, -(b.bottom + b.top) * sy / 2, 0, 1]));
    current.renderer.setMvpMatrix(mvp);
    current.renderer.setRenderState(null, [0, 0, canvas.width, canvas.height]);
    current.renderer.drawModel(SHADERS);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  }
  frames++;
  dirty = false;
}
function invalidate(): void {
  dirty = true;
  scheduleFrame();
}
function canAnimate(): boolean {
  return initialized && !disposed && !paused && !contextLost && !document.hidden &&
    motionEnabled && !reducedMotion.matches && !animationFailed && !!current?.idle.supported;
}
function stopFrames(): void {
  if (pendingFrame) cancelAnimationFrame(pendingFrame);
  pendingFrame = 0;
  lastFrameAt = null;
}
function scheduleFrame(): void {
  if (!pendingFrame && initialized && !paused && !disposed && !contextLost && !document.hidden &&
      (dirty || canAnimate())) pendingFrame = requestAnimationFrame(tick);
}
function tick(timestamp: number): void {
  pendingFrame = 0;
  if (paused || disposed || contextLost || document.hidden) return;
  const active = canAnimate();
  const elapsed = lastFrameAt === null ? 0 : timestamp - lastFrameAt;
  if (dirty || (active && (lastFrameAt === null || elapsed >= 1000 / 30 - 0.5))) {
    try {
      if (active) current!.idle.update(Math.min(elapsed / 1000, 0.1));
      render();
      lastFrameAt = timestamp;
    } catch (error) {
      animationFailed = true;
      stopFrames();
      fail(error, lastRequestId);
      return;
    }
  }
  scheduleFrame();
}
function motionChanged(): void {
  stopFrames();
  if (current && (!motionEnabled || reducedMotion.matches)) {
    current.idle.reset();
    current.model.update();
  }
  invalidate();
}
async function loadModel(raw: unknown, requestId: string): Promise<void> {
  const thisGeneration = ++generation;
  stopFrames();
  animationFailed = false;
  loading?.abort();
  const controller = new AbortController();
  loading = controller;
  lastRequestId = requestId;
  disposeModel(current); current = null;
  clear();
  let moc: CubismMoc | null = null;
  let model: CubismModel | null = null;
  let renderer: CubismRenderer_WebGL | null = null;
  const textures: WebGLTexture[] = [];
  const ensureCurrent = () => {
    if (controller.signal.aborted || thisGeneration !== generation || disposed || contextLost)
      throw new DOMException(text('加载已取消。', 'Loading canceled.'), 'AbortError');
  };
  try {
    if (!initialized || contextLost) throw new Error(text('Live2D 渲染器尚未就绪。', 'The Live2D renderer is not ready.'));
    const url = checkedModelUrl(raw);
    const jsonBytes = await readFile(url, controller.signal, 4 * 1024 * 1024);
    const settings = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(jsonBytes));
    if (!settings || settings.Version !== 3 || !settings.FileReferences ||
        !Array.isArray(settings.FileReferences.Textures) ||
        settings.FileReferences.Textures.length === 0 || settings.FileReferences.Textures.length > 32)
      throw new Error(text('不是受支持的 Cubism 3/4/5 .model3.json 模型。', 'Not a supported Cubism 3/4/5 .model3.json model.'));
    const mocUrl = resourceUrl(url, settings.FileReferences.Moc, /\.moc3$/i);
    const textureUrls = settings.FileReferences.Textures.map((name: unknown) => resourceUrl(url, name, /\.(png|jpe?g|webp)$/i));
    const poseUrl = settings.FileReferences.Pose
      ? resourceUrl(url, settings.FileReferences.Pose, /\.pose3\.json$/i) : null;
    const mocBytes = await readFile(mocUrl, controller.signal);
    ensureCurrent();
    moc = CubismMoc.create(mocBytes, true);
    if (!moc) throw new Error(text('MOC3 校验失败，或模型版本高于当前 Cubism Core。', 'MOC3 validation failed, or the model is newer than this Cubism Core.'));
    model = moc.createModel();
    if (!model || model.getDrawableCount() === 0) throw new Error(text('无法创建 Live2D 模型。', 'Could not create the Live2D model.'));
    // Initialize the pose's default part visibility once. This selects one of
    // mutually exclusive arms/clothes and never advances animation time.
    if (poseUrl) {
      const poseBytes = await readFile(poseUrl, controller.signal, 4 * 1024 * 1024);
      ensureCurrent();
      const data = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(poseBytes));
      if (!data || !Array.isArray(data.Groups) || data.Groups.length > 256 ||
          data.Groups.some((group: unknown) => !Array.isArray(group) || group.length > 256 ||
            group.some(part => !part || typeof part.Id !== 'string' || part.Id.length > 256 ||
              (part.Link !== undefined && (!Array.isArray(part.Link) || part.Link.length > 256 ||
                part.Link.some((id: unknown) => typeof id !== 'string' || id.length > 256))))))
        throw new Error(text('模型 pose 部件定义无效。', 'Invalid model pose part definition.'));
      const pose = CubismPose.create(poseBytes, poseBytes.byteLength);
      if (!pose) throw new Error(text('无法初始化模型默认部件。', 'Could not initialize the model default parts.'));
      pose.updateParameters(model, 0);
      CubismPose.delete(pose);
    }
    // Model-pack motion/expression/physics files are not loaded. Standard idle
    // parameters are driven separately once all resources have loaded.
    model.update();
    const bounds = modelBounds(model);
    renderer = new CubismRenderer_WebGL(canvas.width, canvas.height);
    renderer.initialize(model);
    renderer.startUp(gl);
    // Rebuild each drawable's mask independently. Packed masks in SDK R5 can
    // lose unrelated clipped parts when a closed eye changes the active atlas
    // layout; per-draw masks keep the rest of the character visible.
    renderer.useHighPrecisionMask(true);
    renderer.setIsPremultipliedAlpha(true);
    renderer.setIsCulling(false);
    let totalTexturePixels = 0;
    const maxTexture = Math.min(gl.getParameter(gl.MAX_TEXTURE_SIZE), 8192);
    for (let i = 0; i < textureUrls.length; i++) {
      const bytes = await readFile(textureUrls[i], controller.signal, 64 * 1024 * 1024);
      ensureCurrent();
      const image = await createImageBitmap(new Blob([bytes]), { premultiplyAlpha: 'premultiply', colorSpaceConversion: 'none' });
      try {
        ensureCurrent();
        totalTexturePixels += image.width * image.height;
        if (image.width > maxTexture || image.height > maxTexture || totalTexturePixels > 64 * 1024 * 1024)
          throw new Error(text('模型纹理超出此渲染器的尺寸限制。', 'Model textures exceed this renderer\'s size limits.'));
        const texture = gl.createTexture();
        if (!texture) throw new Error(text('显存不足，无法创建纹理。', 'Not enough GPU memory to create a texture.'));
        textures.push(texture);
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, 0);
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, 1);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
        if (gl.getError() !== gl.NO_ERROR) throw new Error(text('纹理上传失败。', 'Texture upload failed.'));
        renderer.bindTexture(i, texture);
      } finally { image.close(); }
    }
    gl.bindTexture(gl.TEXTURE_2D, null);
    ensureCurrent();
    current = { moc, model, renderer, textures, bounds, url: url.href, requestId, idle: new StandardIdle(model) };
    moc = null; model = null; renderer = null;
    dirty = true;
    if (!paused) render();
    scheduleFrame();
    post({ type: 'loaded', requestId, drawableCount: current.model.getDrawableCount(),
      textureCount: textures.length, mocVersion: current.moc.getMocVersion(), paused,
      canvasWidth: current.model.getCanvasWidth(), canvasHeight: current.model.getCanvasHeight(),
      idle: current.idle.diagnostics() });
  } catch (error) {
    renderer?.release();
    if (moc && model) moc.deleteModel(model);
    moc?.release();
    for (const texture of textures) {
      // Ownership transfers to current only after every resource is ready.
      if (!current || current.textures !== textures) gl?.deleteTexture(texture);
    }
    if (!(error instanceof DOMException && error.name === 'AbortError') && thisGeneration === generation)
      fail(error, requestId);
  } finally { if (loading === controller) loading = null; }
}
function unload(): void {
  generation++;
  loading?.abort(); loading = null;
  stopFrames();
  disposeModel(current); current = null;
  clear();
  dirty = true;
}
window.petDiagnostics = () => {
  if (!gl || contextLost) return { initialized, contextLost, paused, drawableCount: 0 };
  const pixels = new Uint8Array(canvas.width * canvas.height * 4);
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  gl.readPixels(0, 0, canvas.width, canvas.height, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
  let transparentPixels = 0, opaquePixels = 0, fullyOpaquePixels = 0;
  let left = canvas.width, right = -1, bottom = canvas.height, top = -1;
  for (let i = 3; i < pixels.length; i += 4) {
    const alpha = pixels[i];
    if (alpha === 0) transparentPixels++;
    else {
      opaquePixels++;
      if (alpha === 255) fullyOpaquePixels++;
      const position = (i - 3) / 4, x = position % canvas.width, y = Math.floor(position / canvas.width);
      left = Math.min(left, x); right = Math.max(right, x); bottom = Math.min(bottom, y); top = Math.max(top, y);
    }
  }
  return { initialized, contextLost, paused, dirty, width: canvas.width, height: canvas.height,
    transparentPixels, opaquePixels, fullyOpaquePixels, frameCount: frames,
    alpha: gl.getContextAttributes()?.alpha, drawableCount: current?.model.getDrawableCount() ?? 0,
    textureCount: current?.textures.length ?? 0, requestId: current?.requestId ?? null,
    bounds: opaquePixels ? { left, right, top: canvas.height - top - 1, bottom: canvas.height - bottom - 1 } : null,
    autoMotion: false, animationLoop: canAnimate(),
    idle: { enabled: motionEnabled, reducedMotion: reducedMotion.matches,
      documentHidden: document.hidden, active: canAnimate(), ...current?.idle.diagnostics() },
    glError: gl.getError() };
};
function receive(event: { data: unknown }): void {
  if (!event.data || typeof event.data !== 'object' || Array.isArray(event.data) || disposed) return;
  const message = event.data as HostMessage;
  const requestId = typeof message.requestId === 'string' ? message.requestId.slice(0, 200) : '';
  if (message.type === 'load') void loadModel(message.url, requestId);
  else if (message.type === 'pause' && typeof message.paused === 'boolean') {
    paused = message.paused;
    stopFrames();
    if (!paused) scheduleFrame();
  } else if (message.type === 'motion' && typeof message.enabled === 'boolean') {
    if (motionEnabled !== message.enabled) { motionEnabled = message.enabled; motionChanged(); }
  } else if (message.type === 'dispose') { unload(); post({ type: 'disposed' }); }
  else if (message.type === 'stats') post({ type: 'stats', ...window.petDiagnostics(), requestId });
}
async function initialize(): Promise<void> {
  try {
    if (location.origin !== ORIGIN || location.pathname !== '/runtime/index.html')
      throw new Error(text('请从桌宠的本地映射运行时入口打开。', 'Open this page through AI Pet\'s mapped local runtime entry.'));
    if (typeof Live2DCubismCore === 'undefined') throw new Error(text('本地 Cubism Core 文件缺失。', 'Local Cubism Core files are missing.'));
    const option = new Option();
    option.loggingLevel = LogLevel.LogLevel_Error;
    option.logFunction = (message: string) => console.error(`[Cubism] ${message}`);
    if (!CubismFramework.startUp(option)) throw new Error(text('Cubism Framework 初始化失败。', 'Cubism Framework initialization failed.'));
    CubismFramework.initialize();
    gl = canvas.getContext('webgl', { alpha: true, premultipliedAlpha: true,
      antialias: true, depth: false, stencil: false, preserveDrawingBuffer: true, powerPreference: 'low-power' })!;
    if (!gl) throw new Error(text('当前设备无法启用 WebGL，请更新显卡驱动和 WebView2。', 'WebGL is unavailable on this device. Update the graphics driver and WebView2.'));
    const manager = CubismShaderManager_WebGL.getInstance();
    manager.setGlContext(gl);
    const shader = manager.getShader(gl);
    shader.setShaderPath(SHADERS);
    shader.generateShaders();
    const deadline = performance.now() + 15000;
    while (!shader._isShaderLoaded) {
      if (performance.now() > deadline) throw new Error(text('本地 Live2D 着色器初始化超时。', 'Local Live2D shader initialization timed out.'));
      await new Promise(resolve => setTimeout(resolve, 20));
    }
    if (shader._shaderSets.slice(0, 4).some(item => !item?.shaderProgram)) throw new Error(text('Live2D 着色器编译失败。', 'Live2D shader compilation failed.'));
    initialized = true;
    resizeCanvas(); clear();
    window.chrome?.webview?.addEventListener('message', receive);
    post({ type: 'ready', sdkVersion: '5-r.5', coreVersion: Live2DCubismCore.Version.csmGetVersion(),
      protocolVersion: 1, webgl: gl.getParameter(gl.VERSION), alpha: true });
  } catch (error) { fail(error); }
}
const observer = new ResizeObserver(invalidate);
observer.observe(canvas);
window.addEventListener('resize', invalidate);
reducedMotion.addEventListener('change', motionChanged);
document.addEventListener('visibilitychange', () => { stopFrames(); if (!document.hidden) invalidate(); });
canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault(); contextLost = true;
  loading?.abort();
  stopFrames();
  fail(new Error(text('图形上下文已断开，请重新选择此模型。', 'The graphics context was lost. Select this model again.')), lastRequestId);
});
canvas.addEventListener('webglcontextrestored', () => {
  // The host recreates the surface after an error; stale GPU handles are never reused.
  fail(new Error(text('图形上下文已恢复，请重新选择此模型。', 'The graphics context was restored. Select this model again.')), lastRequestId);
});
window.addEventListener('beforeunload', () => {
  disposed = true;
  observer.disconnect();
  unload();
  if (CubismFramework.isInitialized()) CubismFramework.dispose();
  CubismFramework.cleanUp();
});
void initialize();
