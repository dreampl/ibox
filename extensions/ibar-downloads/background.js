// IBAR Downloads — shows the browser's downloads on the IBAR notch.
// Talks only to IBAR's own native host (Native Messaging: no network, no local port). What leaves the browser:
// the file's name (not its folder), sizes, state, speed and the source's domain name — never a full address and
// never any history. Event-driven: the download list is read once a second only while something is downloading.

const api = typeof browser !== "undefined" ? browser : chrome;
const HOST = "com.jacki.ibar";
let port = null;
let failedAt = 0;
let polling = null;
const last = new Map();

function brand() {
  const brands = (navigator.userAgentData && navigator.userAgentData.brands) || [];
  const names = brands.map((b) => b.brand);
  if (names.some((n) => /Edge/i.test(n))) return "edge";
  if (names.some((n) => /Opera/i.test(n))) return "opera";
  if (names.some((n) => /Brave/i.test(n))) return "brave";
  if (names.some((n) => /Vivaldi/i.test(n))) return "vivaldi";
  if (typeof browser !== "undefined" && /Firefox/i.test(navigator.userAgent)) return "firefox";
  if (names.some((n) => /Google Chrome/i.test(n))) return "chrome";
  return "chromium";
}

function connect() {
  if (port) return true;
  // A missing host (IBAR not installed) is not retried in a loop: next download, at most once a minute.
  if (Date.now() - failedAt < 60000) return false;
  try {
    port = api.runtime.connectNative(HOST);
    port.onDisconnect.addListener(() => {
      port = null;
      failedAt = Date.now();
    });
    port.postMessage({ type: "hello", browser: brand() });
    return true;
  } catch (e) {
    port = null;
    failedAt = Date.now();
    return false;
  }
}

function domainOf(item) {
  try {
    const u = new URL(item.finalUrl || item.url || "");
    return u.protocol === "http:" || u.protocol === "https:" ? u.hostname : "";
  } catch (e) {
    return "";
  }
}

function baseName(path) {
  if (!path) return "";
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1];
}

function send(item) {
  if (!connect()) return;
  const message = {
    type: "download",
    id: item.id,
    file: baseName(item.filename),
    received: item.bytesReceived || 0,
    total: item.totalBytes > 0 ? item.totalBytes : 0,
    state: item.state,
    paused: !!item.paused,
    error: item.error || "",
    domain: domainOf(item),
  };
  const key = JSON.stringify(message);
  if (last.get(item.id) === key) return;
  last.set(item.id, key);
  if (item.state !== "in_progress") last.delete(item.id);
  try {
    port.postMessage(message);
  } catch (e) {
    port = null;
  }
}

async function poll() {
  const items = await api.downloads.search({ state: "in_progress" });
  items.forEach(send);
  if (items.length === 0 && polling) {
    clearInterval(polling);
    polling = null;
  }
}

function startPolling() {
  if (!polling) polling = setInterval(poll, 1000);
}

api.downloads.onCreated.addListener((item) => {
  send(item);
  startPolling();
});

api.downloads.onChanged.addListener(async (delta) => {
  const [item] = await api.downloads.search({ id: delta.id });
  if (!item) return;
  send(item);
  if (item.state === "in_progress") startPolling();
});

// Connect when the browser starts, so the host lives as long as the browser does.
api.runtime.onStartup.addListener(() => connect());
api.runtime.onInstalled.addListener(() => connect());
connect();
poll();
