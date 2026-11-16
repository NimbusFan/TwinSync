'use strict';

/* Dialogs, mutations, event handling and boot for TwinSync.
   Loaded after app.js; shares the same global lexical scope in a classic script. */

/* ------------------------------------------------------------------- modals */

function openModal({ title, subtitle = '', body = '', footer = '', wide = false, onClose = null }) {
  const id = `modal-${++modalSequence}`;
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay';
  overlay.dataset.modalId = id;
  overlay.innerHTML = `
    <section class="modal ${wide ? 'is-wide' : ''}" role="dialog" aria-modal="true" aria-labelledby="${id}-title">
      <header class="modal-head">
        <div><h2 id="${id}-title">${esc(title)}</h2>${subtitle ? `<p>${esc(subtitle)}</p>` : ''}</div>
        <button class="icon-btn modal-close" type="button" data-modal-close aria-label="关闭">${icon('x')}</button>
      </header>
      <div class="modal-body">${body}</div>
      ${footer ? `<footer class="modal-foot">${footer}</footer>` : ''}
    </section>`;
  modalRoot.appendChild(overlay);
  const editor = overlay.querySelector('form[data-form]');
  if (editor && ['folder-form', 'peer-form', 'peer-folders', 'device-name'].includes(editor.dataset.form)) {
    editor.dataset.initialValues = JSON.stringify([...editor.elements].filter(el => el.name || el.type === 'checkbox').map(el => [el.name || el.className, el.type === 'checkbox' ? el.checked : el.value]));
    const note = document.createElement('span');
    note.className = 'unsaved-note'; note.dataset.unsavedNote = ''; note.hidden = true; note.textContent = '未保存'; note.setAttribute('role', 'status');
    const title = overlay.querySelector('.modal-head h2');
    title?.classList.add('editor-title');
    title?.append(note);
  }
  if (onClose) modalCloseHandlers.set(id, onClose);
  document.body.classList.add('modal-open');
  requestAnimationFrame(() => {
    overlay.querySelector('input:not([type="hidden"]):not([disabled]), select, textarea, button')?.focus();
  });
  return overlay;
}

function updateEditorState(input) {
  const form = input.closest('form[data-initial-values]');
  if (!form) return;
  const values = JSON.stringify([...form.elements].filter(el => el.name || el.type === 'checkbox').map(el => [el.name || el.className, el.type === 'checkbox' ? el.checked : el.value]));
  const note = form.closest('.modal-overlay')?.querySelector('[data-unsaved-note]');
  if (note) note.hidden = values === form.dataset.initialValues;
  const change = form.querySelector('[data-path-change]');
  if (change) change.hidden = sameDirectory(form.elements.path.value, form.dataset.originalPath);
}

function sameDirectory(a, b) { return String(a || '').replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase() === String(b || '').replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase(); }

function openSyncErrorsModal() {
  const rows = (appState.sync?.errors || []).map(item => `<div class="sync-error-item"><strong>${esc(folderById(item.folderId)?.name || '文件夹')} · ${esc(peerById(item.peerId)?.name || '设备')}</strong>${item.path ? `<code>${esc(item.path)}</code>` : ''}<p>${esc(item.message)}</p></div>`).join('');
  openModal({ title: '本次未完成的项目', body: rows || '<p>没有可显示的失败项目。</p>', footer: '<button class="btn" type="button" data-modal-close>关闭</button><button class="btn btn-primary" type="button" data-action="retry-sync">重试本次范围</button>' });
}

function editorDirty(form) {
  if (!form?.dataset.initialValues) return false;
  const values = JSON.stringify([...form.elements].filter(el => el.name || el.type === 'checkbox').map(el => [el.name || el.className, el.type === 'checkbox' ? el.checked : el.value]));
  return values !== form.dataset.initialValues;
}

function closeModal(overlay, { force = false } = {}) {
  if (!overlay || !overlay.isConnected || overlay.dataset.closing === '1') return;
  const editor = overlay.querySelector('form[data-initial-values]');
  if (!force && editorDirty(editor)) {
    if (overlay.dataset.confirmingClose === '1') return;
    overlay.dataset.confirmingClose = '1';
    confirmDialog({ title: '放弃未保存的修改？', message: '关闭后，这次修改不会保存。', confirmText: '放弃修改', cancelText: '继续编辑', danger: true }).then((discard) => {
      delete overlay.dataset.confirmingClose;
      if (discard) closeModal(overlay, { force: true });
      else if (overlay.isConnected) editor.querySelector('input, select, textarea')?.focus();
    });
    return;
  }
  const id = overlay.dataset.modalId;
  const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  const finish = () => {
    if (overlay.isConnected) overlay.remove();
    const handler = modalCloseHandlers.get(id);
    if (handler) {
      modalCloseHandlers.delete(id);
      try { handler(); } catch { /* ignore */ }
    }
    if (!modalRoot.querySelector('.modal-overlay')) document.body.classList.remove('modal-open');
  };
  if (reduced) { finish(); return; }
  overlay.dataset.closing = '1';
  overlay.classList.add('is-closing');
  setTimeout(finish, 240);
}

function closeTopModal() {
  const overlays = $$('.modal-overlay', modalRoot);
  if (overlays.length) closeModal(overlays[overlays.length - 1]);
}

function closeAllModals(options = {}) {
  for (const overlay of $$('.modal-overlay', modalRoot)) closeModal(overlay, options);
}

function confirmDialog({ title, message, confirmText = '确认', cancelText = '取消', danger = false, subtitle = '' }) {
  return new Promise((resolve) => {
    let settled = false;
    const overlay = openModal({
      title,
      subtitle,
      body: `<p class="confirm-copy">${esc(message)}</p>`,
      footer: `<button class="btn" type="button" data-modal-close>${esc(cancelText)}</button><button class="btn ${danger ? 'btn-danger' : 'btn-primary'}" type="button" data-confirm-ok>${esc(confirmText)}</button>`,
      onClose: () => {
        if (!settled) {
          settled = true;
          resolve(false);
        }
      },
    });
    overlay.addEventListener('click', (event) => {
      if (event.target.closest('[data-confirm-ok]')) {
        settled = true;
        closeModal(overlay);
        resolve(true);
      }
    });
  });
}

function openPairModal(prefill = {}) {
  const nearby = appState?.nearby || [];
  const body = `
    <form id="pair-form" data-form="pair-device" autocomplete="off">
      <div class="form-grid">
        <div class="form-field is-full">
          <label for="pair-host">对方设备的局域网地址</label>
          <input id="pair-host" name="host" placeholder="例如 192.168.1.23" value="${esc(prefill.host || '')}" required>
          <small>在另一台电脑的「设备」页面可以找到它的局域网地址。</small>
        </div>
        <div class="form-field">
          <label for="pair-port">端口</label>
          <input id="pair-port" name="port" type="number" min="1" max="65535" value="${esc(prefill.port || 55220)}" required>
        </div>
        <div class="form-field">
          <label for="pair-code">6 位配对码</label>
          <input id="pair-code" name="code" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" placeholder="000000" value="" required>
        </div>
        ${appState.security?.fingerprint ? `<div class="form-field is-full"><label for="pair-fingerprint">对方设备指纹</label><input id="pair-fingerprint" name="fingerprint" placeholder="从对方的连接信息中复制完整指纹" required autocomplete="off"><small>通过可信渠道核对，用于验证加密连接的设备身份。</small></div>` : ''}
      </div>
      ${nearby.length ? `<div class="nearby-picker"><h4>附近的设备</h4>${nearby.map((item) => `<button class="nearby-picker-item" type="button" data-action="pair-nearby" data-id="${esc(item.id)}">${icon('wifi')}<span>${esc(item.name)}</span><code>${esc(item.address)}:${esc(item.port)}</code></button>`).join('')}</div>` : ''}
      <div class="pair-form-note"> 在对方的「设备」页面查看配对码，10 分钟内有效。</div>
    </form>`;
  const overlay = openModal({
    title: '连接另一台电脑',
    subtitle: '输入对方设备地址和一次性配对码',
    body,
    footer: `<button class="btn" type="button" data-modal-close>取消</button><button class="btn btn-primary" type="submit" form="pair-form">建立连接</button>`,
  });
  if (prefill.code) {
    const input = $('#pair-code', overlay);
    if (input) input.value = prefill.code;
  }
}

