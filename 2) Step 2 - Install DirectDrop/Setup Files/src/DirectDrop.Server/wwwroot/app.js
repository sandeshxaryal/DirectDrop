// DirectDrop mobile client.
// Files shared by the PC are transferred automatically while this page is open.
// Completed file data stays only in this page's memory until the user saves it.

const urlToken = new URLSearchParams(location.search).get('token');
const token = urlToken || sessionStorage.getItem('dd-token') || '';
if (urlToken) sessionStorage.setItem('dd-token', urlToken);
const navigationEntry = performance.getEntriesByType('navigation')[0];

const statusDot = document.getElementById('statusDot');
const statusText = document.getElementById('statusText');
const pcName = document.getElementById('pcName');
const pcNameSecondary = document.getElementById('pcNameSecondary');
const transferList = document.getElementById('transferList');
const emptyState = document.getElementById('emptyState');
const filePicker = document.getElementById('filePicker');
const downloadAllButton = document.getElementById('downloadAllButton');
const cancelAllButton = document.getElementById('cancelAllButton');
const reloadNotice = document.getElementById('reloadNotice');
if (reloadNotice && navigationEntry?.type === 'reload') {
  reloadNotice.hidden = false;
  reloadNotice.textContent = 'Page refreshed. Reconnecting… interrupted transfers stay on the PC and can be resumed by selecting the same file again.';
  setTimeout(() => { reloadNotice.hidden = true; }, 5000);
}

const readyFiles = new Map();
const activeDownloads = new Set();
const knownFileKeys = new Set();
const cancelledTransferKeys = new Set();
const downloadQueue = [];
const transferControllers = new Map();
const transferRows = new Map();
const serverTransferStates = new Map();
let processingQueue = false;
let sessionReady = false;


function authHeaders(extra) {
  return Object.assign({ 'X-DirectDrop-Token': token }, extra || {});
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes, unitIndex = -1;
  do { value /= 1024; unitIndex++; } while (value >= 1024 && unitIndex < units.length - 1);
  return `${value.toFixed(value < 10 ? 2 : 1)} ${units[unitIndex]}`;
}

function formatSpeed(bytesPerSecond) {
  return `${formatBytes(bytesPerSecond)}/s`;
}

