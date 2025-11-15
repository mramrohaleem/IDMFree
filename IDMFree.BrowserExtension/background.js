const LOG_PREFIX = "[IDMFree]";
const BRIDGE_ENDPOINTS = Object.freeze([
  "http://127.0.0.1:5454/api/downloads",
  "http://localhost:5454/api/downloads",
  "http://[::1]:5454/api/downloads",
]);
const BRIDGE_TIMEOUT_MS = 2500;
const CONTEXT_TTL_MS = 5 * 60 * 1000; // 5 minutes
const MAX_CONTEXT_ENTRIES = 400;
const FALLBACK_TTL_MS = 3 * 60 * 1000; // 3 minutes
const CLICK_HINT_TTL_MS = 60 * 1000; // 1 minute

const DEFAULT_SETTINGS = Object.freeze({
  captureDownloads: true,
  aggressiveInterception: true,
  strictMode: true,
  interceptClicks: true,
  interceptNetwork: true,
  interceptResponseHeaders: true,
  interceptDownloadsApi: true,
  interceptPostRequests: false,
  domainExclusions: [],
  alwaysInterceptExtensions: [
    ".7z",
    ".apk",
    ".avi",
    ".bz2",
    ".cab",
    ".dmg",
    ".exe",
    ".flac",
    ".gz",
    ".img",
    ".iso",
    ".mkv",
    ".mov",
    ".mp3",
    ".mp4",
    ".msi",
    ".pdf",
    ".pkg",
    ".rar",
    ".tar",
    ".xz",
    ".zip",
  ],
});

const STREAMING_MIME_PREFIXES = ["video/", "audio/", "application/vnd.apple.mpegurl", "application/x-mpegURL"];
const STREAMING_EXTENSION_HINTS = new Set([".m3u8", ".mpd", ".ism", ".ts"]);

let settings = { ...DEFAULT_SETTINGS };

const requestContexts = new Map();
const recentRequestsByUrl = new Map();
const fallbackHosts = new Map();
const clickHints = new Map();

function now() {
  return Date.now();
}

function generateCorrelationId() {
  try {
    const buffer = new Uint32Array(2);
    crypto.getRandomValues(buffer);
    return Array.from(buffer, (value) => value.toString(16).padStart(8, "0"))
      .join("")
      .slice(0, 12);
  } catch (err) {
    return Math.random().toString(36).slice(2, 10);
  }
}

function logEvent(event, context = {}) {
  console.debug(`${LOG_PREFIX} ${event}`, context);
}

function sanitizeBridgeHeaders(headers) {
  if (!headers || typeof headers !== "object") {
    return undefined;
  }
  const sanitized = {};
  for (const [key, value] of Object.entries(headers)) {
    if (!key || typeof key !== "string") {
      continue;
    }
    if (typeof value !== "string") {
      continue;
    }
    const trimmed = value.trim();
    if (!trimmed) {
      continue;
    }
    sanitized[key] = trimmed;
  }
  return Object.keys(sanitized).length > 0 ? sanitized : undefined;
}

function buildBridgePayload(payload, correlationId) {
  const headers = sanitizeBridgeHeaders(payload.headers);
  const method = payload.method ? String(payload.method).toUpperCase() : "GET";
  const request = {
    url: payload.url,
    fileName: payload.fileName || null,
    method,
    correlationId,
  };
  if (headers) {
    request.headers = headers;
  }
  return request;
}

function summarizeBridgePayload(payload) {
  if (!payload) {
    return {};
  }
  return {
    url: payload.url || null,
    fileName: payload.fileName || null,
    method: payload.method || null,
    hasHeaders: Boolean(payload.headers && Object.keys(payload.headers).length > 0),
  };
}