function openFolderModal(folder = null, preset = null) {
  const peers = appState?.peers || [];
  const editing = Boolean(folder);
  const selected = new Set(folder?.peerIds || preset?.peerIds || []);
  const values = folder || preset;
  const peerRows = peers.length
    ? `<div class="share-list">${peers
        .map(
          (peer) => `<div class="share-row" data-switch-status>
            <div class="share-copy">
              <strong>${esc(peer.name)}</strong>
              <small>${peer.online ? '在线' : '离线'} · <span class="share-state" data-switch-value aria-live="polite">${selected.has(peer.id) ? '已共享' : '未共享'}</span></small>
            </div>
            <label class="peer-share-control"><span>与此设备同步</span><span class="switch"><input class="peer-check" type="checkbox" role="switch" aria-label="${esc(`与${peer.name}同步`)}" data-on-text="已共享" data-off-text="未共享" value="${esc(peer.id)}" ${selected.has(peer.id) ? 'checked' : ''}><i></i></span></label>
          </div>`,
        )
        .join('')}</div>`
    : '<div class="share-empty">尚未配对设备，添加后可再选择共享范围</div>';
  const body = `
    <form id="folder-form" data-form="folder-form" data-id="${esc(folder?.id || '')}" data-original-path="${esc(folder?.path || '')}" data-offer="${esc(preset?.offerId || '')}" autocomplete="off">
      <div class="form-grid">
        <div class="form-field is-full">
          <label for="folder-name">显示名称（别名）</label>
          <input id="folder-name" name="name" value="${esc(values?.name || '')}" placeholder="用于 TwinSync 内显示" required>
          <small>不会重命名磁盘上的文件夹。</small>
        </div>
        <div class="form-field is-full">
          <label for="folder-path">本机目录</label>
          <div class="input-with-button">
            <input id="folder-path" name="path" value="${esc(values?.path || '')}" placeholder="浏览并选择本机目录" required>
            <button class="btn" type="button" data-action="browse-path">浏览</button>
          </div>
          <small>两台电脑可使用不同的目录路径。</small>
          ${editing ? '<p class="path-change-note" data-path-change hidden>更换目录会复制原文件并保留旧目录，完成后先暂停同步。新位置存在不同内容的同名文件时不会覆盖。若要同步另一组文件，请另行添加。</p>' : ''}
        </div>
        <div class="form-field">
          <label for="folder-mode">同步方向</label>
          <select id="folder-mode" name="mode">
            <option value="two-way" ${folder?.mode === 'send-only' || folder?.mode === 'receive-only' ? '' : 'selected'}>双向同步</option>
            <option value="send-only" ${folder?.mode === 'send-only' ? 'selected' : ''}>仅发送（本机 → 对方）</option>
            <option value="receive-only" ${folder?.mode === 'receive-only' ? 'selected' : ''}>仅接收（对方 → 本机）</option>
          </select>
        </div>
        <div class="form-field">
          <label for="folder-paused">暂停此文件夹同步</label>
          <div class="switch-control" data-switch-status>
            <label class="switch"><input id="folder-paused" name="paused" type="checkbox" role="switch" aria-label="暂停此文件夹同步" aria-describedby="folder-pause-help" data-on-text="已暂停" data-off-text="未暂停" ${folder?.paused ? 'checked' : ''}><i></i></label>
            <span class="switch-value" data-switch-value aria-live="polite">${folder?.paused ? '已暂停' : '未暂停'}</span>
          </div>
          <small id="folder-pause-help">暂停后停止监听和同步。</small>
        </div>
        <div class="form-field is-full">
          <label>共享给设备</label>
          ${peerRows}
          ${peers.length ? '<small>更改需保存后生效。</small>' : ''}
        </div>
        <details class="form-field is-full advanced-options" ${(folder?.ignore || []).length ? 'open' : ''}>
          <summary>忽略规则</summary>
          <label for="folder-ignore" class="form-help">每行一条，已自动忽略缓存和临时文件。</label>
          <textarea id="folder-ignore" name="ignore" placeholder="*.tmp&#10;~$*&#10;desktop.ini">${esc((folder?.ignore || []).join('\n'))}</textarea>
        </details>
      </div>
      <div class="browse-box" data-browser hidden></div>
    </form>`;
  const footer = `${editing ? `<button class="btn btn-danger btn-sm" type="button" data-action="delete-folder" data-id="${esc(folder.id)}">移除</button><span class="spacer"></span>` : ''}<button class="btn" type="button" data-modal-close>取消</button><button class="btn btn-primary" type="submit" form="folder-form">${editing ? '保存修改' : '添加文件夹'}</button>`;
  openModal({
    title: editing ? '编辑同步文件夹' : '添加同步文件夹',
    subtitle: editing ? '修改名称、路径、方向或同步设备' : '选择一个目录，并决定它要同步到哪些设备',
    body,
    footer,
    wide: false,
  });
}

function openPeerModal(peer) {
  if (!peer) return;
  const shared = (appState?.folders || []).filter((folder) => folder.peerIds.includes(peer.id));
  const body = `
    <form id="peer-form" data-form="peer-form" data-id="${esc(peer.id)}" autocomplete="off">
      <div class="form-grid">
        <div class="form-field">
          <label for="peer-name">显示名称</label>
          <input id="peer-name" name="name" value="${esc(peer.name)}" required>
        </div>
        <div class="form-field">
          <label for="peer-port">端口</label>
          <input id="peer-port" name="port" type="number" min="1" max="65535" value="${esc(peer.port)}" required>
        </div>
        <div class="form-field is-full">
          <label for="peer-host">地址</label>
          <input id="peer-host" name="host" value="${esc(peer.host)}" placeholder="192.168.1.23">
          <small>如果 IP 变化，可以在这里手动修正，或等待局域网自动发现更新。</small>
        </div>
        <div class="form-field">
          <label for="peer-auto-sync">自动同步</label>
          <label class="switch"><input id="peer-auto-sync" name="autoSync" type="checkbox" ${peer.autoSync !== false ? 'checked' : ''}><i></i></label>
        </div>
        <div class="form-field">
          <label for="peer-paused">暂停连接</label>
          <label class="switch"><input id="peer-paused" name="paused" type="checkbox" ${peer.paused ? 'checked' : ''}><i></i></label>
        </div>
      </div>
      <div class="detail-list" style="margin-top:16px">
        <div class="detail-row"><span>连接状态</span><strong>${peer.online ? `在线 · ${peer.rttMs ?? '—'} ms` : '离线'}</strong></div>
        <div class="detail-row"><span>最近在线</span><strong>${esc(peer.online ? '现在' : fmtRelative(peer.lastSeenAt))}</strong></div>
        <div class="detail-row"><span>配对时间</span><strong>${esc(fmtDateTime(peer.pairedAt))}</strong></div>
        <div class="detail-row"><span>共享文件夹</span><strong>${shared.length ? shared.map((folder) => esc(folder.name)).join('、') : '无'}</strong></div>
        <div class="detail-row"><span>设备标识</span><code title="${esc(peer.id)}">${esc(peer.id)}</code></div>
      </div>
    </form>`;
  openModal({
    title: peer.name,
    subtitle: '设备设置与连接信息',
    body,
    footer: `<button class="btn btn-danger btn-sm" type="button" data-action="remove-peer" data-id="${esc(peer.id)}">移除设备</button><span class="spacer"></span><button class="btn" type="button" data-action="test-peer" data-id="${esc(peer.id)}">测试连接</button><button class="btn" type="button" data-modal-close>取消</button><button class="btn btn-primary" type="submit" form="peer-form">保存</button>`,
    wide: false,
  });
}

