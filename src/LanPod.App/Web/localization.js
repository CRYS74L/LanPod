let language = "zh-Hans";
let messages = {};

export const getLanguage = () => language;

export function t(source, ...arguments_) {
  const template = Object.hasOwn(messages, source) ? messages[source] : source;
  if (!arguments_.length) return String(template ?? "");
  return String(template).replace(/\{(\d+)\}/g, (_, index) => String(arguments_[index] ?? ""));
}

export function applyLocalization(value, strings, root = globalThis.document) {
  if (!["zh-Hans", "zh-Hant", "ja", "en"].includes(value))
    throw new Error("Unsupported interface language: " + value);
  language = value;
  messages = strings;
  if (!root) return;
  root.documentElement.lang = language;
  for (const element of root.querySelectorAll("[data-i18n]"))
    element.textContent = t(element.dataset.i18n);
  for (const element of root.querySelectorAll("[data-i18n-aria-label]"))
    element.setAttribute("aria-label", t(element.dataset.i18nAriaLabel));
}