async function delegateDownloadToApp(kind, payload, correlationId, logMetadata = {}) {
  const correlation = correlationId || payload?.correlationId || generateCorrelationId();
  const bridgePayload = buildBridgePayload(payload, correlation);
  const serialized = JSON.stringify(bridgePayload);
  const attemptBaseContext = {
    kind,
    correlationId: correlation,
    payload: summarizeBridgePayload(bridgePayload),
    ...logMetadata,
  };
  const errors = [];

  for (const endpoint of BRIDGE_ENDPOINTS) {
    const attemptContext = { ...attemptBaseContext, endpoint };
    logEvent("BG: bridge fetch attempt", attemptContext);

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), BRIDGE_TIMEOUT_MS);
    let response;
    try {
      response = await fetch(endpoint, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Accept: "application/json",
          "X-Correlation-ID": correlation,
        },
        body: serialized,
        signal: controller.signal,
        cache: "no-store",
        credentials: "omit",
      });
    } catch (err) {
      clearTimeout(timeoutId);
      const reason = err && err.name === "AbortError" ? "bridge_timeout" : "bridge_fetch_error";
      const message = err?.message || String(err);
      logEvent("BG: bridge fetch failed", { ...attemptContext, reason, error: message });
      errors.push({ reason, message, endpoint });
      continue;
    }

    clearTimeout(timeoutId);

    const statusCode = response.status;
    if (!response.ok) {
      let errorBody = null;
      try {
        errorBody = await response.text();
      } catch (err) {
        errorBody = null;
      }
      const reason = `bridge_http_${statusCode}`;
      logEvent("BG: bridge fetch non-2xx", {
        ...attemptContext,
        statusCode,
        reason,
        error: errorBody,
      });
      errors.push({ reason, message: errorBody || response.statusText, statusCode, endpoint });
      continue;
    }

    let data = null;
    try {
      data = await response.json();
    } catch (err) {
      const reason = "bridge_invalid_json";
      const message = err?.message || String(err);
      logEvent("BG: bridge fetch invalid JSON", {
        ...attemptContext,
        statusCode,
        reason,
        error: message,
      });
      errors.push({ reason, message, statusCode, endpoint });
      continue;
    }

    const handled = data && data.handled === true;
    const status = data && typeof data.status === "string" ? data.status : null;
    const message = data && typeof data.error === "string" ? data.error : null;

    logEvent("BG: bridge fetch response", {
      ...attemptContext,
      statusCode,
      handled,
      status,
    });

    if (handled) {
      return {
        accepted: true,
        handled: true,
        status: status || "accepted",
        correlationId: correlation,
        httpStatus: statusCode,
        response: data,
        reason: "accepted",
      };
    }

    const failureReason = status === "fallback" ? "app_declined" : "app_error";
    return {
      accepted: false,
      handled: Boolean(data?.handled),
      status: status || null,
      correlationId: correlation,
      httpStatus: statusCode,
      error: message || null,
      message: message || null,
      reason: failureReason,
      response: data,
    };
  }

  const primaryError = errors[0] || { reason: "bridge_unreachable", message: "Failed to reach IDMFree bridge." };
  return {
    accepted: false,
    handled: false,
    status: "error",
    correlationId,
    httpStatus: null,
    error: primaryError.message || null,
    message: primaryError.message || null,
    reason: primaryError.reason || "bridge_unreachable",
    errors,
  };
}

async function notifyStrictBlock(details) {
  const { tabId, frameId, url, reason, message, correlationId } = details || {};
  if (tabId == null || tabId < 0 || !chrome.tabs?.sendMessage) {
    return;
  }
  const notification = {
    type: "idmfree:download-blocked",
    url: url || null,
    reason: reason || null,
    message:
      message ||
      "Strict IDM mode prevented Chrome from downloading this file. Please ensure the IDMFree app is running and try again.",
    correlationId: correlationId || null,
  };
  const options = {};
  if (typeof frameId === "number" && frameId >= 0) {
    options.frameId = frameId;
  }
  try {
    await chrome.tabs.sendMessage(tabId, notification, options);
  } catch (err) {
    console.debug(`${LOG_PREFIX} Failed to dispatch strict block notification`, err);
  }
}

function normalizeExtension(ext) {
  if (!ext) {
    return null;
  }
  return ext.startsWith(".") ? ext.toLowerCase() : `.${ext.toLowerCase()}`;
}

function getExtensionFromUrl(url) {
  try {
    const parsed = new URL(url);
    const pathname = parsed.pathname || "";
    const lastSegment = pathname.split("/").pop() || "";
    const dotIndex = lastSegment.lastIndexOf(".");
    if (dotIndex <= 0) {
      return null;
    }
    return normalizeExtension(lastSegment.slice(dotIndex));
  } catch (err) {
    return null;
  }
}

function isExtensionExplicitlyAllowed(ext) {
  if (!ext) {
    return false;
  }
  return settings.alwaysInterceptExtensions.some((item) => item === ext);
}

function looksLikeDownloadExtension(ext) {
  if (!ext) {
    return false;
  }
  if (STREAMING_EXTENSION_HINTS.has(ext)) {
    return false;
  }
  return (
    isExtensionExplicitlyAllowed(ext) ||
    [
      ".pdf",
      ".doc",
      ".docx",
      ".xls",
      ".xlsx",
      ".ppt",
      ".pptx",
      ".csv",
      ".txt",
      ".rtf",
      ".psd",
      ".ai",
      ".eps",
      ".apk",
      ".dmg",
      ".pkg",
      ".img",
      ".bin",
      ".deb",
      ".rpm",
      ".rar",
      ".zip",
      ".7z",
      ".tar",
      ".gz",
      ".xz",
      ".bz2",
      ".msi",
      ".iso",
      ".exe",
      ".mp3",
      ".mp4",
      ".mkv",
      ".avi",
      ".flac",
      ".ogg",
      ".wav",
    ].includes(ext)
  );
}

function isStreamingMimeType(mime) {
  if (!mime) {
    return false;
  }
  const lower = mime.toLowerCase();
  return STREAMING_MIME_PREFIXES.some((prefix) => lower.startsWith(prefix));
}

function hasDownloadKeyword(url) {
  if (!url) {
    return false;
  }
  const lower = url.toLowerCase();
  return (
    lower.includes("download=") ||
    lower.includes("download/") ||
    lower.includes("attachment") ||
    lower.includes("export=") ||
    lower.includes("file=")
  );
}

function pruneMap(map, ttlMs) {
  const threshold = now() - ttlMs;
  for (const [key, value] of Array.from(map.entries())) {
    const timestamp = value && value.timestamp;
    if (!timestamp || timestamp < threshold) {
      map.delete(key);
    }
  }
  while (map.size > MAX_CONTEXT_ENTRIES) {
    const firstKey = map.keys().next().value;
    if (!firstKey) {
      break;
    }
    map.delete(firstKey);
  }
}

