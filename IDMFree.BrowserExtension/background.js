const MAX_RECENT_ENTRIES = 200;
const MAX_ENTRY_AGE_MS = 5 * 60 * 1000; // 5 minutes

const recentRequests = new Map();
const fallbackHosts = new Map();

const FALLBACK_TTL_MS = 3 * 60 * 1000; // 3 minutes

function pruneOldEntries() {
  const now = Date.now();

  for (const [url, ctx] of Array.from(recentRequests.entries())) {
    if (!ctx || typeof ctx.timestamp !== "number") {
      recentRequests.delete(url);
      continue;
    }

    if (now - ctx.timestamp > MAX_ENTRY_AGE_MS) {
      recentRequests.delete(url);
    }
  }

  while (recentRequests.size > MAX_RECENT_ENTRIES) {
    const oldestKey = recentRequests.keys().next().value;
    if (!oldestKey) {
      break;
    }
    recentRequests.delete(oldestKey);
  }

  pruneFallbackHosts();
}

function pruneFallbackHosts() {
  const now = Date.now();
  for (const [host, timestamp] of Array.from(fallbackHosts.entries())) {
    if (!timestamp || now - timestamp > FALLBACK_TTL_MS) {
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
    if (!host) {
      return;
    }
    fallbackHosts.set(host, Date.now());
  } catch (err) {
    console.debug("[IDMFree] Unable to remember fallback host", url, err);
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

    return Date.now() - timestamp < FALLBACK_TTL_MS;
  } catch (err) {
    console.debug("[IDMFree] Failed to evaluate fallback host", err);
    return false;
  }
}

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

function pauseDownload(downloadId) {
  return new Promise((resolve) => {
    chrome.downloads.pause(downloadId, () => {
      if (chrome.runtime.lastError) {
        console.debug(
          "[IDMFree] Unable to pause browser download",
          downloadId,
          chrome.runtime.lastError
        );
        resolve(false);
        return;
      }

      resolve(true);
    });
  });
}

function resumeDownload(downloadId) {
  return new Promise((resolve) => {
    chrome.downloads.resume(downloadId, () => {
      if (chrome.runtime.lastError) {
        console.debug(
          "[IDMFree] Unable to resume browser download",
          downloadId,
          chrome.runtime.lastError
        );
        resolve(false);
        return;
      }

      resolve(true);
    });
  });
}

function cancelAndErase(downloadId) {
  return new Promise((resolve) => {
    chrome.downloads.cancel(downloadId, () => {
      const cancelError = chrome.runtime.lastError;

      chrome.downloads.erase({ id: downloadId }, () => {
        const eraseError = chrome.runtime.lastError;

        if (cancelError) {
          console.warn("[IDMFree] Failed to cancel browser download", cancelError);
        } else {
          console.debug("[IDMFree] Browser download canceled", downloadId);
        }

        if (eraseError) {
          console.warn("[IDMFree] Failed to erase browser download entry", eraseError);
        } else {
          console.debug("[IDMFree] Browser download entry removed", downloadId);
        }

        resolve(!cancelError);
      });
    });
  });
}

chrome.webRequest.onBeforeSendHeaders.addListener(
  (details) => {
    recentRequests.set(details.url, {
      method: details.method,
      requestHeaders: details.requestHeaders || [],
      timestamp: Date.now(),
    });

    pruneOldEntries();
    console.debug("[IDMFree] Captured request", details.url, details.method);
  },
  { urls: ["<all_urls>"] },
  ["requestHeaders", "extraHeaders"]
);

chrome.downloads.onCreated.addListener(async (item) => {
  if (!item || !item.url) {
    return;
  }

  const context = recentRequests.get(item.url);
  if (context) {
    recentRequests.delete(item.url);
  }

  if (shouldBypassBridge(item.url)) {
    console.debug("[IDMFree] Skipping bridge for", item.url, "due to recent fallback");
    return;
  }

  console.debug(
    "[IDMFree] Download detected",
    item.id,
    item.url,
    context ? "with headers" : "without headers"
  );

  const headers = buildHeadersObject(context ? context.requestHeaders : undefined);
  const normalizedFileName = normalizeFileName(item.filename);

  const payload = {
    url: item.url,
    fileName: normalizedFileName,
    method: context ? context.method : "GET",
  };

  if (headers) {
    payload.headers = headers;
  }

  console.debug("[IDMFree] Forwarding download", payload);

  const wasPaused = await pauseDownload(item.id);
  let handledByManager = false;

  try {
    const response = await fetch("http://127.0.0.1:5454/api/downloads", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(payload),
    });

    let body = null;
    try {
      body = await response.json();
    } catch (jsonError) {
      console.warn("[IDMFree] Failed to parse bridge response", jsonError);
    }

    const bridgeStatus = body && typeof body.status === "string" ? body.status : null;
    handledByManager = bridgeStatus === "accepted";

    if (bridgeStatus === "fallback") {
      rememberFallbackHost(item.url);
    }

    if (handledByManager) {
      await cancelAndErase(item.id);
      console.debug("[IDMFree] Download delegated to desktop manager", item.id);
      return;
    }

    const statusCode = body && typeof body.statusCode === "number" ? body.statusCode : response.status;
    if (bridgeStatus === "fallback") {
      console.debug("[IDMFree] Desktop manager requested fallback", statusCode);
    } else if (bridgeStatus === "error") {
      console.warn("[IDMFree] Desktop manager returned error", body && body.error, statusCode);
      rememberFallbackHost(item.url);
    } else if (body && body.error) {
      console.warn("[IDMFree] Desktop manager declined download", body.error, statusCode);
    } else {
      console.debug("[IDMFree] Desktop manager declined download", statusCode);
    }
  } catch (err) {
    console.warn("[IDMFree] Failed to reach desktop manager", err);
    rememberFallbackHost(item.url);
  }

  if (!handledByManager && wasPaused) {
    const resumed = await resumeDownload(item.id);
    if (resumed) {
      console.debug("[IDMFree] Browser download resumed", item.id);
    }
  }
});