function openPeerFoldersModal(peer) {
  if (!peer) return;
  const folders = appState?.folders || [];
  const rows = folders.length
    ? folders
        .map(
          (folder) => `<div class="share-row">
            <span class="share-icon">${icon('folder')}</span>
            <div class="share-copy">
              <strong>${esc(folder.name)}</strong>
              <small>${esc(modeLabel(folder.mode))} · ${fmtNumber(folder.fileCount)} 个文件</small>
            </div>
            <label class="switch"><input class="folder-check" type="checkbox" value="${esc(folder.id)}" ${folder.peerIds.includes(peer.id) ? 'checked' : ''}><i></i></label>
          </div>`,
        )
        .join('')
    : '<div class="share-empty">还没有同步文件夹。先在文件页添加一个目录。</div>';
  const body = `
    <form id="peer-folders-form" data-form="peer-folders" data-id="${esc(peer.id)}">
      <div class="share-summary">选择「${esc(peer.name)}」可以访问的文件夹。关闭后只会停止同步，不会删除任何文件。</div>
      <div class="share-toolbar">
        <span>${folders.length} 个文件夹</span>
        <div class="share-toolbar-actions">
          <button class="text-link" type="button" data-action="share-all">全选</button>
          <button class="text-link" type="button" data-action="share-none">全部关闭</button>
        </div>
      </div>
      <div class="share-list">${rows}</div>
      <div class="share-note">仅共享勾选的文件夹。同步方向在文件夹设置中调整。</div>
    </form>`;
  openModal({
    title: `与「${peer.name}」同步的文件夹`,
    subtitle: peer.online ? '在线 · 修改后会立即通知对方' : '当前离线 · 下次连接后可再次调整',
    body,
    footer: `<button class="btn" type="button" data-modal-close>取消</button><button class="btn btn-primary" type="submit" form="peer-folders-form">保存同步范围</button>`,
    wide: true,
  });
}

function lineDiff(oldText, newText) {
  const before = oldText.split(/\r?\n/);
  const after = newText.split(/\r?\n/);
  if (before.length > 400 || after.length > 400) {
    return { truncated: true, rows: [] };
  }
  const table = Array.from({ length: before.length + 1 }, () => new Uint16Array(after.length + 1));
  for (let i = 1; i <= before.length; i += 1) {
    for (let j = 1; j <= after.length; j += 1) {
      table[i][j] = before[i - 1] === after[j - 1]
        ? table[i - 1][j - 1] + 1
        : Math.max(table[i - 1][j], table[i][j - 1]);
    }
  }
  const rows = [];
  let i = before.length;
  let j = after.length;
  while (i > 0 || j > 0) {
    if (i > 0 && j > 0 && before[i - 1] === after[j - 1]) {
      rows.push({ type: 'same', text: before[i - 1] });
      i -= 1;
      j -= 1;
    } else if (j > 0 && (i === 0 || table[i][j - 1] >= table[i - 1][j])) {
      rows.push({ type: 'add', text: after[j - 1] });
      j -= 1;
    } else {
      rows.push({ type: 'del', text: before[i - 1] });
      i -= 1;
    }
  }
  return { truncated: false, rows: rows.reverse() };
}

async function openFolderVersionsModal(folderId) {
  const folder = folderById(folderId);
  if (!folder) return;
  let versions = [];
  try {
    const result = await api(`/api/versions?folderId=${encodeURIComponent(folderId)}`);
    versions = result.versions || [];
  } catch (error) {
    toast(error.message, 'error');
    return;
  }
  const rows = versions.length
    ? versions.map((version) => `<div class="version-row">
        <input class="version-check" type="checkbox" value="${esc(version.id)}" aria-label="选择此版本">
        <div class="version-copy">
          <strong>${esc(baseName(version.path))}</strong>
          <span>${esc(version.path)} · ${esc(fmtDateTime(version.savedAt))} · ${esc(reasonLabel(version.reason))} · ${fmtBytes(version.size)}</span>
        </div>
        <button class="btn btn-sm" type="button" data-action="version-diff" data-id="${esc(version.id)}" data-folder="${esc(folderId)}" data-path="${esc(version.path)}">查看</button>
        <button class="btn btn-sm btn-soft" type="button" data-action="version-restore" data-id="${esc(version.id)}">恢复</button>
      </div>`).join('')
    : '<div class="empty-state" style="padding:32px 16px"><div class="empty-glyph">' + icon('history') + '</div><h3>还没有历史版本</h3><p>当文件被覆盖、删除或冲突恢复前，旧版本会保存在这里。</p></div>';
  const body = `<form id="versions-form" data-form="versions-batch">
    <div class="version-toolbar"><span>${versions.length} 个版本</span><div class="version-toolbar-actions"><button class="text-link" type="button" data-action="version-select-all">全选</button><button class="text-link" type="button" data-action="version-select-none">清空</button></div></div>
    <div class="version-list">${rows}</div>
    <p class="version-note">恢复前会先备份当前文件。选择多个版本时，会按列表顺序执行。</p>
  </form>`;
  openModal({
    title: '版本历史',
    subtitle: folder.name + ' · 查看改动或恢复旧版本',
    body,
    footer: '<button class="btn" type="button" data-modal-close>取消</button><button class="btn btn-primary" type="submit" form="versions-form">恢复选中版本</button>',
    wide: true,
  });
}

let versionDiffState = null;
let failedRestoreIds = [];
let versionDiffRequest = 0;
let restoreInFlight = false;
async function openVersionDiff(versionId, folderId, pathValue, page = null) {
  const requestId = ++versionDiffRequest;
  if (!page) versionDiffState = { versionId, folderId, pathValue, pages: [{ old: 0, current: 0 }], index: 0 };
  const state = versionDiffState;
  state.busy = true;
  const position = state.pages[state.index];
  try {
    const [oldPage, currentPage] = await Promise.all([
      api(`/api/versions/${encodeURIComponent(versionId)}/preview?offset=${position.old}&stamp=${encodeURIComponent(state.oldStamp || '')}`),
      api(`/api/folders/${encodeURIComponent(folderId)}/preview?path=${encodeURIComponent(pathValue)}&offset=${position.current}&stamp=${encodeURIComponent(state.currentStamp || '')}`),
    ]);
    if (requestId !== versionDiffRequest) return;
    if (page && (!state.overlay?.isConnected || state.overlay.dataset.closing === '1')) return;
    state.oldStamp = oldPage.stamp;
    state.currentStamp = currentPage.stamp;
    state.next = oldPage.eof && currentPage.eof ? null : { old: oldPage.nextOffset, current: currentPage.nextOffset };
    const oldText = oldPage.text || '';
    const currentText = currentPage.text || '';
    let body;
    const segmented = state.index > 0 || !oldPage.eof || !currentPage.eof;
    const subtitle = `与当前文件对比${segmented ? ` · 第 ${state.index + 1} 段` : ''}${currentPage.missing ? ' · 当前文件不存在' : ''}`;
    if (oldPage.binary || currentPage.binary) {
      state.next = null;
      body = '<div class="version-binary">这是二进制文件，无法显示文本差异。可以使用「恢复」直接回退。</div>';
    } else if (oldText === currentText) {
      body = `<div class="version-binary">${segmented ? '这一段内容相同。' : '当前文件与这个历史版本内容相同。'}</div>`;
    } else {
      const diff = lineDiff(oldText, currentText);
      if (diff.truncated) {
        body = `<div class="diff-view"><pre>${esc(oldText)}</pre><hr><pre>${esc(currentText)}</pre></div>`;
      } else {
        body = '<div class="diff-view">' + diff.rows.map((row) => `<div class="diff-row is-${row.type}"><span class="diff-mark">${row.type === 'add' ? '+' : row.type === 'del' ? '−' : ' '}</span><code>${esc(row.text || ' ')}</code></div>`).join('') + '</div><p class="diff-legend"><span class="diff-add">+ 当前版本新增</span><span class="diff-del">− 历史版本删除</span></p>';
      }
    }
    if (segmented) body += `<p class="version-note">分段显示：历史文件 ${fmtBytes(position.old)} / ${fmtBytes(oldPage.totalBytes)}，当前文件 ${fmtBytes(position.current)} / ${fmtBytes(currentPage.totalBytes)}。段内差异按当前范围计算。</p>`;
    const options = {
      title: baseName(pathValue),
      subtitle,
      body,
      footer: `${segmented ? `<button class="btn" type="button" data-action="diff-previous" ${state.index === 0 ? 'disabled' : ''}>上一段</button><button class="btn" type="button" data-action="diff-next" ${state.next ? '' : 'disabled'}>下一段</button>` : ''}<button class="btn" type="button" data-modal-close>关闭</button>`,
      wide: true,
      onClose: () => { if (versionDiffState === state) { versionDiffState = null; versionDiffRequest++; } },
    };
    if (state.overlay?.isConnected && state.overlay.dataset.closing !== '1') {
      state.overlay.querySelector('.modal-head p').textContent = subtitle;
      state.overlay.querySelector('.modal-body').innerHTML = body;
      state.overlay.querySelector('.modal-foot').innerHTML = options.footer;
    } else state.overlay = openModal(options);
    state.displayedIndex = state.index;
  } catch (error) {
    state.index = state.displayedIndex || 0;
    toast(error.message, 'error');
  } finally {
    state.busy = false;
  }
}

