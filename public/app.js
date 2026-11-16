'use strict';

const CSRF = document.querySelector('meta[name="twinsync-csrf"]')?.content || '';
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const viewEl = $('#view');
const modalRoot = $('#modal-root');
const toastRoot = $('#toast-root');

const VIEW_META = {
  overview: { kicker: '工作台', title: '概览' },
  files: { kicker: '文件与同步', title: '文件' },
  devices: { kicker: '连接与信任', title: '设备' },
  history: { kicker: '活动记录', title: '动态' },
  settings: { kicker: '系统设置', title: '设置' },
};

const MODE_LABEL = {
  'two-way': '双向同步',
  'send-only': '仅发送',
  'receive-only': '仅接收',
};

let appState = null;
let activeView = normalizeView(location.hash.replace(/^#/, '')) || 'overview';
let lastSignature = '';
let eventSource = null;
let refreshPromise = null;
let refreshQueued = false;
let modalSequence = 0;
const modalCloseHandlers = new Map();
let themeMedia = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
let fileRequest = 0;
let fileLoadPromise = null;
let fileLoadQueued = false;
let filesPaintNode = null;
let filesPaintHtml = '';
let searchDebounce = null;
let historyLoadToken = 0;
let fileMapResizeTimer = 0;
let lastToastKey = '';
let lastToastAt = 0;
let panelClosed = false;
const panelIntervals = new Set();
const panelRequests = new Set();

const fileState = {
  folderId: null,
  directory: '',
  view: 'folders',
  query: '',
  status: 'all',
  offset: 0,
  limit: 50,
  result: null,
  loading: false,
  error: null,
};

const historyState = {
  items: null,
  filter: 'all',
  loading: false,
  error: null,
};

const browserState = { path: null };
let pairingCodeVisible = false;

function localAddresses() {
  const seen = new Set();
  return (appState?.device.addresses || []).filter(item => {
    if (!item.address || item.address.startsWith('127.') || item.address.startsWith('169.254.') || seen.has(item.address)) return false;
    seen.add(item.address);
    return true;
  }).sort((a, b) => Number(Boolean(b.recommended)) - Number(Boolean(a.recommended)));
}

function connectionDetails() {
  const addresses = localAddresses();
  const addressRow = item => `<div class="address-line"><span>${esc(item.name || '网络')}</span><code>${esc(item.address)}:${esc(appState.device.port)}</code><button class="copy-mini" type="button" data-action="copy" data-copy="${esc(item.address)}" data-copy-label="地址" aria-label="复制地址">${icon('copy')}</button></div>`;
  const fingerprint = appState?.security?.fingerprint;
  return `<details class="private-info"><summary>查看连接信息<span>默认隐藏</span></summary><div class="private-info-body">
    ${addresses.length ? addressRow(addresses[0]) : '<p>暂未发现可用的局域网地址。</p>'}
    ${addresses.length > 1 ? `<details class="other-networks"><summary>其他网卡 · ${addresses.length - 1}</summary>${addresses.slice(1).map(addressRow).join('')}</details>` : ''}
    ${fingerprint ? `<div class="fingerprint-row"><span>设备指纹 · SHA-256</span><code>${esc(fingerprint.match(/.{1,4}/g).join(' '))}</code><button class="btn btn-sm" type="button" data-action="copy" data-copy="${esc(fingerprint)}" data-copy-label="设备指纹">复制指纹</button></div>` : ''}
    ${appState.settings.allowRemoteUi && appState.security?.remoteAccessToken && addresses.length ? `<button class="btn btn-sm" type="button" data-action="copy" data-copy="${esc(`https://${addresses[0].address}:${appState.device.port}/?access=${appState.security.remoteAccessToken}`)}" data-copy-label="加密管理链接">复制远程管理链接</button>` : ''}
  </div></details>`;
}

function pendingFolderCards() {
  const offers = appState?.pendingFolders || [];
  if (!offers.length) return '';
  return `<section class="panel pending-folders" data-folder-offers><div class="panel-head"><div><h2>待确认的共享文件夹</h2><p>选择本机目录后，才会加入下方的文件夹列表。</p></div></div>${offers.map(offer => `<div class="pending-folder"><div><strong>${esc(offer.name)}</strong><p>来自 ${esc(peerById(offer.peerId)?.name || '已配对设备')}</p></div><button class="btn btn-sm" data-action="accept-folder-offer" data-id="${esc(offer.id)}" data-peer="${esc(offer.peerId)}">${folderById(offer.id) ? '确认共享设备' : '选择本机目录'}</button></div>`).join('')}</section>`;
}

/* ------------------------------------------------------------------ helpers */

function normalizeView(value) {
  return Object.hasOwn(VIEW_META, value) ? value : null;
}

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (character) => {
    switch (character) {
      case '&': return '&amp;';
      case '<': return '&lt;';
      case '>': return '&gt;';
      case '"': return '&quot;';
      case "'": return '&#39;';
      default: return character;
    }
  });
}

function icon(name, extra = '') {
  return `<svg class="ic${extra ? ` ${extra}` : ''}" aria-hidden="true"><use href="#i-${name}"></use></svg>`;
}

function fmtBytes(bytes) {
  const value = Number(bytes) || 0;
  if (value < 1024) return `${Math.round(value)} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let amount = value / 1024;
  let unit = 0;
  while (amount >= 1024 && unit < units.length - 1) {
    amount /= 1024;
    unit += 1;
  }
  return `${amount >= 100 ? amount.toFixed(0) : amount >= 10 ? amount.toFixed(1) : amount.toFixed(2)} ${units[unit]}`;
}

function fmtSpeed(bytesPerSecond) {
  return `${fmtBytes(bytesPerSecond)}/s`;
}

function fmtNumber(value) {
  return new Intl.NumberFormat('zh-CN').format(Number(value) || 0);
}

function fmtRelative(iso) {
  if (!iso) return '从未';
  const timestamp = new Date(iso).getTime();
  if (!Number.isFinite(timestamp)) return '从未';
  const seconds = Math.round((Date.now() - timestamp) / 1000);
  if (seconds < 10) return '刚刚';
  if (seconds < 60) return `${seconds} 秒前`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} 分钟前`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} 小时前`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days} 天前`;
  return new Date(iso).toLocaleDateString('zh-CN', { month: 'short', day: 'numeric' });
}

function fmtDateTime(iso) {
  if (!iso) return '—';
  const date = new Date(iso);
  if (!Number.isFinite(date.getTime())) return '—';
  return date.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  });
}

function fmtClock(iso) {
  if (!iso) return '';
  return new Date(iso).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false });
}

function dateGroupLabel(iso) {
  const date = new Date(iso);
  const now = new Date();
  const start = (value) => new Date(value.getFullYear(), value.getMonth(), value.getDate()).getTime();
  const diff = Math.round((start(now) - start(date)) / 86_400_000);
  if (diff === 0) return '今天';
  if (diff === 1) return '昨天';
  if (diff > 1 && diff < 7) return `${diff} 天前`;
  return date.toLocaleDateString('zh-CN', {
    year: date.getFullYear() === now.getFullYear() ? undefined : 'numeric',
    month: 'long',
    day: 'numeric',
  });
}

function baseName(value) {
  const text = String(value || '').replace(/\\/g, '/');
  return text.split('/').filter(Boolean).pop() || text;
}

function dirName(value) {
  const text = String(value || '').replace(/\\/g, '/');
  const index = text.lastIndexOf('/');
  return index <= 0 ? '' : text.slice(0, index);
}

function modeLabel(mode) {
  return MODE_LABEL[mode] || '双向同步';
}

function reasonLabel(reason) {
  return {
    overwrite: '被覆盖前',
    'remote-overwrite': '接收新版本前',
    'peer-overwrite': '对方覆盖前',
    'remote-delete': '接收删除前',
    'peer-delete': '对方删除前',
    'conflict-restore': '恢复冲突前',
    'before-restore': '恢复历史前',
  }[reason] || '历史版本';
}

function onlinePeers(state = appState) {
  return (state?.peers || []).filter((peer) => peer.online && !peer.paused);
}

function primaryPeer(state = appState) {
  return onlinePeers(state)[0] || (state?.peers || [])[0] || null;
}

function lastSyncTime(state = appState) {
  const candidates = [state?.sync?.lastSyncAt, ...(state?.folders || []).map((folder) => folder.lastSyncAt)]
    .filter(Boolean)
    .map((value) => new Date(value).getTime())
    .filter(Number.isFinite);
  if (!candidates.length) return null;
  return new Date(Math.max(...candidates)).toISOString();
}

function currentFolder(state = appState) {
  if (!state?.folders?.length) return null;
  return state.folders.find((folder) => folder.id === fileState.folderId) || state.folders[0];
}