function formatEta(seconds) {
  if (!isFinite(seconds) || seconds <= 0) return '—';
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return m > 0 ? `${m}m ${s}s` : `${s}s`;
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

function fileIcon(name) {
  const ext = (name.split('.').pop() || '').toLowerCase();
  const stroke = 'currentColor';
  const svg = body => `<svg viewBox="0 0 24 24" fill="none" stroke="${stroke}" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
  if (['jpg','jpeg','png','heic','gif','webp','bmp'].includes(ext)) return svg('<rect x="3" y="4" width="18" height="16" rx="3"/><circle cx="8.5" cy="9" r="1.4"/><path d="m5 17 4.2-4 3.2 3 2.3-2.2L19 17"/>');
  if (['mp4','mov','m4v','avi','mkv','webm'].includes(ext)) return svg('<rect x="3" y="5" width="18" height="14" rx="3"/><path d="m10 9 5 3-5 3V9Z" fill="currentColor" stroke="none"/>');
  if (['mp3','m4a','wav','aac','flac'].includes(ext)) return svg('<path d="M9 18V6l9-2v12"/><circle cx="6.5" cy="18" r="2.5"/><circle cx="15.5" cy="16" r="2.5"/>');
  if (['pdf'].includes(ext)) return svg('<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v5h5M8 16h2.5a1.5 1.5 0 0 0 0-3H8v6m7-6h-2v6h2a3 3 0 0 0 0-6Z"/>');
  if (['doc','docx','pages','txt','rtf'].includes(ext)) return svg('<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v5h5M9 12h6M9 16h6"/>');
  if (['zip','rar','7z','tar','gz'].includes(ext)) return svg('<path d="M7 3h10v18H7z"/><path d="M10 3v3h4V3M10 9h4M10 13h4"/>');
  if (['xls','xlsx','csv','numbers'].includes(ext)) return svg('<rect x="4" y="4" width="16" height="16" rx="2"/><path d="M4 9h16M9 4v16M14 9v11"/>');
  return svg('<path d="M4 6a2 2 0 0 1 2-2h5l2 2h5a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6Z"/>');
}

function isPhotoOrVideo(name) {
  const ext = (name.split('.').pop() || '').toLowerCase();
  return ['jpg','jpeg','png','heic','gif','webp','bmp','mp4','mov','m4v','avi','mkv','webm'].includes(ext);
}

function mimeFor(name) {
  const ext = (name.split('.').pop() || '').toLowerCase();
  const map = {
    jpg:'image/jpeg', jpeg:'image/jpeg', png:'image/png', heic:'image/heic',
    gif:'image/gif', webp:'image/webp', bmp:'image/bmp',
    mp4:'video/mp4', mov:'video/quicktime', m4v:'video/x-m4v', webm:'video/webm',
    mp3:'audio/mpeg', m4a:'audio/mp4', wav:'audio/wav', pdf:'application/pdf',
    zip:'application/zip', json:'application/json', txt:'text/plain'
  };
  return map[ext] || 'application/octet-stream';
}

function fileKey(file) {
  return `${file.relativePath}|${file.sizeBytes}|${file.modifiedUtc || file.lastWriteTimeUtc || ''}`;
}

// ---------- Connection ----------

async function refreshSession() {
  if (!token) {
    statusDot.className = 'dot dot--error';
    statusText.textContent = 'Missing session code';
    return false;
  }

  try {
    const res = await fetch(`/api/session?token=${encodeURIComponent(token)}`, {
      headers: authHeaders(),
      cache: 'no-store'
    });
    if (!res.ok) throw new Error(`session (${res.status})`);
    const data = await res.json();
    pcName.textContent = data.pcName;
    if (pcNameSecondary) pcNameSecondary.textContent = data.pcName;
    statusDot.className = 'dot dot--connected';
    statusText.textContent = 'Connected locally';
    sessionReady = true;
    return true;
  } catch {
    sessionReady = false;
    statusDot.className = 'dot dot--error';
    statusText.textContent = 'Connection lost';
    return false;
  }
}

async function fetchTransferSnapshot() {
  const res = await fetch('/api/transfers', { headers: authHeaders(), cache: 'no-store' });
  if (!res.ok) throw new Error(`transfers (${res.status})`);
  return res.json();
}


// ---------- Transfer UI ----------

function createTransferRow(name, direction = 'download', transferKey = '', options = {}) {
  const li = document.createElement('li');
  li.className = 'transferRow';
  if (options.largeBrowserDownload) li.classList.add('transferRow--browserDownload');
  li.innerHTML = `
    <div class="transferTop">
      <div class="name"><span class="fileIcon" aria-hidden="true">${fileIcon(name)}</span><span>${escapeHtml(name)}</span></div>
      <div class="transferTopRight"><span class="state">Preparing…</span><div class="cancelArea"></div></div>
    </div>
    <div class="progressTrack"><div class="progressFill"></div></div>
    <div class="transferMeta"><span class="sizeText"></span><span class="speedText"></span></div>
    <div class="saveArea"></div>`;

  transferList.prepend(li);
  emptyState.style.display = 'none';

  const result = {
    row: li,
    fill: li.querySelector('.progressFill'),
    state: li.querySelector('.state'),
    sizeText: li.querySelector('.sizeText'),
    speedText: li.querySelector('.speedText'),
    saveArea: li.querySelector('.saveArea'),
    cancelArea: li.querySelector('.cancelArea'),
    key: transferKey,
    direction,
    transferId: '',
    resumeKey: '',
    sourceFile: null,
    retryButton: null,
    isRetrying: false,
    largeBrowserDownload: !!options.largeBrowserDownload
  };

  transferRows.set(transferKey, result);
  return result;
}

function transferKeyForServer(item) {
  return item.Direction === 'Download' ? `download:${item.Id}` : `upload:${item.Id}`;
}

function showRetryButton(row, file) {
  if (!row || row.retryButton) return;
  const button = document.createElement('button');
  button.className = 'retryButton';
  button.type = 'button';
  button.textContent = 'Receive again';
  button.addEventListener('click', () => {
    button.disabled = true;
    file.manualRetry = true;
    row.saveArea.innerHTML = '';
    row.state.textContent = 'Starting…';
    row.speedText.textContent = '';
    row.fill.classList.remove('error');
    knownFileKeys.delete(fileKey(file));
    cancelledTransferKeys.delete(row.key);
    transferRows.delete(row.key);
    row.row.remove();
    queueDownload(file);
  });
  row.retryButton = button;
  row.saveArea.appendChild(button);
}

function showRefreshInterrupted(file, status = 'Paused') {
  const key = file.transferId ? `download:${file.transferId}` : fileKey(file);
  if (transferRows.has(key)) return;
  const row = createTransferRow(file.relativePath, 'download', key);
  row.transferId = file.transferId || '';
  row.sourceFile = file;
  row.state.textContent = status === 'Completed' ? 'Not saved after refresh' : 'Interrupted by refresh';
  row.speedText.textContent = 'Select Receive again to restart';
  row.fill.style.width = status === 'Completed' ? '100%' : '0%';
  if (status === 'Completed') row.fill.classList.add('done');
  showRetryButton(row, file);
}

function findUploadRowForServerItem(item) {
  // Phone -> PC rows start life with a client-side key based on filename and
  // timestamp. The server later identifies the same transfer by uploadId.
  // Match the existing row before considering creation, otherwise the UI
  // renders the same upload twice.
  for (const row of transferRows.values()) {
    if (row.direction !== 'upload') continue;
    if (row.transferId === item.Id) return row;
    const file = row.sourceFile;
    if (file && file.name === item.FileName && Number(file.size) === Number(item.TotalBytes)) return row;
  }
  return null;
}

function applyServerTransferSnapshot(items) {
  for (const item of items || []) {
    serverTransferStates.set(item.Id, item.Status);
    const key = transferKeyForServer(item);
    if (cancelledTransferKeys.has(key)) continue;

    // Download rows are created from /api/files, which carries the same
    // server transfer ID. Upload rows are created immediately when the phone
    // selects a file and are reconciled to the server ID here. Never create
    // a second upload row for a transfer that already has a client-side row.
    let row = transferRows.get(key);
    if (!row && item.Direction === 'Upload') row = findUploadRowForServerItem(item);
    if (!row && item.Direction === 'Upload') {
      row = createTransferRow(item.FileName, 'upload', key);
      row.transferId = item.Id;
      row.sourceFile = null;
      row.serverAuthoritative = true;
    }
    if (!row) continue;
    if (item.Direction === 'Upload') {
      row.transferId = item.Id;
      row.serverAuthoritative = true;
    }

    const pct = Math.max(0, Math.min(100, item.PercentComplete || 0));
    row.fill.style.width = `${pct}%`;
    row.sizeText.textContent = `${formatBytes(item.TransferredBytes || 0)} / ${formatBytes(item.TotalBytes || 0)} (${Math.round(pct)}%)`;
    row.speedText.textContent = item.SpeedBytesPerSecond ? `${formatSpeed(item.SpeedBytesPerSecond)}${item.EtaSeconds ? ` · ETA ${formatEta(item.EtaSeconds)}` : ''}` : '';

    if (item.Direction === 'Upload') {
      if (item.Status === 'Completed') {
        row.fill.style.width = '100%';
        row.fill.classList.add('done');
        row.state.textContent = 'Sent to PC';
        row.speedText.textContent = 'Transfer complete';
        row.cancelArea.innerHTML = '';
        row.saveArea.innerHTML = '';
        row.refreshRecovery = false;
      } else if (item.Status === 'InProgress') {
        row.state.textContent = 'Transferring…';
      } else if (item.Status === 'Paused') {
        row.state.textContent = 'Paused after refresh';
        row.speedText.textContent = 'Select the same file again to resume';
        row.cancelArea.innerHTML = '';
        row.refreshRecovery = true;
      } else if (item.Status === 'Cancelled') {
        row.state.textContent = 'Cancelled';
        row.speedText.textContent = 'Transfer cancelled';
        row.cancelArea.innerHTML = '';
        cancelledTransferKeys.add(key);
        activeDownloads.delete(key);
        void fadeOutRow(row);
      } else if (item.Status === 'Failed') {
        row.state.textContent = 'Transfer failed';
        row.speedText.textContent = item.ErrorMessage || 'Try again';
        row.fill.classList.add('error');
        row.cancelArea.innerHTML = '';
      } else {
        row.state.textContent = 'Waiting…';
      }
      continue;
    }

    if (item.Status === 'Queued') {
      row.state.textContent = 'Waiting…';
      row.speedText.textContent = 'Selected on PC';
    } else if (item.Status === 'InProgress') {
      row.state.textContent = 'Receiving…';
    } else if (item.Status === 'Paused') {
      row.state.textContent = 'Interrupted by refresh';
      row.speedText.textContent = 'Select Receive again to restart';
      row.refreshRecovery = true;
      row.cancelArea.innerHTML = '';
      showRetryButton(row, { relativePath: item.FileName, sizeBytes: item.TotalBytes, transferId: item.Id, modifiedUtc: '' });
    } else if (item.Status === 'Completed') {
      row.state.textContent = navigationEntry?.type === 'reload' ? 'Completed before refresh' : 'Completed';
      row.speedText.textContent = navigationEntry?.type === 'reload' ? 'Receive again to download a fresh copy' : 'Transfer complete';
      row.fill.style.width = '100%';
      row.fill.classList.add('done');
      row.cancelArea.innerHTML = '';
      if (navigationEntry?.type === 'reload') {
        showRetryButton(row, { relativePath: item.FileName, sizeBytes: item.TotalBytes, transferId: item.Id, modifiedUtc: '' });
      }
    } else if (item.Status === 'Cancelled') {
      row.state.textContent = 'Cancelled';
      row.speedText.textContent = 'Transfer cancelled';
      row.cancelArea.innerHTML = '';
      cancelledTransferKeys.add(key);
      activeDownloads.delete(key);
      readyFiles.delete(key);
      void fadeOutRow(row);
    } else if (item.Status === 'Failed') {
      row.state.textContent = 'Transfer failed';
      row.speedText.textContent = item.ErrorMessage || 'Try again';
      row.fill.classList.add('error');
      row.cancelArea.innerHTML = '';
    }
  }
  updateSaveAllButton();
}

function updateSaveAllButton() {
  downloadAllButton.disabled = readyFiles.size === 0;
  downloadAllButton.textContent = readyFiles.size ? `Save all (${readyFiles.size})` : 'Save all';
  cancelAllButton.disabled = ![...transferRows.values()].some(row => row.cancelArea?.querySelector('.cancelButton')) && downloadQueue.length === 0;
}

function showSaveButton(key, row, fileData) {
  const label = isPhotoOrVideo(fileData.name) ? 'Save to Photos / device' : 'Save to Files / device';
  const button = document.createElement('button');
  button.className = 'saveButton';
  button.type = 'button';
  button.textContent = label;
  button.addEventListener('click', async () => {
    button.disabled = true;
    button.textContent = 'Opening…';
    try {
      const file = new File([fileData.blob], fileData.name, { type: fileData.blob.type || mimeFor(fileData.name) });
      if (navigator.share && navigator.canShare && navigator.canShare({ files: [file] })) {
        await navigator.share({ files: [file], title: fileData.name });
      } else {
        const url = URL.createObjectURL(fileData.blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileData.name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
      }
      readyFiles.delete(key);
      row.state.textContent = 'Saved';
      row.saveArea.innerHTML = '';
      updateSaveAllButton();
    } catch {
      button.disabled = false;
      button.textContent = label;
    }
  });
  row.saveArea.appendChild(button);
}

function fadeOutRow(entry, duration = 320) {
  return new Promise(resolve => {
    if (!entry?.row || !entry.row.isConnected) return resolve();
    entry.row.classList.add('transferRow--removing');
    entry.row.style.pointerEvents = 'none';
    setTimeout(() => {
      entry.row.remove();
      resolve();
    }, duration);
  });
}

function addCancelButton(transferKey, row, onCancel) {
  if (row.cancelArea.querySelector('.cancelButton')) return;
  const button = document.createElement('button');
  button.className = 'cancelButton';
  button.type = 'button';
  button.textContent = '×';
  button.title = 'Cancel transfer';
  button.setAttribute('aria-label', 'Cancel transfer');
  button.addEventListener('click', async () => {
    button.disabled = true;
    try {
      await onCancel();
    } catch {
      button.disabled = false;
    }
  });
  row.cancelArea.appendChild(button);
  updateSaveAllButton();
}

async function cancelServerTransfer(transferId, kind) {
  const endpoint = kind === 'upload'
    ? `/api/upload/cancel/${encodeURIComponent(transferId)}`
    : `/api/files/cancel/${encodeURIComponent(transferId)}`;
  try {
    await fetch(endpoint, { method: 'POST', headers: authHeaders(), cache: 'no-store', keepalive: true });
  } catch {}
}

async function cancelTransfer(key) {
  const entry = transferRows.get(key);
  if (!entry) return;

  const controller = transferControllers.get(key);
  if (controller?.abort) controller.abort();

  if (entry.direction === 'upload' && entry.resumeKey) sessionStorage.removeItem(entry.resumeKey);
  if (entry.transferId) await cancelServerTransfer(entry.transferId, entry.direction);

  cancelledTransferKeys.add(key);
  activeDownloads.delete(key);
  readyFiles.delete(key);
  downloadQueue.splice(0, downloadQueue.length, ...downloadQueue.filter(item => fileKey(item) !== key));

  entry.state.textContent = 'Cancelled';
  entry.speedText.textContent = 'Transfer cancelled';
  entry.fill.classList.add('error');
  entry.cancelArea.innerHTML = '';
  transferControllers.delete(key);
  transferRows.delete(key);
  await fadeOutRow(entry);
  if (!transferList.children.length) emptyState.style.display = 'block';
  updateSaveAllButton();
}

async function cancelAllTransfers() {
  const active = [...transferRows.entries()].filter(([, row]) => row.cancelArea?.querySelector('.cancelButton'));
  const queued = [...downloadQueue];
  const total = active.length + queued.length;
  if (!total) return;
  if (!window.confirm(`Cancel ${total} active transfer${total === 1 ? '' : 's'}?`)) return;

  downloadQueue.length = 0;
  for (const file of queued) cancelledTransferKeys.add(fileKey(file));
  for (const [key] of active) await cancelTransfer(key);
  if (!transferList.children.length) emptyState.style.display = 'block';
  updateSaveAllButton();
}

// ---------- PC -> phone ----------

async function autoDownload(file) {
  const key = file.transferId ? `download:${file.transferId}` : fileKey(file);
  if (!sessionReady || activeDownloads.has(key) || cancelledTransferKeys.has(key)) return;

  activeDownloads.add(key);
  knownFileKeys.add(key);

  const transferId = `dl-${window.crypto?.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`}`;
  let row = transferRows.get(key);
  if (!row) row = createTransferRow(file.relativePath, 'download', key);
  row.transferId = file.transferId || transferId;
  row.sourceFile = file;
  row.retryButton = null;
  row.isRetrying = !!file.manualRetry;
  const controller = new AbortController();
  transferControllers.set(key, controller);
  addCancelButton(key, row, () => cancelTransfer(key));

  const effectiveTransferId = file.transferId || transferId;
  const href = `/api/files/download?path=${encodeURIComponent(file.relativePath)}&token=${encodeURIComponent(token)}&transferId=${encodeURIComponent(effectiveTransferId)}`;
  const totalBytes = Number(file.sizeBytes) || 0;
  const LARGE_FILE_THRESHOLD = 1024 * 1024 * 1024;
  if (totalBytes > LARGE_FILE_THRESHOLD) {
    row.row.classList.add('transferRow--browserDownload');
    row.largeBrowserDownload = true;
  }

  // Never load very large files into browser memory. Let the browser's native
  // download manager stream the response straight to device storage. The
  // server remains the authority for progress, so the data path is identical.
  if (totalBytes > LARGE_FILE_THRESHOLD) {
    try {
      row.row.classList.add('transferRow--browserDownload');
      row.largeBrowserDownload = true;
      row.state.textContent = 'Downloads in phone browser';
      row.sizeText.textContent = formatBytes(totalBytes);
      row.speedText.textContent = 'Downloads in phone browser';

      const a = document.createElement('a');
      a.href = href;
      a.download = file.relativePath.split('/').pop() || 'download';
      a.rel = 'noopener';
      a.style.display = 'none';
      document.body.appendChild(a);
      a.click();
      a.remove();
      // The browser owns this native download request, so the page cannot
      // reliably abort its underlying network stream. Do not expose a cancel
      // button that would only cancel server bookkeeping. The browser's own
      // download controls remain the authoritative way to cancel it.
      row.cancelArea.innerHTML = '';

      let stable = 0;
      while (sessionReady && !cancelledTransferKeys.has(key) && stable < 3) {
        try {
          const snapshot = await fetchTransferSnapshot();
          const item = snapshot.find(t => t.Id === effectiveTransferId);
          if (item) {
            row.sizeText.textContent = formatBytes(item.TotalBytes || totalBytes);
            row.speedText.textContent = 'Downloads in phone browser';
            if (item.Status === 'Completed') {
              stable++;
              row.state.textContent = 'Download complete';
              row.cancelArea.innerHTML = '';
              break;
            } else if (item.Status === 'Failed') {
              throw new Error(item.ErrorMessage || 'download failed');
            } else if (item.Status === 'Cancelled') {
              return;
            } else {
              stable = 0;
              row.state.textContent = 'Downloads in phone browser';
            }
          }
        } catch (pollError) {
          if (pollError?.message?.includes('download failed')) throw pollError;
        }
        await new Promise(r => setTimeout(r, 700));
      }
      return;
    } catch (err) {
      if (err?.name === 'AbortError' || cancelledTransferKeys.has(key)) return;
      row.state.textContent = 'Transfer failed';
      row.speedText.textContent = err.message || 'Try again';
      row.fill.classList.add('error');
      return;
    } finally {
      activeDownloads.delete(key);
      transferControllers.delete(key);
      updateSaveAllButton();
    }
  }

  try {
    row.state.textContent = '';
    const response = await fetch(href, {
      headers: authHeaders(),
      signal: controller.signal,
      cache: 'no-store'
    });

    if (!response.ok) {
      throw new Error(response.status === 401 ? 'Session expired — scan the new QR code.' : `download failed (${response.status})`);
    }

    const total = Number(response.headers.get('Content-Length')) || file.sizeBytes;
    const reader = response.body?.getReader();
    let received = 0;
    const chunks = [];
    let lastBytes = 0;
    let lastTime = performance.now();

    if (reader) {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        chunks.push(value);
        received += value.byteLength;

        const now = performance.now();
        if (now - lastTime >= 100) {
          const elapsed = Math.max(0.001, (now - lastTime) / 1000);
          const speed = (received - lastBytes) / elapsed;
          const pct = total ? Math.min(100, received / total * 100) : 0;
          row.fill.style.width = `${pct}%`;
          row.sizeText.textContent = `${formatBytes(received)} / ${formatBytes(total)} (${Math.round(pct)}%)`;
          row.speedText.textContent = `${formatSpeed(speed)} · ETA ${formatEta(speed > 0 ? (total - received) / speed : Infinity)}`;
          lastBytes = received;
          lastTime = now;
        }
      }
    } else {
      const blob = await response.blob();
      chunks.push(blob);
      received = blob.size;
    }

    const blob = chunks.length === 1 && chunks[0] instanceof Blob
      ? chunks[0]
      : new Blob(chunks, { type: mimeFor(file.relativePath) });

    row.fill.style.width = '100%';
    row.fill.classList.add('done');
    row.sizeText.textContent = formatBytes(blob.size);
    row.speedText.textContent = 'Transfer complete';
    row.state.textContent = 'Ready to save';
    row.cancelArea.innerHTML = '';

    const fileData = { name: file.relativePath.split('/').pop(), blob };
    readyFiles.set(key, fileData);
    showSaveButton(key, row, fileData);
  } catch (err) {
    if (err?.name === 'AbortError' || cancelledTransferKeys.has(key) || row.state.textContent === 'Cancelled') return;
    row.state.textContent = 'Transfer failed';
    row.speedText.textContent = err.message || 'Try again';
    row.fill.classList.add('error');
  } finally {
    activeDownloads.delete(key);
    transferControllers.delete(key);
    updateSaveAllButton();
  }
}

function pumpDownloadQueue() {
  if (processingQueue) return;
  processingQueue = true;
  (async () => {
    try {
      while (downloadQueue.length) {
        const file = downloadQueue.shift();
        await autoDownload(file);
      }
    } finally {
      processingQueue = false;
      updateSaveAllButton();
    }
  })();
}

function downloadKey(file) {
  return file.transferId ? `download:${file.transferId}` : fileKey(file);
}

function queueDownload(file) {
  const key = downloadKey(file);
  const serverState = file.transferId ? serverTransferStates.get(file.transferId) : null;

  // Always create the one canonical row from /api/files. The transfer ID is
  // already attached to this file record, so an earlier /api/transfers SSE
  // snapshot cannot create a competing row.
  if (!transferRows.has(key) && !cancelledTransferKeys.has(key)) {
    const row = createTransferRow(file.relativePath, 'download', key, {
      largeBrowserDownload: Number(file.sizeBytes) > 1024 * 1024 * 1024
    });
    row.transferId = file.transferId || '';
    row.sourceFile = file;
    row.serverAuthoritative = !!file.transferId;
    if (serverState === 'InProgress') row.state.textContent = 'Downloads in phone browser';
  }

  if (!file.manualRetry && ['Completed', 'InProgress', 'Paused'].includes(serverState)) {
    const row = transferRows.get(key);
    if (row && serverState === 'Paused') {
      row.state.textContent = 'Interrupted by refresh';
      row.speedText.textContent = 'Select Receive again to restart';
      row.refreshRecovery = true;
      showRetryButton(row, file);
    } else if (row && serverState === 'Completed') {
      row.fill.style.width = '100%';
      row.fill.classList.add('done');
      row.state.textContent = navigationEntry?.type === 'reload' ? 'Completed before refresh' : 'Completed';
      row.speedText.textContent = navigationEntry?.type === 'reload' ? 'Receive again to download a fresh copy' : 'Transfer complete';
      if (navigationEntry?.type === 'reload') showRetryButton(row, file);
    }
    return;
  }

  if ((knownFileKeys.has(key) && !file.manualRetry) || activeDownloads.has(key) || cancelledTransferKeys.has(key)) return;
  if (file.manualRetry) {
    knownFileKeys.delete(key);
    const existing = transferRows.get(key);
    if (existing?.row?.isConnected) existing.row.remove();
    transferRows.delete(key);
  }
  knownFileKeys.add(key);
  downloadQueue.push(file);
  pumpDownloadQueue();
  updateSaveAllButton();
}

async function refreshFiles() {
  try {
    const res = await fetch('/api/files', {
      headers: authHeaders(),
      cache: 'no-store'
    });
    if (!res.ok) return;
    const files = await res.json();
    for (const file of files) {
      queueDownload(file);
    }
    if (!files.length && !transferList.children.length) emptyState.style.display = 'block';
  } catch {}
}



// ---------- Send phone -> PC ----------

filePicker.addEventListener('change', () => {
  const files = Array.from(filePicker.files || []);
  filePicker.value = '';
  for (const file of files) queueUpload(file);
});

function xhrUploadStream(url, headers, body, onBytesSent, transferKey) {
  let xhrRef;
  const promise = new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhrRef = xhr;
    xhr.open('POST', url);
    xhr.timeout = 0;
    for (const [key, value] of Object.entries(headers)) xhr.setRequestHeader(key, value);
    xhr.upload.onprogress = e => { if (e.lengthComputable) onBytesSent(e.loaded); };
    xhr.onload = () => resolve({ status: xhr.status, text: xhr.responseText });
    xhr.onerror = () => reject(new Error('network error during file transfer'));
    xhr.onabort = () => reject(new DOMException('File transfer was cancelled', 'AbortError'));
    xhr.send(body);
  });
  promise.xhr = xhrRef;
  transferControllers.set(transferKey, { abort: () => xhrRef.abort() });
  return promise;
}