async function restoreVersions(ids, retry = false) {
  if (restoreInFlight) return;
  if (!ids.length) {
    toast('请选择要恢复的版本', 'warn');
    return;
  }
  const ok = retry || await confirmDialog({
    title: '恢复历史版本？',
    message: '当前文件会先保存为一份新的历史版本，然后被选中的版本覆盖。恢复后会触发一次同步。',
    confirmText: '恢复版本',
  });
  if (!ok) return;
  restoreInFlight = true;
  try {
    const result = await api('/api/versions/restore-batch', { method: 'POST', body: { ids } });
    closeAllModals({ force: true });
    const results = result.results || [];
    failedRestoreIds = results.filter((item) => !item.success).map((item) => item.id);
    openModal({
      title: '恢复结果',
      subtitle: `成功 ${result.count} 项 · 失败 ${failedRestoreIds.length} 项`,
      body: `<div class="version-list">${results.map((item) => `<div class="version-row"><div class="version-copy"><strong>${esc(item.path || '历史版本已不存在')}</strong><span>${item.success ? '已恢复' : esc(item.error || '恢复失败')}</span></div><span class="${item.success ? 'diff-add' : 'diff-del'}">${item.success ? '成功' : '失败'}</span></div>`).join('')}</div>`,
      footer: `<button class="btn" type="button" data-modal-close>关闭</button>${failedRestoreIds.length ? '<button class="btn btn-primary" type="button" data-action="retry-restores">仅重试失败项</button>' : ''}`,
      wide: true,
    });
    await refreshState({ quiet: true });
    if (activeView === 'files') {
      fileState.result = null;
      await loadFiles({ quiet: true });
    }
  } catch (error) {
    toast(error.message, 'error');
  } finally {
    restoreInFlight = false;
  }
}

function openDeviceNameModal() {
  if (!appState) return;
  openModal({
    title: '修改本机名称',
    subtitle: '另一台设备会在设备列表里看到这个名字',
    body: `<form id="device-name-form" data-form="device-name"><div class="form-field"><label for="device-name">名称</label><input id="device-name" name="name" value="${esc(appState.device.name)}" maxlength="64" required></div></form>`,
    footer: `<button class="btn" type="button" data-modal-close>取消</button><button class="btn btn-primary" type="submit" form="device-name-form">保存</button>`,
  });
}

async function openVersionsModal(folderId, relPath) {
  const overlay = openModal({
    title: '历史版本',
    subtitle: relPath,
    body: `<div class="empty-feed">${icon('sync')}<div>正在读取版本记录…</div></div>`,
    footer: `<button class="btn" type="button" data-modal-close>关闭</button>`,
    wide: true,
  });
  const body = $('.modal-body', overlay);
  try {
    const data = await api(`/api/versions?folderId=${encodeURIComponent(folderId)}&path=${encodeURIComponent(relPath)}`);
    const items = data.versions || [];
    body.innerHTML = items.length
      ? `<div class="version-list">${items.map((item) => `<div class="version-item"><div class="feed-icon is-info">${icon('history')}</div><div class="version-copy"><strong>${esc(fmtDateTime(item.savedAt))}</strong><span>${esc(reasonLabel(item.reason))} · ${fmtBytes(item.size)} · ${esc(item.hash.slice(0, 10))}…</span></div><button class="btn btn-sm btn-soft" type="button" data-action="restore-version" data-id="${esc(item.id)}">恢复</button></div>`).join('')}</div>`
      : `<div class="empty-state"><div class="empty-glyph">${icon('history')}</div><h3>还没有历史版本</h3><p>当文件被覆盖或删除时，旧内容会在这里保留。</p></div>`;
  } catch (error) {
    body.innerHTML = `<div class="empty-state"><div class="empty-glyph">${icon('alert')}</div><h3>读取失败</h3><p>${esc(error.message)}</p></div>`;
  }
}

/* -------------------------------------------------------------------- toast */

function toast(message, tone = 'success') {
  if (panelClosed) return;
  if (!message) return;
  const key = `${tone}:${message}`;
  const now = Date.now();
  if (key === lastToastKey && now - lastToastAt < 1800) return;
  lastToastKey = key;
  lastToastAt = now;
  const names = { success: 'check', error: 'alert', warn: 'alert', info: 'sparkle' };
  const element = document.createElement('div');
  element.className = `toast ${tone === 'error' ? 'is-error' : tone === 'warn' ? 'is-warn' : ''}`;
  element.innerHTML = `<span class="toast-icon">${icon(names[tone] || 'check')}</span><span>${esc(message)}</span>`;
  toastRoot.appendChild(element);
  setTimeout(() => {
    element.classList.add('is-out');
    setTimeout(() => element.remove(), 200);
  }, 3600);
}

async function copyText(text, label = '内容') {
  try {
    await navigator.clipboard.writeText(text);
    toast(`${label}已复制`);
  } catch {
    const input = document.createElement('textarea');
    input.value = text;
    input.style.position = 'fixed';
    input.style.opacity = '0';
    document.body.appendChild(input);
    input.select();
    try {
      document.execCommand('copy');
      toast(`${label}已复制`);
    } catch {
      toast('浏览器不允许自动复制，请手动选择', 'warn');
    } finally {
      input.remove();
    }
  }
}

/* ------------------------------------------------------------------ actions */

async function setSyncPaused(paused) {
  try {
    const result = await api('/api/settings', { method: 'PATCH', body: { syncPaused: paused } });
    appState.settings = result.settings;
    toast(paused ? '同步已暂停，服务仍在后台运行' : '同步已恢复');
    await refreshState({ quiet: true });
  } catch (error) {
    toast(error.message, 'error');
  }
}

async function startSync({ peerId = null, folderId = null } = {}) {
  if (!appState?.peers?.length) {
    openPairModal();
    return;
  }
  if (appState.sync?.state === 'syncing') {
    toast('同步正在进行中', 'info');
    return;
  }
  try {
    const result = await api('/api/sync', { method: 'POST', body: { peerId, folderId } });
    toast(result.accepted === false ? result.message || '本次未执行同步' : '已开始同步所选内容', 'info');
    setTimeout(() => refreshState({ quiet: true }), 120);
  } catch (error) {
    toast(error.message, 'error');
  }
}

