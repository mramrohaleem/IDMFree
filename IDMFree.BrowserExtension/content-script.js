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

const FALLBACK_DELAY_MS = 10;

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

function buildCandidate(event) {
  if (event.button === 2 || (event.button === 1 && event.type === "click")) {
    // Ignore right-click and synthetic middle button on click event.
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
  const text = (anchor.getAttribute("aria-label") || anchor.textContent || "").trim();
  const buttonLabel = text ? text.slice(0, 80) : null;
  const highConfidence = hasDownloadAttribute || (extensionHint && INTERCEPTABLE_EXTENSIONS.has(extensionHint));
  return {
    url,
    anchor,
    downloadAttribute,
    hasDownloadAttribute,
    extensionHint,
    buttonLabel,
    target: anchor.getAttribute("target") || null,
    rel: anchor.getAttribute("rel") || null,
    highConfidence,
  };
}

function triggerNativeDownload(candidate) {
  try {
    const anchor = document.createElement("a");
    anchor.href = candidate.url;
    if (
      candidate.hasDownloadAttribute ||
      (candidate.downloadAttribute !== undefined && candidate.downloadAttribute !== null)
    ) {
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
    anchor.remove();
  } catch (err) {
    window.location.href = candidate.url;
  }
}

function handlePointerEvent(event) {
  if (!event.isTrusted) {
    return;
  }
  const candidate = buildCandidate(event);
  if (!candidate) {
    return;
  }
  let defaultPrevented = false;
  const preventDefault = () => {
    if (defaultPrevented) {
      return;
    }
    defaultPrevented = true;
    event.preventDefault();
    event.stopImmediatePropagation();
    event.stopPropagation();
  };
  if (candidate.highConfidence) {
    preventDefault();
  }
  chrome.runtime
    .sendMessage({
      type: "idmfree:candidate-download",
      url: candidate.url,
      downloadAttribute: candidate.downloadAttribute,
      hasDownloadAttribute: candidate.hasDownloadAttribute,
      extensionHint: candidate.extensionHint,
      buttonLabel: candidate.buttonLabel,
    })
    .then((response) => {
      if (response && response.intercept) {
        preventDefault();
      } else if (defaultPrevented) {
        setTimeout(() => triggerNativeDownload(candidate), FALLBACK_DELAY_MS);
      }
    })
    .catch(() => {
      if (defaultPrevented) {
        setTimeout(() => triggerNativeDownload(candidate), FALLBACK_DELAY_MS);
      }
    });
}

document.addEventListener("click", handlePointerEvent, true);
document.addEventListener("auxclick", handlePointerEvent, true);

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "idmfree:resume-download" || !message.url) {
    return;
  }
  triggerNativeDownload({
    url: message.url,
    downloadAttribute: message.downloadAttribute ?? null,
    hasDownloadAttribute: Boolean(message.hasDownloadAttribute),
    target: null,
    rel: null,
  });
  sendResponse?.();
});