function pruneFallbackHosts() {
  const threshold = now() - FALLBACK_TTL_MS;
  for (const [host, timestamp] of Array.from(fallbackHosts.entries())) {
    if (!timestamp || timestamp < threshold) {
      fallbackHosts.delete(host);
    }
  }
}

function rememberFallbackHost(url) {
  if (!url) {
    return;
  }
  try {
    const host = new URL(url).host;
    if (host) {
      fallbackHosts.set(host, now());
    }
  } catch (err) {
    console.debug(`${LOG_PREFIX} Unable to remember fallback host`, url, err);
  }
}

function shouldBypassBridge(url) {
  if (!url) {
    return false;
  }
  pruneFallbackHosts();
  try {
    const host = new URL(url).host;
    if (!host) {
      return false;
    }
    const timestamp = fallbackHosts.get(host);
    if (!timestamp) {
      return false;
    }
    return now() - timestamp < FALLBACK_TTL_MS;
  } catch (err) {
    console.debug(`${LOG_PREFIX} Failed to evaluate fallback host`, err);
    return false;
  }
}

function shouldRememberFallbackHostForResult(result) {
  if (!result || result.accepted) {
    return false;
  }
  return result.status === "fallback";
}

function isExcludedDomain(url) {
  if (!Array.isArray(settings.domainExclusions) || settings.domainExclusions.length === 0) {
    return false;
  }
  try {
    const host = new URL(url).host;
    return settings.domainExclusions.some((pattern) => {
      if (!pattern) {
        return false;
      }
      if (pattern.startsWith("*")) {
        const suffix = pattern.slice(1);
        return host.endsWith(suffix);
      }
      return host === pattern;
    });
  } catch (err) {
    return false;
  }
}

function parseContentDisposition(headers) {
  if (!Array.isArray(headers)) {
    return null;
  }
  const header = headers.find((h) => h.name && h.name.toLowerCase() === "content-disposition");
  if (!header || !header.value) {
    return null;
  }
  const value = header.value;
  const lower = value.toLowerCase();
  const isAttachment = lower.includes("attachment");
  let fileName = null;
  const match = value.match(/filename\*?=\s*(?:UTF-8''|\")?([^";]+)/i);
  if (match && match[1]) {
    try {
      const candidate = match[1].trim();
      fileName = decodeURIComponent(candidate.replace(/\"/g, ""));
    } catch (err) {
      fileName = match[1].trim().replace(/\"/g, "");
    }
  }
  return { isAttachment, fileName };
}

function buildHeadersObject(requestHeaders) {
  if (!Array.isArray(requestHeaders) || requestHeaders.length === 0) {
    return undefined;
  }
  const headersObject = {};
  for (const header of requestHeaders) {
    if (!header || !header.name || typeof header.value !== "string") {
      continue;
    }
    const name = header.name;
    if (headersObject[name]) {
      headersObject[name] = `${headersObject[name]}, ${header.value}`;
    } else {
      headersObject[name] = header.value;
    }
  }
  return Object.keys(headersObject).length > 0 ? headersObject : undefined;
}

function guessFileNameFromUrl(url) {
  if (!url) {
    return null;
  }
  try {
    const { pathname } = new URL(url);
    if (!pathname) {
      return null;
    }
    const segment = pathname.split("/").pop();
    if (!segment) {
      return null;
    }
    return decodeURIComponent(segment.split("?")[0] || segment);
  } catch (err) {
    return null;
  }
}

async function loadSettings() {
  try {
    const stored = await chrome.storage.local.get("idmfreeSettings");
    settings = { ...DEFAULT_SETTINGS, ...(stored.idmfreeSettings || {}) };
  } catch (err) {
    console.warn(`${LOG_PREFIX} Failed to load settings`, err);
    settings = { ...DEFAULT_SETTINGS };
  }
}

async function saveSettings(newSettings) {
  settings = { ...settings, ...newSettings };
  await chrome.storage.local.set({ idmfreeSettings: settings });
  updateActionBadge();
  rebuildContextMenus();
}

function updateActionBadge() {
  if (!chrome.action) {
    return;
  }
  if (!settings.captureDownloads) {
    chrome.action.setBadgeText({ text: "OFF" });
    chrome.action.setBadgeBackgroundColor({ color: "#777777" });
  } else if (!settings.aggressiveInterception) {
    chrome.action.setBadgeText({ text: "ALT" });
    chrome.action.setBadgeBackgroundColor({ color: "#1976d2" });
  } else {
    chrome.action.setBadgeText({ text: "" });
  }
}

const CONTEXT_MENU_IDS = {
  toggleCapture: "idmfree-toggle-capture",
  toggleAggressive: "idmfree-toggle-aggressive",
};

function rebuildContextMenus() {
  if (!chrome.contextMenus) {
    return;
  }
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: CONTEXT_MENU_IDS.toggleCapture,
      title: settings.captureDownloads
        ? "Pause IDMFree interception"
        : "Resume IDMFree interception",
      contexts: ["action"],
    });
    chrome.contextMenus.create({
      id: CONTEXT_MENU_IDS.toggleAggressive,
      title: settings.aggressiveInterception
        ? "Use conservative interception"
        : "Use aggressive interception",
      contexts: ["action"],
      enabled: settings.captureDownloads,
    });
  });
}

