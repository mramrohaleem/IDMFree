const MAX_RECENT_ENTRIES = 200;
const MAX_ENTRY_AGE_MS = 5 * 60 * 1000; // 5 minutes

const recentRequests = new Map();

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

chrome.downloads.onCreated.addListener((item) => {
  if (!item || !item.url) {
    return;
  }

  const context = recentRequests.get(item.url);
  if (context) {
    recentRequests.delete(item.url);
  }

  console.debug(
    "[IDMFree] Download detected",
    item.id,
    item.url,
    context ? "with headers" : "without headers"
  );

  const headersObject = {};
  if (context && Array.isArray(context.requestHeaders)) {
    for (const header of context.requestHeaders) {
      if (!header || !header.name || typeof header.value !== "string") {
        continue;
      }

      headersObject[header.name] = header.value;
    }
  }

  const headers = Object.keys(headersObject).length > 0 ? headersObject : undefined;
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

  const cancelAndErase = () => {
    chrome.downloads.cancel(item.id, () => {
      if (chrome.runtime.lastError) {
        console.warn(
          "[IDMFree] Failed to cancel browser download",
          chrome.runtime.lastError
        );
      } else {
        console.debug("[IDMFree] Browser download canceled", item.id);
      }

      chrome.downloads.erase({ id: item.id }, () => {
        if (chrome.runtime.lastError) {
          console.warn(
            "[IDMFree] Failed to erase browser download entry",
            chrome.runtime.lastError
          );
        } else {
          console.debug("[IDMFree] Browser download entry removed", item.id);
        }
      });
    });
  };

  cancelAndErase();

  let fallbackTriggered = false;
  const triggerBrowserFallback = (reason) => {
    if (fallbackTriggered) {
      return;
    }
    fallbackTriggered = true;

    console.warn("[IDMFree] Falling back to browser download", reason);

    const options = { url: item.url };
    if (item.filename && item.filename.trim().length > 0) {
      options.filename = item.filename;
    } else if (normalizedFileName) {
      options.filename = normalizedFileName;
    }

    chrome.downloads.download(options, (newId) => {
      if (chrome.runtime.lastError) {
        console.error(
          "[IDMFree] Failed to restart browser download",
          chrome.runtime.lastError
        );
      } else {
        console.debug("[IDMFree] Browser download resumed", newId);
      }
    });
  };

  fetch("http://127.0.0.1:5454/api/downloads", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(payload),
  })
    .then((res) => {
      if (res.ok) {
        console.debug("[IDMFree] Download queued in IDMFree", item.id);
      } else {
        triggerBrowserFallback(`status ${res.status}`);
      }
    })
    .catch((err) => {
      triggerBrowserFallback(err);
    });
});