async function resolveConflict(folderId, conflictId, choice) {
  if (choice === 'restore-copy') {
    const ok = await confirmDialog({
      title: '恢复冲突副本？',
      message: '冲突副本会覆盖当前文件，当前内容会先保存到历史版本。之后两台设备会同步这个恢复后的版本。',
      confirmText: '恢复副本',
    });
    if (!ok) return;
  }
  try {
    await api(`/api/conflicts/${encodeURIComponent(folderId)}/${encodeURIComponent(conflictId)}/resolve`, {
      method: 'POST',
      body: { action: choice },
    });
    toast(choice === 'restore-copy' ? '已恢复冲突副本' : '已保留当前文件');
    await refreshState({ quiet: true });
    if (activeView === 'files') {
      fileState.result = null;
      renderView();
    }
  } catch (error) {
    toast(error.message, 'error');
  }
}

async function removeFolder(folderId) {
  const folder = folderById(folderId);
  const ok = await confirmDialog({
    title: '移除同步文件夹？',
    message: `「${folder?.name || '这个文件夹'}」会从 TwinSync 中移除，但本机文件不会被删除。另一台设备上的文件也不会被删除。`,
    confirmText: '移除文件夹',
    danger: true,
  });
  if (!ok) return;
  try {
    await api(`/api/folders/${encodeURIComponent(folderId)}`, { method: 'DELETE' });
    toast('已移除同步文件夹');
    closeAllModals({ force: true });
    fileState.folderId = null;
    fileState.result = null;
    await refreshState({ quiet: true });
  } catch (error) {
    toast(error.message, 'error');
  }
}

async function removePeer(peerId) {
  const peer = peerById(peerId);
  const ok = await confirmDialog({
    title: '移除这台设备？',
    message: `「${peer?.name || '设备'}」将无法再访问你的同步文件夹。本机文件不会被删除。`,
    confirmText: '移除设备',
    danger: true,
  });
  if (!ok) return;
  try {
    await api(`/api/peers/${encodeURIComponent(peerId)}`, { method: 'DELETE' });
    toast('已移除设备');
    closeAllModals({ force: true });
    await refreshState({ quiet: true });
  } catch (error) {
    toast(error.message, 'error');
  }
}

async function testPeer(peerId, button) {
  setLoading(button, true);
  try {
    const result = await api(`/api/peers/${encodeURIComponent(peerId)}/test`, { method: 'POST' });
    toast(result.online === false ? '设备未连接，请检查对方服务和网络' : '设备连接正常', result.online === false ? 'warn' : 'success');
    await refreshState({ quiet: true });
  } catch (error) {
    toast(error.message, 'error');
  } finally {
    setLoading(button, false);
  }
}

async function togglePeerPause(peer) {
  try {
    await api(`/api/peers/${encodeURIComponent(peer.id)}`, {
      method: 'PATCH',
      body: { paused: !peer.paused },
    });
    toast(peer.paused ? '已恢复该设备的同步' : '已暂停该设备');
    await refreshState({ quiet: true });
  } catch (error) {
    toast(error.message, 'error');
  }
}

function setLoading(button, loading, text = '处理中') {
  if (!button) return;
  if (loading) {
    if (!button.dataset.originalHtml) button.dataset.originalHtml = button.innerHTML;
    button.disabled = true;
    button.innerHTML = `${icon('sync')}<span>${esc(text)}</span>`;
    button.classList.add('is-loading');
  } else {
    button.disabled = false;
    button.classList.remove('is-loading');
    if (button.dataset.originalHtml) {
      button.innerHTML = button.dataset.originalHtml;
      delete button.dataset.originalHtml;
    }
  }
}

/* ----------------------------------------------------------------- browser */

async function loadBrowser(overlay, targetPath) {
  const container = $('[data-browser]', overlay);
  if (!container) return;
  container.hidden = false;
  container.innerHTML = `<div class="browse-empty">正在读取目录…</div>`;
  try {
    const query = targetPath ? `?path=${encodeURIComponent(targetPath)}` : '';
    const data = await api(`/api/fs/list${query}`);
    browserState.path = data.path;
    container.innerHTML = browserHtml(data);
  } catch (error) {
    container.innerHTML = `<div class="browse-head"><button class="icon-btn" type="button" data-action="browse-path">${icon('refresh')}</button><code>${esc(targetPath || '')}</code></div><div class="browse-empty">${esc(error.message)}</div>`;
  }
}

function browserHtml(data) {
  const pathValue = data.path || '';
  return `<div class="browse-head">
    <button class="icon-btn" type="button" title="上一级" data-action="browse-enter" data-path="${esc(data.parent || pathValue)}" ${data.parent ? '' : 'disabled'}>${icon('chevron-left')}</button>
    <code title="${esc(pathValue)}">${esc(pathValue)}</code>
    <button class="btn btn-soft btn-sm" type="button" data-action="browse-use">使用此文件夹</button>
  </div>
  <div class="browse-roots">${(data.roots || []).map((root) => `<button class="browse-root" type="button" data-action="browse-enter" data-path="${esc(root)}">${esc(root)}</button>`).join('')}</div>
  <div class="browse-list">${data.dirs?.length ? data.dirs.map((dir) => `<button class="browse-item" type="button" data-action="browse-enter" data-path="${esc(dir.path)}">${icon('folder')}<span>${esc(dir.name)}</span></button>`).join('') : `<div class="browse-empty">这个目录下没有子文件夹</div>`}</div>`;
}

/* ------------------------------------------------------------------ handlers */

function openFileDetails(path) {
  const entry = [...(fileState.result?.children || []), ...(fileState.result?.mapItems || [])].find(item => item.type === 'file' && item.path === path);
  if (!entry) return;
  const folderId = fileState.result.folderId || currentFolder()?.id;
  const state = entry.conflictCount ? '有冲突待处理' : entry.pendingCount ? '待同步' : '已同步';
  const attributes = `data-folder="${esc(folderId)}" data-path="${esc(entry.path)}"`;
  openModal({
    title: entry.name,
    body: `<div class="detail-row"><span>所在位置</span><strong>${esc(entry.path)}</strong></div><div class="detail-row"><span>文件大小</span><strong>${fmtBytes(entry.size)}</strong></div><div class="detail-row"><span>修改时间</span><strong>${esc(fmtDateTime(entry.mtimeMs ? new Date(entry.mtimeMs).toISOString() : null))}</strong></div><div class="detail-row"><span>同步状态</span><strong>${state}</strong></div>`,
    footer: `<button class="btn" type="button" data-action="file-history" ${attributes}>版本历史</button><button class="btn" type="button" data-action="file-reveal" ${attributes}>在文件夹中显示</button><button class="btn btn-primary" type="button" data-action="file-download" ${attributes}>下载</button>`,
  });
}

function closePanel() {
  // Closing a browser-created tab may be denied. Always leave a useful, idle fallback.
  try { window.close(); } catch { /* Use the fallback below. */ }
  setTimeout(() => {
    if (window.closed || panelClosed) return;
    panelClosed = true;
    pairingCodeVisible = false;
    eventSource?.close();
    eventSource = null;
    refreshQueued = false;
    for (const timer of panelIntervals) clearInterval(timer);
    panelIntervals.clear();
    for (const request of panelRequests) request.abort();
    panelRequests.clear();
    clearTimeout(searchDebounce);
    clearTimeout(refreshFilesSoon.timer);
    clearTimeout(fileMapResizeTimer);
    modalRoot.replaceChildren();
    modalCloseHandlers.clear();
    toastRoot.replaceChildren();
    document.body.classList.remove('modal-open');
    closeSidebar();
    document.documentElement.dataset.panelState = 'closed';
    document.dispatchEvent(new Event('twinsync:panel-close'));
    document.title = 'TwinSync · 后台运行';
    viewEl.classList.remove('is-rendering');
    viewEl.innerHTML = `<section class="panel panel-rest" aria-labelledby="panel-rest-title"><p class="panel-rest-brand" lang="en">TwinSync</p><h1 id="panel-rest-title">同步服务继续在后台运行</h1><p>浏览器限制自动关页，可按 <kbd>Ctrl</kbd> + <kbd>W</kbd> 关闭此标签页。</p><button class="btn" type="button" data-action="reload-page">重新打开面板</button></section>`;
    viewEl.focus({ preventScroll: true });
  }, 120);
}

