const assert = require('node:assert/strict');
const Module = require('node:module');
const esbuild = require('esbuild');

// Unit test doubles exercise the parameter driver, never create or impersonate a MOC.
const compiled = esbuild.buildSync({ entryPoints: [require('node:path').join(__dirname, 'standard-idle.ts')],
  bundle: true, platform: 'node', format: 'cjs', write: false });
const code = new Module(__filename + '.compiled', module);
code._compile(compiled.outputFiles[0].text, __filename + '.compiled');
const { StandardIdle } = code.exports;

function parameterModel(definitions) {
  const ids = Object.keys(definitions), values = Object.values(definitions).map(p => p.value);
  const writes = [];
  const model = {
    getModel: () => ({ parameters: { ids } }),
    getParameterMinimumValue: i => definitions[ids[i]].min ?? 0,
    getParameterMaximumValue: i => definitions[ids[i]].max ?? 1,
    getParameterValueByIndex: i => values[i],
    getParameterId: i => ids[i],
    setParameterValueByIndex: (i, value) => {
      assert.ok(i >= 0 && i < ids.length, 'No writes to missing parameters');
      values[i] = Math.max(model.getParameterMinimumValue(i), Math.min(model.getParameterMaximumValue(i), value));
      writes.push(ids[i]);
    },
    setParameterValueById: (id, value) => model.setParameterValueByIndex(ids.indexOf(id), value),
    addParameterValueById: (id, value, weight) => {
      const i = ids.indexOf(id); model.setParameterValueByIndex(i, values[i] + value * weight);
    },
    update: () => {}
  };
  return { model, writes, values: () => Object.fromEntries(ids.map((id, i) => [id, values[i]])) };
}

const random = Math.random;
Math.random = () => 0.5;
try {
  const fixture = parameterModel({ ParamEyeLOpen: { value: 1 }, ParamEyeROpen: { value: 1 },
    ParamBreath: { value: 0.2 }, ParamAngleX: { value: 0.37, min: -30, max: 30 } });
  const idle = new StandardIdle(fixture.model);
  let minEye = 1, maxEye = 0, minBreath = 1, maxBreath = 0, closures = 0, previouslyClosed = false;
  for (let i = 0; i < 720; i++) {
    idle.update(1 / 30);
    const v = fixture.values();
    assert.equal(v.ParamEyeLOpen, v.ParamEyeROpen, 'Both standard eyes blink together');
    for (const name of ['ParamEyeLOpen', 'ParamEyeROpen', 'ParamBreath']) assert.ok(v[name] >= 0 && v[name] <= 1);
    minEye = Math.min(minEye, v.ParamEyeLOpen); maxEye = Math.max(maxEye, v.ParamEyeLOpen);
    minBreath = Math.min(minBreath, v.ParamBreath); maxBreath = Math.max(maxBreath, v.ParamBreath);
    const closed = v.ParamEyeLOpen === 0;
    if (closed && !previouslyClosed) closures++;
    previouslyClosed = closed;
    assert.equal(v.ParamAngleX, 0.37, 'Unrelated model parameters remain untouched');
  }
  assert.equal(minEye, 0); assert.equal(maxEye, 1); assert.ok(closures >= 5);
  assert.ok(minBreath < 0.001 && maxBreath > 0.999, 'Breathing oscillates rather than accumulating');
  idle.reset();
  assert.deepEqual(fixture.values(), { ParamEyeLOpen: 1, ParamEyeROpen: 1, ParamBreath: 0.2, ParamAngleX: 0.37 });
  assert.equal(idle.diagnostics().elapsedSeconds, 0);
  idle.release();

  const absent = parameterModel({ ParamAngleX: { value: 0.4 } });
  const unsupported = new StandardIdle(absent.model);
  assert.equal(unsupported.supported, false);
  unsupported.update(0.1);
  assert.equal(absent.writes.length, 0);
  const oneEye = new StandardIdle(parameterModel({ ParamEyeROpen: { value: 1 } }).model);
  assert.deepEqual(oneEye.diagnostics().blinkParameters, ['ParamEyeROpen']);
  assert.equal(oneEye.diagnostics().breathParameter, null);
  const badRange = new StandardIdle(parameterModel({ ParamBreath: { value: 0.2, min: 0, max: 0.5 } }).model);
  assert.equal(badRange.supported, false, 'Nonstandard ranges are not silently driven');
  console.log(JSON.stringify({ passed: true, simulatedSeconds: 24, closures, minEye, maxEye, minBreath, maxBreath,
    restoredDefaults: true, untouchedOtherParameters: true, missingParameterWrites: absent.writes.length }));
} finally { Math.random = random; }