function folderById(id, state = appState) {
  return (state?.folders || []).find((folder) => folder.id === id) || null;
}

function peerById(id, state = appState) {
  return (state?.peers || []).find((peer) => peer.id === id) || null;
}

function folderStatus(folder) {
  if (!folder) return { label: '未配置', className: '' };
  if (!folder.enabled || folder.paused) return { label: '已暂停', className: 'is-paused' };
  if (folder.state === 'scanning') return { label: '扫描中', className: 'is-syncing' };
  if (folder.state === 'syncing') return { label: '同步中', className: 'is-syncing' };
  if (folder.conflictCount > 0) return { label: `${folder.conflictCount} 个冲突`, className: 'is-conflict' };
  if (folder.state === 'error' || folder.lastError) return { label: '需要处理', className: 'is-error' };
  if (!folder.peerIds?.length) return { label: '未共享', className: '' };
  if (folder.pendingCount > 0) return { label: '待同步', className: '' };
  if (folder.state === 'synced') return { label: '已核对', className: 'is-ok' };
  return { label: folder.lastScanAt ? '等待核对' : '待扫描', className: '' };
}

function activityMeta(type) {
  switch (type) {
    case 'sync': return { icon: 'sync', className: '' };
    case 'file': return { icon: 'file', className: 'is-info' };
    case 'device': return { icon: 'devices', className: '' };
    case 'folder': return { icon: 'folder', className: '' };
    case 'version': return { icon: 'history', className: 'is-info' };
    case 'conflict': return { icon: 'alert', className: 'is-warn' };
    case 'error': return { icon: 'alert', className: 'is-error' };
    default: return { icon: 'sparkle', className: 'is-info' };
  }
}

function activityIcon(item) {
  const meta = activityMeta(item.level === 'error' ? 'error' : item.type);
  return `<div class="feed-icon ${meta.className}">${icon(meta.icon)}</div>`;
}

function statusTone(state = appState) {
  if (!state) return '';
  if (state.sync?.state === 'syncing' || state.sync?.state === 'scanning') return 'is-busy';
  if (state.sync?.state === 'error') return 'is-error';
  if (!state.peers?.length) return '';
  if (onlinePeers(state).length) return 'is-online';
  return 'is-error';
}

function relativeSpan(iso) {
  return `<span data-rel="${esc(iso || '')}">${esc(fmtRelative(iso))}</span>`;
}

/* -------------------------------------------------------------------- state */

async function api(path, { method = 'GET', body = undefined } = {}) {
  if (panelClosed) throw new DOMException('Panel is closed', 'AbortError');
  const controller = new AbortController();
  panelRequests.add(controller);
  try {
  const headers = { accept: 'application/json' };
  if (body !== undefined) headers['content-type'] = 'application/json';
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers['x-twinsync-csrf'] = CSRF;
  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: 'no-store',
    signal: controller.signal,
  });
  const text = await response.text();
  let payload;
  try {
    payload = text ? JSON.parse(text) : {};
  } catch {
    payload = { error: text.slice(0, 220) || `请求失败 (${response.status})` };
  }
  if (!response.ok) throw new Error(payload.error || `请求失败 (${response.status})`);
  return payload;
  } finally {
    panelRequests.delete(controller);
  }
}

async function refreshState({ quiet = false } = {}) {
  if (panelClosed) return;
  if (refreshPromise) {
    refreshQueued = true;
    return refreshPromise;
  }
  refreshPromise = (async () => {
    try {
      const data = await api('/api/state');
      const { ok, ...next } = data;
      applyState(next);
    } catch (error) {
      if (!quiet) toast(error.message || '无法读取运行状态', 'error');
      if (!appState) renderBootError(error);
    } finally {
      refreshPromise = null;
      if (refreshQueued) {
        refreshQueued = false;
        setTimeout(() => refreshState({ quiet: true }), 60);
      }
    }
  })();
  return refreshPromise;
}

function applyState(next, { force = false } = {}) {
  if (panelClosed) return;
  const previous = appState;
  appState = next;
  applyTheme();
  updateShell();
  const signature = viewSignature();
  const typing = document.activeElement?.matches?.('input, textarea, select');
  const preserveEditor = typing && (activeView === 'files' || activeView === 'settings');
  if ((force || signature !== lastSignature) && !preserveEditor) {
    lastSignature = signature;
    renderView();
  } else {
    updateLive();
  }
  if (
    activeView === 'history' &&
    previous &&
    next.activity?.[0]?.id &&
    previous.activity?.[0]?.id !== next.activity[0].id
  ) {
    historyState.items = null;
    loadHistory();
  }
  if (previous && previous.sync?.state === 'syncing' && next.sync?.state !== 'syncing') {
    if (activeView === 'files') refreshFilesSoon();
    if (activeView === 'overview') updateRelTimes();
  }
  if (previous && activeView === 'files') {
    const before = previous.folders.find(folder => folder.id === fileState.folderId);
    const after = next.folders.find(folder => folder.id === fileState.folderId);
    if (before && after && (before.fileCount !== after.fileCount || before.totalBytes !== after.totalBytes || before.lastScanAt !== after.lastScanAt)) refreshFilesSoon();
  }
}

function viewSignature() {
  if (!appState) return '';
  if (activeView === 'files') return JSON.stringify([
    activeView, appState.settings?.theme,
    appState.folders.map(folder => [folder.id, folder.name, folder.path, folder.mode, folder.enabled, folder.paused, folder.peerIds]),
    appState.peers.map(peer => [peer.id, peer.name]),
    (appState.pendingFolders || []).map(item => [item.id, item.peerId, item.name]),
    appState.conflicts.map(item => item.id),
    fileState.folderId, fileState.directory, fileState.view, fileState.status,
  ]);
  const base = [
    activeView, appState.settings?.syncPaused,
    appState.folders.map((folder) => `${folder.id}:${folder.name}:${folder.fileCount}:${folder.totalBytes}:${folder.conflictCount}:${folder.state}:${folder.paused}:${folder.path}:${folder.mode}:${folder.peerIds?.join(',')}:${folder.lastSyncAt}:${folder.lastError}`).join('|'),
    appState.peers.map((peer) => `${peer.id}:${peer.online}:${peer.paused}:${peer.name}:${peer.host}`).join('|'),
    appState.stats?.totalFiles,
    appState.stats?.totalBytes,
    appState.stats?.conflicts,
    appState.sync?.state,
    appState.sync?.finishedAt,
    appState.sync?.lastResult,
    appState.sync?.message,
    appState.startupError,
    appState.discoveryError,
    appState.activity.slice(0, 4).map((item) => item.id).join('|'),
    appState.conflicts.map((item) => item.id).join('|'),
    (appState.pendingFolders || []).map((item) => `${item.id}:${item.peerId}:${item.name}`).join('|'),
    appState.nearby.map((item) => `${item.id}:${item.name}:${item.address}:${item.port}`).join('|'),
    appState.pairing?.code,
    appState.settings?.theme,
  ];
  if (activeView === 'files') base.push(fileState.folderId, fileState.directory, fileState.view, fileState.status);
  if (activeView === 'history') base.push(historyState.filter);
  return base.join('~');
}

function applyTheme() {
  const choice = appState?.settings?.theme || localStorage.getItem('twinsync-theme') || 'auto';
  const actual = choice === 'auto' ? (themeMedia?.matches ? 'dark' : 'light') : choice;
  document.documentElement.dataset.theme = actual;
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', actual === 'dark' ? '#0a0a0a' : '#f8faff');
  const themeButton = $('#theme-btn');
  if (themeButton) {
    themeButton.innerHTML = icon(actual === 'dark' ? 'sun' : 'moon');
    themeButton.setAttribute('aria-label', actual === 'dark' ? '切换到浅色主题' : '切换到深色主题');
  }
}

async function saveTheme(choice) {
  if (!appState) return;
  appState.settings.theme = choice;
  localStorage.setItem('twinsync-theme', choice);
  applyTheme();
  try {
    const result = await api('/api/settings', { method: 'PATCH', body: { theme: choice } });
    if (result.warning) toast(result.warning, 'warn');
    await refreshState({ quiet: true });
  } catch (error) {
    toast(error.message, 'error');
  }
}

function connectEvents() {
  if (eventSource) eventSource.close();
  eventSource = null;
  if (document.hidden || panelClosed) return;
  try {
    eventSource = new EventSource('/api/events');
    eventSource.addEventListener('state', (event) => {
      try {
        const { ok, ...next } = JSON.parse(event.data);
        applyState(next);
      } catch {
        // Ignore malformed frames and let the next one recover.
      }
    });
    eventSource.onerror = () => {
      // EventSource reconnects automatically; polling below is the safety net.
    };
  } catch {
    // Polling still works if EventSource is unavailable.
  }
}