async function handleActionClick(event) {
  const actionElement = event.target.closest('[data-action]');
  if (!actionElement || actionElement.disabled) return;
  const action = actionElement.dataset.action;
  const id = actionElement.dataset.id;
  const folderId = actionElement.dataset.folder;
  const pathValue = actionElement.dataset.path;
  if (action === 'browse-directory') {
    await openDirectory(pathValue || '');
  } else if (action === 'inspect-file') {
    openFileDetails(pathValue);
  } else if (action === 'file-view' || action === 'show-folder-view') {
    fileState.view = action === 'show-folder-view' ? 'folders' : actionElement.dataset.mode === 'space' ? 'space' : 'folders';
    if (fileState.view === 'folders' && fileState.offset) { fileState.offset = 0; await loadFiles({ quiet: true }); }
    else paintFilesResults();
  } else if (action === 'close-panel') {
    closePanel();
  } else if (action === 'toggle-pair-code') {
    pairingCodeVisible = !pairingCodeVisible;
    renderView();
  } else if (action === 'accept-folder-offer') {
    const offer = (appState.pendingFolders || []).find(item => item.id === id && item.peerId === actionElement.dataset.peer);
    if (!offer) return;
    const existing = folderById(id);
    if (existing) openFolderModal({ ...existing, peerIds: [...new Set([...existing.peerIds, offer.peerId])] });
    else openFolderModal(null, { name: offer.name, offerId: offer.id, peerIds: [offer.peerId] });
  } else if (action === 'pause-sync' || action === 'resume-sync') {
    event.preventDefault();
    actionElement.disabled = true;
    try { await setSyncPaused(action === 'pause-sync'); }
    finally { if (actionElement.isConnected) actionElement.disabled = false; }
  } else if (action === 'sync-now') {
    event.preventDefault();
    startSync();
  } else if (action === 'sync-folder') {
    startSync({ folderId: id });
  } else if (action === 'peer-sync') {
    startSync({ peerId: id });
  } else if (action === 'show-sync-errors') {
    openSyncErrorsModal();
  } else if (action === 'retry-sync') {
    const scope = appState.sync?.scope || {};
    closeTopModal();
    startSync({ folderId: scope.folderId || null, peerId: scope.peerId || null });
  } else if (action === 'pair-device') {
    openPairModal();
  } else if (action === 'pair-nearby') {
    const nearby = appState?.nearby?.find((item) => item.id === id);
    const current = $('[data-form="pair-device"]');
    if (current && nearby) {
      const overlay = current.closest('.modal-overlay');
      const host = $('#pair-host', overlay);
      const port = $('#pair-port', overlay);
      if (host) host.value = nearby.address;
      if (port) port.value = nearby.port;
      $('#pair-code', overlay)?.focus();
    } else if (nearby) {
      openPairModal({ host: nearby.address, port: nearby.port });
    }
  } else if (action === 'refresh-pair') {
    setLoading(actionElement, true);
    try {
      const result = await api('/api/pairing/refresh', { method: 'POST' });
      toast('已生成新的配对码');
      await refreshState({ quiet: true });
      if (result.pairing) appState.pairing = result.pairing;
    } catch (error) {
      toast(error.message, 'error');
    } finally {
      setLoading(actionElement, false);
    }
  } else if (action === 'copy') {
    await copyText(actionElement.dataset.copy || '', actionElement.dataset.copyLabel || '内容');
  } else if (action === 'add-folder') {
    openFolderModal();
  } else if (action === 'edit-folder') {
    openFolderModal(folderById(id));
  } else if (action === 'delete-folder') {
    await removeFolder(id);
  } else if (action === 'reveal-folder') {
    try {
      await api(`/api/folders/${encodeURIComponent(id)}/reveal`, { method: 'POST', body: {} });
    } catch (error) {
      toast(error.message, 'error');
    }
  } else if (action === 'open-peer') {
    openPeerModal(peerById(id));
  } else if (action === 'folder-versions') {
    await openFolderVersionsModal(id);
  } else if (action === 'version-diff') {
    await openVersionDiff(id, actionElement.dataset.folder, actionElement.dataset.path || '');
  } else if (action === 'diff-next' || action === 'diff-previous') {
    if (!versionDiffState || versionDiffState.busy) return;
    const state = versionDiffState;
    if (action === 'diff-next' && state.next) { state.pages[++state.index] = state.next; }
    else if (action === 'diff-previous' && state.index > 0) state.index--;
    else return;
    await openVersionDiff(state.versionId, state.folderId, state.pathValue, true);
  } else if (action === 'retry-restores') {
    await restoreVersions([...failedRestoreIds], true);
  } else if (action === 'version-restore') {
    await restoreVersions([id]);
  } else if (action === 'version-select-all' || action === 'version-select-none') {
    const overlay = actionElement.closest('.modal-overlay');
    const checked = action === 'version-select-all';
    overlay?.querySelectorAll('.version-check').forEach((input) => { input.checked = checked; });
  } else if (action === 'peer-folders') {
    openPeerFoldersModal(peerById(id));
  } else if (action === 'share-all' || action === 'share-none') {
    const overlay = actionElement.closest('.modal-overlay');
    const checked = action === 'share-all';
    overlay?.querySelectorAll('.folder-check').forEach((input) => { input.checked = checked; });
    const changed = overlay?.querySelector('.folder-check');
    if (changed) updateEditorState(changed);
  } else if (action === 'remove-peer') {
    await removePeer(id);
  } else if (action === 'test-peer') {
    await testPeer(id, actionElement);
  } else if (action === 'toggle-peer-pause') {
    const peer = peerById(id);
    if (peer) await togglePeerPause(peer);
  } else if (action === 'open-folder') {
    fileState.folderId = id;
    fileState.directory = '';
    fileState.query = '';
    fileState.status = 'all';
    fileState.offset = 0;
    navigate('files', { force: true });
  } else if (action === 'select-folder') {
    if (id === fileState.folderId) return;
    fileState.folderId = id;
    fileState.directory = '';
    fileState.offset = 0;
    fileState.result = null;
    fileState.error = null;
    renderView();
    loadFiles();
  } else if (action === 'file-filter') {
    fileState.status = actionElement.dataset.filter || 'all';
    fileState.offset = 0;
    $$('.filter-chip[data-action="file-filter"]').forEach((chip) => chip.classList.toggle('is-active', chip === actionElement));
    loadFiles();
  } else if (action === 'clear-file-filter') {
    fileState.query = '';
    fileState.status = 'all';
    fileState.offset = 0;
    renderView();
    loadFiles();
  } else if (action === 'refresh-files') {
    await loadFiles({ quiet: true });
  } else if (action === 'rescan-folder') {
    try {
      await api('/api/scan', { method: 'POST', body: { folderId: id } });
      toast('已开始重新扫描', 'info');
      setTimeout(() => loadFiles({ quiet: true }), 1200);
    } catch (error) {
      toast(error.message, 'error');
    }
  } else if (action === 'page-prev') {
    if (fileState.offset <= 0) return;
    fileState.offset = Math.max(0, fileState.offset - fileState.limit);
    await loadFiles();
  } else if (action === 'page-next') {
    fileState.offset += fileState.limit;
    await loadFiles();
  } else if (action === 'file-download') {
    const link = document.createElement('a');
    link.href = `/api/folders/${encodeURIComponent(folderId)}/download?path=${encodeURIComponent(pathValue || '')}`;
    link.download = baseName(pathValue || 'download');
    document.body.appendChild(link);
    link.click();
    link.remove();
  } else if (action === 'file-reveal') {
    try {
      await api(`/api/folders/${encodeURIComponent(folderId)}/reveal`, { method: 'POST', body: { path: pathValue || '' } });
    } catch (error) {
      toast(error.message, 'error');
    }
  } else if (action === 'file-history') {
    await openVersionsModal(folderId, pathValue || '');
  } else if (action === 'restore-version') {
    const ok = await confirmDialog({
      title: '恢复这个历史版本？',
      message: '当前文件会先保存到历史版本，然后被这个旧版本覆盖。恢复后会立即触发一次同步。',
      confirmText: '恢复版本',
    });
    if (!ok) return;
    try {
      await api(`/api/versions/${encodeURIComponent(id)}/restore`, { method: 'POST' });
      toast('已恢复历史版本');
      closeAllModals({ force: true });
      await refreshState({ quiet: true });
      if (activeView === 'files') {
        fileState.result = null;
        loadFiles();
      }
    } catch (error) {
      toast(error.message, 'error');
    }
  } else if (action === 'resolve-conflict') {
    await resolveConflict(folderId, id, actionElement.dataset.choice);
  } else if (action === 'history-filter') {
    historyState.filter = actionElement.dataset.filter || 'all';
    $$('.filter-chip[data-action="history-filter"]').forEach((chip) => chip.classList.toggle('is-active', chip === actionElement));
    paintHistory();
  } else if (action === 'refresh-history') {
    historyState.items = null;
    await loadHistory();
  } else if (action === 'show-conflicts') {
    const conflict = appState?.conflicts?.[0];
    if (conflict) fileState.folderId = conflict.folderId;
    fileState.directory = '';
    fileState.status = 'conflict';
    fileState.offset = 0;
    fileState.result = null;
    activeView = 'files';
    history.replaceState(null, '', '#files');
    lastSignature = '';
    updateShell();
    renderViewWithTransition();
    loadFiles();
  } else if (action === 'edit-device-name') {
    openDeviceNameModal();
  } else if (action === 'open-path') {
    if (!pathValue) return;
    try {
      await api('/api/reveal', { method: 'POST', body: { path: pathValue } });
    } catch (error) {
      toast(error.message, 'error');
    }
  } else if (action === 'shutdown') {
    const ok = await confirmDialog({
      title: '退出 TwinSync？',
      message: '退出后另一台设备会看到本机离线，自动同步将停止。之后可从开始菜单重新打开 TwinSync。',
      confirmText: '退出应用',
      danger: true,
    });
    if (!ok) return;
    try {
      await api('/api/shutdown', { method: 'POST' });
      toast('TwinSync 已退出，可以关闭此页面', 'info');
    } catch (error) {
      toast(error.message, 'error');
    }
  } else if (action === 'browse-path') {
    const overlay = actionElement.closest('.modal-overlay');
    const input = $('#folder-path', overlay);
    await loadBrowser(overlay, input?.value?.trim() || '');
  } else if (action === 'browse-enter') {
    const overlay = actionElement.closest('.modal-overlay');
    await loadBrowser(overlay, actionElement.dataset.path || browserState.path || '');
  } else if (action === 'browse-use') {
    const overlay = actionElement.closest('.modal-overlay');
    const input = $('#folder-path', overlay);
    if (input) input.value = browserState.path || input.value;
    const nameInput = $('#folder-name', overlay);
    if (nameInput && input?.value && !nameInput.value.trim()) {
      nameInput.value = baseName(input.value) || '同步文件夹';
    }
    if (input) updateEditorState(input);
    const container = $('[data-browser]', overlay);
    if (container) container.hidden = true;
  } else if (action === 'reload-page') {
    location.reload();
  }
}