chrome.contextMenus?.onClicked.addListener(async (info) => {
  if (info.menuItemId === CONTEXT_MENU_IDS.toggleCapture) {
    await saveSettings({ captureDownloads: !settings.captureDownloads });
    logEvent(settings.captureDownloads ? "interception_resumed" : "interception_paused");
  }
  if (info.menuItemId === CONTEXT_MENU_IDS.toggleAggressive) {
    await saveSettings({ aggressiveInterception: !settings.aggressiveInterception });
    logEvent(
      settings.aggressiveInterception
        ? "aggressive_interception_enabled"
        : "aggressive_interception_disabled"
    );
  }
});

chrome.storage?.onChanged.addListener((changes, area) => {
  if (area !== "local" || !changes.idmfreeSettings) {
    return;
  }
  const { newValue } = changes.idmfreeSettings;
  settings = { ...DEFAULT_SETTINGS, ...(newValue || {}) };
  updateActionBadge();
  rebuildContextMenus();
});

function rememberClickHint(tabId, url, hint) {
  if (tabId == null || tabId < 0 || !url) {
    return;
  }
  const key = `${tabId}:${url}`;
  clickHints.set(key, { ...hint, timestamp: now() });
  pruneMap(clickHints, CLICK_HINT_TTL_MS);
}

function readClickHint(tabId, url) {
  const key = `${tabId}:${url}`;
  const value = clickHints.get(key);
  if (!value) {
    return null;
  }
  if (now() - value.timestamp > CLICK_HINT_TTL_MS) {
    clickHints.delete(key);
    return null;
  }
  return value;
}

function clearClickHint(tabId, url) {
  const key = `${tabId}:${url}`;
  clickHints.delete(key);
}

function shouldInterceptClick(message) {
  if (!settings.captureDownloads || !settings.interceptClicks) {
    return { intercept: false };
  }
  const {
    url,
    downloadAttribute,
    hasDownloadAttribute,
    extensionHint,
    buttonLabel,
    customDownloadHint,
  } = message;
  if (!url || shouldBypassBridge(url) || isExcludedDomain(url)) {
    return { intercept: false };
  }
  const ext = extensionHint || getExtensionFromUrl(url);
  if (hasDownloadAttribute) {
    return { intercept: true, reason: "download-attribute" };
  }
  if (customDownloadHint) {
    return { intercept: true, reason: "custom-hint" };
  }
  if (isExtensionExplicitlyAllowed(ext)) {
    return { intercept: true, reason: "extension-whitelist" };
  }
  if (settings.aggressiveInterception && looksLikeDownloadExtension(ext)) {
    return { intercept: true, reason: "extension-heuristic" };
  }
  if (settings.aggressiveInterception && hasDownloadKeyword(url)) {
    return { intercept: true, reason: "download-keyword" };
  }
  if (buttonLabel && /download|save|export/i.test(buttonLabel)) {
    return { intercept: true, reason: "button-label" };
  }
  return { intercept: false };
}

