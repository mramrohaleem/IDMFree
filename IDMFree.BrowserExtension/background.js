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

  const payload = {
    url: item.url,
    fileName: item.filename || null,
    method: context ? context.method : "GET",
    headers: Object.keys(headersObject).length > 0 ? headersObject : undefined,
  };

  console.debug("[IDMFree] Forwarding download", payload);

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
        chrome.downloads.cancel(item.id, () => {
          if (chrome.runtime.lastError) {
            console.warn(
              "[IDMFree] Failed to cancel browser download",
              chrome.runtime.lastError
            );
          }
        });
      } else {
        console.warn(
          "[IDMFree] IDMFree API returned non-success status",
          res.status
        );
      }
    })
    .catch((err) => {
      console.error("[IDMFree] Failed to forward download", err);
      // On error, do NOT cancel the browser download.
    });
});
