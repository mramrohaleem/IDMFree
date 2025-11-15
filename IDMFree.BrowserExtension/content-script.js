const INTERCEPTABLE_EXTENSIONS = new Set([
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
]);

const CUSTOM_DOWNLOAD_HINT_ATTRIBUTES = [
  "data-download",
  "data-file",
  "data-file-download",
  "data-idm-download",
  "data-idmfree-download",
];

const INTENT_RESPONSE_TIMEOUT_MS = 1000;
const FALLBACK_DELAY_MS = 20;
const STRICT_NOTICE_DURATION_MS = 8000;
const STRICT_NOTICE_CONTAINER_ID = "idmfree-strict-notice";

const DEBUG_LOGGING_ENABLED = true;

function logDebug(event, details = {}) {
  if (!DEBUG_LOGGING_ENABLED) {
    return;
  }
  try {
    console.debug(`[IDMFree][CS] ${event}`, details);
  } catch (err) {
    // no-op
  }
}

function ensureNoticeContainer() {
  let container = document.getElementById(STRICT_NOTICE_CONTAINER_ID);
  if (container) {
    return container;
  }
  container = document.createElement("div");
  container.id = STRICT_NOTICE_CONTAINER_ID;
  container.style.position = "fixed";
  container.style.top = "16px";
  container.style.right = "16px";
  container.style.zIndex = "2147483647";
  container.style.display = "flex";
  container.style.flexDirection = "column";
  container.style.gap = "8px";
  container.style.maxWidth = "360px";
  container.style.fontFamily = "system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";
  const parent = document.body || document.documentElement;
  if (parent) {
    parent.appendChild(container);
  }
  return container;
}

function createStrictNoticeElement({ title, message }) {
  const wrapper = document.createElement("div");
  wrapper.style.background = "rgba(24, 24, 27, 0.95)";
  wrapper.style.color = "#ffffff";
  wrapper.style.padding = "12px 16px";
  wrapper.style.borderRadius = "8px";
  wrapper.style.boxShadow = "0 8px 24px rgba(0, 0, 0, 0.35)";
  wrapper.style.backdropFilter = "blur(4px)";
  wrapper.style.border = "1px solid rgba(255, 255, 255, 0.2)";

  const heading = document.createElement("div");
  heading.textContent = title;
  heading.style.fontSize = "14px";
  heading.style.fontWeight = "600";
  heading.style.marginBottom = "6px";

  const description = document.createElement("div");
  description.textContent = message;
  description.style.fontSize = "13px";
  description.style.lineHeight = "1.4";

  wrapper.appendChild(heading);
  wrapper.appendChild(description);
  return wrapper;
}