async function processDownloadIntent(payload, sender) {
  const correlationId = payload?.correlationId || generateCorrelationId();

  if (!payload || !payload.url) {
    logEvent("BG: download-intent invalid payload", {
      correlationId,
    });
    return { strategy: "browser", reason: "invalid_payload", correlationId };
  }
  if (!settings.captureDownloads || !settings.interceptClicks) {
    logEvent("BG: download-intent capture disabled", {
      correlationId,
      url: payload.url,
      captureDownloads: settings.captureDownloads,
      interceptClicks: settings.interceptClicks,
    });
    return { strategy: "browser", reason: "capture_disabled", correlationId };
  }
  if (shouldBypassBridge(payload.url) || isExcludedDomain(payload.url)) {
    logEvent("BG: download-intent bypassed", {
      correlationId,
      url: payload.url,
    });
    return { strategy: "browser", reason: "capture_off_or_app_unreachable", correlationId };
  }

  const decision = shouldInterceptClick({
    url: payload.url,
    downloadAttribute: payload.hints?.downloadAttribute ?? null,
    hasDownloadAttribute: Boolean(payload.hints?.hasDownloadAttribute),
    extensionHint: payload.hints?.extensionHint ?? null,
    buttonLabel: payload.hints?.buttonLabel ?? null,
    customDownloadHint: Boolean(payload.hints?.customDownloadHint),
  });

  if (!decision.intercept) {
    logEvent("BG: download-intent declined", {
      url: payload.url,
      reason: decision.reason || "policy-declined",
      correlationId,
    });
    const policyReason = decision.reason
      ? `policy_${String(decision.reason)
          .toLowerCase()
          .replace(/[^a-z0-9]+/g, "_")
          .replace(/^_+|_+$/g, "")}`
      : "policy_declined";
    return {
      strategy: "browser",
      reason: policyReason,
      correlationId,
    };
  }

  const tabId = sender.tab?.id ?? -1;
  const frameId = sender.frameId ?? 0;
  const intent = {
    url: payload.url,
    referrer: payload.referrer || sender.url || null,
    pageUrl: payload.pageUrl || sender.url || null,
    pageTitle: payload.pageTitle || null,
    tabId,
    frameId,
    suggestedFileName: payload.suggestedFileName || null,
    correlationId,
    hints: {
      downloadAttribute: payload.hints?.downloadAttribute ?? null,
      hasDownloadAttribute: Boolean(payload.hints?.hasDownloadAttribute),
      extensionHint: payload.hints?.extensionHint ?? null,
      customDownloadHint: Boolean(payload.hints?.customDownloadHint),
      buttonLabel: payload.hints?.buttonLabel || null,
    },
  };

  if (payload.navigation) {
    intent.navigation = {
      target: payload.navigation.target || null,
      rel: payload.navigation.rel || null,
      openInNewTab: Boolean(payload.navigation.openInNewTab),
      eventButton: payload.navigation.eventButton ?? 0,
      modifiers: payload.navigation.modifiers || {},
    };
  }

  rememberClickHint(tabId, intent.url, {
    correlationId,
    reason: decision.reason,
    downloadAttribute: intent.hints.downloadAttribute,
    hasDownloadAttribute: intent.hints.hasDownloadAttribute,
    extensionHint: intent.hints.extensionHint,
    customDownloadHint: intent.hints.customDownloadHint,
    buttonLabel: intent.hints.buttonLabel,
  });

  logEvent("BG: received download-intent", {
    correlationId,
    url: intent.url,
    tabId,
    frameId,
    reason: decision.reason,
  });

  logEvent("BG: delegating to app via bridge", {
    correlationId,
    url: intent.url,
    tabId,
    frameId,
  });

  const bridgeHeaders = {};
  if (intent.referrer) {
    bridgeHeaders.Referer = intent.referrer;
  }
  const bridgePayload = {
    url: intent.url,
    fileName: intent.suggestedFileName || intent.hints?.downloadAttribute || null,
    method: "GET",
  };
  if (Object.keys(bridgeHeaders).length > 0) {
    bridgePayload.headers = bridgeHeaders;
  }

  const bridgeResult = await delegateDownloadToApp("click-intent", bridgePayload, correlationId, {
    tabId,
    frameId,
  });

  if (bridgeResult.accepted) {
    logEvent("BG: app delegation succeeded", {
      correlationId,
      url: intent.url,
      tabId,
      frameId,
      status: bridgeResult.status || "accepted",
    });
    return { strategy: "external", status: bridgeResult.status || "accepted", correlationId };
  }

  logEvent("BG: app delegation FAILED", {
    correlationId,
    url: intent.url,
    tabId,
    frameId,
    status: bridgeResult.status || "error",
    error: bridgeResult.error || null,
    reason: bridgeResult.reason || null,
  });

  const shouldRemember = shouldRememberFallbackHostForResult(bridgeResult);
  if (shouldRemember) {
    rememberFallbackHost(intent.url);
  }

  const failureReason = bridgeResult.reason || "bridge_delegate_failed";
  const failureStatus = bridgeResult.status || "error";
  const failureMessage = bridgeResult.message || bridgeResult.error || null;

  if (settings.strictMode) {
    logEvent("BG: strict block decision", {
      correlationId,
      url: intent.url,
      reason: failureReason,
      status: failureStatus,
      rememberHost: shouldRemember,
    });
    return {
      strategy: "blocked",
      status: failureStatus,
      correlationId,
      reason: failureReason,
      message: failureMessage,
    };
  }

  const fallbackStrategy = "browser_download";
  logEvent("BG: fallback decision", {
    correlationId,
    url: intent.url,
    strategy: fallbackStrategy,
    rememberHost: shouldRemember,
  });

  return {
    strategy: "browser",
    status: failureStatus,
    error: bridgeResult.error || null,
    correlationId,
    reason: failureReason,
    message: failureMessage,
    fallback: fallbackStrategy,
  };
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "idmfree:download-intent") {
    return;
  }
  processDownloadIntent(message.payload || {}, sender)
    .then((result) => {
      sendResponse(result);
    })
    .catch((err) => {
      console.warn(`${LOG_PREFIX} Failed to process download intent`, err);
      if (settings.strictMode) {
        sendResponse({
          strategy: "blocked",
          status: "error",
          reason: "internal-error",
          message: "Strict IDM mode blocked this download because the extension encountered an internal error.",
        });
        return;
      }
      sendResponse({ strategy: "browser", status: "error", error: "internal-error" });
    });
  return true;
});

function ensureRequestContext(details) {
  let context = requestContexts.get(details.requestId);
  if (!context) {
    context = {
      id: details.requestId,
      url: details.url,
      originalUrl: details.url,
      method: details.method,
      tabId: details.tabId ?? -1,
      frameId: details.frameId ?? 0,
      type: details.type,
      timestamp: now(),
      initiator: details.initiator || details.documentUrl || null,
      requestHeaders: [],
      responseHeaders: [],
      fromClick: readClickHint(details.tabId ?? -1, details.url),
      shouldIntercept: false,
      interceptReason: null,
      interceptLayer: null,
      cancelled: false,
      delegated: false,
      correlationId: null,
    };
    requestContexts.set(details.requestId, context);
  }
  return context;
}

function ensureContextCorrelationId(context) {
  if (!context) {
    return generateCorrelationId();
  }
  if (context.correlationId) {
    return context.correlationId;
  }
  const fromClickId = context.fromClick?.correlationId;
  const correlationId = fromClickId || generateCorrelationId();
  context.correlationId = correlationId;
  return correlationId;
}