async function getUploadStatus(uploadId) {
  const res = await fetch(`/api/upload/status/${encodeURIComponent(uploadId)}`, { headers: authHeaders(), cache: 'no-store' });
  if (res.status === 404) return null;
  if (!res.ok) throw new Error(`status failed (${res.status})`);
  return res.json();
}

async function initUpload(file) {
  const res = await fetch('/api/upload/init', {
    method: 'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify({ fileName: file.name, fileSize: file.size }),
  });
  if (!res.ok) throw new Error(`init failed (${res.status})`);
  return res.json();
}

async function uploadFile(file, row) {
  const transferKey = `upload:${file.name}|${file.size}|${file.lastModified}`;
  const resumeKey = `dd-upload:${token}:${file.name}:${file.size}:${file.lastModified}`;
  let uploadId = '';

  try {
    row.state.textContent = 'Preparing…';
    // Resume state is intentionally session-only. A cancelled/completed file
    // never gets resurrected in a later DirectDrop session.
    uploadId = sessionStorage.getItem(resumeKey) || '';
    if (!uploadId) {
      const init = await initUpload(file);
      uploadId = init.uploadId;
      row.transferId = uploadId;
      row.resumeKey = resumeKey;
      sessionStorage.setItem(resumeKey, uploadId);
    } else {
      row.transferId = uploadId;
      row.resumeKey = resumeKey;
    }

    addCancelButton(transferKey, row, () => cancelTransfer(transferKey));

    // A refresh can leave an old upload id in sessionStorage after the server
    // has already completed/cancelled that upload. In that case start a fresh
    // upload instead of leaving the file stuck in a failed "status 404" stage.
    const existingStatus = await getUploadStatus(uploadId);
    if (!existingStatus) {
      sessionStorage.removeItem(resumeKey);
      const init = await initUpload(file);
      uploadId = init.uploadId;
      row.transferId = uploadId;
      row.resumeKey = resumeKey;
      sessionStorage.setItem(resumeKey, uploadId);
    }

    const MAX_STREAM_ATTEMPTS = 8;
    for (let attempt = 0; attempt < MAX_STREAM_ATTEMPTS; attempt++) {
      const status = await getUploadStatus(uploadId);
      if (!status) throw new Error('Upload session expired — please select the file again.');
      const offset = Math.min(file.size, status.offset || 0);
      row.sizeText.textContent = `${formatBytes(offset)} / ${formatBytes(file.size)}`;
      if (offset >= file.size || status.complete) break;

      row.state.textContent = '';
      let lastBytes = offset, lastTime = performance.now(), lastUiUpdate = 0;
      const result = await xhrUploadStream(
        `/api/upload/stream/${encodeURIComponent(uploadId)}`,
        authHeaders({ 'X-Upload-Offset': String(offset), 'Content-Type': 'application/octet-stream' }),
        file.slice(offset),
        loaded => {
          const now = performance.now();
          const elapsed = Math.max(0.001, (now - lastTime) / 1000);
          const transferred = offset + loaded;
          if (now - lastUiUpdate >= 100) {
            const pct = file.size ? transferred / file.size * 100 : 0;
            row.fill.style.width = `${pct}%`;
            row.sizeText.textContent = `${formatBytes(transferred)} / ${formatBytes(file.size)} (${pct.toFixed(0)}%)`;
            row.speedText.textContent = formatSpeed((transferred - lastBytes) / elapsed);
            lastBytes = transferred;
            lastTime = now;
            lastUiUpdate = now;
          }
        },
        transferKey
      );

      if (result.status >= 200 && result.status < 300) {
        const after = await getUploadStatus(uploadId);
        if (after.offset >= file.size) break;
      }
      await new Promise(r => setTimeout(r, Math.min(300 * (attempt + 1), 1500)));
    }

    const finalStatus = await getUploadStatus(uploadId);
    if (finalStatus.offset < file.size) throw new Error('transfer could not finish automatically');

    row.state.textContent = 'Finishing…';
    const completeRes = await fetch(`/api/upload/complete/${encodeURIComponent(uploadId)}`, { method: 'POST', headers: authHeaders() });
    if (!completeRes.ok) throw new Error(`complete failed (${completeRes.status})`);

    sessionStorage.removeItem(resumeKey);
    transferControllers.delete(transferKey);
    row.cancelArea.innerHTML = '';
    row.fill.style.width = '100%';
    row.fill.classList.add('done');
    row.state.textContent = 'Sent to PC';
    row.speedText.textContent = 'Transfer complete';
  } catch (err) {
    transferControllers.delete(transferKey);
    if (err?.name === 'AbortError' || row.state.textContent === 'Cancelled') return;
    row.fill.classList.add('error');
    row.state.textContent = 'Transfer interrupted';
    row.speedText.textContent = err.message || 'Try again';
  } finally {
    updateSaveAllButton();
  }
}

