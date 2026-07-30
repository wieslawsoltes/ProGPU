// Algorithm: apply one immutable scalar gain while copying each available
// input channel to the corresponding output channel; missing channels are
// filled with silence.
// Time complexity: O(F * C) per render quantum for F frames and C output
// channels.
// Space complexity: O(1) private state and no application allocation per
// render quantum; the browser owns the input and output channel arrays.
class ProGpuSmokeGainProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const configuredGain =
      Number(options?.processorOptions?.gain);
    this.gain =
      Number.isFinite(configuredGain)
        ? Math.max(0, configuredGain)
        : 1;
  }

  process(inputs, outputs) {
    const input = inputs[0];
    const output = outputs[0];
    if (!output) return true;

    for (let channel = 0;
         channel < output.length;
         channel++) {
      const target = output[channel];
      const source = input?.[channel];
      if (!source) {
        target.fill(0);
        continue;
      }
      if (this.gain === 1) {
        target.set(source);
        continue;
      }
      for (let frame = 0;
           frame < target.length;
           frame++) {
        target[frame] = source[frame] * this.gain;
      }
    }
    return true;
  }
}

registerProcessor(
  'progpu-smoke-gain',
  ProGpuSmokeGainProcessor);
