// Exercise the actual localization and path-validation functions without a
// browser, GPU, model, network access, or user configuration.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const source = fs.readFileSync(path.join(__dirname, 'renderer.ts'), 'utf8');
const start = source.indexOf("const ORIGIN = 'https://aipet.invalid';");
const end = source.indexOf('function disposeModel(');
assert.ok(start >= 0 && end > start);
const code = ts.transpileModule(source.slice(start, end), {
  compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.None }
}).outputText;

async function verify(search, english) {
  const canvas = { setAttribute(name, value) { this[name] = value; } };
  const document = { getElementById: () => canvas, documentElement: {}, title: '' };
  let requests = 0;
  const context = vm.createContext({ document, location: { search },
    window: { matchMedia: () => ({ matches: false }) }, URL, URLSearchParams,
    Error, DOMException, fetch: async () => { requests++; return { ok: false, status: 404 }; } });
  vm.runInContext(code, context);
  assert.equal(document.documentElement.lang, english ? 'en-US' : 'zh-CN');
  assert.equal(document.title, english ? 'AI Pet Live2D' : 'AI 桌宠 Live2D');
  assert.equal(canvas['aria-label'], english ? 'Live2D character' : 'Live2D 角色');
  const invalid = english ? 'Invalid model path.' : '模型路径无效。';
  assert.throws(() => vm.runInContext('checkedModelUrl(null)', context), { message: invalid });
  const localOnly = english ? 'Only .model3.json files in the mapped local directory can be loaded.' : '只允许加载本机映射目录中的 .model3.json。';
  for (const url of ['https://outside.invalid/model/a.model3.json', 'https://aipet.invalid/model/a.model3.json?lang=en-US']) {
    context.input = url;
    assert.throws(() => vm.runInContext('checkedModelUrl(input)', context), { message: localOnly });
  }
  assert.equal(vm.runInContext("checkedModelUrl('https://aipet.invalid/model/a.model3.json').pathname", context), '/model/a.model3.json');
  const traversal = english ? 'The model resource path contains a forbidden parent directory.' : '模型资源路径包含不允许的上级目录。';
  assert.throws(() => vm.runInContext("resourceUrl(new URL('https://aipet.invalid/model/a.model3.json'), '%2e%2e/escape.moc3', /\\.moc3$/i)", context), { message: traversal });
  assert.equal(requests, 0, 'Rejected resource paths must never be fetched');
  await assert.rejects(vm.runInContext("readFile(new URL('https://aipet.invalid/model/missing.moc3'), {})", context), {
    message: english ? 'Could not read model resource (HTTP 404).' : '无法读取模型资源（HTTP 404）。'
  });
  assert.equal(requests, 1);
}

(async () => {
  await verify('?lang=en-US', true);
  await verify('?lang=zh-CN', false);
  await verify('', false);
  await verify('?lang=not-a-language', false);
  console.log('PASS: Live2D English/Chinese/default language, translated errors, and unchanged path restrictions (no GPU/model/network).');
})().catch(error => { console.error(error); process.exitCode = 1; });
