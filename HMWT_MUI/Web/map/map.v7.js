/* HMWT: Р С”Р В°РЎР‚РЎвЂљР В° Р Р…Р В° PixiJS/WebGL.
   C# Р С•РЎРѓРЎвЂљР В°РЎвЂРЎвЂљРЎРѓРЎРЏ РЎвЂ¦Р С•Р В·РЎРЏР С‘Р Р…Р С•Р С РЎРѓРЎвЂ Р ВµР Р…РЎвЂ№, РЎвЂљР С•Р С”Р ВµР Р…Р С•Р Р†, РЎвЂљРЎС“Р СР В°Р Р…Р В° Р С‘ РЎРѓР ВµРЎвЂљР С‘; РЎРѓРЎР‹Р Т‘Р В° Р С—РЎР‚Р С‘РЎвЂ¦Р С•Р Т‘Р С‘РЎвЂљ Р С–Р С•РЎвЂљР С•Р Р†Р С•Р Вµ
   РЎРѓР С•РЎРѓРЎвЂљР С•РЎРЏР Р…Р С‘Р Вµ Р С‘ Р С•РЎвЂљРЎРѓРЎР‹Р Т‘Р В° РЎС“РЎвЂ¦Р С•Р Т‘РЎРЏРЎвЂљ РЎвЂљР С•Р В»РЎРЉР С”Р С• Р С”Р С•Р СР В°Р Р…Р Т‘РЎвЂ№ Р С—Р ВµРЎР‚Р ВµР СР ВµРЎвЂ°Р ВµР Р…Р С‘РЎРЏ Р В¶Р ВµРЎвЂљР С•Р Р…Р В°. */