async function handleSubmit(event) {
  const form = event.target.closest('form[data-form]');
  if (!form) return;
  event.preventDefault();
  const type = form.dataset.form;
  const submit = document.querySelector(`button[type="submit"][form="${form.id}"]`);
  if (type === 'pair-device') {
    const host = form.elements.host?.value.trim();
    const port = Number(form.elements.port?.value) || 55220;
    const code = form.elements.code?.value.trim();
    if (!/^\d{6}$/.test(code || '')) {
      toast('请输入 6 位数字配对码', 'warn');
      form.elements.code?.focus();
      return;
    }
    setLoading(submit, true, '正在连接');
    api('/api/peers/pair', { method: 'POST', body: { host, port, code, fingerprint: form.elements.fingerprint?.value.trim() || '' } })
      .then(async () => {
        closeAllModals({ force: true });
        toast('设备已配对，请选择需要共享的文件夹');
        await refreshState({ quiet: true });
        navigate('devices', { force: true });
      })
      .catch((error) => toast(error.message, 'error'))
      .finally(() => setLoading(submit, false));
  } else if (type === 'folder-form') {
    const editingId = form.dataset.id;
    const name = form.elements.name.value.trim();
    const pathValue = form.elements.path.value.trim();
    const mode = form.elements.mode.value;
    const paused = form.elements.paused.checked;
    const peerIds = [...form.querySelectorAll('.peer-check:checked')].map((input) => input.value);
    const ignore = form.elements.ignore.value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
    if (!name || !pathValue) {
      toast('请填写显示名称和本机目录', 'warn');
      return;
    }
    setLoading(submit, true, '保存中');
    const body = { name, path: pathValue, mode, paused, peerIds, ignore, offerId: form.dataset.offer || '' };
    const migrating = editingId && !sameDirectory(pathValue, form.dataset.originalPath);
    if (migrating) {
      setLoading(submit, false);
      const confirmed = await confirmDialog({ title: '迁移同步目录？', message: `将原目录的文件复制到「${pathValue}」，保留旧目录，不覆盖新位置已有的不同内容文件。迁移完成后保持暂停，核对后可恢复同步。`, confirmText: '复制并迁移' });
      if (!confirmed) return;
      body.pathChangeMode = 'migrate';
      setLoading(submit, true, '迁移中');
    }
    const request = editingId
      ? api(`/api/folders/${encodeURIComponent(editingId)}`, { method: 'PATCH', body })
      : api('/api/folders', { method: 'POST', body });
    request
      .then(async (result) => {
        closeAllModals({ force: true });
        toast(migrating ? '目录已迁移，当前已暂停；核对后可恢复同步' : editingId ? '文件夹设置已保存' : '已添加同步文件夹');
        if (result.folder?.id) fileState.folderId = result.folder.id;
        fileState.result = null;
        await refreshState({ quiet: true });
        if (!editingId) navigate('files', { force: true });
      })
      .catch((error) => toast(error.message, 'error'))
      .finally(() => setLoading(submit, false));
  } else if (type === 'peer-folders') {
    const peerIdValue = form.dataset.id;
    const folderIds = [...form.querySelectorAll('.folder-check:checked')].map((input) => input.value);
    setLoading(submit, true, '保存中');
    api(`/api/peers/${encodeURIComponent(peerIdValue)}/folders`, { method: 'POST', body: { folderIds } })
      .then(async () => {
        closeAllModals({ force: true });
        toast('同步范围已保存');
        await refreshState({ quiet: true });
      })
      .catch((error) => toast(error.message, 'error'))
      .finally(() => setLoading(submit, false));
  } else if (type === 'versions-batch') {
    const ids = [...form.querySelectorAll('.version-check:checked')].map((input) => input.value);
    await restoreVersions(ids);
  } else if (type === 'peer-form') {
    const idValue = form.dataset.id;
    const body = {
      name: form.elements.name.value.trim(),
      host: form.elements.host.value.trim(),
      port: Number(form.elements.port.value),
      autoSync: form.elements.autoSync.checked,
      paused: form.elements.paused.checked,
    };
    setLoading(submit, true, '保存中');
    api(`/api/peers/${encodeURIComponent(idValue)}`, { method: 'PATCH', body })
      .then(async () => {
        closeAllModals({ force: true });
        toast('设备设置已保存');
        await refreshState({ quiet: true });
      })
      .catch((error) => toast(error.message, 'error'))
      .finally(() => setLoading(submit, false));
  } else if (type === 'device-name') {
    const name = form.elements.name.value.trim();
    setLoading(submit, true, '保存中');
    api('/api/device', { method: 'POST', body: { name } })
      .then(async () => {
        closeAllModals({ force: true });
        toast('本机名称已更新');
        await refreshState({ quiet: true });
      })
      .catch((error) => toast(error.message, 'error'))
      .finally(() => setLoading(submit, false));
  }
}

