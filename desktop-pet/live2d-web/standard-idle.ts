import { CubismEyeBlink } from './vendor/Framework/src/effect/cubismeyeblink';
import { CubismBreath, BreathParameterData } from './vendor/Framework/src/effect/cubismbreath';
import { CubismModel } from './vendor/Framework/src/model/cubismmodel';

type Parameter = { name: string; index: number; initial: number };

// Only real, exported standard parameters are eligible. Cubism's getParameterIndex
// can invent a virtual parameter for a missing ID, so inspect the Core IDs first.
export class StandardIdle {
  private readonly eyes: Parameter[];
  private readonly breath: Parameter | null;
  private blinkDriver: CubismEyeBlink | null = null;
  private breathDriver: CubismBreath | null = null;
  private elapsed = 0;

  constructor(private readonly model: CubismModel) {
    const find = (name: string): Parameter | null => {
      const index = model.getModel().parameters.ids.indexOf(name);
      if (index < 0 || model.getParameterMinimumValue(index) > 0 ||
          model.getParameterMaximumValue(index) < 1) return null;
      return { name, index, initial: model.getParameterValueByIndex(index) };
    };
    this.eyes = [find('ParamEyeLOpen'), find('ParamEyeROpen')].filter((p): p is Parameter => p !== null);
    this.breath = find('ParamBreath');
    this.reset();
  }

  get supported(): boolean { return this.eyes.length > 0 || this.breath !== null; }

  reset(): void {
    this.releaseDrivers();
    this.elapsed = 0;
    for (const p of this.eyes) this.model.setParameterValueByIndex(p.index, p.initial);
    if (this.breath) this.model.setParameterValueByIndex(this.breath.index, this.breath.initial);
    if (this.eyes.length) {
      this.blinkDriver = CubismEyeBlink.create();
      this.blinkDriver.setParameterIds(this.eyes.map(p => this.model.getParameterId(p.index)));
      this.blinkDriver.setBlinkingInterval(4);
      this.blinkDriver.setBlinkingSetting(0.10, 0.06, 0.16);
    }
    if (this.breath) {
      this.breathDriver = CubismBreath.create();
      this.breathDriver.setParameters([new BreathParameterData(
        this.model.getParameterId(this.breath.index), 0.5, 0.5, 3.6, 1)]);
    }
  }

  update(seconds: number): void {
    this.elapsed += seconds;
    this.blinkDriver?.updateParameters(this.model, seconds);
    if (this.breathDriver && this.breath) {
      // CubismBreath adds its waveform. Clear this one parameter each tick so
      // breathing never accumulates or sticks at its maximum across frames.
      this.model.setParameterValueByIndex(this.breath.index, 0);
      this.breathDriver.updateParameters(this.model, seconds);
    }
    this.model.update();
  }

  diagnostics(): Record<string, unknown> {
    const parameters = [...this.eyes, ...(this.breath ? [this.breath] : [])];
    return { supported: this.supported, elapsedSeconds: this.elapsed,
      blinkParameters: this.eyes.map(p => p.name), breathParameter: this.breath?.name ?? null,
      values: Object.fromEntries(parameters.map(p => [p.name, this.model.getParameterValueByIndex(p.index)])),
      restValues: Object.fromEntries(parameters.map(p => [p.name, p.initial])) };
  }

  private releaseDrivers(): void {
    if (this.blinkDriver) CubismEyeBlink.delete(this.blinkDriver);
    if (this.breathDriver) CubismBreath.delete(this.breathDriver);
    this.blinkDriver = null;
    this.breathDriver = null;
  }

  release(): void { this.releaseDrivers(); }
}