(() => {
  function showError(message) {
    let el = document.getElementById('js-error');
    if (!el) {
      el = document.createElement('div');
      el.id = 'js-error';
      el.style.cssText = 'position:absolute;left:12px;top:12px;z-index:99;color:#ff9b9b;' +
        'font:12px monospace;background:#1a0d12;padding:8px 10px;border:1px solid #6b2b36;' +
        'max-width:90%;white-space:pre-wrap';
      document.body.appendChild(el);
    }
    el.textContent = String(message || 'unknown error');
  }
  window.addEventListener('error', e => showError(e.message + '\n' + (e.filename || '') + ':' + (e.lineno || '')));
  window.addEventListener('unhandledrejection', e => showError(e.reason && e.reason.stack || e.reason));

  const app = new PIXI.Application({
    resizeTo: window,
    backgroundColor: 0x0b0d16,
    antialias: true,
    autoDensity: true,
    resolution: window.devicePixelRatio || 1
  });
  document.getElementById('stage').appendChild(app.view);
  app.stage.eventMode = 'static';
  app.stage.hitArea = app.screen;

  let state = null;
  let mapW = 1, mapH = 1;
  let hasFit = false;
  let dragging = null;
  let panning = null;
  let fogSeen = [];
  let fogKey = '';
  let mapImageHash = '';

  const world = new PIXI.Container();
  world.sortableChildren = true;
  app.stage.addChild(world);

  const mapLayer = new PIXI.Container();
  const gridLayer = new PIXI.Container();
  const wallLayer = new PIXI.Container();
  const shapeLayer = new PIXI.Container();
  const trailLayer = new PIXI.Container();
  const fogLayer = new PIXI.Container();
  const tokenLayer = new PIXI.Container();
  tokenLayer.sortableChildren = true;
  world.addChild(mapLayer, gridLayer, wallLayer, shapeLayer, trailLayer, fogLayer, tokenLayer);

  const trailG = new PIXI.Graphics();
  trailLayer.addChild(trailG);

  const gridG = new PIXI.Graphics();
  gridLayer.addChild(gridG);
  const wallG = new PIXI.Graphics();
  wallLayer.addChild(wallG);
  const fogCanvas = document.createElement('canvas');
  fogCanvas.width = 1024;
  fogCanvas.height = 1024;
  const fogCtx = fogCanvas.getContext('2d');
  const fogTexture = PIXI.Texture.from(fogCanvas);
  const fogSprite = new PIXI.Sprite(fogTexture);
  fogLayer.addChild(fogSprite);

  const tokenNodes = new Map();
  const textureCache = new Map();

  function rgba(hex, fallback) {
    if (!hex) return fallback;
    let s = String(hex).replace('#', '');
    if (s.length === 8) {
      const a = parseInt(s.slice(0, 2), 16) / 255;
      const r = parseInt(s.slice(2, 4), 16);
      const g = parseInt(s.slice(4, 6), 16);
      const b = parseInt(s.slice(6, 8), 16);
      return { color: (r << 16) | (g << 8) | b, alpha: a || 0 };
    }
    if (s.length === 6) {
      return { color: parseInt(s, 16), alpha: 1 };
    }
    return fallback;
  }

  function fitSprite(sprite, w, h) {
    const apply = () => {
      if (!sprite || sprite.destroyed) return;
      sprite.width = w;
      sprite.height = h;
    };
    apply();
    const tex = sprite.texture;
    if (tex && tex.baseTexture) {
      if (tex.baseTexture.valid) apply();
      else {
        tex.baseTexture.once('loaded', apply);
        tex.baseTexture.once('update', apply);
      }
    }
  }

  function texture(src) {
    if (!src) return null;
    if (textureCache.has(src)) return textureCache.get(src);
    let t = null;
    try { t = PIXI.Texture.from(src); } catch (e) { t = null; }
    textureCache.set(src, t);
    return t;
  }

  function applyScene(data) {
    const oldW = mapW, oldH = mapH;
    state = data;
    mapW = Math.max(1, data.width || 1);
    mapH = Math.max(1, data.height || 1);

    if (data.image !== null && data.image !== undefined) {
      if (!data.image) {
        mapImageHash = data.imageHash || '';
        mapLayer.removeChildren().forEach(c => c.destroy({ children: true }));
      } else if (data.imageHash !== mapImageHash) {
        mapImageHash = data.imageHash || '';
        mapLayer.removeChildren().forEach(c => c.destroy({ children: true }));
        const t = texture(data.image);
        if (t) {
          const sprite = new PIXI.Sprite(t);
          fitSprite(sprite, mapW, mapH);
          mapLayer.addChild(sprite);
        }
      }
    }

    buildGrid(data);
    buildWalls(data);
    buildShapes(data);
    buildTrails(data);
    buildTokens(data);
    buildFog(data);

    if (!hasFit || oldW !== mapW || oldH !== mapH) {
      fit();
      hasFit = true;
      // На первом кадре app.screen иногда ещё нулевой — повторяем после
      // раскладки, иначе карта уезжает в угол и выглядит чёрной.
      requestAnimationFrame(fit);
      setTimeout(fit, 120);
    }
    const hint = document.getElementById('hint');
    if (hint) hint.textContent = 'ЛКМ — жетон / панорама · колесо — масштаб';
  }

  function buildGrid(data) {
    gridG.clear();
    const g = data.grid || {};
    if (!g.on) return;
    const step = g.px > 4 ? g.px : 70;
    const stroke = rgba(g.color, { color: 0xffffff, alpha: 0.2 });
    const thickness = Math.max(0.5, g.thickness || 1);
    gridG.lineStyle(thickness, stroke.color, Math.max(0.04, stroke.alpha));
    const ox = ((g.offsetX || 0) % step + step) % step;
    const oy = ((g.offsetY || 0) % step + step) % step;
    for (let x = ox; x <= mapW; x += step) {
      gridG.moveTo(x, 0);
      gridG.lineTo(x, mapH);
    }
    for (let y = oy; y <= mapH; y += step) {
      gridG.moveTo(0, y);
      gridG.lineTo(mapW, y);
    }
  }

  function buildWalls(data) {
    wallG.clear();
    const walls = data.walls || [];
    for (const w of walls) {
      if (w.isOpen) continue;
      const door = w.kind === 'door';
      wallG.lineStyle(door ? 4 : 5, door ? 0xcfa15a : 0x8891aa, 0.95);
      wallG.moveTo(w.x1, w.y1);
      wallG.lineTo(w.x2, w.y2);
    }
  }

  function buildShapes(data) {
    shapeLayer.removeChildren().forEach(c => c.destroy({ children: true }));
    const cell = data.grid && data.grid.px > 4 ? data.grid.px : 70;
    for (const s of (data.shapes || [])) {
      const col = rgba(s.color, { color: 0xb98bff, alpha: 1 });
      const radius = Math.max(cell * 0.5, (s.radius || 0) * cell);
      const g = new PIXI.Graphics();
      g.lineStyle(3, col.color, 0.9);
      g.beginFill(col.color, 0.16);
      if (s.kind === 'square') {
        g.drawRoundedRect(s.x - radius, s.y - radius, radius * 2, radius * 2, 6);
      } else if (s.kind === 'cone') {
        const a = (s.angle || 0) * Math.PI / 180;
        g.moveTo(s.x, s.y);
        g.arc(s.x, s.y, radius, a - Math.PI / 4, a + Math.PI / 4);
        g.lineTo(s.x, s.y);
      } else if (s.kind === 'line') {
        const a = (s.angle || 0) * Math.PI / 180;
        g.moveTo(s.x, s.y);
        g.lineTo(s.x + Math.cos(a) * radius, s.y + Math.sin(a) * radius);
      } else {
        g.drawCircle(s.x, s.y, radius);
      }
      g.endFill();
      shapeLayer.addChild(g);
    }
  }

  function makeTokenNode(t) {
    const node = new PIXI.Container();
    node.eventMode = 'none';
    const halo = new PIXI.Graphics();
    const photo = new PIXI.Sprite();
    const tokenMask = new PIXI.Graphics();
    const frame = new PIXI.Graphics();
    const label = new PIXI.Text(t.label || '', {
      fontFamily: 'Palatino Linotype, Georgia, Segoe UI',
      fontSize: 15,
      fill: 0xf1f4ff,
      stroke: 0x090c16,
      strokeThickness: 3,
      align: 'center'
    });
    label.anchor.set(0.5, 0);
    node.addChild(halo, photo, tokenMask, frame, label);
    photo.mask = tokenMask;
    node._halo = halo;
    node._photo = photo;
    node._tokenMask = tokenMask;
    node._frame = frame;
    node._label = label;
    tokenLayer.addChild(node);
    return node;
  }

  function buildTrails(data) {
    trailG.clear();
    const col = rgba(data.trailColor, { color: 0x9b8bff, alpha: 0.85 });
    const thickness = Math.max(3, (data.grid && data.grid.px ? data.grid.px : 70) * 0.08);
    const all = (data.trails || []).map(t => ({ id: t.id, points: t.points || [] }));
    if (dragging && dragging.path && dragging.path.length > 1)
      all.push({ id: dragging.id, points: dragging.path });

    for (const trail of all) {
      if (!trail.points || trail.points.length < 2) continue;
      trailG.lineStyle(thickness, col.color, 0.8);
      trailG.moveTo(trail.points[0].x, trail.points[0].y);
      for (let i = 1; i < trail.points.length; i++)
        trailG.lineTo(trail.points[i].x, trail.points[i].y);
    }
  }

  function trailMeasure(points) {
    const cell = state && state.grid && state.grid.px > 4 ? state.grid.px : 70;
    let pixels = 0;
    for (let i = 1; i < points.length; i++) {
      const dx = points[i].x - points[i - 1].x;
      const dy = points[i].y - points[i - 1].y;
      pixels += Math.sqrt(dx * dx + dy * dy);
    }
    const cells = pixels / Math.max(1, cell);
    const feet = cells * 5;
    const meters = cells * 1.5;
    const el = document.getElementById('measure');
    if (el) {
      el.style.display = 'block';
      el.textContent = cells.toFixed(1) + ' кл · ' + feet.toFixed(0) + ' фт · ' +
        meters.toFixed(1) + ' м';
    }
  }

  function hideMeasureLater() {
    setTimeout(() => {
      const el = document.getElementById('measure');
      if (el) el.style.display = 'none';
    }, 3500);
  }

  function buildTokens(data) {
    const cell = data.grid && data.grid.px > 4 ? data.grid.px : 70;
    const alive = new Set();
    for (const t of (data.tokens || [])) {
      alive.add(t.id);
      let node = tokenNodes.get(t.id);
      if (!node) {
        node = makeTokenNode(t);
        tokenNodes.set(t.id, node);
      }
      if (dragging && dragging.id === t.id) continue;
      const size = Math.max(24, cell * (t.sizeCells || 1));
      node.position.set(t.x, t.y);
      node.zIndex = t.isPlayer ? 1000 : 100;
      node._size = size;
      node._token = t;

      node._halo.clear();
      node._halo.beginFill(t.color ? rgba(t.color, { color: 0x28324a }).color : 0x28324a, 0.9);
      node._halo.drawCircle(0, 0, size / 2 + 3);
      node._halo.endFill();

      if (t.photo !== null && t.photo !== undefined) {
        node._photo.visible = false;
        const tex = t.photo ? texture(t.photo) : null;
        if (tex) {
          node._photo.texture = tex;
          node._photo.visible = true;
          fitSprite(node._photo, size, size);
          node._photo.anchor.set(0.5);
        }
      }

      // Размер аватарки обновляем на каждом кадре, даже если сама картинка
      // не менялась: иначе при смене размера клетки растёт кружок, а фото
      // остаётся старого размера.
      if (node._photo && node._photo.visible) {
        fitSprite(node._photo, size, size);
        node._tokenMask.clear();
        node._tokenMask.beginFill(0xffffff);
        node._tokenMask.drawCircle(0, 0, size / 2);
        node._tokenMask.endFill();
      }

      node._frame.clear();
      node._frame.lineStyle(t.selected ? 4 : 3,
        t.selected ? 0xffd27a : (t.isPlayer ? 0x5fd4dd : 0x0b0d16), 1);
      node._frame.drawCircle(0, 0, size / 2);
      node._label.y = size / 2 + 4;
      node._label.text = t.label || '';
      node.alpha = t.hidden ? 0.4 : 1;
    }

    for (const [id, node] of [...tokenNodes]) {
      if (alive.has(id)) continue;
      tokenNodes.delete(id);
      node.destroy({ children: true });
    }
  }

  function parsePolygon(text) {
    const pts = [];
    for (const pair of String(text || '').split(';')) {
      const p = pair.split(',');
      if (p.length < 2) continue;
      const x = parseFloat(p[0]), y = parseFloat(p[1]);
      if (Number.isFinite(x) && Number.isFinite(y)) pts.push([x, y]);
    }
    return pts;
  }

  function buildFog(data) {
    if (Array.isArray(data.seen)) fogSeen = data.seen;
    const fog = data.fog || {};
    fogSprite.visible = !!fog.on;
    if (!fog.on) return;

    const key = [data.isDm ? 'dm' : 'player', fogSeen.length, mapW, mapH,
      fogSeen.length ? fogSeen[fogSeen.length - 1] : ''].join('|');
    if (key === fogKey) return;
    fogKey = key;

    const max = 2048;
    const scale = Math.min(1, max / Math.max(mapW, mapH));
    const w = Math.max(1, Math.round(mapW * scale));
    const h = Math.max(1, Math.round(mapH * scale));
    if (fogCanvas.width !== w || fogCanvas.height !== h) {
      fogCanvas.width = w;
      fogCanvas.height = h;
    }

    fogCtx.setTransform(1, 0, 0, 1, 0, 0);
    fogCtx.globalCompositeOperation = 'source-over';
    fogCtx.clearRect(0, 0, w, h);
    fogCtx.fillStyle = data.isDm ? 'rgba(5,7,15,0.82)' : 'rgba(5,7,15,0.98)';
    fogCtx.fillRect(0, 0, w, h);

    fogCtx.globalCompositeOperation = 'destination-out';
    fogCtx.fillStyle = '#fff';
    fogCtx.beginPath();
    for (const poly of fogSeen) {
      const pts = parsePolygon(poly);
      if (pts.length < 3) continue;
      fogCtx.moveTo(pts[0][0] * scale, pts[0][1] * scale);
      for (let i = 1; i < pts.length; i++)
        fogCtx.lineTo(pts[i][0] * scale, pts[i][1] * scale);
      fogCtx.closePath();
    }
    fogCtx.fill();

    fogSprite.width = mapW;
    fogSprite.height = mapH;
    fogTexture.update();
  }

  function fit() {
    const pad = 24;
    const host = document.getElementById('stage');
    let w = (app.view && app.view.clientWidth) || (host && host.clientWidth) ||
      document.documentElement.clientWidth || window.innerWidth || 0;
    let h = (app.view && app.view.clientHeight) || (host && host.clientHeight) ||
      document.documentElement.clientHeight || window.innerHeight || 0;

    // WebView2 на первом кадре может отдать 0x0. Берём разумный размер,
    // иначе карта масштабируется в 0.03 и жмётся в левый верхний угол.
    if (w < 20 || h < 20) { w = 1200; h = 800; }
    const sx = (w - pad * 2) / mapW;
    const sy = (h - pad * 2) / mapH;
    const s = Math.max(0.02, Math.min(sx, sy));
    world.scale.set(s);
    world.position.set((w - mapW * s) / 2, (h - mapH * s) / 2);
  }

  function screenPoint(e) {
    const r = app.view.getBoundingClientRect();
    return new PIXI.Point(e.clientX - r.left, e.clientY - r.top);
  }

  function hitToken(p) {
    const local = world.toLocal(p);
    let found = null;
    for (const [id, node] of tokenNodes) {
      const t = node._token;
      if (!t || t.canMove === false) continue;
      const size = node._size || 30;
      if (Math.abs(local.x - t.x) <= size / 2 && Math.abs(local.y - t.y) <= size / 2)
        found = { id, node, t };
    }
    return found;
  }

  app.view.addEventListener('pointerdown', e => {
    if (e.button !== 0) return;
    const p = screenPoint(e);
    const hit = hitToken(p);
    if (hit) {
      dragging = {
        id: hit.id, node: hit.node, dx: 0, dy: 0,
        path: [{ x: hit.t.x, y: hit.t.y }]
      };
      panning = null;
      app.view.setPointerCapture(e.pointerId);
      e.preventDefault();
      return;
    }
    panning = { x: p.x, y: p.y, wx: world.x, wy: world.y };
    app.view.setPointerCapture(e.pointerId);
    e.preventDefault();
  });

  app.view.addEventListener('pointermove', e => {
    const p = screenPoint(e);
    if (dragging) {
      const local = world.toLocal(p);
      const t = dragging.node._token;
      if (t) {
        t.x = local.x;
        t.y = local.y;
        dragging.node.position.set(local.x, local.y);
        const last = dragging.path[dragging.path.length - 1];
        if (!last || Math.hypot(last.x - local.x, last.y - local.y) > 4) {
          dragging.path.push({ x: local.x, y: local.y });
          buildTrails(state || {});
          trailMeasure(dragging.path);
        }
      }
      return;
    }
    if (panning) {
      world.position.set(panning.wx + (p.x - panning.x),
        panning.wy + (p.y - panning.y));
    }
  });

  function endPointer(e) {
    if (dragging) {
      const t = dragging.node._token;
      if (t && window.chrome && window.chrome.webview) {
        if (!dragging.path || dragging.path.length < 2)
          dragging.path = [{ x: t.x, y: t.y }];
        trailMeasure(dragging.path);
        hideMeasureLater();
        window.chrome.webview.postMessage({
          type: 'moveToken', id: t.id, x: t.x, y: t.y,
          points: dragging.path
        });
      }
      dragging = null;
    }
    panning = null;
    try { app.view.releasePointerCapture(e.pointerId); } catch (_) { }
  }
  app.view.addEventListener('pointerup', endPointer);
  app.view.addEventListener('pointercancel', endPointer);

  app.view.addEventListener('wheel', e => {
    const p = screenPoint(e);
    const old = world.scale.x;
    const next = Math.max(0.03, Math.min(8, old * (e.deltaY > 0 ? 1 / 1.12 : 1.12)));
    if (Math.abs(next - old) < 0.0001) return;
    const local = world.toLocal(p);
    world.scale.set(next);
    const after = world.toGlobal(local);
    world.position.set(world.position.x + (p.x - after.x),
      world.position.y + (p.y - after.y));
    e.preventDefault();
  }, { passive: false });

  window.addEventListener('contextmenu', e => e.preventDefault());
  window.addEventListener('resize', () => {
    try { app.renderer.resize(window.innerWidth, window.innerHeight); } catch (_) { }
    if (hasFit) fit();
  });
  setTimeout(() => {
    try { app.renderer.resize(window.innerWidth, window.innerHeight); } catch (_) { }
    if (hasFit) fit();
  }, 50);

  if (window.ResizeObserver) {
    new ResizeObserver(() => { if (hasFit) fit(); })
      .observe(document.getElementById('stage'));
  }

  window.__hmwtApply = payload => {
    let data = payload;
    if (typeof data === 'string') {
      try { data = JSON.parse(data); } catch (_) { return; }
    }
    if (!data || data.type !== 'scene') return;
    try { applyScene(data); } catch (err) { showError(err && err.stack || err); }
  };

  window.__hmwtToast = (text, error) => {
    let el = document.getElementById('toast');
    if (!el) return;
    el.textContent = text || '';
    el.style.color = error ? '#ff9b9b' : '#9ff0d0';
    el.style.borderColor = error ? '#e06a63aa' : '#5fd4ddaa';
    el.style.display = 'block';
    clearTimeout(window.__hmwtToastTimer);
    window.__hmwtToastTimer = setTimeout(() => { el.style.display = 'none'; }, 3200);
  };

  window.chrome.webview.addEventListener('message', e => {
    window.__hmwtApply(e.data);
  });

  window.chrome.webview.postMessage({ type: 'ready' });
})();