function shouldInterceptAtRequestStage(context) {
  if (!settings.captureDownloads || !settings.interceptNetwork) {
    return false;
  }
  if (shouldBypassBridge(context.url) || isExcludedDomain(context.url)) {
    return false;
  }
  if (context.method === "POST" && !settings.interceptPostRequests) {
    return false;
  }
  if (context.fromClick) {
    context.interceptReason = `click:${context.fromClick.reason || "unknown"}`;
    return true;
  }
  const ext = getExtensionFromUrl(context.url);
  if (isExtensionExplicitlyAllowed(ext)) {
    context.interceptReason = "extension-whitelist";
    return true;
  }
  if (settings.aggressiveInterception) {
    if (looksLikeDownloadExtension(ext)) {
      context.interceptReason = "extension-heuristic";
      return true;
    }
    if (hasDownloadKeyword(context.url)) {
      context.interceptReason = "download-keyword";
      return true;
    }
  }
  return false;
}

function evaluateResponseForDownload(details) {
  const headers = details.responseHeaders || [];
  const disposition = parseContentDisposition(headers);
  if (disposition && (disposition.isAttachment || disposition.fileName)) {
    return {
      shouldIntercept: true,
      reason: disposition.isAttachment ? "content-disposition-attachment" : "content-disposition-filename",
      fileName: disposition.fileName || null,
    };
  }
  const contentTypeHeader = headers.find((h) => h.name && h.name.toLowerCase() === "content-type");
  const contentType = contentTypeHeader ? contentTypeHeader.value : null;
  if (contentType && isStreamingMimeType(contentType)) {
    return { shouldIntercept: false };
  }
  if (contentType && /application\//i.test(contentType) && !/json|javascript|xml|html/i.test(contentType)) {
    return { shouldIntercept: true, reason: "content-type-binary", fileName: null };
  }
  if (!contentType && details.statusCode === 200) {
    const ext = getExtensionFromUrl(details.url);
    if (looksLikeDownloadExtension(ext)) {
      return { shouldIntercept: true, reason: "no-content-type-extension", fileName: null };
    }
  }
  return { shouldIntercept: false };
}

function rememberRecentRequest(context) {
  if (!context || !context.url) {
    return;
  }
  recentRequestsByUrl.set(context.url, {
    url: context.url,
    method: context.method,
    requestHeaders: context.requestHeaders,
    timestamp: now(),
    correlationId: context.correlationId || context.fromClick?.correlationId || null,
    initiator: context.initiator || null,
    delegated: Boolean(context.delegated),
  });
  pruneMap(recentRequestsByUrl, CONTEXT_TTL_MS);
}

function cleanupRequestContext(requestId) {
  requestContexts.delete(requestId);
}

async function fallbackToBrowser(context) {
  logEvent("BG: fallback executed", {
    correlationId: context.correlationId || context.fromClick?.correlationId || null,
    url: context.url,
    layer: context.interceptLayer,
    reason: context.interceptReason,
  });
  if (context.tabId != null && context.tabId >= 0) {
    try {
      await chrome.tabs.sendMessage(context.tabId, {
        type: "idmfree:resume-download",
        url: context.url,
        downloadAttribute:
          typeof context.fromClick?.downloadAttribute === "string"
            ? context.fromClick.downloadAttribute
            : null,
        hasDownloadAttribute: Boolean(context.fromClick?.hasDownloadAttribute),
      }, {
        frameId: context.frameId,
      });
      return;
    } catch (err) {
      console.debug(`${LOG_PREFIX} Unable to resume download via content script`, err);
    }
  }
  try {
    await chrome.downloads.download({ url: context.url });
  } catch (err) {
    console.warn(`${LOG_PREFIX} Failed to restart browser download`, err);
  }
}

async function delegateToManager(context) {
  if (context.delegated) {
    return;
  }
  context.delegated = true;
  rememberRecentRequest(context);
  const correlationId = ensureContextCorrelationId(context);
  const headers = buildHeadersObject(context.requestHeaders);
  const clickDownloadName =
    typeof context.fromClick?.downloadAttribute === "string"
      ? context.fromClick.downloadAttribute.trim()
      : null;
  const bridgePayload = {
    url: context.url,
    method: context.method,
    fileName:
      (clickDownloadName && clickDownloadName.length > 0
        ? clickDownloadName
        : context.fromResponseFileName || guessFileNameFromUrl(context.url)) || null,
    headers,
  };
  logEvent("BG: delegating to app via bridge", {
    correlationId,
    url: context.url,
    layer: context.interceptLayer,
    reason: context.interceptReason,
    source: "network",
  });

  const bridgeResult = await delegateDownloadToApp("network-intercept", bridgePayload, correlationId, {
    layer: context.interceptLayer,
    reason: context.interceptReason,
    tabId: context.tabId,
    frameId: context.frameId,
  });

  if (bridgeResult.accepted) {
    logEvent("BG: app delegation succeeded", {
      correlationId,
      url: context.url,
      layer: context.interceptLayer,
      status: bridgeResult.status || "accepted",
      source: "network",
    });
    cleanupRequestContext(context.id);
    return;
  }

  logEvent("BG: app delegation FAILED", {
    correlationId,
    url: context.url,
    layer: context.interceptLayer,
    status: bridgeResult.status || "error",
    error: bridgeResult.error || null,
    reason: bridgeResult.reason || null,
    source: "network",
  });

  const shouldRemember = shouldRememberFallbackHostForResult(bridgeResult);
  if (shouldRemember) {
    rememberFallbackHost(context.url);
  }

  const failureReason = bridgeResult.reason || "bridge_delegate_failed";
  const failureMessage = bridgeResult.message || bridgeResult.error || null;
  const failureStatus = bridgeResult.status || "error";

  if (settings.strictMode) {
    logEvent("BG: strict block decision", {
      correlationId,
      url: context.url,
      layer: context.interceptLayer,
      reason: failureReason,
      status: failureStatus,
      rememberHost: shouldRemember,
      source: "network",
    });
    await notifyStrictBlock({
      tabId: context.tabId,
      frameId: context.frameId,
      url: context.url,
      reason: failureReason,
      message: failureMessage,
      correlationId,
    });
    cleanupRequestContext(context.id);
    return;
  }

  logEvent("BG: fallback decision", {
    correlationId,
    url: context.url,
    layer: context.interceptLayer,
    strategy: "browser_download",
    rememberHost: shouldRemember,
    source: "network",
  });

  await fallbackToBrowser(context);
  cleanupRequestContext(context.id);
}

