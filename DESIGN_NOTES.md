# Download Session Design Notes

## DownloadSession model

Each `DownloadTask` owns a `DownloadSession` instance that captures the
diagnostic and metadata state for a single download. The session tracks:

* The original, normalized, and final request URLs.
* The HTTP method used during probing and the resulting status code.
* Raw header values used for size detection (`Content-Length`,
  `Content-Range`, `Accept-Ranges`) and the resolved `Content-Type`.
* Transfer characteristics such as range support and chunked delivery.
* Reported, downloaded, and finalized byte counts together with the
  source of the filesize information (content-length, content-range,
  disk-final, etc.).
* File planning data including the planned filename, the final filename,
  the temporary filename used for chunk merging, and the destination
  directory.
* Runtime signals (`BytesDownloadedSoFar`, `WasResumed`,
  `CompletedAt`) that allow logs and resume operations to report accurate
  state.

The session is serialized as JSON to the state store (`SessionMetadata`
column) so downloads survive restarts with their diagnostic context.

## Filename resolution

Filename selection now follows a strict priority order:

1. `Content-Disposition` (RFC 5987 aware via
   `FileNameHelper.NormalizeContentDispositionFileName`).
2. Final response URL path.
3. Browser/browser-extension provided suggestion.
4. A safe fallback (`download`).

Whenever the chosen filename lacks a useful extension or uses a generic
placeholder (`.bin`, `.tmp`), the extension is normalised via the
`Content-Type` header. All filenames pass through
`EnsureUniqueFileName`, which avoids collisions with existing files and
other tracked downloads by appending "(n)" suffixes.

## Filesize detection flow

`NetworkClient.ProbeAsync` executes a HEAD probe followed by a fallback
GET with a `Range: bytes=0-0` request when the initial headers are
insufficient. The resulting headers are parsed into a `ProbeSnapshot`
and merged into `HttpResourceInfo`, recording:

* Content length and range headers.
* `Accept-Ranges`, transfer encoding, and resumability.
* Content disposition, type, and reported filesize source.

During actual downloads every response is wrapped in
`DownloadResponseMetadata`, feeding late-discovered metadata into the
session and `DownloadTask`. When the file hits disk we mark the filesize
source as `disk-final` and update the true size.

## Structured logging

All major lifecycle events emit structured logs with consistent keys:

* **Start (`DOWNLOAD_RUN_START`)** – includes URLs, probe method/status,
  header values, resumability, chosen filenames, and planning data.
* **Progress (`DOWNLOAD_PROGRESS_*`)** – reports bytes, total, percent,
  speed, ETA, and active segment count.
* **Completion (`DOWNLOAD_RUN_COMPLETED`)** – records final file path,
  size, filesize source, duration, resume state, and connections used.
* **Errors** – attach the `Stage` (prepare, segment-download,
  merge-segments, final-validation, completed) plus HTTP status and
  error codes for precise diagnostics.

The session values are always in sync with the log output, enabling
post-mortem analysis from a single `downloadId` trace.