async function handleSettingChange(input) {
  if (!appState) return;
  const key = input.dataset.setting;
  if (!key) return;
  let value;
  if (input.type === 'checkbox') value = input.checked;
  else if (input.type === 'number') value = Number(input.value);
  else if (key === 'syncIntervalSec') value = Number(input.value);
  else value = input.value;
  const previous = appState.settings[key];
  appState.settings[key] = value;
  try {
    const result = await api('/api/settings', { method: 'PATCH', body: { [key]: value } });
    appState.settings = result.settings;
    if (result.warning) {
      toast(result.warning, 'warn');
      if (input.type === 'checkbox') input.checked = Boolean(result.settings[key]);
    }
    if (key === 'theme') applyTheme();
    updateShell();
    await refreshState({ quiet: true });
  } catch (error) {
    appState.settings[key] = previous;
    if (input.type === 'checkbox') input.checked = Boolean(previous);
    else input.value = previous;
    toast(error.message, 'error');
  }
}

function handleInput(event) {
  if (event.target.id === 'file-search') {
    fileState.query = event.target.value;
    fileState.offset = 0;
    clearTimeout(searchDebounce);
    searchDebounce = setTimeout(() => loadFiles({ quiet: true }), 320);
  }
}

function handleClick(event) {
  const menu = event.target.closest('.row-menu');
  $$('.row-menu[open]').forEach((item) => {
    if (item !== menu || event.target.closest('[data-action]')) item.open = false;
  });
  const closeButton = event.target.closest('[data-modal-close]');
  if (closeButton) {
    closeModal(closeButton.closest('.modal-overlay'));
    return;
  }
  if (event.target.classList?.contains('modal-overlay')) {
    closeModal(event.target);
    return;
  }
  const navElement = event.target.closest('[data-view]');
  const actionElement = event.target.closest('[data-action]');
  if (navElement && !actionElement) {
    event.preventDefault();
    navigate(navElement.dataset.view);
    return;
  }
  if (actionElement) {
    event.preventDefault();
    handleActionClick(event).catch((error) => toast(error.message, 'error'));
  }
}

function handleKeydown(event) {
  if (event.key === 'Escape') {
    $$('.row-menu[open]').forEach((menu) => { menu.open = false; menu.querySelector('summary')?.focus(); });
    const overlays = $$('.modal-overlay', modalRoot);
    if (overlays.length) closeModal(overlays[overlays.length - 1]);
    else closeSidebar();
  }
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k' && activeView === 'files') {
    event.preventDefault();
    $('#file-search')?.focus();
  }
}

function openSidebar() {
  $('#sidebar')?.classList.add('is-open');
  $('#sidebar-scrim')?.classList.add('is-open');
}

function closeSidebar() {
  $('#sidebar')?.classList.remove('is-open');
  $('#sidebar-scrim')?.classList.remove('is-open');
}

function updatePairCountdown() {
  const element = $('#pair-countdown');
  if (!element || !appState?.pairing?.expiresAt) return;
  const remaining = Math.max(0, Math.round((new Date(appState.pairing.expiresAt).getTime() - Date.now()) / 1000));
  element.textContent = remaining > 0
    ? `${String(Math.floor(remaining / 60)).padStart(2, '0')}:${String(remaining % 60).padStart(2, '0')}`
    : '已过期';
}

function updateRelTimes() {
  $$('[data-rel]').forEach((element) => {
    element.textContent = fmtRelative(element.dataset.rel);
  });
}

function renderBootError(error) {
  viewEl.innerHTML = `<div class="panel"><div class="empty-state"><div class="empty-glyph">${icon('alert')}</div><h3>无法连接 TwinSync 服务</h3><p>${esc(error?.message || '请从开始菜单打开 TwinSync，然后刷新页面。')}</p><button class="btn btn-primary" type="button" data-action="reload-page">重新加载</button></div></div>`;
}

function bindEvents() {
  window.addEventListener('resize', () => {
    clearTimeout(fileMapResizeTimer);
    fileMapResizeTimer = setTimeout(() => { if (!panelClosed && activeView === 'files' && fileState.view === 'space' && fileState.result) paintFilesResults(); }, 120);
  }, { passive: true });
  document.addEventListener('click', handleClick);
  document.addEventListener('submit', handleSubmit);
  document.addEventListener('change', (event) => {
    updateEditorState(event.target);
    if (event.target.matches('[data-on-text][data-off-text]')) {
      const output = event.target.closest('[data-switch-status]')?.querySelector('[data-switch-value]');
      if (output) output.textContent = event.target.checked ? event.target.dataset.onText : event.target.dataset.offText;
    }
    if (event.target.matches('[data-setting]')) handleSettingChange(event.target);
  });
  document.addEventListener('input', (event) => { updateEditorState(event.target); handleInput(event); });
  document.addEventListener('keydown', handleKeydown);
  $('#menu-btn')?.addEventListener('click', openSidebar);
  $('#sidebar-scrim')?.addEventListener('click', closeSidebar);
  $('#sync-btn')?.addEventListener('click', () => {
    if (appState?.settings?.syncPaused) setSyncPaused(false);
    else startSync();
  });
  $('#theme-btn')?.addEventListener('click', () => {
    const actual = document.documentElement.dataset.theme;
    saveTheme(actual === 'dark' ? 'light' : 'dark');
  });
  document.addEventListener('click', (event) => {
    const themeChoice = event.target.closest('[data-theme-choice]');
    if (themeChoice) {
      event.preventDefault();
      saveTheme(themeChoice.dataset.themeChoice);
    }
  });
  window.addEventListener('popstate', () => {
    if (panelClosed) return;
    const next = normalizeView(location.hash.replace(/^#/, '')) || 'overview';
    if (next !== activeView) {
      activeView = next;
      lastSignature = '';
      if (next === 'files') {
        fileState.folderId = fileState.folderId || appState?.folders?.[0]?.id || null;
        fileState.result = null;
        loadFiles();
      }
      if (next === 'history') loadHistory();
      renderViewWithTransition();
    }
  });
  window.addEventListener('beforeunload', (event) => {
    if ($$('.modal-overlay:not([data-closing="1"]) form[data-initial-values]', modalRoot).some(editorDirty)) {
      event.preventDefault(); event.returnValue = ''; return;
    }
    eventSource?.close();
  });
  themeMedia?.addEventListener?.('change', applyTheme);
}

async function init() {
  applyTheme();
  const initial = normalizeView(location.hash.replace(/^#/, ''));
  if (initial) activeView = initial;
  else history.replaceState(null, '', '#overview');
  bindEvents();
  await refreshState();
  connectEvents();
  panelIntervals.add(setInterval(() => { if (!document.hidden && activeView === 'devices') updatePairCountdown(); }, 1000));
  panelIntervals.add(setInterval(() => { if (!document.hidden) updateRelTimes(); }, 20_000));
  panelIntervals.add(setInterval(() => {
    if (!document.hidden && (!eventSource || eventSource.readyState !== 1)) refreshState({ quiet: true });
  }, 15_000));
  document.addEventListener('visibilitychange', () => {
    if (panelClosed) return;
    if (document.hidden) {
      pairingCodeVisible = false;
      if (activeView === 'devices') renderView();
      eventSource?.close();
      eventSource = null;
    } else {
      connectEvents();
      refreshState({ quiet: true });
    }
  });
}

init();