function cancelAndDelegate(context, layer) {
  if (context.cancelled) {
    return;
  }
  context.cancelled = true;
  context.interceptLayer = layer;
  const correlationId = ensureContextCorrelationId(context);
  rememberRecentRequest(context);
  logEvent("BG: network interception", {
    correlationId,
    url: context.url,
    layer,
    reason: context.interceptReason,
  });
  delegateToManager(context);
}

chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (!settings.captureDownloads || !settings.interceptNetwork) {
      return {};
    }
    const context = ensureRequestContext(details);
    if (!context.shouldIntercept) {
      context.shouldIntercept = shouldInterceptAtRequestStage(context);
    }
    if (context.shouldIntercept) {
      // Defer actual cancellation to onBeforeSendHeaders so we capture headers.
    }
    return {};
  },
  { urls: ["<all_urls>"] },
  ["blocking", "requestBody"]
);

chrome.webRequest.onBeforeSendHeaders.addListener(
  (details) => {
    const context = ensureRequestContext(details);
    context.requestHeaders = details.requestHeaders || [];
    rememberRecentRequest(context);
    if (context.cancelled) {
      return { cancel: true };
    }
    if (context.shouldIntercept) {
      cancelAndDelegate(context, "network:beforeSendHeaders");
      return { cancel: true };
    }
    return {};
  },
  { urls: ["<all_urls>"] },
  ["blocking", "requestHeaders", "extraHeaders"]
);

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (!settings.captureDownloads || !settings.interceptResponseHeaders) {
      return {};
    }
    const context = ensureRequestContext(details);
    context.responseHeaders = details.responseHeaders || [];
    if (context.cancelled) {
      return { cancel: true };
    }
    const evaluation = evaluateResponseForDownload(details);
    if (evaluation.shouldIntercept && !context.shouldIntercept) {
      if (shouldBypassBridge(details.url) || isExcludedDomain(details.url)) {
        return {};
      }
      context.shouldIntercept = true;
      context.interceptReason = evaluation.reason;
      context.fromResponseFileName = evaluation.fileName || null;
      cancelAndDelegate(context, "network:headers");
      return { cancel: true };
    }
    return {};
  },
  { urls: ["<all_urls>"] },
  ["blocking", "responseHeaders", "extraHeaders"]
);

chrome.webRequest.onBeforeRedirect.addListener((details) => {
  const context = requestContexts.get(details.requestId);
  if (!context) {
    return;
  }
  context.url = details.redirectUrl || context.url;
  rememberRecentRequest(context);
}, { urls: ["<all_urls>"] });

chrome.webRequest.onCompleted.addListener((details) => {
  cleanupRequestContext(details.requestId);
}, { urls: ["<all_urls>"] });

chrome.webRequest.onErrorOccurred.addListener((details) => {
  cleanupRequestContext(details.requestId);
}, { urls: ["<all_urls>"] });

function normalizeFileName(filePath) {
  if (!filePath || typeof filePath !== "string") {
    return null;
  }
  const trimmed = filePath.trim();
  if (!trimmed) {
    return null;
  }
  const parts = trimmed.split(/[\\/]/);
  let base = parts[parts.length - 1];
  if (!base) {
    return null;
  }
  if (base.toLowerCase().endsWith(".crdownload")) {
    base = base.slice(0, -".crdownload".length);
  }
  return base || null;
}

function cancelAndErase(downloadId) {
  return new Promise((resolve) => {
    chrome.downloads.cancel(downloadId, () => {
      const cancelError = chrome.runtime.lastError;
      chrome.downloads.erase({ id: downloadId }, () => {
        const eraseError = chrome.runtime.lastError;
        if (cancelError) {
          console.warn(`${LOG_PREFIX} Failed to cancel browser download`, cancelError);
        }
        if (eraseError) {
          console.warn(`${LOG_PREFIX} Failed to erase browser download entry`, eraseError);
        }
        resolve(!cancelError);
      });
    });
  });
}