function showBlockingNotice({ correlationId, url, reason, message }) {
  try {
    const container = ensureNoticeContainer();
    const notice = createStrictNoticeElement({
      title: "Download blocked by IDMFree",
      message:
        message ||
        "Strict IDM mode prevented Chrome from downloading this file. Please ensure the IDMFree app is running and try again.",
    });

    notice.dataset.correlationId = correlationId || "";
    notice.dataset.url = url || "";
    notice.dataset.reason = reason || "";

    container.appendChild(notice);

    setTimeout(() => {
      notice.classList.add("idmfree-strict-notice-fade");
      notice.style.transition = "opacity 200ms ease";
      notice.style.opacity = "0";
      setTimeout(() => {
        notice.remove();
      }, 240);
    }, STRICT_NOTICE_DURATION_MS);
  } catch (err) {
    console.warn("[IDMFree][CS] Failed to display strict-mode notice", err);
  }
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

function normalizeExtension(ext) {
  if (!ext) {
    return null;
  }
  return ext.startsWith(".") ? ext.toLowerCase() : `.${ext.toLowerCase()}`;
}

function getExtensionFromUrl(url) {
  try {
    const parsed = new URL(url, document.baseURI);
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

function hasCustomDownloadHint(anchor) {
  return CUSTOM_DOWNLOAD_HINT_ATTRIBUTES.some((attr) => anchor.hasAttribute(attr));
}

function findClickableAnchor(startNode) {
  let node = startNode;
  while (node && node !== document.documentElement) {
    if (node instanceof HTMLAnchorElement && node.href) {
      return node;
    }
    node = node.parentElement;
  }
  return null;
}

function extractFileNameFromUrl(url) {
  try {
    const parsed = new URL(url, document.baseURI);
    const segment = parsed.pathname.split("/").pop();
    if (!segment) {
      return null;
    }
    const cleanSegment = segment.split("?")[0];
    return cleanSegment ? decodeURIComponent(cleanSegment) : null;
  } catch (err) {
    return null;
  }
}

function deriveSuggestedFileName({ downloadAttribute, titleAttribute, textContent, url }) {
  if (downloadAttribute && downloadAttribute.trim()) {
    return downloadAttribute.trim();
  }
  if (titleAttribute && titleAttribute.trim()) {
    return titleAttribute.trim();
  }
  if (textContent && textContent.trim()) {
    const normalized = textContent.trim().replace(/\s+/g, " ");
    if (normalized) {
      return normalized.slice(0, 120);
    }
  }
  return extractFileNameFromUrl(url);
}

function buildCandidate(event) {
  if (event.button === 2 || (event.button === 1 && event.type === "click")) {
    // Ignore right-click and the synthetic middle button click event.
    return null;
  }
  const anchor = findClickableAnchor(event.target);
  if (!anchor) {
    return null;
  }
  const url = anchor.href;
  if (!url || url.startsWith("javascript:")) {
    return null;
  }
  if (/^data:|^blob:/i.test(url)) {
    return null;
  }
  const hasDownloadAttribute = anchor.hasAttribute("download");
  const downloadAttribute = hasDownloadAttribute ? anchor.getAttribute("download") : null;
  const extensionHint = getExtensionFromUrl(url);
  const customDownloadHint = hasCustomDownloadHint(anchor);
  const text = (anchor.getAttribute("aria-label") || anchor.textContent || "").trim();
  const buttonLabel = text ? text.slice(0, 80) : null;
  const looksLikeDownload =
    hasDownloadAttribute ||
    customDownloadHint ||
    (extensionHint && INTERCEPTABLE_EXTENSIONS.has(extensionHint));
  if (!looksLikeDownload) {
    return null;
  }
  const suggestedFileName = deriveSuggestedFileName({
    downloadAttribute,
    titleAttribute: anchor.getAttribute("title"),
    textContent: text,
    url,
  });
  const targetAttribute = anchor.getAttribute("target") || null;
  const openInNewTab =
    event.button === 1 ||
    event.ctrlKey ||
    event.metaKey ||
    (targetAttribute && targetAttribute.toLowerCase() === "_blank");
  return {
    url,
    anchor,
    downloadAttribute,
    hasDownloadAttribute,
    extensionHint,
    buttonLabel,
    customDownloadHint,
    suggestedFileName,
    target: targetAttribute,
    rel: anchor.getAttribute("rel") || null,
    eventButton: event.button,
    eventType: event.type,
    modifiers: {
      altKey: event.altKey,
      ctrlKey: event.ctrlKey,
      metaKey: event.metaKey,
      shiftKey: event.shiftKey,
    },
    openInNewTab,
  };
}

function triggerBrowserDownload(candidate) {
  try {
    const anchor = document.createElement("a");
    anchor.href = candidate.url;
    if (candidate.hasDownloadAttribute) {
      const value =
        candidate.downloadAttribute !== undefined && candidate.downloadAttribute !== null
          ? candidate.downloadAttribute
          : "";
      anchor.setAttribute("download", value);
    }
    if (candidate.target) {
      anchor.setAttribute("target", candidate.target);
    }
    if (candidate.rel) {
      anchor.setAttribute("rel", candidate.rel);
    }
    anchor.style.display = "none";
    (document.body || document.documentElement).appendChild(anchor);
    anchor.click();
    requestAnimationFrame(() => anchor.remove());
  } catch (err) {
    window.location.href = candidate.url;
  }
}

function fallbackToBrowser(candidate) {
  if (!candidate || !candidate.url) {
    return;
  }
  logDebug("CS: executing browser fallback", {
    url: candidate.url,
    openInNewTab: Boolean(candidate.openInNewTab),
  });
  if (candidate.openInNewTab) {
    setTimeout(() => {
      window.open(candidate.url, candidate.target || "_blank");
    }, FALLBACK_DELAY_MS);
    return;
  }
  setTimeout(() => triggerBrowserDownload(candidate), FALLBACK_DELAY_MS);
}

function handlePointerEvent(event) {
  if (!event.isTrusted) {
    return;
  }
  const candidate = buildCandidate(event);
  if (!candidate) {
    return;
  }

  const correlationId = generateCorrelationId();

  logDebug("CS: click captured", {
    correlationId,
    url: candidate.url,
    eventType: event.type,
    button: event.button,
    hasDownloadAttribute: candidate.hasDownloadAttribute,
  });

  event.preventDefault();
  event.stopImmediatePropagation();
  event.stopPropagation();

  const payload = {
    url: candidate.url,
    referrer: document.referrer || null,
    pageUrl: window.location.href,
    pageTitle: document.title || null,
    suggestedFileName: candidate.suggestedFileName || null,
    correlationId,
    hints: {
      downloadAttribute: candidate.downloadAttribute,
      hasDownloadAttribute: candidate.hasDownloadAttribute,
      extensionHint: candidate.extensionHint,
      buttonLabel: candidate.buttonLabel,
      customDownloadHint: candidate.customDownloadHint,
    },
    navigation: {
      target: candidate.target,
      rel: candidate.rel,
      openInNewTab: candidate.openInNewTab,
      eventButton: candidate.eventButton,
      modifiers: candidate.modifiers,
    },
  };

  let settled = false;
  const finalize = (decision) => {
    if (settled) {
      return;
    }
    settled = true;
    clearTimeout(timeoutId);
    const strategy = decision?.strategy || "browser";
    const reason = decision?.reason || null;
    const status = decision?.status || null;
    const message = decision?.message || null;

    if (strategy === "external") {
      logDebug("CS: strategy=external, suppressing browser", {
        correlationId,
        url: candidate.url,
        reason,
      });
      return;
    }
    if (strategy === "blocked") {
      logDebug("CS: strategy=blocked, preventing browser download", {
        correlationId,
        url: candidate.url,
        reason,
        status,
      });
      showBlockingNotice({
        correlationId,
        url: candidate.url,
        reason,
        message,
      });
      return;
    }
    logDebug("CS: strategy=browser, allowing native download", {
      correlationId,
      url: candidate.url,
      reason,
      status,
    });
    fallbackToBrowser(candidate);
  };

  const timeoutId = setTimeout(() => {
    logDebug("CS: background timeout, blocking download", {
      correlationId,
      url: candidate.url,
      timeoutMs: INTENT_RESPONSE_TIMEOUT_MS,
    });
    finalize({ strategy: "blocked", reason: "timeout" });
  }, INTENT_RESPONSE_TIMEOUT_MS);

  logDebug("CS: sending download intent to background", { correlationId, url: candidate.url });

  chrome.runtime
    .sendMessage({ type: "idmfree:download-intent", payload })
    .then((response) => {
      if (!response) {
        logDebug("CS: download intent response missing", {
          correlationId,
          url: candidate.url,
        });
        finalize({ strategy: "blocked", reason: "no-response" });
        return;
      }

      const { strategy, reason, status, message } = response;
      logDebug("CS: download intent response received", {
        correlationId,
        strategy: strategy || null,
        status: status || null,
        reason: reason || null,
      });

      finalize({ strategy: strategy || "browser", reason, status, message });
    })
    .catch((err) => {
      logDebug("CS: download intent send failed", {
        correlationId,
        error: err?.message || String(err),
      });
      finalize({ strategy: "blocked", reason: "send-error", message: err?.message || String(err) });
    });
}

document.addEventListener("click", handlePointerEvent, true);
document.addEventListener("auxclick", handlePointerEvent, true);

logDebug("CS: registered download-intent listeners", {
  events: ["click", "auxclick"],
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "idmfree:resume-download" || !message.url) {
    if (message && message.type === "idmfree:download-blocked") {
      logDebug("CS: strict-mode block notification received", {
        url: message.url || null,
        reason: message.reason || null,
        correlationId: message.correlationId || null,
      });
      showBlockingNotice({
        correlationId: message.correlationId || null,
        url: message.url || null,
        reason: message.reason || null,
        message:
          message.message ||
          "Strict IDM mode prevented Chrome from downloading this file. Please ensure the IDMFree app is running and try again.",
      });
    }
    return;
  }
  logDebug("CS: resume-download request received", {
    url: message.url,
  });
  fallbackToBrowser({
    url: message.url,
    downloadAttribute: message.downloadAttribute ?? null,
    hasDownloadAttribute: Boolean(message.hasDownloadAttribute),
    target: message.target || null,
    rel: message.rel || null,
    openInNewTab: Boolean(message.openInNewTab),
  });
  sendResponse?.();
});