function queueUpload(file) {
  const transferKey = `upload:${file.name}|${file.size}|${file.lastModified}`;
  if (transferRows.has(transferKey)) return;
  const row = createTransferRow(file.name, 'upload', transferKey);
  row.sourceFile = file;
  uploadFile(file, row);
}

// ---------- Save all ----------

downloadAllButton.addEventListener('click', async () => {
  const entries = [...readyFiles.entries()];
  if (!entries.length) return;

  downloadAllButton.disabled = true;
  downloadAllButton.textContent = 'Preparing…';
  try {
    const files = entries.map(([, data]) => new File([data.blob], data.name, { type: data.blob.type || mimeFor(data.name) }));
    if (navigator.share && navigator.canShare && navigator.canShare({ files })) {
      await navigator.share({ files, title: `DirectDrop · ${files.length} files` });
    } else {
      for (const [, data] of entries) {
        const url = URL.createObjectURL(data.blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = data.name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
        await new Promise(r => setTimeout(r, 150));
      }
    }
    for (const [key] of entries) readyFiles.delete(key);
    for (const [, row] of transferRows) {
      if (row.direction === 'download' && row.state.textContent === 'Ready to save') {
        row.state.textContent = 'Saved';
        row.saveArea.innerHTML = '';
      }
    }
  } catch {}
  updateSaveAllButton();
});

cancelAllButton.addEventListener('click', cancelAllTransfers);

let transferEvents = null;

function connectTransferEvents() {
  if (!window.EventSource || !token) return;
  try {
    transferEvents?.close();
    transferEvents = new EventSource(`/api/events?token=${encodeURIComponent(token)}`);
    transferEvents.addEventListener('transfers', event => {
      try { applyServerTransferSnapshot(JSON.parse(event.data)); } catch {}
    });
    transferEvents.onerror = () => {
      // Browser will retry the SSE connection automatically.
    };
  } catch {}
}


// Never allow an accidental page reload/navigation while a transfer is active.
// Browsers control the final dialog, but this prevents the page from silently
// resetting transfer state. Once every transfer is complete, normal reloads work.
function hasActiveTransfer() {
  if (downloadQueue.length || activeDownloads.size || processingQueue) return true;
  for (const row of transferRows.values()) {
    const state = (row.state?.textContent || '').toLowerCase();
    if (state.includes('preparing') || state.includes('waiting') || state.includes('transferring') ||
        state.includes('uploading') || state.includes('finishing') || state.includes('starting')) return true;
  }
  return false;
}

window.addEventListener('beforeunload', event => {
  if (!hasActiveTransfer()) return;
  event.preventDefault();
  event.returnValue = '';
});

// Also intercept the common reload shortcuts while a transfer is running.
// ---------- Boot ----------

(async function boot() {
  const ok = await refreshSession();
  if (!ok) return;
  try {
    const snapshot = await fetchTransferSnapshot();
    applyServerTransferSnapshot(snapshot);
  } catch {}
  await refreshFiles();
  connectTransferEvents();
  setInterval(refreshSession, 5000);
  setInterval(refreshFiles, 2500);
  updateSaveAllButton();
})();
