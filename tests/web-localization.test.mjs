import { test, afterEach } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { applyLocalization, getLanguage, t } from "../src/LanPod.App/Web/localization.js";
import { relativeDate, relativeTime, duration, dateRange, sessionsMarkup } from "../src/LanPod.App/Web/ui.js";

const decode = (value) => value.replace(/&(amp|lt|gt|quot|apos);/g, (_, name) =>
  ({ amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" })[name]);
function strings(suffix = "") {
  const xml = readFileSync(new URL(`../src/LanPod.App/Resources/Strings${suffix}.resx`, import.meta.url), "utf8");
  return Object.fromEntries([...xml.matchAll(/<data name="([^"]+)"[^>]*><value>([\s\S]*?)<\/value><\/data>/g)]
    .map((match) => [decode(match[1]), decode(match[2])]));
}
const neutral = strings();
afterEach(() => applyLocalization("zh-Hans", neutral));

test("all four languages have matching resources and cover every desktop and static web label", () => {
  for (const suffix of ["", ".zh-Hant", ".ja", ".en"])
    assert.deepEqual(Object.keys(strings(suffix)).sort(), Object.keys(neutral).sort());
  const xaml = readFileSync(new URL("../src/LanPod.App/MainWindow.xaml", import.meta.url), "utf8");
  for (const match of xaml.matchAll(/\{DynamicResource '?Text\.([^}'"]+)'?\}/g))
    assert.ok(Object.hasOwn(neutral, decode(match[1])), match[1]);
  const html = readFileSync(new URL("../src/LanPod.App/Web/index.html", import.meta.url), "utf8");
  for (const match of html.matchAll(/data-i18n(?:-aria-label)?="([^"]+)"/g))
    assert.ok(Object.hasOwn(neutral, decode(match[1])), match[1]);
  for (const match of html.matchAll(/<(span|button|h1|h2|a|div)(\s[^<>]*?)?>(\s*[^<>]*?[\p{Script=Han}][^<>]*?)<\/\1\s*>/gu))
    assert.match(match[2] || "", /data-i18n=/, match[3].trim());
});

test("English labels, relative times, durations and history switch together while names remain intact", () => {
  applyLocalization("en", strings(".en"));
  assert.equal(getLanguage(), "en");
  assert.equal(t("颜色主题"), "Color theme");
  assert.equal(t("自定义设备名称 ABC"), "自定义设备名称 ABC");
  const now = "2026-11-15T12:00:00+08:00";
  assert.equal(relativeDate("2026-11-14T12:00:00+08:00", now), "Yesterday");
  assert.equal(relativeTime("2026-11-15T11:45:00+08:00", now), "15\u2009min ago");
  assert.equal(duration("2026-11-15T10:35:00+08:00", now), "1\u2009h\u200925\u2009min");
  assert.equal(dateRange("2026-11-14T12:00:00+08:00", now), "11/14 ~ 11/15");
  const markup = sessionsMarkup([{ onlineAt: "2026-11-15T11:00:00+08:00", offlineAt: null }], now);
  assert.match(markup, /Currently online/);
  assert.match(markup, /Disconnected/);
  assert.doesNotMatch(markup, /当前在线|离线时间|上线时间/);
});

test("Traditional Chinese, Japanese and returning to the default use their own strings", () => {
  for (const [language, suffix, settings, theme] of [
    ["zh-Hant", ".zh-Hant", "設定", "色彩主題"],
    ["ja", ".ja", "設定", "カラーテーマ"],
    ["zh-Hans", "", "设置", "颜色主题"],
  ]) {
    applyLocalization(language, strings(suffix));
    assert.equal(t("设置"), settings);
    assert.equal(t("颜色主题"), theme);
    assert.equal(t("请求失败（{0}）", 503).includes("503"), true);
  }
});

test("static text and accessible labels update without replacing the surrounding controls", () => {
  const label = { dataset: { i18n: "在线" }, textContent: "在线" };
  const action = { dataset: { i18nAriaLabel: "路由器与上网信息" }, setAttribute(name, value) { this[name] = value; } };
  const root = {
    documentElement: { lang: "zh-Hans" },
    querySelectorAll(selector) { return selector === "[data-i18n]" ? [label] : [action]; },
  };
  applyLocalization("en", strings(".en"), root);
  assert.equal(root.documentElement.lang, "en");
  assert.equal(label.textContent, "Online");
  assert.equal(action["aria-label"], "Router and internet");
});