async function handleDownloadsApiCreated(item) {
  if (!settings.captureDownloads || !settings.interceptDownloadsApi) {
    return;
  }
  if (!item || !item.url || shouldBypassBridge(item.url) || isExcludedDomain(item.url)) {
    return;
  }

  const cached = recentRequestsByUrl.get(item.url) || null;
  const correlationId = cached?.correlationId || generateCorrelationId();

  if (cached?.delegated) {
    logEvent("BG: downloads.onCreated ignored", {
      correlationId,
      url: item.url,
      id: item.id,
      reason: "already-delegated",
    });
    return;
  }

  logEvent("BG: downloads.onCreated triggered", {
    correlationId,
    url: item.url,
    id: item.id,
    captureEnabled: settings.captureDownloads,
    interceptDownloadsApi: settings.interceptDownloadsApi,
  });

  const refererHeader = item.referrer || cached?.initiator || null;
  const headerBag = cached && cached.requestHeaders ? buildHeadersObject(cached.requestHeaders) : undefined;
  const bridgeHeaders = headerBag ? { ...headerBag } : {};
  if (refererHeader) {
    bridgeHeaders.Referer = refererHeader;
  }

  const bridgePayload = {
    url: item.url,
    fileName:
      normalizeFileName(item.filename) ||
      guessFileNameFromUrl(item.finalUrl || item.url) ||
      null,
    method: cached ? cached.method : "GET",
  };
  if (Object.keys(bridgeHeaders).length > 0) {
    bridgePayload.headers = bridgeHeaders;
  }

  logEvent("BG: delegating to app via bridge", {
    correlationId,
    url: item.url,
    source: "downloads-api",
  });

  let strictCancellationLogged = false;
  if (settings.strictMode) {
    const cancelStartedAt = now();
    const cancelled = await cancelAndErase(item.id);
    const cancelDurationMs = now() - cancelStartedAt;
    logEvent("BG: downloads.onCreated cancellation", {
      correlationId,
      url: item.url,
      id: item.id,
      cancelled,
      durationMs: cancelDurationMs,
      strict: true,
    });
    strictCancellationLogged = true;
  }

  const bridgeResult = await delegateDownloadToApp("downloads-api", bridgePayload, correlationId, {
    downloadId: item.id,
    tabId: item.tabId ?? -1,
  });

  if (bridgeResult.accepted) {
    if (!settings.strictMode) {
      const cancelStartedAt = now();
      const cancelled = await cancelAndErase(item.id);
      const cancelDurationMs = now() - cancelStartedAt;

      logEvent("BG: downloads.onCreated cancellation", {
        correlationId,
        url: item.url,
        id: item.id,
        cancelled,
        durationMs: cancelDurationMs,
      });
    }

    logEvent("BG: app delegation succeeded", {
      correlationId,
      url: item.url,
      status: bridgeResult.status || "accepted",
      source: "downloads-api",
    });
    return;
  }

  logEvent("BG: app delegation FAILED", {
    correlationId,
    url: item.url,
    source: "downloads-api",
    status: bridgeResult.status || "error",
    error: bridgeResult.error || null,
    reason: bridgeResult.reason || null,
  });

  const shouldRemember = shouldRememberFallbackHostForResult(bridgeResult);
  if (shouldRemember) {
    rememberFallbackHost(item.url);
  }

  const failureReason = bridgeResult.reason || "bridge_delegate_failed";
  const failureMessage = bridgeResult.message || bridgeResult.error || null;
  const failureStatus = bridgeResult.status || "error";

  if (settings.strictMode) {
    if (!strictCancellationLogged) {
      const cancelStartedAt = now();
      const cancelled = await cancelAndErase(item.id);
      const cancelDurationMs = now() - cancelStartedAt;
      logEvent("BG: downloads.onCreated cancellation", {
        correlationId,
        url: item.url,
        id: item.id,
        cancelled,
        durationMs: cancelDurationMs,
        strict: true,
      });
    }

    logEvent("BG: strict block decision", {
      correlationId,
      url: item.url,
      source: "downloads-api",
      reason: failureReason,
      status: failureStatus,
      rememberHost: shouldRemember,
    });

    await notifyStrictBlock({
      tabId: item.tabId ?? -1,
      frameId: null,
      url: item.url,
      reason: failureReason,
      message: failureMessage,
      correlationId,
    });
    return;
  }

  logEvent("BG: fallback decision", {
    correlationId,
    url: item.url,
    source: "downloads-api",
    strategy: "browser_download",
    rememberHost: shouldRemember,
  });
}

chrome.downloads.onCreated.addListener((item) => {
  handleDownloadsApiCreated(item);
});

chrome.downloads.onDeterminingFilename?.addListener((item, suggest) => {
  if (!item || !item.url) {
    suggest();
    return;
  }
  logEvent("download_filename_determining", { url: item.url, id: item.id });
  suggest();
});

chrome.runtime.onInstalled.addListener(async () => {
  const stored = await chrome.storage.local.get("idmfreeSettings");
  if (!stored.idmfreeSettings) {
    await chrome.storage.local.set({ idmfreeSettings: DEFAULT_SETTINGS });
  }
  await loadSettings();
  updateActionBadge();
  rebuildContextMenus();
});

(async () => {
  await loadSettings();
  updateActionBadge();
  rebuildContextMenus();
})();
