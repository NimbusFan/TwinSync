/* Ambient particles and refractive glass. No dependencies or network requests. */
(() => {
  'use strict';
  const root = document.documentElement;
  const layer = document.querySelector('.ambient-effects');
  const canvas = document.querySelector('#ambient-particles');
  const definitions = document.querySelector('#glass-filter-defs');
  if (!layer || !canvas || !definitions) return;
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  const finePointer = matchMedia('(hover: hover) and (pointer: fine)');
  const svgNS = 'http://www.w3.org/2000/svg';
  const refractionSupported = CSS.supports('backdrop-filter', 'url("#glass-lens") blur(4px)');
  const context = canvas.getContext('2d', { alpha: true });
  const header = document.querySelector('.app-header');
  const lenses = new Map();
  const pointer = { x: -1000, y: -1000, targetX: -1000, targetY: -1000 };
  let width = 0, height = 0, particles = [], dark = root.dataset.theme === 'dark';
  let animation = 0, previousFrame = 0, time = 0, frames = 0, visible = true;
  let lensSequence = 0, refreshTimer = 0, resizeTimer = 0, highlighted = null;
  let highlightFrame = 0, highlightTarget = null, highlightX = 0, highlightY = 0;
  const lensMaps = new Map();
  let spriteAtlas = null, spriteKey = '', spriteTile = 0, spriteColumns = 0, spriteRatio = 1;
  let seed = 7391;
  const random = () => {
    seed ^= seed << 13; seed ^= seed >>> 17; seed ^= seed << 5;
    return (seed >>> 0) / 4294967296;
  };

  function buildSprites() {
    const key = `${dark}:${particles.length}:${spriteRatio}`;
    if (key === spriteKey) return;
    spriteTile = Math.ceil(12 * spriteRatio);
    spriteColumns = Math.max(1, Math.ceil(Math.sqrt(particles.length * 2)));
    const rows = Math.max(1, Math.ceil(particles.length * 2 / spriteColumns));
    const atlas = document.createElement('canvas');
    atlas.width = spriteColumns * spriteTile; atlas.height = rows * spriteTile;
    const paint = atlas.getContext('2d');
    if (!paint) return;
    paint.fillStyle = dark ? '#d5e6ff' : '#355b91';
    for (let i = 0; i < particles.length; i++) {
      for (let part = 0; part < 2; part++) {
        if (part && !particles[i].glow) continue;
        const slot = i * 2 + part;
        const x = slot % spriteColumns * spriteTile + spriteTile / 2;
        const y = Math.floor(slot / spriteColumns) * spriteTile + spriteTile / 2;
        paint.beginPath();
        paint.arc(x, y, particles[i].radius * spriteRatio * (part ? 3.2 : 1), 0, Math.PI * 2);
        paint.fill();
      }
    }
    spriteAtlas = atlas; spriteKey = key;
  }

  function paintSprite(slot, x, y) {
    const sx = slot % spriteColumns * spriteTile;
    const sy = Math.floor(slot / spriteColumns) * spriteTile;
    const size = spriteTile / spriteRatio;
    context.drawImage(spriteAtlas, sx, sy, spriteTile, spriteTile, x - size / 2, y - size / 2, size, size);
  }

  function draw(delta = 0) {
    if (!context) return;
    time += delta;
    frames++;
    pointer.x += (pointer.targetX - pointer.x) * .12;
    pointer.y += (pointer.targetY - pointer.y) * .12;
    context.clearRect(0, 0, width, height);
    if (!spriteAtlas) return;
    const pointerActive = finePointer.matches && pointer.x > -200 && pointer.y > -200 && !reducedMotion.matches;
    for (let i = 0; i < particles.length; i++) {
      const point = particles[i];
      let x = (point.x * (width + 60) + time * point.speed) % (width + 60) - 30;
      let y = point.y * height + Math.sin(time * .16 + point.phase + x * .002) * 18;
      if (pointerActive) {
        const dx = x - pointer.x, dy = y - pointer.y;
        const squaredDistance = dx * dx + dy * dy;
        if (squaredDistance < 22500 && squaredDistance > 0) {
          const distance = Math.sqrt(squaredDistance);
          const force = (1 - distance / 150) ** 2 * 24;
          x += dx / distance * force; y += dy / distance * force;
        }
      }
      const quietTextArea = x > width * .26 && x < width * .74 && y > 155 && y < 410 ? .35 : 1;
      const alpha = point.alpha * (dark ? 1 : .48) * quietTextArea * (.78 + Math.sin(time * .4 + point.phase) * .22);
      if (point.glow) { context.globalAlpha = alpha * .1; paintSprite(i * 2 + 1, x, y); }
      context.globalAlpha = alpha; paintSprite(i * 2, x, y);
    }
    context.globalAlpha = 1;
  }

  function tick(now) {
    animation = 0;
    if (root.dataset.panelState === 'closed' || document.hidden || !visible || reducedMotion.matches) return;
    const interval = 1000 / 30;
    if (!previousFrame || now - previousFrame >= interval - .5) {
      const delta = previousFrame ? Math.min((now - previousFrame) / 1000, .08) : 0;
      previousFrame = now; draw(delta);
    }
    animation = requestAnimationFrame(tick);
  }

  function synchronizeMotion() {
    cancelAnimationFrame(animation); animation = 0; previousFrame = 0;
    const paused = root.dataset.panelState === 'closed' || document.hidden || !visible || reducedMotion.matches;
    layer.classList.toggle('is-paused', paused);
    layer.dataset.motion = reducedMotion.matches ? 'reduced' : paused ? 'paused' : 'running';
    if (paused) {
      if (!document.hidden && visible) draw();
    } else if (context) animation = requestAnimationFrame(tick);
  }

  function resize() {
    if (!context) return;
    const rect = layer.getBoundingClientRect();
    width = Math.round(rect.width); height = Math.round(rect.height);
    if (!width || !height) return;
    const ratio = Math.min(devicePixelRatio || 1, width < 600 ? 1.25 : 1.5);
    spriteRatio = ratio;
    canvas.width = Math.round(width * ratio); canvas.height = Math.round(height * ratio);
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    const count = reducedMotion.matches ? 36 : Math.max(90, Math.min(width < 600 ? 160 : 520, Math.round(width * height / 2600)));
    seed = 7391;
    particles = Array.from({ length: count }, () => ({
      x: random(), y: random(), radius: .45 + random() * .9,
      alpha: .22 + random() * .42, phase: random() * Math.PI * 2,
      speed: 1.5 + random() * 3.5, glow: random() > .88,
    }));
    buildSprites();
    draw(); synchronizeMotion();
  }

  function svg(name, attributes = {}) {
    const element = document.createElementNS(svgNS, name);
    for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value);
    return element;
  }

  // A neutral centre with a curved displacement field only around the rim.
  // The filter refracts the backdrop; labels are painted above it and stay sharp.
  function updateLens(element) {
    const record = lenses.get(element);
    if (!record || !element.isConnected) return;
    const rect = element.getBoundingClientRect();
    const w = Math.round(rect.width), h = Math.round(rect.height);
    if (!w || !h) return;
    const radius = Math.min(parseFloat(getComputedStyle(element).borderTopLeftRadius) || 12, w / 2, h / 2);
    const key = `${w}:${h}:${radius}`;
    if (record.key === key) return;
    record.key = key;
    const cached = lensMaps.get(key);
    if (cached) {
      record.image.setAttribute('href', cached);
      element.style.setProperty('--glass-refraction', `url("#${record.filter.id}")`);
      element.classList.add('has-glass-refraction');
      return;
    }
    const scale = Math.min(1, 300 / w, 160 / h);
    const map = document.createElement('canvas');
    map.width = Math.max(1, Math.round(w * scale)); map.height = Math.max(1, Math.round(h * scale));
    const mapContext = map.getContext('2d');
    if (!mapContext) return;
    const pixels = mapContext.createImageData(map.width, map.height);
    const rim = Math.min(10, radius);
    for (let y = 0; y < map.height; y++) {
      for (let x = 0; x < map.width; x++) {
        const px = (x + .5) / scale, py = (y + .5) / scale;
        const cx = Math.max(radius, Math.min(w - radius, px));
        const cy = Math.max(radius, Math.min(h - radius, py));
        const vx = px - cx, vy = py - cy;
        let nx = 0, ny = 0, distance;
        if (vx && vy) {
          const length = Math.hypot(vx, vy);
          nx = vx / length; ny = vy / length; distance = radius - length;
        } else {
          distance = px; nx = -1;
          if (w - px < distance) { distance = w - px; nx = 1; }
          if (py < distance) { distance = py; nx = 0; ny = -1; }
          if (h - py < distance) { distance = h - py; nx = 0; ny = 1; }
        }
        const bend = distance >= 0 && distance < rim ? (1 - distance / rim) ** 2 * 90 : 0;
        const index = (y * map.width + x) * 4;
        pixels.data[index] = 128 + nx * bend;
        pixels.data[index + 1] = 128 + ny * bend;
        pixels.data[index + 2] = 128; pixels.data[index + 3] = 255;
      }
    }
    mapContext.putImageData(pixels, 0, 0);
    const mapURL = map.toDataURL();
    lensMaps.set(key, mapURL);
    if (lensMaps.size > 32) lensMaps.delete(lensMaps.keys().next().value);
    record.image.setAttribute('href', mapURL);
    element.style.setProperty('--glass-refraction', `url("#${record.filter.id}")`);
    element.classList.add('has-glass-refraction');
  }

  const sizeObserver = new ResizeObserver(entries => {
    for (const { target } of entries) updateLens(target);
  });

  function refreshGlass() {
    for (const [element, record] of lenses) {
      if (!element.isConnected) {
        sizeObserver.unobserve(element); record.filter.remove(); lenses.delete(element);
      }
    }
    if (!refractionSupported) return;
    const targets = document.querySelectorAll('.hero-actions .btn-primary, .modal-foot .btn-primary, .row-menu[open] .row-menu-popover');
    for (const element of targets) {
      if (!lenses.has(element)) {
        const filter = svg('filter', { id: `glass-lens-${++lensSequence}`, x: '0%', y: '0%', width: '100%', height: '100%', 'color-interpolation-filters': 'sRGB' });
        const image = svg('feImage', { x: '0', y: '0', width: '100%', height: '100%', preserveAspectRatio: 'none', result: 'rim' });
        filter.append(image, svg('feDisplacementMap', { in: 'SourceGraphic', in2: 'rim', scale: '12', xChannelSelector: 'R', yChannelSelector: 'G' }));
        definitions.appendChild(filter);
        lenses.set(element, { filter, image, key: '' }); sizeObserver.observe(element);
      }
      updateLens(element);
    }
  }

  function queueGlass() { clearTimeout(refreshTimer); refreshTimer = setTimeout(refreshGlass, 40); }
  const contentObserver = new MutationObserver(queueGlass);
  for (const scope of [document.querySelector('#view'), document.querySelector('#modal-root')]) {
    if (scope) contentObserver.observe(scope, { childList: true, subtree: true, attributes: true, attributeFilter: ['open'] });
  }
  new MutationObserver(() => {
    const nextDark = root.dataset.theme === 'dark';
    if (nextDark !== dark) { dark = nextDark; buildSprites(); }
    if (!animation) draw();
  }).observe(root, { attributes: true, attributeFilter: ['data-theme'] });

  function updateHighlight() {
    highlightFrame = 0;
    const target = highlightTarget;
    if (highlighted && highlighted !== target) highlighted.classList.remove('is-glass-hovered');
    highlighted = target;
    if (target) {
      const box = target.getBoundingClientRect();
      target.style.setProperty('--glass-x', `${((highlightX - box.left) / box.width * 100).toFixed(1)}%`);
      target.style.setProperty('--glass-y', `${((highlightY - box.top) / box.height * 100).toFixed(1)}%`);
      target.classList.add('is-glass-hovered');
    }
  }
  document.addEventListener('pointermove', event => {
    if (root.dataset.panelState === 'closed' || document.hidden || !finePointer.matches || reducedMotion.matches) return;
    pointer.targetX = event.clientX; pointer.targetY = event.pageY;
    highlightX = event.clientX; highlightY = event.clientY;
    highlightTarget = event.target.closest?.('.btn, .row-menu-popover, .panel, .table-shell, .identity-card, .pairing-card, .peer-card, .settings-card, .info-card');
    if (!highlightFrame) highlightFrame = requestAnimationFrame(updateHighlight);
  }, { passive: true });
  document.documentElement.addEventListener('pointerleave', () => {
    cancelAnimationFrame(highlightFrame); highlightFrame = 0; highlightTarget = null;
    pointer.targetX = -1000; pointer.targetY = -1000;
    highlighted?.classList.remove('is-glass-hovered'); highlighted = null;
  });
  const scroll = () => header?.classList.toggle('is-scrolled', scrollY > 24);
  addEventListener('scroll', scroll, { passive: true });
  addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(resize, 120); }, { passive: true });
  document.addEventListener('visibilitychange', synchronizeMotion);
  document.addEventListener('twinsync:panel-close', () => {
    cancelAnimationFrame(highlightFrame); highlightFrame = 0;
    clearTimeout(resizeTimer); clearTimeout(refreshTimer);
    highlighted?.classList.remove('is-glass-hovered'); highlighted = null;
    synchronizeMotion();
  });
  reducedMotion.addEventListener('change', () => {
    highlighted?.classList.remove('is-glass-hovered'); highlighted = null;
    pointer.x = pointer.targetX = -1000; pointer.y = pointer.targetY = -1000;
    resize();
  });
  new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; synchronizeMotion(); }).observe(layer);
  addEventListener('pagehide', () => { cancelAnimationFrame(animation); animation = 0; cancelAnimationFrame(highlightFrame); highlightFrame = 0; });
  addEventListener('pageshow', synchronizeMotion);
  // Read-only diagnostics for local QA; nothing is persisted or transmitted.
  window.TwinSyncEffects = Object.freeze({ getState: () => ({ frames, particles: particles.length, running: Boolean(animation), reducedMotion: reducedMotion.matches, visible, dark, lenses: lenses.size, refractionSupported, width, height, targetFps: 30, renderer: 'sprite-atlas', cachedLensMaps: lensMaps.size }) });
  scroll(); resize(); refreshGlass();
})();
