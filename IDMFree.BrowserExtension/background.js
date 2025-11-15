const LOG_PREFIX = "[IDMFree]";
const NATIVE_HOST_NAME = "com.idmfree.bridge";
const CONTEXT_TTL_MS = 5 * 60 * 1000; // 5 minutes
const MAX_CONTEXT_ENTRIES = 400;
const FALLBACK_TTL_MS = 3 * 60 * 1000; // 3 minutes
const CLICK_HINT_TTL_MS = 60 * 1000; // 1 minute

const DEFAULT_SETTINGS = Object.freeze({
  captureDownloads: true,
  aggressiveInterception: true,
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

function logEvent(event, context = {}) {
  console.debug(`${LOG_PREFIX} ${event}`, context);
}

function sendNativeBridgeMessage(message) {
  return new Promise((resolve, reject) => {
    try {
      chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, message, (response) => {
        if (chrome.runtime.lastError) {
          reject(new Error(chrome.runtime.lastError.message || "native_messaging_failed"));
          return;
        }
        resolve(response || null);
      });
    } catch (err) {
      reject(err);
    }
  });
}

async function dispatchDownloadToNative(kind, payload) {
  try {
    const response = await sendNativeBridgeMessage({
      type: "idmfree.download",
      kind,
      payload,
    });
    const status = response && typeof response.status === "string" ? response.status : null;
    return {
      accepted: status === "accepted",
      status: status || null,
      response,
    };
  } catch (err) {
    return {
      accepted: false,
      status: "error",
      error: err?.message || String(err),
    };
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
  if (!payload || !payload.url) {
    return { handled: false, reason: "invalid-payload" };
  }
  if (!settings.captureDownloads || !settings.interceptClicks) {
    return { handled: false, reason: "capture-disabled" };
  }
  if (shouldBypassBridge(payload.url) || isExcludedDomain(payload.url)) {
    logEvent("download_intent_bypassed", { url: payload.url });
    return { handled: false, reason: "bypass" };
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
    logEvent("download_intent_declined", {
      url: payload.url,
      reason: decision.reason || "policy-declined",
    });
    return { handled: false, reason: "policy-declined" };
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

  logEvent("download_flow_click_intent", {
    url: intent.url,
    tabId,
    frameId,
    reason: decision.reason,
  });

  const nativeResult = await dispatchDownloadToNative("click-intent", intent);

  if (nativeResult.accepted) {
    logEvent("download_flow_click_forwarded", {
      url: intent.url,
      tabId,
      frameId,
      status: nativeResult.status || "accepted",
      finalOwner: "idm",
    });
    return { handled: true, status: nativeResult.status || "accepted" };
  }

  rememberFallbackHost(intent.url);

  logEvent("download_flow_click_fallback", {
    url: intent.url,
    tabId,
    frameId,
    status: nativeResult.status || "error",
    error: nativeResult.error || null,
    finalOwner: "browser",
  });

  return {
    handled: false,
    status: nativeResult.status || "error",
    error: nativeResult.error || null,
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
      sendResponse({ handled: false, status: "error", error: "internal-error" });
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
    };
    requestContexts.set(details.requestId, context);
  }
  return context;
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
  });
  pruneMap(recentRequestsByUrl, CONTEXT_TTL_MS);
}

function cleanupRequestContext(requestId) {
  requestContexts.delete(requestId);
}

async function fallbackToBrowser(context) {
  rememberFallbackHost(context.url);
  logEvent("download_fell_back_to_browser", {
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
  const headers = buildHeadersObject(context.requestHeaders);
  const clickDownloadName =
    typeof context.fromClick?.downloadAttribute === "string"
      ? context.fromClick.downloadAttribute.trim()
      : null;
  const payload = {
    url: context.url,
    method: context.method,
    fileName:
      (clickDownloadName && clickDownloadName.length > 0
        ? clickDownloadName
        : context.fromResponseFileName || guessFileNameFromUrl(context.url)) || null,
    headers,
    metadata: {
      interceptLayer: context.interceptLayer,
      interceptReason: context.interceptReason,
      tabId: context.tabId,
      frameId: context.frameId,
      resourceType: context.type,
      initiator: context.initiator,
      clickDownloadAttribute: context.fromClick?.downloadAttribute ?? null,
      clickHasDownloadAttribute: Boolean(context.fromClick?.hasDownloadAttribute),
      clickLabel: context.fromClick?.buttonLabel || null,
    },
  };
  logEvent("download_forwarded_to_native_manager", {
    url: context.url,
    layer: context.interceptLayer,
    reason: context.interceptReason,
  });

  const nativeResult = await dispatchDownloadToNative("network-intercept", payload);

  if (nativeResult.accepted) {
    logEvent("download_flow_network_forwarded", {
      url: context.url,
      layer: context.interceptLayer,
      reason: context.interceptReason,
      status: nativeResult.status || "accepted",
      finalOwner: "idm",
    });
    cleanupRequestContext(context.id);
    return;
  }

  rememberFallbackHost(context.url);

  logEvent("download_flow_network_fallback", {
    url: context.url,
    layer: context.interceptLayer,
    reason: context.interceptReason,
    status: nativeResult.status || "error",
    error: nativeResult.error || null,
    finalOwner: "browser",
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
  rememberRecentRequest(context);
  logEvent("download_intercepted_at_network_layer", {
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

  logEvent("download_flow_downloads_api_created", {
    url: item.url,
    id: item.id,
  });

  const cancelStartedAt = now();
  const cancelled = await cancelAndErase(item.id);
  const cancelDurationMs = now() - cancelStartedAt;

  logEvent("download_flow_downloads_api_cancelled", {
    url: item.url,
    id: item.id,
    cancelled,
    durationMs: cancelDurationMs,
  });

  const cached = recentRequestsByUrl.get(item.url) || null;
  const payload = {
    url: item.url,
    finalUrl: item.finalUrl || item.url,
    fileName:
      normalizeFileName(item.filename) ||
      guessFileNameFromUrl(item.finalUrl || item.url) ||
      null,
    mime: item.mime || null,
    referrer: item.referrer || null,
    method: cached ? cached.method : "GET",
    tabId: item.tabId ?? -1,
    danger: item.danger || null,
    totalBytes: Number.isFinite(item.totalBytes) ? item.totalBytes : null,
    bytesReceived: Number.isFinite(item.bytesReceived) ? item.bytesReceived : null,
  };
  if (cached && cached.requestHeaders) {
    payload.headers = buildHeadersObject(cached.requestHeaders);
  }

  const nativeResult = await dispatchDownloadToNative("downloads-api", payload);

  if (nativeResult.accepted) {
    logEvent("download_flow_downloads_api_forwarded", {
      url: item.url,
      status: nativeResult.status || "accepted",
      finalOwner: "idm",
    });
    return;
  }

  rememberFallbackHost(item.url);

  logEvent("download_flow_downloads_api_fallback", {
    url: item.url,
    status: nativeResult.status || "error",
    error: nativeResult.error || null,
    finalOwner: "browser",
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