/* -------------------------------------------------------------------- shell */

function updateShell() {
  if (!appState) return;
  const meta = VIEW_META[activeView] || VIEW_META.overview;
  $('#page-kicker').textContent = meta.kicker;
  $('#page-title').textContent = meta.title;
  document.title = `${meta.title} · TwinSync`;
  $$('.nav-item, .mobile-nav button').forEach((button) => {
    button.classList.toggle('is-active', button.dataset.view === activeView);
    if (button.dataset.view === activeView) button.setAttribute('aria-current', 'page');
    else button.removeAttribute('aria-current');
  });
  const conflicts = appState.conflicts?.length || 0;
  const filesBadge = $('#nav-files-badge');
  if (filesBadge) {
    filesBadge.hidden = conflicts === 0;
    filesBadge.textContent = conflicts > 9 ? '9+' : String(conflicts);
  }
  const devicesBadge = $('#nav-devices-badge');
  if (devicesBadge) {
    const count = appState.nearby?.length || 0;
    devicesBadge.hidden = count === 0;
    devicesBadge.textContent = String(count);
  }

  const online = onlinePeers().length;
  const paused = (appState.peers || []).filter((peer) => peer.paused).length;
  const deviceBox = $('#sidebar-device');
  if (deviceBox) {
    const isOnline = online > 0;
    deviceBox.classList.toggle('is-online', isOnline);
    deviceBox.classList.toggle('is-error', !isOnline && (appState.peers || []).length > 0);
    deviceBox.innerHTML = `
      <div class="status-orb"></div>
      <div>
        <strong>${esc(appState.device.name || '未命名设备')}</strong>
        <span>${isOnline ? `${online} 台设备在线` : (appState.peers || []).length ? (paused ? '设备已暂停' : '等待另一台设备') : '尚未连接另一台设备'}</span>
      </div>`;
  }

  const pill = $('#connection-pill');
  if (pill) {
    let text = '尚未配对';
    let tone = '';
    if (appState.settings?.syncPaused && (appState.peers || []).length) {
      text = '同步已暂停';
      tone = '';
    } else if (appState.sync?.state === 'syncing') {
      text = '正在同步';
      tone = 'is-busy';
    } else if (appState.sync?.state === 'scanning') {
      text = '正在扫描';
      tone = 'is-busy';
    } else if (appState.sync?.state === 'error') {
      text = '同步异常';
      tone = 'is-error';
    } else if (appState.sync?.state === 'offline') {
      text = '设备离线';
      tone = 'is-error';
    } else if (online > 0) {
      text = `已连接 · ${online} 台`;
      tone = 'is-online';
    } else if ((appState.peers || []).length) {
      text = paused ? '同步已暂停' : '等待设备上线';
      tone = 'is-error';
    }
    pill.className = `connection-pill ${tone}`;
    pill.innerHTML = `<span class="status-dot"></span><span>${esc(text)}</span>`;
  }

  const syncBtn = $('#sync-btn');
  if (syncBtn) {
    const busy = appState.sync?.state === 'syncing' || appState.sync?.state === 'scanning';
    const canSync = (appState.peers || []).length > 0 && (appState.folders || []).length > 0;
    const paused = Boolean(appState.settings?.syncPaused);
    syncBtn.disabled = busy || !canSync;
    syncBtn.classList.toggle('is-loading', busy);
    syncBtn.innerHTML = `${icon(paused ? 'play' : 'sync')}<span>${paused ? '开始同步' : busy ? '同步中' : '立即同步'}</span>`;
  }

  const rail = $('.signal-rail');
  if (rail) {
    const busy = appState.sync?.state === 'syncing' || appState.sync?.state === 'scanning';
    rail.classList.toggle('is-busy', busy);
    rail.classList.toggle('is-conflict', !busy && conflicts > 0);
  }
  const railDevice = $('#rail-device');
  if (railDevice) railDevice.textContent = `LOCAL / ${appState.device.name} · ${appState.device.port}`;
}

function syncProgress(sync) {
  return sync.transferActive ? { done: Number(sync.fileBytesDone) || 0, total: Number(sync.fileBytesTotal) || 0 } : { done: Number(sync.done) || 0, total: Number(sync.total) || 0 };
}

function transferLabel(sync) {
  if (!sync.transferActive) return '';
  const done = Number(sync.fileBytesDone) || 0;
  const total = Number(sync.fileBytesTotal) || 0;
  return `${sync.transferDirection === 'upload' ? '发送' : '接收'} ${fmtBytes(done)} / ${fmtBytes(total)}${total ? ` · ${Math.min(100, Math.floor(done / total * 100))}%` : ''}`;
}

function updateLive() {
  if (!appState) return;
  $$('[data-folder-count]').forEach(element => {
    const folder = folderById(element.dataset.folderCount);
    if (folder) element.textContent = fmtNumber(folder.fileCount);
  });
  const sync = appState.sync || {};
  const conflictCount = appState.conflicts?.length || 0;
  const liveMessage = sync.state === 'syncing' || sync.state === 'scanning'
    ? (sync.message || '正在同步')
    : conflictCount > 0
      ? `${conflictCount} 个冲突待处理`
      : (sync.message || '等待同步');
  $$('[data-live-message]').forEach((element) => {
    element.textContent = liveMessage;
  });
  $$('[data-live-file]').forEach((element) => {
    element.textContent = sync.currentFile || (sync.state === 'syncing' ? '正在检查文件…' : '');
  });
  $$('[data-live-speed]').forEach((element) => {
    element.textContent = sync.speed ? fmtSpeed(sync.speed) : '';
  });
  $$('[data-live-transfer]').forEach((element) => { element.textContent = transferLabel(sync); });
  $$('[data-live-progress]').forEach((element) => {
    const { done, total } = syncProgress(sync);
    if (total > 0) {
      element.style.width = `${Math.max(0, Math.min(100, (done / total) * 100))}%`;
      element.parentElement?.classList.remove('is-indeterminate');
    } else {
      element.style.width = '';
      element.parentElement?.classList.add('is-indeterminate');
    }
  });
  $$('[data-rel]').forEach((element) => {
    element.textContent = fmtRelative(element.dataset.rel);
  });
  updatePairCountdown();
}

/* ------------------------------------------------------------------- router */

function renderViewWithTransition() {
  const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  if (!document.startViewTransition || reduced) {
    window.scrollTo(0, 0);
    renderView();
    return;
  }
  try {
    document.startViewTransition(() => {
      window.scrollTo(0, 0);
      renderView();
    });
  } catch {
    window.scrollTo(0, 0);
    renderView();
  }
}
function navigate(next, { replace = false, force = false } = {}) {
  if (panelClosed) return;
  const normalized = normalizeView(next) || 'overview';
  if (!force && normalized === activeView && location.hash === `#${normalized}`) return;
  activeView = normalized;
  if (replace) history.replaceState(null, '', `#${normalized}`);
  else history.pushState(null, '', `#${normalized}`);
  lastSignature = '';
  if (normalized === 'files') {
    fileState.folderId = fileState.folderId || appState?.folders?.[0]?.id || null;
    fileState.result = null;
    loadFiles();
  }
  if (normalized === 'history') loadHistory();
  renderViewWithTransition();
  closeSidebar();
}

