import { test } from "node:test";
import assert from "node:assert/strict";
import { TrafficChart, ChartValueMotion, ChartTimeline, ChartSamplingClock, EChartsLiveCurve } from "../src/LanDeviceMonitor.App/Web/chart.js";

// Exercise the production frame update and SVG transform with a controlled clock.
function fixture(t, duration, highPeak = [510, 540]) {
  let tick = 1000;
  t.mock.method(performance, "now", () => tick);
  const previous = { matchMedia: globalThis.matchMedia, getComputedStyle: globalThis.getComputedStyle };
  globalThis.matchMedia = () => ({ matches: duration === 0 });
  globalThis.getComputedStyle = () => ({ transitionDuration: `${duration}ms` });
  t.after(() => {
    for (const [name, value] of Object.entries(previous)) {
      if (value === undefined) delete globalThis[name]; else globalThis[name] = value;
    }
  });
  const node = () => ({ attributes: {}, getAttribute(key) { return this.attributes[key]; },
    setAttribute(key, value) { this.attributes[key] = value; } });
  const empty = {};
  const chart = Object.assign(Object.create(TrafficChart.prototype), {
    el: { clientWidth: 760, clientHeight: 220, querySelector: () => empty },
    range: 10, rangeControl: { querySelector: () => ({}) }, rangeMotion: new ChartValueMotion(10),
    presentationClock: new ChartTimeline(), samplingClock: new ChartSamplingClock(),
    liveCurves: { download: new EChartsLiveCurve(), upload: new EChartsLiveCurve() },
    seriesNodes: Object.fromEntries(["download", "upload"].map(direction => [direction,
      { line: node(), area: node(), fill: node() }])), seriesLayer: node(),
    clear() {}, drawAxes() {}, drawCursor() {},
  });
  const end = Date.parse("2026-11-01T00:00:00Z");
  const samples = Array.from({ length: 661 }, (_, i) => {
    const age = 660 - i;
    return { at: new Date(end - age * 1000).toISOString(), upload: age > 360 ? 0 : 9000,
      download: age >= highPeak[0] && age <= highPeak[1] ? 1_000_000 : age >= 160 && age <= 190 ? 50000 : 12000 };
  });
  const draw = (range, elapsed = 0) => {
    tick += elapsed;
    chart.update(samples, new Date(end + 7000 + tick - 1000).toISOString(), range);
  };
  draw(10);
  return { chart, samples, draw };
}

function settled(chart) {
  assert.ok(Math.abs((chart.end - chart.start) / 60000 - chart.range) < 1e-9,
    "The horizontal viewport did not reach the selected range.");
  assert.ok(Math.abs(chart.max / chart.targetScale.max - 1) < 1e-9,
    `The slider finished with the vertical maximum still moving: ${chart.max}/${chart.targetScale.max}`);
  assert.ok(Math.abs(chart.min - chart.targetScale.min) < 1e-9,
    `The slider finished with the vertical minimum still moving: ${chart.min}/${chart.targetScale.min}`);
  const matrix = chart.seriesLayer.attributes.transform.match(/matrix\((.*)\)/)[1].split(" ").map(Number);
  const scaleY = (chart.bottom - chart.top) * 1024 / (chart.targetScale.max - chart.targetScale.min);
  assert.ok(Math.abs(matrix[3] / scaleY - 1) < 1e-9, "The actual SVG curve transform did not settle.");
}

for (const duration of [220, 480, 0]) {
  test(`range changes finish both axes within the ${duration}ms indicator transition`, t => {
    const { chart, samples, draw } = fixture(t, duration);
    for (const range of [5, 1, 10]) {
      const before = chart.max;
      draw(range);
      for (let elapsed = 10; elapsed <= duration; elapsed += 10) {
        draw(range, 10);
        assert.ok(chart.max >= chart.targetScale.max && chart.min <= chart.targetScale.min,
          "Range scaling clipped a currently visible peak or valley.");
        if (elapsed === duration / 2 && range < 10)
          assert.ok(chart.max < before && chart.max > chart.targetScale.max, "Vertical scaling skipped its intermediate frames.");
      }
      settled(chart);
      if (range < 10) assert.ok(chart.max < before / 2, "The fixture did not expose a large scale contraction.");
      const finished = [chart.max, chart.min];
      draw(range, 700);
      assert.deepEqual([chart.max, chart.min], finished, "The curve continued rising after the slider stopped.");
      assert.equal(chart.samples.length, samples.length);
      assert.equal(chart.samples[300].time, Date.parse(samples[300].at));
    }
  });
}

test("rapid range reversals keep the displayed vertical scale and finish at the new deadline", t => {
  const { chart, draw } = fixture(t, 480);
  draw(1); draw(1, 90);
  const before = [chart.max, chart.min];
  draw(5);
  assert.deepEqual([chart.max, chart.min], before, "Reversal jumped from the displayed scale.");
  for (let elapsed = 10; elapsed <= 480; elapsed += 10) draw(5, 10);
  settled(chart);
});

test("a peak leaving in the last frames cannot extend vertical scaling past the indicator deadline", t => {
  const { chart, draw } = fixture(t, 220, [301, 302]);
  draw(5);
  draw(5, 160);
  assert.ok(chart.targetScale.max > 500000, "The old peak left before the late-transition check.");
  for (let elapsed = 170; elapsed <= 220; elapsed += 10) draw(5, 10);
  settled(chart);
  assert.ok(chart.max < 60000, "The expired peak is still controlling the vertical scale.");
});
