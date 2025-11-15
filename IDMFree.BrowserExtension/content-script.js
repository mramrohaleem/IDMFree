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

const INTENT_RESPONSE_TIMEOUT_MS = 1500;
const FALLBACK_DELAY_MS = 20;

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

  event.preventDefault();
  event.stopImmediatePropagation();
  event.stopPropagation();

  const payload = {
    url: candidate.url,
    referrer: document.referrer || null,
    pageUrl: window.location.href,
    pageTitle: document.title || null,
    suggestedFileName: candidate.suggestedFileName || null,
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
  const finalize = (handled) => {
    if (settled) {
      return;
    }
    settled = true;
    clearTimeout(timeoutId);
    if (!handled) {
      fallbackToBrowser(candidate);
    }
  };

  const timeoutId = setTimeout(() => finalize(false), INTENT_RESPONSE_TIMEOUT_MS);

  chrome.runtime
    .sendMessage({ type: "idmfree:download-intent", payload })
    .then((response) => {
      finalize(Boolean(response && response.handled));
    })
    .catch(() => {
      finalize(false);
    });
}

document.addEventListener("click", handlePointerEvent, true);
document.addEventListener("auxclick", handlePointerEvent, true);

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "idmfree:resume-download" || !message.url) {
    return;
  }
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