window.addEventListener('hashchange', () => {
  if (panelClosed) return;
  const next = normalizeView(location.hash.replace(/^#/, '')) || 'overview';
  if (next !== activeView) {
    activeView = next;
    lastSignature = '';
    if (next === 'history') loadHistory();
    if (next === 'files') {
      fileState.folderId = fileState.folderId || appState?.folders?.[0]?.id || null;
      fileState.result = null;
      loadFiles();
    }
    updateShell();
    renderViewWithTransition();
  }
});

/* ------------------------------------------------------------------- render */

function renderView() {
  if (!appState || panelClosed) return;
  updateShell();
  viewEl.classList.add('is-rendering');
  if (activeView === 'overview') viewEl.innerHTML = renderOverview();
  else if (activeView === 'files') viewEl.innerHTML = renderFilesView();
  else if (activeView === 'devices') viewEl.innerHTML = renderDevicesView();
  else if (activeView === 'history') viewEl.innerHTML = renderHistoryView();
  else if (activeView === 'settings') viewEl.innerHTML = renderSettingsView();
  requestAnimationFrame(() => {
    viewEl.classList.remove('is-rendering');
    updateLive();
    if (activeView === 'files' && appState?.folders?.length && !fileState.result && !fileState.loading) {
      loadFiles();
    }
    if (activeView === 'history' && historyState.items === null && !historyState.loading) {
      loadHistory();
    }
  });
}

/* ----------------------------------------------------------------- overview */

function rowMenu(label, actions) {
  return `<details class="row-menu"><summary class="icon-btn" aria-label="${esc(label)}" title="${esc(label)}">${icon('more')}</summary><div class="row-menu-popover">${actions}</div></details>`;
}

function renderOverview() {
  const peers = appState.peers || [];
  const online = onlinePeers();
  const folders = appState.folders || [];
  const conflicts = appState.conflicts || [];
  const sync = appState.sync || {};
  const busy = sync.state === 'syncing' || sync.state === 'scanning';
  const paused = Boolean(appState.settings?.syncPaused);
  const lastSync = lastSyncTime();
  let title = '等待同步核对';
  let description = lastSync ? `上次同步 ${fmtRelative(lastSync)}` : '设备已连接，随时可以同步。';
  if (!peers.length) {
    title = '连接你的另一台电脑';
    description = '在同一局域网中配对，开始同步文件。';
  } else if (paused) {
    title = '同步已暂停';
    description = '恢复后继续同步文件变化。';
  } else if (busy) {
    title = sync.state === 'scanning' ? '正在检查文件' : '正在同步文件';
    description = sync.message || '正在比对两台电脑的文件。';
  } else if (sync.state === 'error') {
    title = '同步遇到问题';
    description = sync.message || '请检查设备连接后重试。';
  } else if (sync.state === 'partial') {
    title = '部分同步失败';
    description = sync.error || sync.message || '部分项目尚未完成，请重试。';
  } else if (conflicts.length) {
    title = `${conflicts.length} 个文件需要确认`;
    description = '两个版本都已保留。';
  } else if (sync.lastResult === 'skipped') {
    title = sync.summary?.succeeded > 0 ? '本次同步未全部完成' : '本次未执行同步';
    description = sync.message || '请检查共享范围和暂停状态。';
  } else if (!online.length) {
    title = '等待另一台电脑上线';
    description = '重新连接后自动继续同步。';
  } else if (!folders.length) {
    title = '选择要同步的文件夹';
    description = '添加本机目录，与另一台电脑保持一致。';
  } else if (!lastSync) {
    title = '准备好同步了';
  } else if (folders.some(folder => !folder.paused && folder.pendingCount > 0)) {
    title = '有文件等待同步';
  } else if (sync.lastResult === 'success') {
    title = sync.scope?.folderId || sync.scope?.peerId ? '所选内容同步完成' : '本次同步完成';
  }
  const action = !peers.length
    ? '<button class="btn btn-primary" type="button" data-action="pair-device">连接设备</button>'
    : !folders.length
      ? '<button class="btn btn-primary" type="button" data-action="add-folder">添加文件夹</button>'
      : paused
        ? '<button class="btn btn-primary" type="button" data-action="resume-sync">恢复同步</button>'
        : conflicts.length && !busy
          ? '<button class="btn btn-primary" type="button" data-action="show-conflicts">查看冲突</button>'
          : `<button class="btn btn-primary" type="button" data-action="sync-now" ${busy ? 'disabled' : ''}>${busy ? '同步中…' : '立即同步'}</button>`;
  const secondary = !peers.length
    ? '<button class="btn btn-ghost" type="button" data-view="devices">查看配对码</button>'
    : folders.length && !paused
      ? '<button class="btn btn-ghost" type="button" data-action="pause-sync">暂停同步</button>'
      : '<button class="btn btn-ghost" type="button" data-view="files">查看文件</button>';
  const progress = busy ? `<div class="hero-syncbar"><div class="hero-syncbar-top"><span data-live-file>${esc(sync.currentFile || '正在检查文件…')}</span><span data-live-speed>${sync.speed ? fmtSpeed(sync.speed) : ''}</span></div><div class="hero-transfer" data-live-transfer>${esc(transferLabel(sync))}</div><div class="hero-progress ${Number(sync.total) > 0 ? '' : 'is-indeterminate'}"><i data-live-progress></i></div></div>` : '';
  const endpoint = peers.length ? `<div class="connection-route"><span>${esc(appState.device.name)}</span><span class="route-line" aria-label="连接至"></span><button type="button" data-view="devices">${esc(primaryPeer()?.name || '另一台电脑')}<span class="status-dot ${online.length ? 'is-online' : ''}"></span></button></div>` : '';
  return `
    <section class="hero" aria-labelledby="hero-title">
      <div class="hero-copy"><div class="hero-heading"><h1 id="hero-title" class="hero-product" lang="en">TwinSync</h1><span class="hero-label">双机同步</span></div><h2 id="sync-title" class="hero-status">${esc(title)}</h2><p>${esc(description)}</p><div class="hero-actions">${action}${secondary}${['failed', 'partial'].includes(sync.lastResult) && sync.errors?.length ? '<button class="text-link" type="button" data-action="show-sync-errors">查看失败项目</button>' : ''}</div>${progress}</div>
    </section>
    <section class="panel folders-panel" aria-labelledby="folders-title">
      <div class="panel-head"><div class="panel-title"><h2 id="folders-title">同步文件夹</h2><span class="section-meta">${fmtNumber(appState.stats?.totalFiles)} 个文件<span class="meta-separator">·</span>${fmtBytes(appState.stats?.totalBytes)}</span></div><button class="btn btn-outline" type="button" data-action="add-folder">添加文件夹</button></div>
      ${endpoint}
      ${folders.length ? `<div class="folder-list">${folders.map(folderRow).join('')}</div>` : '<div class="empty-state"><h3>还没有同步文件夹</h3><p>添加一个本机目录，开始同步。</p></div>'}
    </section>
    <section class="recent-section" aria-labelledby="recent-title"><div class="section-head"><h2 id="recent-title">最近动态</h2><button class="text-link" type="button" data-view="history">查看全部</button></div><div class="feed">${activityFeed(appState.activity || [], 4)}</div></section>`;
}

function folderRow(folder) {
  const status = folderStatus(folder);
  const peers = (folder.peerIds || []).map((id) => peerById(id)).filter(Boolean);
  const shared = peers.length ? peers.map((peer) => peer.name).join('、') : '仅本机';
  const actions = `<button type="button" data-action="open-folder" data-id="${esc(folder.id)}">查看文件</button><button type="button" data-action="sync-folder" data-id="${esc(folder.id)}">立即同步</button><button type="button" data-action="edit-folder" data-id="${esc(folder.id)}">文件夹设置</button><button type="button" data-action="folder-versions" data-id="${esc(folder.id)}">版本历史</button><button type="button" data-action="reveal-folder" data-id="${esc(folder.id)}">打开本机目录</button>`;
  return `<div class="folder-row"><div class="folder-meta"><button class="folder-name" type="button" data-action="open-folder" data-id="${esc(folder.id)}">${esc(folder.name)}</button><span title="${esc(folder.path)}">${esc(folder.path)}</span></div><div class="folder-info"><strong>${fmtNumber(folder.fileCount)} 个文件<span class="meta-separator">·</span>${fmtBytes(folder.totalBytes)}</strong><span title="${esc(shared)}">${esc(shared)}${peers.length ? ` · ${esc(modeLabel(folder.mode))}` : ''}</span></div><span class="status-tag ${status.className}">${esc(status.label)}</span>${rowMenu(`${folder.name}的操作`, actions)}</div>`;
}

function activityFeed(items, limit = 8) {
  const list = (items || []).slice(0, limit);
  if (!list.length) return `<div class="empty-feed">还没有动态</div>`;
  return list
    .map((item) => {
      const folder = item.folderId ? folderById(item.folderId) : null;
      const detail = [item.detail, folder && !String(item.detail || '').includes(folder.name) ? folder.name : '']
        .filter(Boolean)
        .join(' · ');
      return `<div class="feed-item">
        ${activityIcon(item)}
        <div class="feed-copy"><strong>${esc(item.title)}</strong><span>${esc(detail || '—')}</span></div>
        <div class="feed-time">${esc(fmtRelative(item.at))}</div>
      </div>`;
    })
    .join('');
}

/* ------------------------------------------------------------------ devices */

function renderDevicesView() {
  const device = appState.device;
  const peers = appState.peers || [];
  const nearby = appState.nearby || [];
  const pair = appState.pairing || {};
  const code = pairingCodeVisible ? pair.code || '------' : '••••••';
  const foldersShared = (folderId) => (appState.folders || []).filter((folder) => folder.peerIds.includes(folderId)).length;

  return `
    <div class="page-intro">
      <div>
        <h2>设备</h2>
      </div>
      <div class="intro-actions">
        <button class="btn btn-primary" type="button" data-action="pair-device">添加设备</button>
      </div>
    </div>

    <div class="device-grid">
      <section class="identity-card">
        <div class="identity-head">
          <div class="device-avatar">${icon('laptop')}</div>
          <div>
            <h3><button class="device-name-button" type="button" data-action="edit-device-name">${esc(device.name || '设置本机名称')}${icon('edit')}</button></h3>
            <p>本机 · Windows</p>
          </div>
        </div>
        <div class="identity-body">
          ${connectionDetails()}
        </div>
        <div class="identity-foot">
          <span>设备标识 · ${esc(device.id.slice(0, 8))}…${esc(device.id.slice(-4))}</span>
          <button class="copy-mini" type="button" data-action="copy" data-copy="${esc(device.id)}" data-copy-label="设备标识">${icon('copy')}复制完整标识</button>
        </div>
      </section>

      <section class="pairing-card">
        <div class="panel-head">
          <div><h2>配对码</h2><p>在另一台电脑的 TwinSync 中输入这组数字</p></div>
          <button class="icon-btn" type="button" title="刷新配对码" data-action="refresh-pair">${icon('refresh')}</button>
        </div>
        <div class="pair-code">${code.split('').map((digit) => `<i>${esc(digit)}</i>`).join('')}</div>
        <div class="pair-help">
          <span>10 分钟有效，用后更换</span>
          <strong id="pair-countdown">${pair.ttlSeconds ? `${String(Math.floor(pair.ttlSeconds / 60)).padStart(2, '0')}:${String(pair.ttlSeconds % 60).padStart(2, '0')}` : '—'}</strong>
        </div>
        <div class="pair-actions">
          <button class="btn btn-soft btn-sm" type="button" data-action="toggle-pair-code">${pairingCodeVisible ? '隐藏配对码' : '显示配对码'}</button>
          ${pairingCodeVisible ? `<button class="btn btn-sm" type="button" data-action="copy" data-copy="${esc(code)}" data-copy-label="配对码">复制</button>` : ''}
        </div>
        <p class="pair-note">在另一台电脑输入此配对码。</p>
      </section>
    </div>

    ${pendingFolderCards()}
    ${appState.discoveryError ? `<p class="version-note">${esc(appState.discoveryError)}</p>` : ''}
    <div class="section-head">
      <div><h3>已配对的设备</h3><p>${peers.length ? `${peers.length} 台设备可以访问同步文件夹` : '还没有其他设备'}</p></div>
    </div>
    ${peers.length ? `<div class="peer-grid">${peers.map((peer) => peerCard(peer, foldersShared(peer.id))).join('')}</div>` : `<div class="panel"><div class="empty-state"><h3>还没有配对的设备</h3><p>点击上方「添加设备」开始连接。</p></div></div>`}

    ${nearby.length ? `<div class="section-head"><div><h3>附近的设备</h3><p>在同一局域网内自动发现</p></div></div><div class="panel"><div class="nearby-list" style="margin-top:0">${nearby.map((item) => `<div class="nearby-item"><div class="nearby-icon">${icon('wifi')}</div><div class="nearby-copy"><strong>${esc(item.name)}</strong><span>${esc(item.address)}:${esc(item.port)} · ${esc(item.hostname || 'TwinSync')}</span></div><button class="btn btn-soft btn-sm" type="button" data-action="pair-nearby" data-id="${esc(item.id)}">使用配对码连接</button></div>`).join('')}</div></div>` : ''}
  `;
}

function peerCard(peer, sharedFolderCount) {
  const latency = peer.online ? `${peer.rttMs ?? '—'} ms` : '离线';
  return `<article class="peer-card ${peer.online ? '' : 'is-offline'} ${peer.paused ? 'is-offline' : ''}">
    <div class="peer-card-head">
      <div class="peer-avatar">${icon(peer.hostname?.toLowerCase().includes('laptop') ? 'laptop' : 'monitor')}</div>
      <div class="peer-title">
        <strong>${esc(peer.name)}</strong>
        <span>${peer.fingerprint ? '已核验的加密连接' : '已配对设备'}</span>
      </div>
      <span class="status-tag ${peer.paused ? 'is-paused' : peer.online ? 'is-ok' : 'is-error'}">${peer.paused ? '已暂停' : peer.online ? '在线' : '离线'}</span>
    </div>
    <div class="peer-details">
      <div class="peer-detail"><span>连接延迟</span><strong>${esc(latency)}</strong></div>
      <div class="peer-detail"><span>共享文件夹</span><strong>${fmtNumber(sharedFolderCount)} 个</strong></div>
      <div class="peer-detail"><span>最近在线</span><strong>${peer.online ? '现在' : esc(fmtRelative(peer.lastSeenAt))}</strong></div>
      <div class="peer-detail"><span>同步协调</span><strong>${peer.initiator ? '本机发起' : '对方发起'}</strong></div>
    </div>
    <button class="peer-scope" type="button" data-action="peer-folders" data-id="${esc(peer.id)}">
      <span>同步文件夹</span>
      <strong>${fmtNumber(sharedFolderCount)} 个 ${icon('chevron-right')}</strong>
    </button>
    <div class="peer-actions">
      <button class="btn btn-soft" type="button" data-action="peer-sync" data-id="${esc(peer.id)}">立即同步</button>
      <button class="btn" type="button" data-action="test-peer" data-id="${esc(peer.id)}">${icon('wifi')}测试</button>
      <button class="icon-btn" type="button" title="${peer.paused ? '恢复同步' : '暂停同步'}" data-action="toggle-peer-pause" data-id="${esc(peer.id)}">${icon(peer.paused ? 'play' : 'pause')}</button>
      <button class="icon-btn" type="button" title="设备详情" data-action="open-peer" data-id="${esc(peer.id)}">${icon('more')}</button>
    </div>
  </article>`;
}

/* -------------------------------------------------------------------- files */

function renderFilesView() {
  if (!appState.folders?.length) {
    return `<div class="panel"><div class="empty-state"><div class="empty-glyph">${icon('folder-open')}</div><h3>选择要同步的文件夹</h3><p>由你选择本机目录和同步方向。</p><button class="btn btn-primary" type="button" data-action="add-folder">添加文件夹</button></div></div>${pendingFolderCards()}`;
  }
  if (!fileState.folderId || !folderById(fileState.folderId)) { fileState.folderId = appState.folders[0].id; fileState.directory = ''; }
  const folder = folderById(fileState.folderId) || appState.folders[0];
  const folderConflicts = (appState.conflicts || []).filter((item) => item.folderId === folder.id);
  const sharedNames = (folder.peerIds || []).map(id => peerById(id)?.name || '未命名设备');
  const tabs = appState.folders.map((item) => `<button class="tab ${item.id === folder.id ? 'is-active' : ''}" type="button" data-action="select-folder" data-id="${esc(item.id)}">${esc(item.name)}<span class="count" data-folder-count="${esc(item.id)}">${fmtNumber(item.fileCount)}</span></button>`).join('');
  return `
    <div class="page-intro files-intro">
      <div>
        <h2>${esc(folder.name)}</h2>
        <p class="folder-location">${esc(modeLabel(folder.mode))} · ${sharedNames.length ? `共享给 ${esc(sharedNames.join('、'))}` : '尚未共享给设备'}</p>
      </div>
      <div class="intro-actions">
        <button class="btn" type="button" data-action="edit-folder" data-id="${esc(folder.id)}">文件夹设置</button>
        <button class="btn btn-primary" type="button" data-action="sync-now">同步</button>
        ${rowMenu('文件夹操作', `<button type="button" data-action="folder-versions" data-id="${esc(folder.id)}">版本历史</button><button type="button" data-action="add-folder">添加文件夹</button><button type="button" data-action="reveal-folder" data-id="${esc(folder.id)}">打开本机目录</button>`)}
      </div>
    </div>
    ${pendingFolderCards()}
    <div class="folder-tabs" role="group" aria-label="本机同步文件夹"><span class="folder-tabs-label">本机文件夹</span>${tabs}</div>
    <div class="toolbar files-toolbar">
      <label class="search-box">${icon('search')}<input id="file-search" type="search" placeholder="搜索文件名或路径…" value="${esc(fileState.query)}" autocomplete="off"></label>
      <div class="filter-chips">
        ${fileFilterChip('all', '全部')}
        ${fileFilterChip('synced', '已同步')}
        ${fileFilterChip('pending', '待同步')}
        ${fileFilterChip('conflict', '冲突')}
      </div>
      <button class="icon-btn" type="button" title="刷新" data-action="refresh-files">${icon('refresh')}</button>
    </div>
    ${folderConflicts.length ? conflictPanel(folderConflicts, folder) : ''}
    <div id="files-results">${filesResultsHtml()}</div>`;
}

function fileFilterChip(value, label) {
  return `<button class="filter-chip ${fileState.status === value ? 'is-active' : ''}" type="button" data-action="file-filter" data-filter="${value}">${label}</button>`;
}

function conflictPanel(conflicts, folder) {
  return `<div class="conflict-panel">
    <div class="conflict-panel-head">${icon('alert')}<h3>文件冲突</h3><span>${conflicts.length} 个文件需要确认 · 两个版本都已保留</span></div>
    <div class="conflict-list">${conflicts.map((conflict) => {
      const canRestore = Boolean(conflict.conflictPath);
      return `<div class="conflict-card">
        <div class="conflict-card-copy"><strong>${esc(baseName(conflict.path))}</strong><span>${esc(conflict.message || '两台设备同时修改了这个文件')}${conflict.conflictPath ? ` · 副本：${esc(baseName(conflict.conflictPath))}` : ''}</span></div>
        <div class="conflict-actions">
          ${canRestore ? `<button class="btn btn-sm btn-soft" type="button" data-action="resolve-conflict" data-folder="${esc(folder.id)}" data-id="${esc(conflict.id)}" data-choice="restore-copy">恢复副本</button>` : ''}
          <button class="btn btn-sm" type="button" data-action="resolve-conflict" data-folder="${esc(folder.id)}" data-id="${esc(conflict.id)}" data-choice="keep-current">保留当前</button>
          <button class="icon-btn" type="button" title="在文件夹中显示" data-action="file-reveal" data-folder="${esc(folder.id)}" data-path="${esc(conflict.path)}">${icon('external')}</button>
        </div>
      </div>`;
    }).join('')}</div>
  </div>`;
}

function filesResultsHtml() {
  if (!fileState.result && fileState.loading) return loadingBlock('正在读取目录…');
  if (!fileState.result && fileState.error) return `<div class="panel"><div class="empty-state"><div class="empty-glyph">${icon('alert')}</div><h3>无法读取目录</h3><p>${esc(fileState.error)}</p><button class="btn" type="button" data-action="refresh-files">重试</button></div></div>`;
  if (!fileState.result) return loadingBlock('正在读取目录…');
  return renderFileBrowser(fileState.result);
}

function loadingBlock(message) {
  return `<div class="table-shell"><div class="empty-state"><div class="empty-glyph">${icon('sync')}</div><h3>${esc(message)}</h3><p>第一次扫描大目录可能需要一点时间。</p></div></div>`;
}

function browserEntryAction(entry) {
  return entry.type === 'directory' ? 'browse-directory' : entry.type === 'other' ? 'show-folder-view' : 'inspect-file';
}

function browserBreadcrumbs() {
  const folder = currentFolder();
  const parts = fileState.directory.split('/').filter(Boolean);
  return `<nav class="directory-crumbs" aria-label="文件夹路径"><button type="button" data-action="browse-directory" data-path="" ${parts.length ? '' : 'aria-current="location"'}>${esc(folder?.name || '同步目录')}</button>${parts.map((part, index) => `<span aria-hidden="true">/</span><button type="button" data-action="browse-directory" data-path="${esc(parts.slice(0, index + 1).join('/'))}" ${index === parts.length - 1 ? 'aria-current="location"' : ''}>${esc(part)}</button>`).join('')}</nav>`;
}

function renderFileBrowser(result) {
  const entries = result.children || [];
  const filtered = Boolean(fileState.query || fileState.status !== 'all');
  const page = Math.floor((result.offset || 0) / (result.limit || 50)) + 1;
  const pages = Math.max(1, Math.ceil((result.total || 0) / (result.limit || 50)));
  const modes = `<div class="segmented file-view-switch" aria-label="文件显示方式">${[['folders', '文件夹'], ['space', '空间分布']].map(([value, label]) => `<button type="button" data-action="file-view" data-mode="${value}" class="${fileState.view === value ? 'is-active' : ''}" aria-pressed="${fileState.view === value}">${label}</button>`).join('')}</div>`;
  const empty = `<div class="empty-state"><h3>${filtered ? '当前范围没有匹配的文件' : '当前目录还没有文件'}</h3>${filtered ? '<button class="btn" type="button" data-action="clear-file-filter">清除筛选</button>' : ''}</div>`;
  return `<section class="panel directory-browser"><div class="directory-head">${browserBreadcrumbs()}${modes}</div><div class="directory-summary"><div>${fileState.directory ? `<button class="text-link" type="button" data-action="browse-directory" data-path="${esc(dirName(fileState.directory))}">${icon('arrow-left')}上一级</button>` : ''}<span>${fmtNumber(result.directoryCount || 0)} 个文件夹</span><span>${fmtNumber(result.fileCount || 0)} 个文件</span></div><strong>${fmtBytes(result.totalBytes || 0)}</strong></div>${!entries.length ? empty : fileState.view === 'space' ? renderSpaceMap(result) : `<div class="directory-grid">${entries.map(entry => {
    const directory = entry.type === 'directory';
    const dot = entry.name.lastIndexOf('.');
    const extension = directory ? '' : dot > 0 ? entry.name.slice(dot + 1).slice(0, 4).toUpperCase() : 'FILE';
    const percent = result.totalBytes > 0 ? Math.min(100, entry.size / result.totalBytes * 100) : 0;
    const state = entry.conflictCount ? `${entry.conflictCount} 个冲突` : entry.pendingCount ? `${entry.pendingCount} 个待同步` : '';
    return `<button class="directory-entry ${directory ? 'is-directory' : 'is-file'}" type="button" data-action="${browserEntryAction(entry)}" data-path="${esc(entry.path)}" title="${esc(entry.path)}"><span class="entry-icon">${directory ? icon('folder') : `<span class="entry-file-icon">${esc(extension || 'FILE')}</span>`}</span><strong>${esc(entry.name)}</strong><span class="entry-meta">${directory ? `${fmtNumber(entry.fileCount)} 个文件 · ` : ''}${fmtBytes(entry.size)}</span>${state ? `<span class="entry-state ${entry.conflictCount ? 'has-conflict' : ''}">${esc(state)}</span>` : ''}<span class="entry-sizebar" aria-hidden="true"><i style="width:${percent.toFixed(2)}%"></i></span></button>`;
  }).join('')}</div>`}${entries.length && fileState.view === 'folders' && pages > 1 ? `<div class="table-foot"><span>${fmtNumber((result.offset || 0) + 1)}–${fmtNumber((result.offset || 0) + entries.length)} / ${fmtNumber(result.total)} 项</span><div class="pager"><button class="icon-btn" type="button" title="上一页" data-action="page-prev" ${page <= 1 ? 'disabled' : ''}>${icon('chevron-left')}</button><span>${page} / ${pages}</span><button class="icon-btn" type="button" title="下一页" data-action="page-next" ${page >= pages ? 'disabled' : ''}>${icon('chevron-right')}</button></div></div>` : ''}</section>`;
}

function renderSpaceMap(result) {
  const byBytes = result.totalBytes > 0;
  const entries = (result.mapItems || []).map(entry => ({ ...entry, weight: byBytes ? entry.size : entry.fileCount })).filter(entry => entry.weight > 0).sort((a, b) => b.weight - a.weight);
  const width = Math.max(260, viewEl.clientWidth - (innerWidth <= 760 ? 72 : 120));
  const height = innerWidth <= 760 ? 440 : 400;
  const rectangles = [];
  function split(items, x, y, w, h) {
    if (!items.length) return;
    if (items.length === 1) { rectangles.push({ entry: items[0], x, y, w, h }); return; }
    const total = items.reduce((sum, item) => sum + item.weight, 0);
    let cut = 1, left = items[0].weight;
    while (cut < items.length - 1 && Math.abs(total / 2 - left - items[cut].weight) < Math.abs(total / 2 - left)) left += items[cut++].weight;
    const fraction = left / total;
    if (w >= h) { split(items.slice(0, cut), x, y, w * fraction, h); split(items.slice(cut), x + w * fraction, y, w * (1 - fraction), h); }
    else { split(items.slice(0, cut), x, y, w, h * fraction); split(items.slice(cut), x, y + h * fraction, w, h * (1 - fraction)); }
  }
  split(entries, 0, 0, width, height);
  const total = entries.reduce((sum, entry) => sum + entry.weight, 0);
  return `<div class="space-map" role="group" aria-label="当前目录空间分布，点击色块查看" style="height:${height}px">${rectangles.map(({ entry, x, y, w, h }, index) => {
    const percent = total ? entry.weight / total * 100 : 0;
    const detail = `${fmtBytes(entry.size)} · ${fmtNumber(entry.fileCount)} 个文件 · ${percent.toFixed(1)}%`;
    return `<button type="button" class="space-tile ${w < 110 || h < 65 ? 'is-small' : ''}" data-action="${browserEntryAction(entry)}" data-path="${esc(entry.path || '')}" aria-label="${esc(entry.name + '，' + detail)}" title="${esc(entry.name + '\n' + detail)}" style="left:${x / width * 100}%;top:${y / height * 100}%;width:${w / width * 100}%;height:${h / height * 100}%;--tile-hue:${[211, 187, 233, 201, 172][index % 5]}"><strong>${esc(entry.name)}</strong><span>${fmtBytes(entry.size)}</span><span class="space-tile-count">${fmtNumber(entry.fileCount)} 个文件</span></button>`;
  }).join('')}</div><div class="space-caption"><span>${byBytes ? '面积代表文件总大小，点击文件夹进入下一层' : '文件大小均为 0 B，面积按文件数量分配'}</span>${byBytes && result.zeroItems ? `<button type="button" class="text-link" data-action="show-folder-view">另有 ${fmtNumber(result.zeroItems)} 个 0 B 项目</button>` : ''}</div>`;
}

async function openDirectory(directory) {
  fileState.directory = directory || '';
  fileState.offset = 0;
  fileState.result = null;
  await loadFiles();
  const browser = document.querySelector('.directory-browser');
  browser?.scrollIntoView({ block: 'start' });
  browser?.querySelector('[aria-current="location"]')?.focus({ preventScroll: true });
}

function fileContext() {
  const folder = currentFolder();
  if (!folder) return null;
  return { folderId: folder.id, query: fileState.query, status: fileState.status,
    limit: String(fileState.limit), offset: String(fileState.offset), directory: fileState.directory };
}

async function loadFiles({ quiet = false } = {}) {
  if (fileLoadPromise) {
    fileLoadQueued = true;
    return fileLoadPromise;
  }
  const context = fileContext();
  if (!context) {
    fileState.result = null;
    if (activeView === 'files') paintFilesResults();
    return;
  }
  fileState.folderId = context.folderId;
  const contextKey = JSON.stringify(context);
  const requestId = ++fileRequest;
  fileState.loading = true;
  fileState.error = null;
  if (!quiet && activeView === 'files') paintFilesResults();
  fileLoadPromise = (async () => {
    try {
      const { folderId, ...query } = context;
      const data = await api(`/api/folders/${encodeURIComponent(folderId)}/browse?${new URLSearchParams(query)}`);
      if (requestId !== fileRequest || JSON.stringify(fileContext()) !== contextKey) return;
      fileState.offset = Math.max(0, Number(data.offset) || 0);
      fileState.directory = data.directory ?? context.directory;
      fileState.result = { ...data, folderId };
    } catch (error) {
      if (requestId !== fileRequest || JSON.stringify(fileContext()) !== contextKey) return;
      fileState.error = error.message;
      if (!quiet) toast(error.message, 'error');
    } finally {
      fileState.loading = false;
      fileLoadPromise = null;
      if (activeView === 'files') paintFilesResults();
      if (fileLoadQueued) {
        fileLoadQueued = false;
        if (activeView === 'files') refreshFilesSoon();
      }
    }
  })();
  return fileLoadPromise;
}

function paintFilesResults() {
  const container = $('#files-results');
  if (!container) return;
  const html = filesResultsHtml();
  if (filesPaintNode !== container || filesPaintHtml !== html) {
    container.innerHTML = html;
    filesPaintNode = container;
    filesPaintHtml = html;
  }
  const count = fileState.result?.total;
  if (typeof count === 'number') {
    const panel = $('.conflict-panel');
    if (panel) panel.dataset.total = String(count);
  }
}

function refreshFilesSoon() {
  clearTimeout(refreshFilesSoon.timer);
  refreshFilesSoon.timer = setTimeout(() => {
    if (activeView === 'files') loadFiles({ quiet: true });
  }, 500);
}

/* ------------------------------------------------------------------ history */

function renderHistoryView() {
  const items = historyState.items || appState.activity || [];
  const syncCount = items.filter((item) => item.type === 'sync').length;
  const conflictCount = items.filter((item) => item.type === 'conflict').length;
  return `
    <div class="page-intro">
      <div><h2>动态</h2></div>
      <div class="intro-actions"><a class="btn" href="/api/diagnostics/export" download="TwinSync-diagnostics.json">导出诊断</a><button class="btn" type="button" data-action="refresh-history">刷新</button></div>
    </div>
    <div class="history-summary">
      <div class="summary-chip"><span>记录总数</span><strong data-history-total>${fmtNumber(items.length)}</strong></div>
      <div class="summary-chip"><span>同步记录</span><strong data-history-sync>${fmtNumber(syncCount)}</strong></div>
      <div class="summary-chip"><span>冲突处理</span><strong data-history-conflict>${fmtNumber(conflictCount)}</strong></div>
    </div>
    <div class="toolbar">
      <div class="filter-chips">${['all', 'sync', 'file', 'conflict', 'device'].map((type) => `<button class="filter-chip ${historyState.filter === type ? 'is-active' : ''}" type="button" data-action="history-filter" data-filter="${type}">${type === 'all' ? '全部' : type === 'sync' ? '同步' : type === 'file' ? '文件' : type === 'conflict' ? '冲突' : '设备'}</button>`).join('')}</div>
    </div>
    <div class="panel" style="padding-top:0"><div id="history-timeline">${historyTimelineHtml()}</div></div>`;
}

function activityStatsHtml(item) {
  const data = item.data;
  if (!data) return '';
  const parts = [];
  if (item.type === 'sync') {
    const seconds = Math.max(0, Number(data.durationMs) || 0) / 1000;
    const duration = seconds < 1 ? '<1 秒' : seconds < 60 ? `${seconds.toFixed(1)} 秒` : `${Math.floor(Math.round(seconds) / 60)} 分 ${Math.round(seconds) % 60} 秒`;
    parts.push(`同步 ${fmtNumber(data.files || 0)} 个文件`, fmtBytes(data.bytes || 0), `耗时 ${duration}`);
    parts.push(`发送 ${fmtNumber(data.uploaded || 0)} · 接收 ${fmtNumber(data.downloaded || 0)}`);
    if (data.deleted) parts.push(`删除 ${fmtNumber(data.deleted)} 个文件`);
    if (data.failed) parts.push(`未完成 ${fmtNumber(data.failed)} 项`);
    parts.push(`核对 ${fmtNumber(data.checked || 0)} 项`);
    if (!data.files && !data.deleted) parts.push('没有需要传输的变更');
  } else if (item.type === 'file') {
    if (data.kind === 'directory') parts.push(`${fmtNumber(data.files || 0)} 个文件`);
    parts.push(fmtBytes(data.bytes || 0));
    parts.push(data.direction === 'incoming' ? '从对方同步' : '本机变更');
  }
  return parts.length ? `<div class="activity-stats">${parts.map(part => `<span>${esc(part)}</span>`).join('')}</div>` : '';
}

function historyTimelineHtml() {
  if (historyState.loading && !historyState.items) return `<div class="empty-state"><div class="empty-glyph">${icon('history')}</div><h3>正在读取动态…</h3></div>`;
  if (historyState.error && !historyState.items) return `<div class="empty-state"><div class="empty-glyph">${icon('alert')}</div><h3>读取失败</h3><p>${esc(historyState.error)}</p><button class="btn" type="button" data-action="refresh-history">重试</button></div>`;
  const items = (historyState.items || appState.activity || []).filter((item) => historyState.filter === 'all' || item.type === historyState.filter || historyState.filter === 'file' && item.type === 'folder');
  if (!items.length) return `<div class="empty-state"><div class="empty-glyph">${icon('history')}</div><h3>还没有符合条件的动态</h3><p>当文件发生变化，或者设备连接、断开时，这里会出现记录。</p></div>`;
  const groups = new Map();
  for (const item of items) {
    const label = dateGroupLabel(item.at);
    if (!groups.has(label)) groups.set(label, []);
    groups.get(label).push(item);
  }
  return `<div class="timeline">${[...groups.entries()].map(([label, list]) => `<div class="timeline-group"><div class="timeline-date">${esc(label)}</div>${list.map((item) => {
    const meta = activityMeta(item.level === 'error' ? 'error' : item.type);
    const folder = item.folderId ? folderById(item.folderId) : null;
    const detail = [...new Set([item.detail, folder ? folder.name : ''].filter(Boolean))].join(' · ');
    return `<div class="timeline-item"><div class="feed-icon ${meta.className}">${icon(meta.icon)}</div><div class="timeline-copy"><strong>${esc(item.title)}</strong>${detail ? `<p>${esc(detail)}</p>` : ''}${activityStatsHtml(item)}</div><time>${esc(fmtClock(item.at))}</time></div>`;
  }).join('')}</div>`).join('')}</div>`;
}

async function loadHistory() {
  const token = ++historyLoadToken;
  historyState.loading = true;
  historyState.error = null;
  if (activeView === 'history') {
    const container = $('#history-timeline');
    if (container) container.innerHTML = historyTimelineHtml();
  }
  try {
    const data = await api('/api/activity?limit=300');
    if (token !== historyLoadToken) return;
    historyState.items = Array.isArray(data.activity) ? data.activity : Array.isArray(data.items) ? data.items : [];
  } catch (error) {
    if (token !== historyLoadToken) return;
    historyState.error = error.message;
    historyState.items = appState?.activity || [];
  } finally {
    historyState.loading = false;
    if (activeView === 'history') paintHistory();
  }
}

function paintHistory() {
  const container = $('#history-timeline');
  if (container) container.innerHTML = historyTimelineHtml();
  const items = historyState.items || [];
  const total = $('[data-history-total]');
  const syncCount = $('[data-history-sync]');
  const conflictCount = $('[data-history-conflict]');
  if (total) total.textContent = fmtNumber(items.length);
  if (syncCount) syncCount.textContent = fmtNumber(items.filter((item) => item.type === 'sync').length);
  if (conflictCount) conflictCount.textContent = fmtNumber(items.filter((item) => item.type === 'conflict').length);
}

/* ----------------------------------------------------------------- settings */

function renderSettingsView() {
  const settings = appState.settings;
  const device = appState.device;
  return `
    <div class="page-intro">
      <div><h2>设置</h2></div>
    </div>
    <div class="settings-layout">
      <div class="settings-stack">
        <section class="settings-card">
          <h3>后台运行</h3>
          ${settingToggle('syncPaused', '暂停同步', '停止监听和自动同步。', settings.syncPaused)}
          <div class="setting-row">
            <div class="setting-copy"><strong>管理面板</strong><p>关闭浏览器页面，同步服务不会退出。</p></div>
            <div class="setting-control"><button class="btn btn-sm" type="button" data-action="close-panel">关闭面板</button></div>
          </div>
        </section>

        <section class="settings-card">
          <h3>外观</h3>
          <div class="setting-row">
            <div class="setting-copy"><strong>主题</strong></div>
            <div class="setting-control"><div class="segmented">${[['auto', '跟随系统'], ['light', '浅色'], ['dark', '深色']].map(([value, label]) => `<button type="button" class="${settings.theme === value ? 'is-active' : ''}" data-theme-choice="${value}">${label}</button>`).join('')}</div></div>
          </div>
        </section>

        <section class="settings-card">
          <h3>同步与版本</h3>
          ${settingToggle('autoSync', '自动同步', '检测到文件变化后自动与在线设备同步。', settings.autoSync)}
          ${settingSelect('syncIntervalSec', '检查间隔', '定时检查对方设备。', settings.syncIntervalSec, [[10, '10 秒'], [30, '30 秒'], [60, '1 分钟'], [300, '5 分钟'], [900, '15 分钟']])}
          ${settingSelect('conflictPolicy', '冲突处理', '两台设备同时修改同一文件时的处理方式。', settings.conflictPolicy, [['copy', '保留冲突副本'], ['newest', '使用较新版本']])}
          ${settingToggle('versioningEnabled', '保留历史版本', '被对方覆盖或删除前保存旧版本。', settings.versioningEnabled)}
          ${settingNumber('maxVersionsPerFile', '每个文件最多保留', '超出数量后自动清理最早的版本。', settings.maxVersionsPerFile, 1, 200, '个')}
          ${settingNumber('versionRetentionDays', '版本保留天数', '超过天数的历史版本会被清理。', settings.versionRetentionDays, 1, 3650, '天')}
        </section>

        <section class="settings-card">
          <h3>网络</h3>
          ${settingToggle('discoveryEnabled', '局域网自动发现', '查找附近运行 TwinSync 的电脑。', settings.discoveryEnabled)}
          ${settingToggle('allowRemoteUi', '远程管理', appState.security?.fingerprint ? '开启后，通过连接信息中的加密管理链接访问。' : '仅用于可信的开发网络。', settings.allowRemoteUi)}
          <div class="setting-row">
            <div class="setting-copy"><strong>同步端口</strong><p>另一台设备通过这个端口传输文件。</p></div>
            <div class="setting-control"><span class="unit">TCP ${esc(device.port)}</span></div>
          </div>
          <div class="setting-row">
            <div class="setting-copy"><strong>连接信息</strong><p>添加设备时按需查看地址和指纹。</p></div>
            <div class="setting-control"><button class="btn btn-sm" type="button" data-view="devices">前往设备页</button></div>
          </div>
        </section>

        <section class="settings-card">
          <h3>启动与运行</h3>
          ${settingToggle('openAtLogin', '登录 Windows 时自动启动', '在后台启动同步服务，不自动打开浏览器。', settings.openAtLogin)}
          ${appState.startupError ? `<p class="form-note">开机启动设置未生效：${esc(appState.startupError)}</p>` : ''}
          <div class="setting-row">
            <div class="setting-copy"><strong>本机名称</strong><p>显示在另一台电脑上的名字。</p></div>
            <div class="setting-control"><button class="btn btn-sm" type="button" data-action="edit-device-name">${esc(device.name || '设置名称')} ${icon('edit')}</button></div>
          </div>
          <div class="setting-row">
            <div class="setting-copy"><strong>运行时长</strong><p>本次服务已经运行的时间。</p></div>
            <div class="setting-control"><span class="unit">${formatUptime(appState.server?.uptimeSec || 0)}</span></div>
          </div>
        </section>
      </div>

      <aside class="settings-side">
        <section class="info-card">
          <h3>本机信息</h3>
          <div class="info-list">
            <details class="private-info"><summary>查看本机信息<span>默认隐藏</span></summary><div class="private-info-body">
            <div class="info-line"><span>设备标识</span><code>${esc(device.id)}</code></div>
            <div class="info-line"><span>主机名</span><code>${esc(device.hostname)}</code></div>
            <div class="info-line"><span>数据目录</span><code title="${esc(appState.paths?.home || '')}">${esc(appState.paths?.home || '')}</code></div>
            </div></details>
            <div class="info-line"><span>历史版本</span><strong>${fmtNumber(appState.versionsCount || 0)} 个</strong></div>
          </div>
          <button class="btn btn-sm btn-ghost" style="margin-top:10px;padding-left:0" type="button" data-action="open-path" data-path="${esc(appState.paths?.home || '')}">打开数据目录</button>
        </section>


        <section class="info-card danger-zone">
          <h3>退出 TwinSync</h3>
          <p>退出后停止同步。</p>
          <button class="btn btn-danger btn-sm" style="margin-top:12px" type="button" data-action="shutdown">退出应用</button>
        </section>
      </aside>
    </div>`;
}

function settingToggle(key, title, help, checked) {
  return `<div class="setting-row"><div class="setting-copy"><strong>${esc(title)}</strong><p>${esc(help)}</p></div><div class="setting-control"><label class="switch"><input type="checkbox" data-setting="${esc(key)}" ${checked ? 'checked' : ''}><i></i></label></div></div>`;
}

function settingSelect(key, title, help, value, options) {
  return `<div class="setting-row"><div class="setting-copy"><strong>${esc(title)}</strong><p>${esc(help)}</p></div><div class="setting-control"><select data-setting="${esc(key)}" aria-label="${esc(title)}">${options.map(([optionValue, label]) => `<option value="${esc(optionValue)}" ${String(value) === String(optionValue) ? 'selected' : ''}>${esc(label)}</option>`).join('')}</select></div></div>`;
}

function settingNumber(key, title, help, value, min, max, unit) {
  return `<div class="setting-row"><div class="setting-copy"><strong>${esc(title)}</strong><p>${esc(help)}</p></div><div class="setting-control"><input type="number" min="${min}" max="${max}" value="${esc(value)}" data-setting="${esc(key)}"><span class="unit">${esc(unit)}</span></div></div>`;
}

function formatUptime(seconds) {
  const value = Number(seconds) || 0;
  if (value < 60) return `${value} 秒`;
  if (value < 3600) return `${Math.floor(value / 60)} 分钟`;
  if (value < 86400) return `${Math.floor(value / 3600)} 小时 ${Math.floor((value % 3600) / 60)} 分钟`;
  return `${Math.floor(value / 86400)} 天 ${Math.floor((value % 86400) / 3600)} 小时`;
}

