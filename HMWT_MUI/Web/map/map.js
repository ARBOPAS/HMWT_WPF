/* HMWT: карта на PixiJS/WebGL.
   C# остаётся хозяином сцены, жетонов, тумана и сети; сюда приходим только
   рисовать и отдавать ему то, что сделал игрок мышью: перетаскивание жетона
   и фигуры, штрихи кисти, стены и двери, пункты меню жетона. */
(() => {
  'use strict';

  // ─────────────────────────── мост в C# ───────────────────────────

  function post(message) {
    try {
      if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
    } catch (_) { }
  }

  let logBudget = 0;
  function log(text) {
    logBudget++;
    if (logBudget > 2000) return;
    post({ type: 'jsLog', text: String(text) });
  }

  function showError(message) {
    let el = document.getElementById('js-error');
    if (!el) {
      el = document.createElement('div');
      el.id = 'js-error';
      el.style.cssText = 'position:absolute;left:12px;top:12px;z-index:99;color:#ff9b9b;' +
        'font:12px monospace;background:#1a0d12;padding:8px 10px;border:1px solid #6b2b36;' +
        'max-width:90%;white-space:pre-wrap;pointer-events:none';
      document.body.appendChild(el);
    }
    el.textContent = String(message || 'unknown error');
    post({ type: 'jsError', text: String(message || 'unknown error') });
  }

  window.addEventListener('error', e =>
    showError((e.message || '') + '\n' + (e.filename || '') + ':' + (e.lineno || '')));
  window.addEventListener('unhandledrejection', e =>
    showError(e.reason && e.reason.stack || e.reason));

  // ─────────────────────────── Pixi ───────────────────────────

  const app = new PIXI.Application({
    resizeTo: window,
    backgroundColor: 0x0b0d16,
    antialias: true,
    autoDensity: true,
    resolution: window.devicePixelRatio || 1
  });
  document.getElementById('stage').appendChild(app.view);

  const world = new PIXI.Container();
  world.sortableChildren = true;
  app.stage.addChild(world);

  // Вся картинка сцены (карта, сетка, стены, туман, жетоны) живёт в одном
  // контейнере: поворот и отражение применяются к нему целиком, поэтому
  // картинка, сетка и туман не могут разъехаться.
  const sceneRoot = new PIXI.Container();
  sceneRoot.sortableChildren = true;
  world.addChild(sceneRoot);

  const mapLayer = new PIXI.Container();
  const gridLayer = new PIXI.Container();
  const wallLayer = new PIXI.Container();
  const shapeLayer = new PIXI.Container();
  const strokeLayer = new PIXI.Container();
  const trailLayer = new PIXI.Container();
  const fogLayer = new PIXI.Container();
  const fovLayer = new PIXI.Container();
  const tokenLayer = new PIXI.Container();
  tokenLayer.sortableChildren = true;
  // Штрихи кисти — поверх тумана: рисунок, сделанный ведущим, должен быть
  // виден и там, где карта ещё закрыта, иначе «кисть не работает».
  sceneRoot.addChild(mapLayer, gridLayer, wallLayer, shapeLayer,
    trailLayer, fogLayer, strokeLayer, fovLayer, tokenLayer);

  // Подсказка-линейка и меню рисуются поверх карты, без масштаба.
  const hud = new PIXI.Container();
  app.stage.addChild(hud);
  const rulerG = new PIXI.Graphics();
  hud.addChild(rulerG);

  const gridG = new PIXI.Graphics();
  gridLayer.addChild(gridG);
  const wallG = new PIXI.Graphics();
  wallLayer.addChild(wallG);
  const shapeG = new PIXI.Graphics();
  shapeLayer.addChild(shapeG);
  const strokeG = new PIXI.Graphics();
  strokeLayer.addChild(strokeG);
  const previewG = new PIXI.Graphics();
  strokeLayer.addChild(previewG);
  const trailG = new PIXI.Graphics();
  trailLayer.addChild(trailG);
  const fovG = new PIXI.Graphics();
  fovLayer.addChild(fovG);

  // Пинг — расходящееся кольцо в координатах картинки (WPF-слой лежит
  // под WebView, поэтому своя копия нужна и здесь).
  const pingG = new PIXI.Graphics();
  fovLayer.addChild(pingG);
  const pings = [];
  let pingActive = false;

  function addPing(x, y) {
    pings.push({ x: x, y: y, t0: performance.now() });
    pingActive = true;
  }

  // Пинг держится ~3 секунды и расходится тремя волнами, как круги на воде.
  const PING_MS = 3200;
  const PING_WAVES = 3;

  app.ticker.add(() => {
    if (!pingActive) return;
    const now = performance.now();
    for (let i = pings.length - 1; i >= 0; i--)
      if (now - pings[i].t0 > PING_MS + 500) pings.splice(i, 1);

    pingG.clear();
    for (const p of pings) {
      const k = Math.min(1, (now - p.t0) / PING_MS);
      for (let w = 0; w < PING_WAVES; w++) {
        const kw = k - w * 0.18;
        if (kw <= 0 || kw >= 1) continue;
        pingG.lineStyle(2 + 3 * (1 - kw), 0x5fd4dd, 0.85 * (1 - kw));
        pingG.drawCircle(p.x, p.y, cellPx() * (0.4 + 6.5 * kw));
      }
    }
    if (pings.length === 0) pingActive = false;
  });

  const tokenNodes = new Map();
  const textureCache = new Map();

  // ─────────────────────────── состояние ───────────────────────────

  let state = null;
  let mapW = 1, mapH = 1;
  let mapRot = 0, mapFlipH = false, mapFlipV = false;
  let boundsW = 1, boundsH = 1;
  let hasFit = false;
  // Автоподгонка должна срабатывать ровно один раз на сцену и на новую
  // картинку. Раньше здесь был только хэш картинки, но при снимке без
  // адреса картинки («ещё не скачана») хэш сбрасывался в пустую строку, и
  // следующий снимок снова считался «новой картой» — отсюда прыжок
  // масштаба у клиента при любом изменении сцены.
  let lastFitHash = "";
  let lastFitSceneId = -1;
  let lastViewSize = { w: 0, h: 0 };
  let isDm = false;
  let cell = 70;
  let fovTokens = [];
  let fogSeen = [];
  let fogKey = '';
  let mapImageUrl = '';
  let mapImageHash = '';
  let mapSprite = null;
  let needImageAt = 0;
  let selectedTokenId = 0;

  let drag = null;
  let lastFogAt = 0;
  let lastLiveAt = 0;
  let viewLogs = 0;
  let sceneCount = 0;

  // Угол обзора приходит из C# ~30 раз в секунду, а кадров больше. Показываем
  // плавно доводимый угол, иначе конус двигается рывками.
  function shownFacing(t) {
    return t && t._shown !== undefined ? t._shown : ((t && t.facing) || 0);
  }

  /** Новый набор областей обзора: сохраняем плавный угол и скорость по id. */
  function setFovTokens(list) {
    const now = performance.now();
    const prev = new Map(fovTokens.map(f => [f.id, f]));

    for (const t of list) {
      const old = prev.get(t.id);
      t._shown = old && old._shown !== undefined ? old._shown : (t.facing || 0);
      t._target = t.facing || 0;
      t._msgAt = now;

      if (old && old._target !== undefined) {
        // Скорость поворота в градусах на миллисекунду: по ней WebView
        // продолжает движение, если следующий пакет задержался.
        const d = ((t._target - old._target + 540) % 360) - 180;
        const dt = Math.max(16, now - (old._msgAt || now));
        const v = d / dt;
        t._vel = Math.abs(v) > 0.4 ? 0 : (old._vel || 0) * 0.5 + v * 0.5;
      } else {
        t._vel = 0;
      }
    }

    fovTokens = list;
  }

  /**
   * Поворот в координатах картинки. На экране E — по часовой, Q — против,
   * но у отражённой карты направление зеркалится.
   */
  function screenTurn(delta) {
    return (mapFlipH !== mapFlipV) ? -delta : delta;
  }

  const tools = {
    shape: null, shapeRadius: 0, ping: false, measure: false,
    brush: false, eraser: false, wall: false,
    brushColor: '#FFE06A63', brushThickness: 4
  };

  const FRAME_COLORS = [
    { name: 'Золотая', hex: '#FFFFD479' },
    { name: 'Красная', hex: '#FFE06A63' },
    { name: 'Голубая', hex: '#FF74A9FF' },
    { name: 'Зелёная', hex: '#FF66D19A' },
    { name: 'Фиолетовая', hex: '#FFB98BFF' },
    { name: 'Белая', hex: '#FFF2F5FF' },
    { name: 'Серая', hex: '#FF9AA4BF' }
  ];

  const SHAPE_COLORS = [
    { name: 'Фиолетовый', hex: '#FFB98BFF' },
    { name: 'Красный', hex: '#FFE06A63' },
    { name: 'Зелёный', hex: '#FF7FD98A' },
    { name: 'Синий', hex: '#FF6FA8FF' },
    { name: 'Золотой', hex: '#FFE8C07F' },
    { name: 'Серый', hex: '#FF9AA4BF' }
  ];

  // ─────────────────────────── помощники ───────────────────────────

  function rgba(hex, fallback) {
    if (!hex) return fallback;
    let s = String(hex).replace('#', '');
    if (s.length === 8) {
      const a = parseInt(s.slice(0, 2), 16) / 255;
      const r = parseInt(s.slice(2, 4), 16);
      const g = parseInt(s.slice(4, 6), 16);
      const b = parseInt(s.slice(6, 8), 16);
      if (!Number.isFinite(r) || !Number.isFinite(g) || !Number.isFinite(b)) return fallback;
      return { color: (r << 16) | (g << 8) | b, alpha: Number.isFinite(a) ? a : 1 };
    }
    if (s.length === 6) {
      const value = parseInt(s, 16);
      if (!Number.isFinite(value)) return fallback;
      return { color: value, alpha: 1 };
    }
    return fallback;
  }

  function colorCss(hex) {
    const c = rgba(hex, { color: 0x9aa4bf, alpha: 1 });
    return '#' + c.color.toString(16).padStart(6, '0');
  }

  function sameColor(a, b) {
    if (!a && !b) return true;
    if (!a || !b) return false;
    return colorCss(a) === colorCss(b);
  }

  function cellPx() {
    return cell > 4 ? cell : 70;
  }

  function texture(src) {
    if (!src) return null;

    // Уничтоженная текстура в кэше — причина падения «Cannot read properties
    // of null (reading 'valid')» при смене сцены: спрайту отдавали битую.
    const cached = textureCache.get(src);
    if (cached && !cached.destroyed && cached.baseTexture) return cached;
    if (cached) textureCache.delete(src);

    let t = null;
    try { t = PIXI.Texture.from(src); } catch (e) { t = null; }
    if (t && t.baseTexture) textureCache.set(src, t);
    return t;
  }

  function fitSprite(sprite, w, h) {
    const apply = () => {
      if (!sprite || sprite.destroyed) return;
      const tex = sprite.texture;
      if (tex && tex.baseTexture && !tex.baseTexture.valid) return;
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

  function parsePolygon(text) {
    const pts = [];
    for (const pair of String(text || '').split(';')) {
      const p = pair.split(',');
      if (p.length < 2) continue;
      const x = parseFloat(p[0]), y = parseFloat(p[1]);
      if (Number.isFinite(x) && Number.isFinite(y)) pts.push(x, y);
    }
    return pts;
  }

  // Разбор разведанных полигонов кэшируется: при перетаскивании туман
  // пересобирается несколько раз в секунду, а секторов бывает сотни.
  const polyCache = new Map();

  function polygon(text) {
    let pts = polyCache.get(text);
    if (pts) return pts;
    pts = parsePolygon(text);
    if (polyCache.size > 600) polyCache.clear();
    polyCache.set(text, pts);
    return pts;
  }

  // ─────────────────────────── карта-картинка ───────────────────────────

  function applyMapSpriteSize() {
    if (!mapSprite || mapSprite.destroyed) return;
    const tex = mapSprite.texture;
    if (tex && tex.baseTexture && !tex.baseTexture.valid) return;
    mapSprite.width = mapW;
    mapSprite.height = mapH;
  }

  // ── анимированная GIF-карта ──
  // Chromium умеет декодировать GIF покадрово (ImageDecoder), поэтому
  // анимация идёт из памяти: файл скачивается один раз, по сети кадры
  // не гоняются. Для больших карт (150 МБ) это важно.
  let gifDecoder = null;
  let gifTimer = 0;
  let gifUrl = '';

  function stopMapGif() {
    if (gifTimer) { clearTimeout(gifTimer); gifTimer = 0; }
    if (gifDecoder) {
      try { gifDecoder.close(); } catch (_) { }
      gifDecoder = null;
    }
    gifUrl = '';
  }

  async function playMapGif(url) {
    if (!('ImageDecoder' in window)) return false;
    if (gifUrl === url) return true;
    stopMapGif();

    try {
      const response = await fetch(url);
      if (!response.ok) return false;
      const data = await response.arrayBuffer();
      const decoder = new ImageDecoder({ data: data, type: 'image/gif' });
      await decoder.tracks.ready;
      const track = decoder.tracks.selectedTrack;
      if (!track || track.frameCount <= 1) {
        try { decoder.close(); } catch (_) { }
        return false;
      }

      gifDecoder = decoder;
      gifUrl = url;
      log('gif map: frames=' + track.frameCount);

      const showFrame = async index => {
        if (!gifDecoder || gifUrl !== url) return;
        try {
          const { image } = await gifDecoder.decode({ frameIndex: index });
          const canvas = document.createElement('canvas');
          canvas.width = image.displayWidth;
          canvas.height = image.displayHeight;
          canvas.getContext('2d').drawImage(image, 0, 0);

          const duration = Math.max(40, (image.duration || 100000) / 1000);
          image.close();

          if (!mapSprite || mapSprite.destroyed) return;
          const old = mapSprite.texture;
          mapSprite.texture = PIXI.Texture.from(canvas);
          applyMapSpriteSize();
          if (old && old !== mapSprite.texture) {
            try { old.destroy(true); } catch (_) { }
          }

          const next = (index + 1) % track.frameCount;
          gifTimer = setTimeout(() => showFrame(next), duration);
        } catch (err) {
          log('gif frame error: ' + err);
          stopMapGif();
        }
      };

      showFrame(0);
      return true;
    } catch (err) {
      log('gif decode error: ' + err);
      stopMapGif();
      return false;
    }
  }

  function ensureMapSprite(url, hash) {
    // Один и тот же адрес — не пересоздаём спрайт на каждом кадре.
    if (mapSprite && url && url === mapImageUrl) { applyMapSpriteSize(); return; }

    // Пустой адрес при непустом хэше — это «картинка ещё не скачана», а не
    // «карты нет»: старый спрайт и хэш трогать нельзя, иначе следующий
    // снимок выглядит как смена карты и сбрасывает масштаб.
    if (!url && hash) { if (!mapSprite) requestImage(); return; }

    mapImageUrl = url || '';
    mapImageHash = hash || '';

    if (mapSprite) {
      try { mapLayer.removeChild(mapSprite); } catch (_) { }
      try { mapSprite.destroy({ texture: false }); } catch (_) { }
      mapSprite = null;
    }

    if (!url) return;

    const tex = texture(url);
    if (!tex || !tex.baseTexture) return;

    try {
      mapSprite = new PIXI.Sprite(tex);
    } catch (err) {
      log('map sprite error: ' + err);
      mapSprite = null;
      return;
    }
    mapSprite.anchor.set(0, 0);
    mapSprite.position.set(0, 0);
    mapLayer.addChild(mapSprite);
    applyMapSpriteSize();

    const base = tex.baseTexture;
    if (base) {
      base.on('loaded', applyMapSpriteSize);
      base.on('update', applyMapSpriteSize);
      base.on('error', () => log('map image error: ' + url));
    }

    // Если карта — анимированный GIF, включаем покадровое воспроизведение
    if (/\.gif(\?|$)/i.test(url)) {
      playMapGif(url).then(ok => { if (!ok) log('gif map: статичный первый кадр'); });
    } else {
      stopMapGif();
    }
  }

  // Текстура грузится асинхронно: пока размеры неизвестны, width ставить
  // нельзя (получится бесконечный масштаб). Досчитываем в тикере.
  app.ticker.add(() => {
    if (!mapSprite || mapSprite.destroyed) return;
    const tex = mapSprite.texture;
    if (!tex || !tex.baseTexture || !tex.baseTexture.valid) return;
    if (Math.abs(mapSprite.width - mapW) > 0.5 || Math.abs(mapSprite.height - mapH) > 0.5)
      applyMapSpriteSize();
  });

  // ─────────────────────────── сцена ───────────────────────────

  function applyOrientation() {
    const rot90 = mapRot === 90 || mapRot === 270;
    boundsW = Math.max(1, rot90 ? mapH : mapW);
    boundsH = Math.max(1, rot90 ? mapW : mapH);

    // Вращаем вокруг центра картинки и ставим центр повёрнутого
    // прямоугольника в центр его же границ: тогда содержимое всегда лежит
    // в [0..boundsW]×[0..boundsH], а fit() считает по ним.
    sceneRoot.pivot.set(mapW / 2, mapH / 2);
    sceneRoot.position.set(boundsW / 2, boundsH / 2);
    sceneRoot.rotation = (mapRot * Math.PI) / 180;
    sceneRoot.scale.set(mapFlipH ? -1 : 1, mapFlipV ? -1 : 1);
  }

  function applyScene(data) {
    const oldW = mapW, oldH = mapH, oldRot = mapRot, oldFlipH = mapFlipH, oldFlipV = mapFlipV;

    state = data;
    mapW = Math.max(1, data.width || 1);
    mapH = Math.max(1, data.height || 1);
    cell = data.grid && data.grid.px > 4 ? data.grid.px : 70;
    isDm = !!data.isDm;
    mapRot = ((Math.round(data.rotation || 0) % 360) + 360) % 360;
    mapFlipH = !!data.flipH;
    mapFlipV = !!data.flipV;

    if (data.tool) {
      tools.shape = data.tool.shape || null;
      tools.shapeRadius = data.tool.shapeRadius || 0;
      tools.ping = !!data.tool.ping;
      tools.measure = !!data.tool.measure;
      tools.brush = !!data.tool.brush;
      tools.eraser = !!data.tool.eraser;
      tools.wall = !!data.tool.wall;
      tools.brushColor = data.tool.brushColor || tools.brushColor;
      tools.brushThickness = data.tool.brushThickness || tools.brushThickness;
    }

    // «Разведанное» приходит в fog.seen. Раньше читали data.seen — и старые
    // пятна не сбрасывались никогда, потому что fog.seen никто не смотрел.
    if (data.fog && Array.isArray(data.fog.seen)) fogSeen = data.fog.seen;
    else if (Array.isArray(data.seen)) fogSeen = data.seen;

    const incomingFov = (data.fovTokens || []).map(t => Object.assign({}, t));
    if (drag && drag.kind === 'token' && drag.token) {
      for (const t of incomingFov) {
        if (t.id !== drag.token.id) continue;
        t.x = drag.token.x;
        t.y = drag.token.y;
        // Угол берём из C#: пока жетон тащат, его могли повернуть Q/E, и
        // старая копия в drag уже не соответствует настоящему взгляду.
        drag.token.facing = t.facing;
      }

      // Снимок карты пришёл во время драга: в state.tokens держим те же
      // координаты, что и у перетаскиваемого жетона.
      const fresh = (data.tokens || []).find(t => t.id === drag.id);
      if (fresh) {
        fresh.x = drag.token.x;
        fresh.y = drag.token.y;
        fresh.facing = drag.token.facing;
      }
    }

    // Поворот Q/E: не даём снимку карты вернуть старый угол.
    if (rotateHold && rotateHold.token) {
      const fresh = (data.tokens || []).find(t => t.id === rotateHold.token.id);
      if (fresh) {
        fresh.facing = rotateHold.token.facing;
        rotateHold.token = fresh;
      }
      if (drag && drag.kind === 'token' && drag.token && drag.token.id === rotateHold.token.id)
        drag.token.facing = rotateHold.token.facing;

      for (const t of incomingFov) {
        if (t.id === rotateHold.token.id) t.facing = rotateHold.token.facing;
      }
    }

    setFovTokens(incomingFov);

    applyOrientation();

    // Картинку меняем только когда C# действительно прислал адрес: null —
    // «эта карта уже отправлена», "" — «файл ещё не скачан». Пустую строку в
    // ensureMapSprite не отдаём, иначе спрайт уничтожался бы на каждом снимке.
    if (data.image) ensureMapSprite(data.image, data.imageHash || '');
    else if (!data.imageHash && mapSprite) ensureMapSprite('', '');
    applyMapSpriteSize();

    buildGrid(data);
    buildWalls(data);
    buildShapes(data);
    buildStrokes(data);
    buildTrails();
    buildTokens(data);
    buildFog();
    buildFov();
    updateHint();

    // Автоподгонку делаем только при первой загрузке, смене сцены и смене
    // картинки. Масштаб и позиция, выставленные игроком, между снимками
    // сохраняются: снимок сцены не должен «прыгать».
    const fitSceneId = data.sceneId || 0;
    const fitHash = data.imageHash || '';
    const fitNeeded = !hasFit ||
      (fitSceneId !== lastFitSceneId) ||
      (!!mapSprite && fitHash && fitHash !== lastFitHash);
    if (fitNeeded) {
      hasFit = true;
      lastFitSceneId = fitSceneId;
      // Хэш запоминаем только когда картинка уже есть: иначе первый снимок
      // «без файла» запомнил бы хэш, и приход картинки не подогнал бы вид.
      lastFitHash = mapSprite ? fitHash : '';

      // Если страница перезагрузилась, у неё нет ни масштаба, ни панорамы —
      // возвращаем то, что было до перезагрузки. На новую сцену/картинку
      // сохранённый вид не подходит (ключ другой) — подгоняем как обычно.
      if (!restoreView(fitSceneId, fitHash)) {
        fit();
        // На первом кадре app.screen иногда ещё нулевой — повторяем после
        // раскладки, иначе карта уезжает в угол и выглядит чёрной.
        requestAnimationFrame(fit);
        setTimeout(fit, 120);
      }
    }

    if (!mapSprite && mapImageHash) requestImage();

    const lastLog = window.__hmwtLastSceneLog || { id: -1, seen: -1, count: 0 };
    lastLog.count++;
    if (lastLog.id !== data.sceneId || lastLog.seen !== fogSeen.length ||
        lastLog.count % 25 === 0) {
      lastLog.id = data.sceneId;
      lastLog.seen = fogSeen.length;
      log('scene ' + (data.sceneId || '?') + ' ' + mapW + 'x' + mapH + ' cell=' + cell +
        ' rot=' + mapRot + ' image=' + (mapSprite ? 'yes' : 'no') +
        ' tokens=' + ((data.tokens || []).length) +
        ' fog=' + (data.fog && data.fog.on ? 1 : 0) + ' seen=' + fogSeen.length +
        ' shapes=' + ((data.shapes || []).length) + ' walls=' + ((data.walls || []).length) +
        ' strokes=' + ((data.strokes || []).length));
    }
    window.__hmwtLastSceneLog = lastLog;

    // Первые кадры пишем с экранными координатами жетонов: по ним проверки
    // и отладка понимают, куда нажимать мышью поверх WebGL.
    sceneCount++;
    if (viewLogs < 3 || sceneCount % 30 === 0) {
      viewLogs++;
      const parts = [];
      for (const [id, node] of tokenNodes) {
        const p = node.toGlobal(new PIXI.Point(0, 0));
        parts.push(id + ':' + Math.round(p.x) + ',' + Math.round(p.y));
      }
      const shapeParts = [];
      for (const s of ((state && state.shapes) || [])) {
        const p = sceneToScreen(s.x, s.y);
        shapeParts.push(s.id + ':' + Math.round(p.x) + ',' + Math.round(p.y));
      }
      const wallParts = [];
      for (const w of ((state && state.walls) || [])) {
        const p = sceneToScreen((w.x1 + w.x2) / 2, (w.y1 + w.y2) / 2);
        wallParts.push(w.id + ':' + Math.round(p.x) + ',' + Math.round(p.y));
      }
      const visParts = [];
      for (const t of ((state && state.tokens) || [])) {
        visParts.push(t.id + ':' + (pointVisible(t.x, t.y, ensureVision()) ? 1 : 0));
      }
      log('view ' + Math.round(app.screen.width) + 'x' + Math.round(app.screen.height) +
        ' s=' + world.scale.x.toFixed(4) +
        ' pos=' + Math.round(world.x) + ',' + Math.round(world.y) +
        ' tokens ' + parts.join(' ') +
        ' shapes ' + shapeParts.join(' ') +
        ' walls ' + wallParts.join(' ') +
        ' visible ' + visParts.join(' '));
    }
  }

  function applyFogUpdate(data) {
    if (!state) return;
    if (data.sceneId && state.sceneId && data.sceneId !== state.sceneId) return;

    isDm = !!data.isDm;
    if (data.grid && data.grid.px > 4) cell = data.grid.px;
    if (data.fog && Array.isArray(data.fog.seen)) fogSeen = data.fog.seen;
    if (data.fog && typeof data.fog.on === 'boolean') {
      state.fog = data.fog;
    }

    const incoming = (data.fovTokens || []).map(t => Object.assign({}, t));
    if (drag && drag.kind === 'token' && drag.token) {
      for (const t of incoming) {
        if (t.id !== drag.token.id) continue;
        t.x = drag.token.x;
        t.y = drag.token.y;
        // Угол — из C#: его мог повернуть Q/E во время драга.
        drag.token.facing = t.facing;
      }
    }
    // Пока Q/E держат, угол у нас свежее, чем в сообщении из C#.
    if (rotateHold && rotateHold.token) {
      if (drag && drag.kind === 'token' && drag.token && drag.token.id === rotateHold.token.id)
        drag.token.facing = rotateHold.token.facing;

      for (const t of incoming) {
        if (t.id === rotateHold.token.id) t.facing = rotateHold.token.facing;
      }
    }
    setFovTokens(incoming);

    buildFog();
    buildFov();
  }

  // Лёгкое сообщение «только угол»: без списка разведанных секторов.
  function applyFacingUpdate(data) {
    if (!state) return;
    if (data.sceneId && state.sceneId && data.sceneId !== state.sceneId) return;

    isDm = !!data.isDm;
    if (data.grid && data.grid.px > 4) cell = data.grid.px;
    if (data.fog && typeof data.fog.on === 'boolean') state.fog = { on: data.fog.on };

    const incoming = (data.fovTokens || []).map(t => Object.assign({}, t));
    if (drag && drag.kind === 'token' && drag.token) {
      for (const t of incoming) {
        if (t.id !== drag.token.id) continue;
        t.x = drag.token.x;
        t.y = drag.token.y;
        drag.token.facing = t.facing;
      }
    }
    if (rotateHold && rotateHold.token) {
      for (const t of incoming) {
        if (t.id === rotateHold.token.id) t.facing = rotateHold.token.facing;
      }
    }

    setFovTokens(incoming);

    buildFov();

    const now = performance.now();
    if (now - lastFogAt >= 40) {
      lastFogAt = now;
      buildFog();
    }
  }

  // Новые разведанные сектора (обычно 1–3 штуки): дописываем к туману без
  // полного списка. Так разведка видна прямо во время поворота и драга.
  function applySeenAdd(data) {
    if (!state) return;
    if (data.sceneId && state.sceneId && data.sceneId !== state.sceneId) return;
    if (!Array.isArray(data.seen) || data.seen.length === 0) return;

    for (const text of data.seen) {
      if (typeof text === 'string' && text.length > 0) fogSeen.push(text);
    }

    buildFog();
  }

  // Чужой жетон поехал (лёгкое сообщение, без полного снимка карты).
  function applyTokenLive(d) {
    if (!state || !state.tokens) return;
    const t = state.tokens.find(x => x.id === d.id);
    if (!t) return;

    t.x = d.x;
    t.y = d.y;
    if (typeof d.facing === 'number' && d.facing >= 0) t.facing = d.facing;

    const node = tokenNodes.get(d.id);
    if (node && !(drag && drag.kind === 'token' && drag.id === d.id)) {
      node._targetX = d.x;
      node._targetY = d.y;
      // Отметка «пакет только что был»: снимок сцены, собранный раньше,
      // не должен откатывать жетон назад.
      node._liveAt = performance.now();
    }

    for (const f of fovTokens) {
      if (f.id !== d.id) continue;
      f.x = d.x;
      f.y = d.y;
      if (typeof d.facing === 'number' && d.facing >= 0) {
        f.facing = d.facing;
        f._target = d.facing;
        f._msgAt = performance.now();
        f._vel = 0;
      }
    }

    buildFov();
    const now = performance.now();
    if (now - lastFogAt >= 60) {
      lastFogAt = now;
      buildFog();
    }
  }

  // Фигуру ведёт другой участник: обновляем только цель, а дорисовывает
  // плавно тот же тикер, что и для жетонов (см. shapeDisplay).
  function applyShapeLive(d) {
    if (!d || !d.id) return;
    const shapes = (state && state.shapes) || [];
    for (const s of shapes) {
      if (s.id === d.id) { s.x = d.x; s.y = d.y; break; }
    }
    const target = shapeTargets.get(d.id);
    if (target) {
      target.x = d.x;
      target.y = d.y;
      shapeAnim = true;
    }
  }

  function requestImage() {
    const now = Date.now();
    if (now - needImageAt < 2500) return;
    needImageAt = now;
    post({ type: 'needImage' });
  }

  // ─────────────────────────── сетка ───────────────────────────

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

  // ─────────────────────────── стены ───────────────────────────

  function buildWalls(data) {
    wallG.clear();
    wallsSig = (data.walls || []).map(w =>
      [w.id, w.isOpen ? 1 : 0, Math.round(w.x1), Math.round(w.y1),
       Math.round(w.x2), Math.round(w.y2)].join(':')).join(';');

    const thick = Math.max(4, cellPx() / 12);
    for (const w of (data.walls || [])) {
      if (w.isOpen) continue;
      const door = w.kind === 'door';
      // Тёмная подложка + светлая линия: иначе стена теряется на текстуре.
      wallG.lineStyle(thick + 3, 0x06080f, 0.55);
      wallG.moveTo(w.x1, w.y1);
      wallG.lineTo(w.x2, w.y2);
      wallG.lineStyle(door ? thick : thick + 1, door ? 0xffd479 : 0xaeb8d8, 0.95);
      wallG.moveTo(w.x1, w.y1);
      wallG.lineTo(w.x2, w.y2);
    }
  }

  // ─────────────────────────── фигуры ───────────────────────────

  function shapeRadius(s) {
    return Math.max(cellPx() * 0.5, (s.radius || 0) * cellPx());
  }

  function shapeEndpoint(s) {
    const a = ((s.angle || 0) * Math.PI) / 180;
    const r = shapeRadius(s);
    return { x: s.x + Math.cos(a) * r, y: s.y + Math.sin(a) * r };
  }

  // Дисплейные позиции фигур: C# присылает новые координаты не чаще 2–3 раз
  // в секунду, а фигура должна ехать плавно, без рывков.
  const shapeTargets = new Map();
  const shapeDisplay = new Map();
  let shapeAnim = false;

  function buildShapes(data) {
    shapeTargets.clear();
    for (const s of (data.shapes || [])) {
      shapeTargets.set(s.id, {
        id: s.id, kind: s.kind, radius: s.radius, angle: s.angle,
        color: s.color, x: s.x, y: s.y, label: s.label,
        script: s.script || '', scriptLabel: s.scriptLabel || ''
      });
      if (!shapeDisplay.has(s.id)) shapeDisplay.set(s.id, { x: s.x, y: s.y });
    }

    for (const id of [...shapeDisplay.keys()])
      if (!shapeTargets.has(id)) shapeDisplay.delete(id);

    drawShapes();
    shapeAnim = true;
  }

  // Подписи фигур и точек интереса: отдельные тексты, чтобы их можно было
  // читать поверх карты (Graphics текст не рисует).
  const shapeLabels = new Map();

  function drawShapes() {
    shapeG.clear();

    const seen = new Set();
    for (const [id, target] of shapeTargets) {
      const dragged = drag && drag.kind === 'shape' && drag.shape && drag.shape.id === id;
      const pos = dragged ? drag.shape : (shapeDisplay.get(id) || target);
      const s = Object.assign({}, target, { x: pos.x, y: pos.y });

      // Тёмная зона — рабочий инструмент мастера: игрокам её не показываем.
      if (s.kind && s.kind.indexOf('darkzone') === 0 && !isDm) continue;

      const col = rgba(s.color, { color: 0xb98bff, alpha: 1 });
      const radius = shapeRadius(s);
      seen.add(id);

      if (s.kind === 'point') {
        // Точка интереса: маленькая метка, ничего не заливает
        const r = Math.max(4, cellPx() * 0.12);
        shapeG.lineStyle(2.5, col.color, 0.95);
        shapeG.beginFill(col.color, 0.85);
        shapeG.drawCircle(s.x, s.y, r);
        shapeG.endFill();
        shapeG.lineStyle(1.5, col.color, 0.5);
        shapeG.drawCircle(s.x, s.y, r * 2.4);
      } else if (s.kind && s.kind.indexOf('darkzone') === 0) {
        // Тёмная зона: плотное затемнение, игрок её не видит вообще
        shapeG.lineStyle(2, 0x6b5bd6, 0.9);
        shapeG.beginFill(0x05070f, 0.72);
        if (s.kind === 'darkzone_square')
          shapeG.drawRoundedRect(s.x - radius, s.y - radius, radius * 2, radius * 2, 6);
        else
          shapeG.drawCircle(s.x, s.y, radius);
        shapeG.endFill();
        shapeG.lineStyle(1, 0x8f7dff, 0.55);
        if (s.kind !== 'darkzone_square') shapeG.drawCircle(s.x, s.y, radius * 0.94);
      } else {
        shapeG.lineStyle(3, col.color, 0.9);
        shapeG.beginFill(col.color, 0.16);
        if (s.kind === 'square') {
          shapeG.drawRoundedRect(s.x - radius, s.y - radius, radius * 2, radius * 2, 6);
        } else if (s.kind === 'cone') {
          const a = ((s.angle || 0) * Math.PI) / 180;
          shapeG.moveTo(s.x, s.y);
          shapeG.arc(s.x, s.y, radius, a - Math.PI / 4, a + Math.PI / 4);
          shapeG.lineTo(s.x, s.y);
        } else if (s.kind === 'line') {
          const end = shapeEndpoint(s);
          shapeG.moveTo(s.x, s.y);
          shapeG.lineTo(end.x, end.y);
        } else {
          shapeG.drawCircle(s.x, s.y, radius);
        }
        shapeG.endFill();
      }

      // Подпись
      const text = (s.label || '').trim();
      let node = shapeLabels.get(id);
      if (text.length > 0) {
        if (!node) {
          node = new PIXI.Text('', {
            fontFamily: 'Palatino Linotype, Georgia, Segoe UI',
            fontSize: 16,
            fill: 0xf1f4ff,
            stroke: 0x090c16,
            strokeThickness: 4,
            align: 'center'
          });
          node.anchor.set(0.5, 0);
          shapeLayer.addChild(node);
          shapeLabels.set(id, node);
        }
        node.text = text;
        node.style.fontSize = Math.max(14, Math.round(cellPx() * 0.28));
        node.position.set(
          s.x,
          s.kind === 'point' ? s.y - radius - 22 : s.y - 8
        );
        node.visible = true;
      } else if (node) {
        node.visible = false;
      }
    }

    for (const [id, node] of [...shapeLabels]) {
      if (seen.has(id)) continue;
      shapeLabels.delete(id);
      node.destroy();
    }
  }

  // ─────────────────────────── штрихи ───────────────────────────

  function drawStroke(g, text, color, thickness) {
    const pts = polygon(text);
    if (pts.length < 4) return;
    const col = rgba(color, { color: 0xe06a63, alpha: 1 });
    g.lineStyle(Math.max(1, thickness || 4), col.color, 0.92);
    g.moveTo(pts[0], pts[1]);
    for (let i = 2; i < pts.length; i += 2) g.lineTo(pts[i], pts[i + 1]);
  }

  function buildStrokes(data) {
    strokeG.clear();
    for (const s of (data.strokes || [])) drawStroke(strokeG, s.points, s.color, s.thickness);
  }

  // ─────────────────────────── следы ───────────────────────────

  function buildTrails() {
    trailG.clear();
    if (!state) return;
    const col = rgba(state.trailColor, { color: 0x9b8bff, alpha: 0.85 });
    const thickness = Math.max(3, cellPx() * 0.08);

    // След тащимого жетона рисуем из drag.path, а старый маршрут этого же
    // жетона скрываем: иначе получалась линия от прошлой остановки к новой,
    // по которой жетон никогда не шёл.
    const all = [];
    for (const t of (state.trails || [])) {
      if (drag && drag.kind === 'token' && drag.id === t.id) continue;
      all.push({ id: t.id, points: t.points || [] });
    }
    if (drag && drag.kind === 'token' && drag.path && drag.path.length > 1)
      all.push({ id: drag.id, points: drag.path });

    for (const trail of all) {
      if (!trail.points || trail.points.length < 2) continue;

      // След невидимого жетона — это утечка: по нему видно, где прошёл
      // чужой токен, хотя самого токена на карте нет. Рисуем только след
      // тех жетонов, которые игрок действительно видит.
      if (!trailIdVisible(trail.id)) continue;

      trailG.lineStyle(thickness, col.color, 0.8);
      trailG.moveTo(trail.points[0].x, trail.points[0].y);
      for (let i = 1; i < trail.points.length; i++)
        trailG.lineTo(trail.points[i].x, trail.points[i].y);
    }
  }

  /**
   * Виден ли игроку жетон с этим id: та же логика, что у самих жетонов
   * (без тумана — все, свой — всегда, «Токены отряда» — игроки, иначе
   * только в собственном обзоре). Мастер видит всё.
   */
  function trailIdVisible(id) {
    if (isDm) return true;
    const t = (state.tokens || []).find(x => x && x.id === id);
    if (!t) return false;
    return tokenSeenByPlayer(t);
  }

  function tokenSeenByPlayer(t) {
    if (!t) return false;
    if (t.mine) return true;              // свой жетон виден всегда
    if (t.hidden) return false;           // скрытый чужой не выдаёт себя
    const fogOn = !!(state && state.fog && state.fog.on);
    if (!fogOn) return true;
    if (state && state.showParty && t.isPlayer) return true;
    return pointVisible(t.x, t.y, ensureVision());
  }

  // ─────────────────────────── жетоны ───────────────────────────

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
    // Хиты сверху, состояния снизу — как на карточке бойца.
    const hpLabel = new PIXI.Text('', {
      fontFamily: 'Palatino Linotype, Georgia, Segoe UI',
      fontSize: 14,
      fill: 0xffb3b3,
      stroke: 0x090c16,
      strokeThickness: 4,
      align: 'center'
    });
    hpLabel.anchor.set(0.5, 1);

    const condLabel = new PIXI.Text('', {
      fontFamily: 'Palatino Linotype, Georgia, Segoe UI',
      fontSize: 12,
      fill: 0xffd27a,
      stroke: 0x090c16,
      strokeThickness: 3,
      align: 'center'
    });
    condLabel.anchor.set(0.5, 0);

    node.addChild(halo, photo, tokenMask, frame, hpLabel, label, condLabel);
    photo.mask = tokenMask;
    node._halo = halo;
    node._photo = photo;
    node._tokenMask = tokenMask;
    node._frame = frame;
    node._label = label;
    node._hp = hpLabel;
    node._cond = condLabel;
    tokenLayer.addChild(node);
    return node;
  }

  function buildTokens(data) {
    const alive = new Set();
    for (const t of (data.tokens || [])) {
      alive.add(t.id);
      let node = tokenNodes.get(t.id);
      if (!node) {
        node = makeTokenNode(t);
        tokenNodes.set(t.id, node);
      }

      // Пока крутим обзор из меню, снимок из C# может прийти со старым
      // углом — он «отматывал» конус назад. Держим локальный угол.
      if (rotateHold && rotateHold.token && rotateHold.token.id === t.id)
        t.facing = rotateHold.token.facing;

      node._token = t;
      if (t.selected) selectedTokenId = t.id;

      // Свежий live-пакет важнее снимка: снимок мог быть собран раньше и
      // «отмотал» бы жетон назад — это и давало рывки при каждом ходе.
      const liveRecent = node._liveAt && (performance.now() - node._liveAt < 900);
      if (liveRecent && node._targetX !== undefined) {
        t.x = node._targetX;
        t.y = node._targetY;
      } else {
        node._targetX = t.x;
        node._targetY = t.y;
      }

      if (node._placed !== true ||
          Math.hypot(t.x - node.x, t.y - node.y) > cellPx() * 12) {
        node.position.set(t.x, t.y);
        node._placed = true;
      }
      if (drag && drag.kind === 'token' && drag.id === t.id) {
        node.visible = true;
        continue;
      }

      const size = Math.max(24, cellPx() * (t.sizeCells || 1));
      node.zIndex = t.isPlayer ? 1000 : 100;
      node._size = size;

      node._halo.clear();
      node._halo.beginFill(t.color ? rgba(t.color, { color: 0x28324a }).color : 0x28324a, 0.9);
      node._halo.drawCircle(0, 0, size / 2 + 3);
      node._halo.endFill();

      if (t.photo !== null && t.photo !== undefined) {
        node._photo.visible = false;
        const tex = t.photo ? texture(t.photo) : null;
        if (tex && tex.baseTexture) {
          node._photo.texture = tex;
          node._photo.visible = true;
          node._photo.anchor.set(0.5);
          fitSprite(node._photo, size, size);
        }
      }

      if (node._photo && node._photo.visible) {
        fitSprite(node._photo, size, size);
        node._tokenMask.clear();
        node._tokenMask.beginFill(0xffffff);
        node._tokenMask.drawCircle(0, 0, size / 2);
        node._tokenMask.endFill();
      }

      // Рамка: свой цвет важнее «выбран/игрок/монстр». Подсветка врага в
      // обзоре и попадания в фигуру рисуется там же (см. drawTokenFrame).
      drawTokenFrame(node, t);

      // Имя должно читаться и на жетоне в 4 клетки: масштабируем к размеру.
      // Верхний предел нужен для жетонов до 10 клеток — иначе подпись
      // занимала пол-карты.
      node._label.style.fontSize = Math.min(40, Math.max(13, Math.round(size * 0.2)));
      node._label.style.strokeThickness = Math.max(3, Math.round(size * 0.05));
      node._label.y = size / 2 + 4;
      node._label.text = t.label || '';

      // Хиты сверху, состояния — под именем.
      const hpText = (t.hpMax > 0) ? (t.hp + '/' + t.hpMax) : '';
      node._hp.text = hpText;
      node._hp.visible = !!hpText;
      if (hpText) {
        node._hp.style.fontSize = Math.min(30, Math.max(12, Math.round(size * 0.17)));
        node._hp.style.strokeThickness = Math.max(3, Math.round(size * 0.045));
        node._hp.y = -size / 2 - 6;
        // Хиты кончились — краснее, чтобы бросалось в глаза
        node._hp.style.fill = (t.hp <= 0) ? 0x8a2030 : 0xffb3b3;
      }

      // Состояния хранятся ключами («charmed»), показываем русские названия.
      const condNames = {};
      for (const o of ((state && state.conditionList) || [])) if (o && o.key) condNames[o.key.toLowerCase()] = o.name || o.key;
      const condAll = (t.conditions || '').split(/[,;]/).map(x => x.trim()).filter(Boolean)
        .map(k => condNames[k.toLowerCase()] || k);
      const condText = condAll.length > 1 ? condAll[0] + '…' : (condAll[0] || '');
      node._cond.text = condText;
      node._cond.visible = condText.length > 0;
      if (node._cond.visible) {
        node._cond.style.fontSize = Math.min(24, Math.max(11, Math.round(size * 0.15)));
        node._cond.style.strokeThickness = Math.max(3, Math.round(size * 0.04));
        node._cond.y = size / 2 + 4 + node._label.height;
      }

      // Враг в зоне обзора и жетон внутри фигуры подсвечиваются в
      // drawTokenFrame — она же вызывается при движении (см. тикер ниже).

      // Без тумана видно всех; «Токены отряда» показывают игроков всегда.
      // Свой жетон виден всегда: иначе при выключенном обзоре пропадал
      // и он сам, и его нельзя было вернуть без движения от ведущего.
      const fogOn = !!(state && state.fog && state.fog.on);
      const partyAlways = !!(state && state.showParty);
      const inVision = fogOn ? pointVisible(t.x, t.y, ensureVision()) : true;
      const seen = !fogOn || t.mine || (partyAlways && t.isPlayer) || inVision;
      if (t.hidden) {
        node.alpha = isDm ? 0.4 : 0;
      } else {
        node.alpha = isDm ? (seen ? 1 : 0.5) : (seen ? 1 : 0);
      }
      node.visible = node.alpha > 0.01;
    }

    for (const [id, node] of [...tokenNodes]) {
      if (alive.has(id)) continue;
      tokenNodes.delete(id);
      node.destroy({ children: true });
    }
  }

  /**
   * Точка внутри фигуры-области. Точки интереса и тёмные зоны не считаются:
   * это метки/инструмент мастера, а не заклинание.
   */
  function shapeContains(s, x, y) {
    if (!s || !s.kind) return false;
    const r = shapeRadius(s);
    const dx = x - s.x, dy = y - s.y;

    if (s.kind === 'square') return Math.abs(dx) <= r && Math.abs(dy) <= r;

    if (s.kind === 'line') {
      const end = shapeEndpoint(s);
      return distToSegment({ x: x, y: y }, { x: s.x, y: s.y }, end) <=
        Math.max(cellPx() * 0.5, r * 0.12);
    }

    const d = Math.hypot(dx, dy);
    if (d > r) return false;

    if (s.kind === 'cone') {
      const dir = ((s.angle || 0) * Math.PI) / 180;
      let diff = Math.atan2(dy, dx) - dir;
      diff = Math.atan2(Math.sin(diff), Math.cos(diff));
      return Math.abs(diff) <= Math.PI / 4;
    }

    return true;   // circle
  }

  /** Попал ли жетон хотя бы в одну фигуру (по её отображаемой позиции). */
  function shapeAt(x, y) {
    for (const [id, target] of shapeTargets) {
      const kind = target.kind || '';
      if (kind === 'point' || kind.indexOf('darkzone') === 0) continue;
      const pos = shapeDisplay.get(id) || target;
      if (shapeContains({ x: pos.x, y: pos.y, kind: kind, angle: target.angle, radius: target.radius }, x, y))
        return true;
    }
    return false;
  }

  /**
   * Рамка жетона: свой цвет/выбор, плюс подсветка. Враг в поле обзора —
   * красное кольцо, жетон в фигуре — золотое. При подсветке рамка чуть
   * увеличивается по радиусу, чтобы её было видно на любом размере.
   */
  function drawTokenFrame(node, t) {
    if (!node || node.destroyed || !t) return;

    const size = node._size || Math.max(24, cellPx() * (t.sizeCells || 1));
    const fogOn = !!(state && state.fog && state.fog.on);
    const inVision = fogOn ? pointVisible(t.x, t.y, ensureVision()) : true;
    const inShape = shapeAt(t.x, t.y);
    // Подсветка врага — для игрока (мастер и так видит всё, красные кольца
    // ему только мешают). Мастеру важен второй случай — кто попал в фигуру.
    const enemyInSight = !isDm && !t.isPlayer && !t.hidden && inVision;

    node._hlVision = enemyInSight;
    node._hlShape = inShape;

    const grow = (enemyInSight || inShape) ? 3 : 0;
    const r = size / 2 + grow;

    const frameColor = t.frameColor ? rgba(t.frameColor, null) : null;
    const width = t.selected ? 4 : 3;

    node._frame.clear();
    if (frameColor) {
      node._frame.lineStyle(width + 1, frameColor.color, 1);
      node._frame.drawCircle(0, 0, r);
    } else {
      node._frame.lineStyle(width,
        t.selected ? 0xffd27a : (t.isPlayer ? 0x5fd4dd : 0x0b0d16), 1);
      node._frame.drawCircle(0, 0, r);
    }

    if (t.selected) {
      node._frame.lineStyle(1.5, 0xffd27a, 0.85);
      node._frame.drawCircle(0, 0, r + 3);
    }

    if (enemyInSight) {
      node._frame.lineStyle(2.5, 0xff5a5a, 0.95);
      node._frame.drawCircle(0, 0, r + 2);
      node._frame.lineStyle(1, 0xff5a5a, 0.35);
      node._frame.drawCircle(0, 0, r + 5);
    }

    if (inShape) {
      node._frame.lineStyle(2.5, 0xffd27a, 0.95);
      node._frame.drawCircle(0, 0, r + 4);
    }
  }

  // Плавное движение: C# присылает позиции с задержкой (сеть + снимок карты),
  // поэтому жетоны и фигуры догоняют цель кадрами, а не прыгают рывками.
  // Плавное движение токенов: времязависимое сглаживание (кадры могут быть
  // 60 и 144 Гц) плюс небольшая задержка для чужих жетонов — движение
  // приходит из сети рывками, а показываем его плавно.
  let lastMoveFrame = performance.now();
  let lastHighlightAt = 0;
  app.ticker.add(() => {
    const frameNow = performance.now();
    const frameDt = Math.min(0.1, Math.max(0.001, (frameNow - lastMoveFrame) / 1000));
    lastMoveFrame = frameNow;

    for (const [id, node] of tokenNodes) {
      if (!node || node.destroyed || node._targetX === undefined) continue;
      if (drag && drag.kind === 'token' && (drag.id === id || (drag.group && drag.group.some(g => g.token && g.token.id === id)))) continue;

      const dx = node._targetX - node.x;
      const dy = node._targetY - node.y;
      if (Math.abs(dx) < 0.6 && Math.abs(dy) < 0.6) {
        if (dx !== 0 || dy !== 0) node.position.set(node._targetX, node._targetY);
        continue;
      }

      // Чужие шаги приходят из сети рывками (10 Гц, а снимки — 2.5 Гц):
      // доводим их мягче, свои — быстрее, чтобы не чувствовалось задержки
      // под рукой. Задержка ~0.2 с и есть та «плавность с отставанием»,
      // из-за которой движение перестаёт дёргаться.
      const mine = node._token && (node._token.canMove === true || node._token.mine === true);
      const smooth = (drag && drag.kind === 'token') ? 0.05 : (mine ? 0.07 : 0.2);
      const k = 1 - Math.exp(-frameDt / smooth);
      node.position.set(node.x + dx * k, node.y + dy * k);
    }

    // Подсветка: враг в обзоре и попадание в фигуру пересчитываются по ходу
    // движения, а рамка перерисовывается только при изменении состояния.
    if (frameNow - lastHighlightAt >= 120) {
      lastHighlightAt = frameNow;
      const fogOn = !!(state && state.fog && state.fog.on);
      for (const [, node] of tokenNodes) {
        const t = node && node._token;
        if (!t || node.destroyed) continue;
        const inVision = fogOn ? pointVisible(t.x, t.y, ensureVision()) : true;
        const enemy = !isDm && !t.isPlayer && !t.hidden && inVision;
        const inShape = shapeAt(t.x, t.y);
        if (node._hlVision !== enemy || node._hlShape !== inShape) drawTokenFrame(node, t);
      }
    }

    // Плавный доворот конуса: цель приходит из C# ~30 раз в секунду,
    // а рисуем каждый кадр — иначе поворот выглядит рывками.
    const nowFov = performance.now();
    let fovMoving = false;
    for (const f of fovTokens) {
      if (f._target === undefined) f._target = f.facing || 0;
      if (f._shown === undefined) f._shown = f._target;

      // Если пакет задержался (UI-поток занят), продолжаем ехать с последней
      // скоростью: иначе конус замирает и следующий пакет даёт рывок.
      const idle = Math.min(160, nowFov - (f._msgAt || nowFov));
      const predicted = f._target + (f._vel || 0) * idle;

      const d = ((predicted - f._shown + 540) % 360) - 180;
      if (Math.abs(d) < 0.05) {
        f._shown = ((predicted % 360) + 360) % 360;
        continue;
      }
      f._shown = ((f._shown + d * 0.35) % 360 + 360) % 360;
      fovMoving = true;
    }

    if (fovMoving) {
      buildFov();
      if (nowFov - lastFogAt >= 40) {
        lastFogAt = nowFov;
        buildFog();
      }
    }

    if (!shapeAnim) return;
    let moving = false;
    // Тот же принцип, что и у жетонов: время, а не кадры — иначе на 144 Гц
    // фигура догоняет вдвое быстрее, чем на 60.
    const shapeK = 1 - Math.exp(-frameDt / 0.16);
    for (const [id, target] of shapeTargets) {
      if (drag && drag.kind === 'shape' && drag.shape && drag.shape.id === id) continue;
      const d = shapeDisplay.get(id);
      if (!d) continue;

      const dx = target.x - d.x, dy = target.y - d.y;
      if (Math.abs(dx) < 0.6 && Math.abs(dy) < 0.6) {
        if (dx !== 0 || dy !== 0) { d.x = target.x; d.y = target.y; moving = true; }
        continue;
      }
      d.x += dx * shapeK;
      d.y += dy * shapeK;
      moving = true;
    }

    if (moving) drawShapes();
    else shapeAnim = false;
  });

  // ─────────────────────────── обзор и туман ───────────────────────────
  //
  // Обзор считаем полигоном видимости: из жетона пускаем лучи в углы стен
  // и по кругу, каждый луч обрезаем о ближайшую стену. Это даёт и вырезание
  // тумана, и правильную форму сектора — за стеной ничего не видно.
  // Соглашение об углах — как в C#: 0° смотрит вправо (+X), углы растут
  // по часовой стрелке (ось Y вниз). Раньше здесь вычитали 90°, и все
  // конусы смотрели на север, а туман резался в сторону.

  let wallsSig = '';
  const visionCache = new Map();

  function wallSegments() {
    const segs = [];
    for (const w of ((state && state.walls) || [])) {
      if (w.isOpen) continue;                 // открытая дверь не мешает
      segs.push(w);
    }
    return segs;
  }

  /** Расстояние до ближайшей стены по лучу (или max). */
  function rayDistance(ox, oy, dx, dy, max, segs) {
    let best = max;
    for (let i = 0; i < segs.length; i++) {
      const w = segs[i];
      const ex = w.x2 - w.x1, ey = w.y2 - w.y1;
      const den = dx * ey - dy * ex;
      if (Math.abs(den) < 1e-9) continue;     // луч параллелен стене

      const ax = w.x1 - ox, ay = w.y1 - oy;
      const t = (ax * ey - ay * ex) / den;
      const u = (ax * dy - ay * dx) / den;
      if (t > 0.001 && t < best && u >= 0 && u <= 1) best = t;
    }
    return best;
  }

  /** Полигон видимости жетона: плоский массив [x,y,x,y,…]. */
  function visionPolygon(t, segs) {
    const ox = t.x, oy = t.y;
    const radius = Math.max(1, (t.revealCells || 6) * cellPx());
    const full = (t.viewAngle || 360) >= 359;
    const half = full
      ? Math.PI
      : Math.min(Math.PI, Math.max((2.5 * Math.PI) / 180, (((t.viewAngle || 360) / 2) * Math.PI) / 180));
    // Для отрисовки берём плавно доводимый угол: из C# он приходит 30 раз
    // в секунду, а кадров больше — иначе поворот выглядит рывками.
    const facing = ((shownFacing(t)) * Math.PI) / 180;

    const rel = [];
    const pushPoint = (px, py) => {
      let a = Math.atan2(py - oy, px - ox) - facing;
      a = Math.atan2(Math.sin(a), Math.cos(a));
      if (!full && Math.abs(a) > half) return;
      rel.push(a - 0.0012, a, a + 0.0012);
    };

    for (const w of segs) {
      pushPoint(w.x1, w.y1);
      pushPoint(w.x2, w.y2);
    }

    // Равномерные лучи закрывают «дыры» между углами стен.
    const rays = full ? 72 : 56;
    for (let i = 0; i <= rays; i++)
      rel.push(full ? -Math.PI + (i / rays) * Math.PI * 2 : -half + (i / rays) * half * 2);

    rel.sort((a, b) => a - b);

    // Полигон начинается с центра: иначе веер замыкается хордой и вместо
    // сектора получается «линза» — туман вырезался полумесяцем.
    const pts = [ox, oy];
    for (const r of rel) {
      const ang = facing + r;
      const dx = Math.cos(ang), dy = Math.sin(ang);
      const d = rayDistance(ox, oy, dx, dy, radius, segs);
      pts.push(ox + dx * d, oy + dy * d);
    }
    return pts;
  }

  function visionKey(t) {
    return [t.id, Math.round(t.x), Math.round(t.y),
      Math.round(shownFacing(t) * 10), Math.round((t.viewAngle || 360) * 10),
      Math.round((t.revealCells || 0) * 10), wallsSig].join('|');
  }

  function ensureVision() {
    const segs = wallSegments();
    const alive = new Set();

    for (const t of fovTokens) {
      alive.add(t.id);
      const key = visionKey(t);
      const cached = visionCache.get(t.id);
      if (cached && cached.key === key) continue;
      visionCache.set(t.id, { key: key, pts: visionPolygon(t, segs) });
    }

    for (const id of [...visionCache.keys()])
      if (!alive.has(id)) visionCache.delete(id);

    return visionCache;
  }

  function pointInPoly(x, y, pts) {
    let inside = false;
    for (let i = 0, j = pts.length - 2; i < pts.length; j = i, i += 2) {
      const xi = pts[i], yi = pts[i + 1];
      const xj = pts[j], yj = pts[j + 1];
      if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi)
        inside = !inside;
    }
    return inside;
  }

  /** Видит ли этот зритель точку (учитывая только «развеивающие» жетоны). */
  function pointVisible(x, y, vision) {
    for (const f of fovTokens) {
      if (!isDm && f.reveals === false && !f.mine) continue;

      // Свой центр виден всегда: полигон начинается ровно в нём, и проверка
      // «точка внутри» на вершине может дать ложное «нет».
      if (Math.hypot(x - f.x, y - f.y) <= cellPx() * 0.6) return true;

      const v = vision.get(f.id);
      if (v && pointInPoly(x, y, v.pts)) return true;
    }
    return false;
  }

  function buildFov() {
    fovG.clear();
    const vision = ensureVision();

    for (const t of fovTokens) {
      // Мастер видит конусы всех, игрок — только свои и отряда.
      if (!isDm && t.reveals === false && !t.mine) continue;

      // Скрытый жетон не должен выдавать себя сектором обзора.
      if (!isDm && !t.mine && !tokenSeenByPlayer(t)) continue;

      const v = vision.get(t.id);
      if (!v || v.pts.length < 6) continue;

      const custom = t.viewColor ? rgba(t.viewColor, null) : null;
      // Цвет с нулевой альфой (#00000000) — «без заливки»: рисуем только
      // контур. Цвет контура берём обычный для стороны, иначе прозрачный
      // цвет означал бы чёрную рамку.
      const noFill = !!custom && custom.alpha <= 0.01;
      const baseColor = t.isPlayer ? 0x5fd4dd : 0xa98bec;
      const color = custom && !noFill ? custom.color : baseColor;

      // У мастера заливка только у выбранного жетона: иначе конусы всех
      // монстров накладываются и карта превращается в сплошное пятно.
      // Жетоны игроков заливаются всегда — их немного.
      const filled = !isDm || t.isPlayer || t.id === selectedTokenId;
      const fillAlpha = filled && !noFill ? 0.14 : 0;

      if (fillAlpha > 0) fovG.beginFill(color, fillAlpha);
      fovG.lineStyle(2, noFill ? baseColor : color, filled ? 0.75 : 0.4);
      fovG.moveTo(v.pts[0], v.pts[1]);
      for (let i = 2; i < v.pts.length; i += 2) fovG.lineTo(v.pts[i], v.pts[i + 1]);
      fovG.closePath();
      if (fillAlpha > 0) fovG.endFill();
    }
  }

  // Туман рисуется в отдельный canvas, который затем растягивается ровно
  // на картинку карты (mapW×mapH). Холст создаётся один раз на размер карты:
  // если менять canvas.width у уже созданной текстуры, PIXI продолжает
  // считать её прежней 1024×1024 — туман уезжает и превращается в овалы.
  const FOG_MAX = 1024;
  let fogCanvas = null, fogCtx = null, fogTex = null, fogSprite = null;

  // Отдельный холст «разведанного»: перерисовывается только когда меняется
  // список секторов. Иначе каждый кадр поворота обводил бы их все заново.
  let fogBase = null, fogBaseCtx = null, fogBaseKey = '';

  function buildFogBase(w, h) {
    let sum = 0;
    for (const s of fogSeen) sum += s.length;
    const key = [w, h, isDm ? 1 : 0, mapW, mapH, fogSeen.length, sum, wallsSig].join('|');

    if (fogBase && fogBase.width === w && fogBase.height === h && key === fogBaseKey) return;

    if (!fogBase) {
      fogBase = document.createElement('canvas');
      fogBaseCtx = fogBase.getContext('2d');
    }
    if (fogBase.width !== w || fogBase.height !== h) {
      fogBase.width = w;
      fogBase.height = h;
    }

    fogBaseCtx.setTransform(1, 0, 0, 1, 0, 0);
    fogBaseCtx.globalCompositeOperation = 'source-over';
    fogBaseCtx.clearRect(0, 0, w, h);
    fogBaseCtx.fillStyle = isDm ? 'rgba(5,7,15,0.62)' : 'rgba(5,7,15,0.985)';
    fogBaseCtx.fillRect(0, 0, w, h);

    fogBaseCtx.setTransform(w / mapW, 0, 0, h / mapH, 0, 0);
    fogBaseCtx.globalCompositeOperation = 'destination-out';
    fogBaseCtx.fillStyle = '#fff';
    fogBaseCtx.beginPath();
    for (const text of fogSeen) {
      const pts = polygon(text);
      if (pts.length < 6) continue;
      fogBaseCtx.moveTo(pts[0], pts[1]);
      for (let i = 2; i < pts.length; i += 2) fogBaseCtx.lineTo(pts[i], pts[i + 1]);
      fogBaseCtx.closePath();
    }
    fogBaseCtx.fill();
    fogBaseCtx.setTransform(1, 0, 0, 1, 0, 0);

    fogBaseKey = key;
  }

  function ensureFogSurface() {
    const scale = Math.min(1, FOG_MAX / Math.max(mapW, mapH));
    const w = Math.max(1, Math.round(mapW * scale));
    const h = Math.max(1, Math.round(mapH * scale));
    if (fogCanvas && fogCanvas.width === w && fogCanvas.height === h) return;

    if (fogSprite) {
      try { fogLayer.removeChild(fogSprite); } catch (_) { }
      try { fogSprite.destroy({ texture: true, baseTexture: true }); } catch (_) { }
      fogSprite = null;
      fogTex = null;
    }

    fogCanvas = document.createElement('canvas');
    fogCanvas.width = w;
    fogCanvas.height = h;
    fogCtx = fogCanvas.getContext('2d');
    fogTex = PIXI.Texture.from(fogCanvas);
    fogSprite = new PIXI.Sprite(fogTex);
    fogSprite.position.set(0, 0);
    fogLayer.addChild(fogSprite);
    fogKey = '';
  }

  function fogSignature() {
    const fov = fovTokens.map(t =>
      [t.id, Math.round(t.x * 2), Math.round(t.y * 2),
       Math.round(shownFacing(t) * 10), Math.round((t.viewAngle || 360) * 10),
       Math.round((t.revealCells || 0) * 10), t.reveals === false ? 0 : 1].join(',')).join(';');
    const seenSig = fogSeen.length + ':' +
      (fogSeen.length ? fogSeen[0].length + ':' + fogSeen[fogSeen.length - 1].length : '0');
    return [isDm ? 1 : 0, (state && state.showParty) ? 1 : 0,
      mapW, mapH, cell, fogSeen.length, seenSig, wallsSig, fov].join('|');
  }

  function buildFog() {
    const fog = (state && state.fog) || {};
    ensureFogSurface();
    if (!fogSprite) return;

    fogSprite.visible = !!fog.on;
    if (!fog.on) return;

    const key = fogSignature();
    if (key === fogKey) return;
    fogKey = key;

    const w = fogCanvas.width, h = fogCanvas.height;

    // Разведанные сектора перерисовываем только когда они изменились:
    // их бывает больше двухсот, и заново обводить все на каждом кадре
    // поворота — это и были те самые подтормаживания.
    buildFogBase(w, h);

    fogCtx.setTransform(1, 0, 0, 1, 0, 0);
    fogCtx.globalCompositeOperation = 'source-over';
    fogCtx.clearRect(0, 0, w, h);
    if (fogBase) fogCtx.drawImage(fogBase, 0, 0);

    // Дальше рисуем в координатах картинки — холст сам растянется.
    fogCtx.setTransform(w / mapW, 0, 0, h / mapH, 0, 0);
    fogCtx.globalCompositeOperation = 'destination-out';
    fogCtx.fillStyle = '#fff';
    fogCtx.beginPath();

    // Текущий обзор — те же полигоны видимости, что и у сектора на экране,
    // поэтому за стеной туман не вырезается. Мастеру режет туман любой конус
    // (так он видит, что увидит монстр), игрокам — только отрядный.
    const vision = ensureVision();
    for (const t of fovTokens) {
      if (!isDm && t.reveals === false && !t.mine) continue;

      // Если «Токены отряда» выключены и чужой жетон вне обзора, его зрение
      // не должно рассеивать туман: иначе игрок видит разведанный коридор
      // от токена, которого сам не видит. Свой жетон режет туман всегда.
      if (!isDm && !t.mine && !tokenSeenByPlayer(t)) continue;

      const v = vision.get(t.id);
      if (!v || v.pts.length < 6) continue;
      fogCtx.moveTo(v.pts[0], v.pts[1]);
      for (let i = 2; i < v.pts.length; i += 2) fogCtx.lineTo(v.pts[i], v.pts[i + 1]);
      fogCtx.closePath();
    }
    fogCtx.fill();
    fogCtx.setTransform(1, 0, 0, 1, 0, 0);

    fogSprite.position.set(0, 0);
    fogSprite.width = mapW;
    fogSprite.height = mapH;
    fogTex.update();
  }

  // ─────────────────────────── кадр ───────────────────────────

  function fit() {
    const pad = 24;
    const host = document.getElementById('stage');
    let w = (app.view && app.view.clientWidth) || (host && host.clientWidth) ||
      document.documentElement.clientWidth || window.innerWidth || 0;
    let h = (app.view && app.view.clientHeight) || (host && host.clientHeight) ||
      document.documentElement.clientHeight || window.innerHeight || 0;

    if (w < 20 || h < 20) { w = 1200; h = 800; }

    const sx = (w - pad * 2) / boundsW;
    const sy = (h - pad * 2) / boundsH;
    const s = Math.max(0.02, Math.min(sx, sy));
    world.scale.set(s);
    world.position.set((w - boundsW * s) / 2, (h - boundsH * s) / 2);
    lastViewSize = { w: w, h: h };
    saveView(true);
  }

  // ── вид переживает перезагрузку страницы ──
  // WebView2 может пересоздать страницу (спрятали под оверлей, обновили
  // сборку) — без запоминания масштаба карта возвращалась «отдалённой по
  // умолчанию». Ключ — сцена + картинка: на новую карту вид не переносим.
  const VIEW_KEY = 'hmwt.map.view';
  let viewSavedAt = 0;

  function saveView(force) {
    const now = Date.now();
    if (!force && now - viewSavedAt < 400) return;
    viewSavedAt = now;
    try {
      sessionStorage.setItem(VIEW_KEY, JSON.stringify({
        scene: (state && state.sceneId) || 0,
        hash: mapImageHash || '',
        s: world.scale.x,
        x: world.position.x,
        y: world.position.y
      }));
    } catch (_) { }
  }

  function restoreView(sceneId, hash) {
    try {
      const raw = sessionStorage.getItem(VIEW_KEY);
      if (!raw) return false;
      const v = JSON.parse(raw);
      if (!v || v.scene !== sceneId) return false;
      if (hash && v.hash && v.hash !== hash) return false;
      if (!(v.s > 0.01 && v.s < 9)) return false;

      world.scale.set(v.s);
      world.position.set(v.x, v.y);
      const w = (app.view && app.view.clientWidth) || window.innerWidth || 800;
      const h = (app.view && app.view.clientHeight) || window.innerHeight || 600;
      lastViewSize = { w: w, h: h };
      log('view restored s=' + v.s.toFixed(4) +
        ' pos=' + Math.round(v.x) + ',' + Math.round(v.y));
      return true;
    } catch (_) {
      return false;
    }
  }

  /**
   * Окно изменило размер (в том числе WebView спрятался под оверлей и
   * вернулся). Масштаб и точку в центре сохраняем: автоподгонка здесь
   * недопустима — из-за неё карта «прыгала» при каждом открытии окна.
   */
  function keepViewOnResize() {
    const host = document.getElementById('stage');
    const w = (app.view && app.view.clientWidth) || (host && host.clientWidth) ||
      document.documentElement.clientWidth || window.innerWidth || 0;
    const h = (app.view && app.view.clientHeight) || (host && host.clientHeight) ||
      document.documentElement.clientHeight || window.innerHeight || 0;
    if (w < 20 || h < 20) return;

    const prev = lastViewSize;
    lastViewSize = { w: w, h: h };
    if (!hasFit || prev.w < 20 || prev.h < 20) return;

    // Что было в центре экрана — то и остаётся в центре.
    const s = world.scale.x || 1;
    const wx = (prev.w / 2 - world.position.x) / s;
    const wy = (prev.h / 2 - world.position.y) / s;
    world.position.set(w / 2 - wx * s, h / 2 - wy * s);
  }

  /** Зум вокруг центра экрана — кнопки «+» и «−» панели. */
  function zoomBy(factor) {
    const w = (app.view && app.view.clientWidth) || window.innerWidth || 800;
    const h = (app.view && app.view.clientHeight) || window.innerHeight || 600;
    const cx = w / 2, cy = h / 2;
    const cur = world.scale.x || 1;
    const next = Math.max(0.02, Math.min(8, cur * (factor || 1)));
    const wx = (cx - world.x) / cur;
    const wy = (cy - world.y) / cur;
    world.scale.set(next);
    world.position.set(cx - wx * next, cy - wy * next);
    saveView();
  }

  function screenPoint(e) {
    const r = app.view.getBoundingClientRect();
    return new PIXI.Point(e.clientX - r.left, e.clientY - r.top);
  }

  // Перевод экран ↔ картинка считаем сами: у sceneRoot есть поворот и
  // отражение, а PIXI-овский toLocal на матрице с отражением иногда отдаёт
  // NaN (и тогда не срабатывает ни один щелчок по жетону).
  function sceneToScreen(x, y) {
    const c = Math.cos(sceneRoot.rotation), s = Math.sin(sceneRoot.rotation);
    const lx = (x - sceneRoot.pivot.x) * sceneRoot.scale.x;
    const ly = (y - sceneRoot.pivot.y) * sceneRoot.scale.y;
    const wx = sceneRoot.position.x + lx * c - ly * s;
    const wy = sceneRoot.position.y + lx * s + ly * c;
    return {
      x: world.position.x + wx * world.scale.x,
      y: world.position.y + wy * world.scale.y
    };
  }

  function screenToScene(p) {
    const sx = world.scale.x || 1;
    const sy = world.scale.y || 1;
    const wx = (p.x - world.position.x) / sx;
    const wy = (p.y - world.position.y) / sy;
    const dx = wx - sceneRoot.position.x;
    const dy = wy - sceneRoot.position.y;
    const c = Math.cos(sceneRoot.rotation), s = Math.sin(sceneRoot.rotation);
    const rx = dx * c + dy * s;
    const ry = -dx * s + dy * c;
    return new PIXI.Point(
      sceneRoot.pivot.x + rx / (sceneRoot.scale.x || 1),
      sceneRoot.pivot.y + ry / (sceneRoot.scale.y || 1));
  }

  function toScene(e) {
    return screenToScene(screenPoint(e));
  }

  function snapPoint(x, y, prevX, prevY) {
    if (!state || !state.grid || !state.grid.snap) return { x, y };
    const c = cellPx();

    // Гистерезис: жетон остаётся в текущей клетке, пока курсор не ушёл от её
    // центра больше чем на 60%. Без этого оси округлялись по очереди и жетон
    // «объезжал» клетку по сторонам вместо хода по диагонали.
    const snapAxis = (v, prev) => {
      const center = Math.round((v - c / 2) / c) * c + c / 2;
      if (prev === undefined || prev === null) return center;
      if (Math.abs(v - prev) < c * 0.6) return prev;
      return center;
    };

    return { x: snapAxis(x, prevX), y: snapAxis(y, prevY) };
  }

  function snapWall(x, y) {
    return snapCross(x, y);
  }

  /** Привязка к перекрестьям сетки (линиям), а не к центрам клеток. */
  function snapCross(x, y) {
    const c = cellPx();
    const isGrid = !!(state && state.grid && state.grid.on);
    const sx = isGrid ? Math.round(x / c) * c : x;
    const sy = isGrid ? Math.round(y / c) * c : y;
    // Не уезжаем за картинку: там туман и обзор считаются иначе.
    return {
      x: Math.max(0, Math.min(mapW, sx)),
      y: Math.max(0, Math.min(mapH, sy))
    };
  }

  // ─────────────────────────── попадания ───────────────────────────

  // ── выделение группы рамкой (Shift + ЛКМ по пустому месту) ──
  const groupSel = new Set();
  const groupG = new PIXI.Graphics();
  shapeLayer.addChild(groupG);

  function drawGroup() {
    groupG.clear();
    for (const id of groupSel) {
      const node = tokenNodes.get(id);
      const t = node && node._token;
      if (!t) continue;
      const size = Math.max(24, cellPx() * (t.sizeCells || 1));
      groupG.lineStyle(2.5, 0x7fe3ff, 0.9);
      groupG.drawCircle(t.x, t.y, size / 2 + 4);
    }
  }

  function clearGroup() {
    if (groupSel.size === 0) return;
    groupSel.clear();
    drawGroup();
  }

  function finishRubber() {
    const a = drag.from, b = drag.to;
    const x1 = Math.min(a.x, b.x), x2 = Math.max(a.x, b.x);
    const y1 = Math.min(a.y, b.y), y2 = Math.max(a.y, b.y);

    groupSel.clear();
    if (Math.abs(x2 - x1) > 6 || Math.abs(y2 - y1) > 6) {
      for (const [id, node] of tokenNodes) {
        const t = node && node._token;
        if (!t) continue;
        if (t.x >= x1 && t.x <= x2 && t.y >= y1 && t.y <= y2) groupSel.add(id);
      }
    }
    previewG.clear();
    drawGroup();
    log('group select ' + groupSel.size);
  }

  function hitToken(local) {    let found = null;
    let best = Infinity;
    for (const [id, node] of tokenNodes) {
      const t = node._token;
      if (!t) continue;
      const half = (node._size || 30) / 2 + 4;
      const dx = Math.abs(local.x - t.x), dy = Math.abs(local.y - t.y);
      if (dx > half || dy > half) continue;
      const d = dx * dx + dy * dy;
      if (d < best) { best = d; found = { id, node, t }; }
    }
    return found;
  }

  function distToSegment(p, a, b) {
    const dx = b.x - a.x, dy = b.y - a.y;
    const len = dx * dx + dy * dy;
    if (len < 0.0001) return Math.hypot(p.x - a.x, p.y - a.y);
    let t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / len;
    t = Math.max(0, Math.min(1, t));
    return Math.hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t));
  }

  function hitShape(local) {
    const shapes = (state && state.shapes) || [];
    let found = null;
    for (const s of shapes) {
      const radius = shapeRadius(s);
      let hit = false;
      if (s.kind === 'square') {
        hit = Math.abs(local.x - s.x) <= radius && Math.abs(local.y - s.y) <= radius;
      } else if (s.kind === 'cone' || s.kind === 'line') {
        hit = distToSegment(local, { x: s.x, y: s.y }, shapeEndpoint(s)) <=
          Math.max(10, radius * 0.12);
      } else {
        hit = Math.hypot(local.x - s.x, local.y - s.y) <= radius;
      }
      if (hit) found = s;
    }
    return found;
  }

  function hitWall(local) {
    let found = null;
    let best = Infinity;
    for (const w of ((state && state.walls) || [])) {
      const d = distToSegment(local, { x: w.x1, y: w.y1 }, { x: w.x2, y: w.y2 });
      const limit = Math.max(9, cellPx() * 0.14);
      if (d <= limit && d < best) { best = d; found = w; }
    }
    return found;
  }

  // ─────────────────────────── меню (ПКМ) ───────────────────────────

  let menuEl = null;
  let menuSubEl = null;
  let submenuTimer = 0;

  function hideSubmenu() {
    if (submenuTimer) { clearTimeout(submenuTimer); submenuTimer = 0; }
    if (menuSubEl) menuSubEl.style.display = 'none';
  }

  function hideMenu() {
    if (menuEl) menuEl.style.display = 'none';
    hideSubmenu();
  }

  function scheduleSubmenuHide() {
    if (submenuTimer) clearTimeout(submenuTimer);
    submenuTimer = setTimeout(hideSubmenu, 260);
  }

  function ensureMenuEls() {
    if (!menuEl) {
      menuEl = document.createElement('div');
      menuEl.className = 'hmwt-menu';
      document.body.appendChild(menuEl);
    }
    if (!menuSubEl) {
      menuSubEl = document.createElement('div');
      menuSubEl.className = 'hmwt-menu hmwt-submenu';
      document.body.appendChild(menuSubEl);
      // Пока курсор в подменю — не закрываем: по состояниям кликают подряд.
      menuSubEl.addEventListener('pointerenter', () => {
        if (submenuTimer) { clearTimeout(submenuTimer); submenuTimer = 0; }
      });
      menuSubEl.addEventListener('pointerleave', () => scheduleSubmenuHide());
    }
    return menuEl;
  }

  /** Открывает подменю рядом с пунктом (наведение или клик). */
  function openSubmenu(row, children, onPick) {
    ensureMenuEls();
    if (submenuTimer) { clearTimeout(submenuTimer); submenuTimer = 0; }

    menuSubEl.innerHTML = '';
    buildMenuItems(menuSubEl, children, onPick);
    menuSubEl.style.display = 'block';
    menuSubEl.style.left = '0px';
    menuSubEl.style.top = '0px';

    const r = row.getBoundingClientRect();
    const rect = menuSubEl.getBoundingClientRect();
    let left = r.right - 3;
    if (left + rect.width > window.innerWidth - 8)
      left = Math.max(8, r.left - rect.width + 3);
    let top = r.top - 4;
    if (top + rect.height > window.innerHeight - 8)
      top = Math.max(8, window.innerHeight - rect.height - 8);
    menuSubEl.style.left = left + 'px';
    menuSubEl.style.top = top + 'px';
  }

  function buildMenuItems(host, items, onPick) {
    for (const item of items) {
      if (item.separator) {
        const sep = document.createElement('div');
        sep.className = 'hmwt-sep';
        host.appendChild(sep);
        continue;
      }

      const row = document.createElement('div');
      row.className = 'hmwt-item' +
        (item.disabled ? ' hmwt-disabled' : '') +
        (item.danger ? ' hmwt-danger' : '') +
        (item.check ? ' hmwt-check' : '') +
        (item.children ? ' hmwt-parent' : '');

      // Место под галочку есть у всех строк — текст пунктов не «пляшет».
      const tick = document.createElement('span');
      tick.className = 'hmwt-tick';
      tick.textContent = item.check ? '\u2713' : '';
      row.appendChild(tick);

      if (item.color) {
        const sw = document.createElement('span');
        sw.className = 'hmwt-swatch';
        sw.style.background = colorCss(item.color);
        row.appendChild(sw);
      }

      const text = document.createElement('span');
      text.className = 'hmwt-text';
      text.textContent = item.text || '';
      row.appendChild(text);

      if (item.children && item.children.length) {
        const arrow = document.createElement('span');
        arrow.className = 'hmwt-arrow';
        arrow.textContent = '\u25B8';
        row.appendChild(arrow);

        row.addEventListener('pointerenter', () => openSubmenu(row, item.children, onPick));
        row.addEventListener('pointerleave', () => scheduleSubmenuHide());
        // Тач и «нажал, а не навёл»: то же подменю открывается кликом.
        row.addEventListener('pointerdown', ev => {
          ev.preventDefault();
          ev.stopPropagation();
          openSubmenu(row, item.children, onPick);
        });
      } else if (!item.disabled && item.action) {
        row.addEventListener('pointerdown', ev => {
          ev.preventDefault();
          ev.stopPropagation();

          // Состояния переключаются подряд, не закрывая меню: галочка
          // должна появиться сразу, а не после повторного открытия.
          if (item.toggle) {
            item.check = !item.check;
            tick.textContent = item.check ? '\u2713' : '';
            row.classList.toggle('hmwt-check', !!item.check);
          }
          if (!item.keepOpen) hideMenu();
          onPick(item);
        });
      }

      host.appendChild(row);
    }
  }

  function renderMenu(x, y, items, onPick) {
    ensureMenuEls();
    hideSubmenu();

    menuEl.innerHTML = '';
    buildMenuItems(menuEl, items, onPick);
    menuEl.style.display = 'block';
    menuEl.style.left = '0px';
    menuEl.style.top = '0px';

    const rect = menuEl.getBoundingClientRect();
    let left = x, top = y;
    if (left + rect.width > window.innerWidth - 8) left = Math.max(8, window.innerWidth - rect.width - 8);
    if (top + rect.height > window.innerHeight - 8) top = Math.max(8, window.innerHeight - rect.height - 8);
    menuEl.style.left = left + 'px';
    menuEl.style.top = top + 'px';
  }

  /** Список состояний для подменю жетона: галочки по текущему набору. */
  function conditionItems(token) {
    const list = (state && state.conditionList) || [];
    const have = new Set(String(token.conditions || '')
      .split(/[,;]/).map(x => x.trim().toLowerCase()).filter(Boolean));
    const items = [];

    for (const o of list) {
      if (!o || !o.key) continue;
      items.push({
        text: o.name || o.key,
        check: have.has(String(o.key).toLowerCase()),
        toggle: true,
        keepOpen: true,
        action: 'condition',
        value: o.key
      });
    }

    if (have.size > 0)
      items.push({ separator: true }, { text: 'Снять все состояния', keepOpen: true, action: 'conditionsClear', value: '' });

    return items;
  }

  function openTokenMenu(e, token) {
    if (!token) return;
    // Игрок может посмотреть любой жетон; править — только свой. Признак
    // своего жетона приходит из C# (canMove/mine): у клиента нет базы боёв,
    // и сравнивать имя здесь не с чем.
    const own = token.canMove === true || token.mine === true;
    if (!isDm && !own) {
      const items = [];
      items.push({ text: token.label || 'Жетон', disabled: true });
      if (token.hpMax > 0) items.push({ text: 'Хиты: ' + token.hp + ' / ' + token.hpMax, disabled: true });

      const names = {};
      for (const o of ((state && state.conditionList) || [])) if (o && o.key) names[o.key.toLowerCase()] = o.name || o.key;
      const have = (token.conditions || '').split(/[,;]/).map(x => x.trim()).filter(Boolean)
        .map(k => names[k.toLowerCase()] || k);
      items.push({ text: have.length ? ('Состояния: ' + have.join(', ')) : 'Состояний нет', disabled: true });
      renderMenu(e.clientX, e.clientY, items, () => { });
      e.preventDefault();
      return;
    }

    const items = [];
    items.push({ text: token.label || 'Жетон', disabled: true });
    items.push({ separator: true });

    // Размер — отдельным подменю: 10 пунктов в общем списке растягивали меню
    // на весь экран. От 1 до 10 клеток: большие существа бывают и «огромными».
    const sizeItems = [];
    for (let n = 1; n <= 10; n++) {
      const word = n === 1 ? 'клетка' : (n < 5 ? 'клетки' : 'клеток');
      sizeItems.push({
        text: n + ' ' + word,
        check: Math.abs((token.sizeCells || 1) - n) < 0.1,
        action: 'size', value: String(n)
      });
    }
    items.push({ text: 'Размер', children: sizeItems });

    items.push({ separator: true });
    for (const a of [360, 120, 90, 60])
      items.push({
        text: a >= 360 ? 'Обзор: круговой 360°' : 'Конус ' + a + '°',
        check: Math.abs((token.viewAngle || 360) - a) < 1,
        action: 'viewAngle', value: String(a)
      });

    items.push({ text: 'Повернуть взгляд ⟳', action: 'facing', value: String(screenTurn(45)) });
    items.push({ text: 'Повернуть взгляд ⟲', action: 'facing', value: String(screenTurn(-45)) });
    items.push({ text: 'Поворот: Q / E на клавиатуре', disabled: true });

    items.push({ separator: true });
    for (const r of [4, 6, 8, 12])
      items.push({
        text: 'Радиус обзора: ' + r + ' кл.',
        check: Math.abs((token.revealCells || 0) - r) < 0.5,
        action: 'reveal', value: String(r)
      });

    // У каждого игрока свой обзор: можно выключить совсем.
    const visionOn = (token.revealCells || 0) > 0;
    items.push({
      text: visionOn ? 'Обзор: выключить' : 'Обзор: включить (6 кл.)',
      action: 'reveal', value: visionOn ? '0' : '6'
    });

    items.push({ separator: true });
    items.push({
      text: 'Цвет обзора: по умолчанию',
      check: !token.viewColor,
      action: 'viewcolor', value: ''
    });
    // Полностью прозрачная заливка: остаётся только контур сектора. Нужно,
    // когда цвет закрывает карту под конусом.
    items.push({
      text: 'Цвет обзора: без заливки (только рамка)',
      check: sameColor(token.viewColor, '#00000000'),
      action: 'viewcolor', value: '#00000000'
    });
    for (const c of FRAME_COLORS)
      items.push({
        text: 'Цвет обзора: ' + c.name,
        color: c.hex,
        check: sameColor(token.viewColor, c.hex),
        action: 'viewcolor', value: c.hex
      });

    if (isDm) {
      items.push({ separator: true });
      items.push({
        text: 'Рамка: по умолчанию',
        check: !token.frameColor,
        action: 'frame', value: ''
      });
      for (const c of FRAME_COLORS)
        items.push({
          text: 'Рамка: ' + c.name,
          color: c.hex,
          check: sameColor(token.frameColor, c.hex),
          action: 'frame', value: c.hex
        });

      items.push({ separator: true });
      // Состояния — подменю с галочками: окно с картой поверх WebGL прятало
      // карту и сбрасывало масштаб, а галочка появлялась только после
      // повторного открытия.
      const dmConditions = conditionItems(token);
      items.push({
        text: 'Состояния',
        children: dmConditions.length ? dmConditions : [{ text: 'Справочник пуст', disabled: true }]
      });

      items.push({ separator: true });
      items.push({ text: 'Убрать с карты', danger: true, action: 'remove', value: '' });
    } else {
      // Игрок: своему жетону — та же кнопка, что у ведущего; чужому —
      // только список уже выставленных состояний.
      const names = {};
      for (const o of ((state && state.conditionList) || [])) if (o && o.key) names[o.key.toLowerCase()] = o.name || o.key;
      const have2 = (token.conditions || '').split(/[,;]/).map(x => x.trim()).filter(Boolean)
        .map(k => names[k.toLowerCase()] || k);

      items.push({ separator: true });
      if (token.canMove !== false) {
        const myConditions = conditionItems(token);
        items.push({
          text: 'Состояния',
          children: myConditions.length ? myConditions : [{ text: 'Справочник пуст', disabled: true }]
        });
      } else {
        items.push({ text: have2.length ? ('Состояния: ' + have2.join(', ')) : 'Состояний нет', disabled: true });
      }
    }

    renderMenu(e.clientX, e.clientY, items, item =>
      post({ type: 'tokenMenuAction', id: token.id, action: item.action, value: item.value }));
  }

  // Меню по правой кнопке на пустом месте карты (только ведущий).
  function openMapMenu(e, sp) {
    const items = [
      { text: 'Точка интереса здесь', action: 'point' },
      { text: 'Пинг здесь', action: 'ping' }
    ];
    renderMenu(e.clientX, e.clientY, items, item => {
      if (item.action === 'point')
        post({ type: 'placePoint', x: sp.x, y: sp.y });
      else
        post({ type: 'ping', x: Math.round(sp.x), y: Math.round(sp.y) });
    });
  }

  function openWallMenu(e, wall) {    if (!isDm) return;

    const items = [];
    items.push({ text: wall.kind === 'door' ? 'Дверь' : 'Стена', disabled: true });
    items.push({ separator: true });
    if (wall.kind === 'door')
      items.push({ text: wall.isOpen ? 'Закрыть дверь' : 'Открыть дверь', action: 'toggle', value: '' });
    items.push({ text: 'Убрать', danger: true, action: 'remove', value: '' });

    renderMenu(e.clientX, e.clientY, items, item =>
      post({ type: 'wallAction', id: wall.id, action: item.action }));
  }

  const SHAPE_TITLES = {
    circle: 'Круг', square: 'Квадрат', cone: 'Конус', line: 'Линия',
    point: 'Точка интереса', darkzone: 'Тёмная зона', darkzone_square: 'Тёмная зона (квадрат)'
  };

  function openShapeMenu(e, shape) {
    const items = [];
    const shapeTitle = shape.kind === 'point'
      ? (SHAPE_TITLES.point || 'Точка')
      : (SHAPE_TITLES[shape.kind] || 'Фигура') + ' ' + (shape.radius || 0) + ' кл.';
    items.push({ text: shapeTitle, disabled: true });
    items.push({ separator: true });

    // У точки интереса нет ни поворота, ни размера: это просто метка
    if (shape.kind !== 'point') {
      items.push({ text: 'Повернуть ⟳', action: 'rotate', value: '45' });
      items.push({ text: 'Повернуть ⟲', action: 'rotate', value: '-45' });
    }

    // Подпись: у точки интереса это главное, у фигуры — просто удобно
    items.push({ separator: true });
    items.push({
      text: (shape.label || '').length ? 'Изменить подпись…' : 'Подписать…',
      action: 'rename', value: ''
    });
    if ((shape.label || '').length)
      items.push({ text: 'Убрать подпись', action: 'rename', value: '' });

    items.push({ separator: true });
    if (shape.kind !== 'point') {
      items.push({ text: 'Больше на 1 клетку', action: 'radius', value: '1' });
      items.push({ text: 'Меньше на 1 клетку', action: 'radius', value: '-1' });
    }

    // Скрипт входа — только у тёмной зоны: остальные фигуры игроки видят,
    // и «невидимая ловушка» из них не получится.
    if ((shape.kind || '').indexOf('darkzone') === 0) {
      items.push({ separator: true });
      items.push({
        text: (shape.scriptLabel || '').length
          ? 'Скрипт: ' + shape.scriptLabel + ' — изменить…'
          : 'Скрипт зоны…',
        action: 'script', value: ''
      });
    }

    items.push({ separator: true });

    for (const c of SHAPE_COLORS)
      items.push({
        text: 'Цвет: ' + c.name,
        color: c.hex,
        check: sameColor(shape.color, c.hex),
        action: 'color', value: c.hex
      });

    items.push({ separator: true });
    items.push({ text: 'Убрать фигуру', danger: true, action: 'delete', value: '' });

    renderMenu(e.clientX, e.clientY, items, item =>
      post({ type: 'shapeAction', id: shape.id, action: item.action, value: item.value }));
  }

  // ─────────────────────────── линейка ───────────────────────────

  const measureEl = () => document.getElementById('measure');

  function showMeasure(text, x, y) {
    const el = measureEl();
    if (!el) return;
    el.textContent = text;
    el.style.display = 'block';
    if (typeof x === 'number' && typeof y === 'number') {
      el.style.left = x + 'px';
      el.style.top = Math.max(8, y - 34) + 'px';
      el.style.transform = 'translateX(-50%)';
    }
  }

  function hideMeasureLater(delay) {
    clearTimeout(window.__hmwtMeasureTimer);
    window.__hmwtMeasureTimer = setTimeout(() => {
      const el = measureEl();
      if (el) el.style.display = 'none';
    }, delay || 3500);
  }

  function measureText(cells) {
    return cells.toFixed(1) + ' кл · ' + (cells * 5).toFixed(0) + ' фт · ' +
      (cells * 1.5).toFixed(1) + ' м';
  }

  function trailMeasure(points) {
    let pixels = 0;
    for (let i = 1; i < points.length; i++)
      pixels += Math.hypot(points[i].x - points[i - 1].x, points[i].y - points[i - 1].y);
    showMeasure('путь ' + measureText(pixels / Math.max(1, cellPx())));
  }

  function drawRuler(from, to) {
    rulerG.clear();
    const a = sceneToScreen(from.x, from.y);
    const b = sceneToScreen(to.x, to.y);
    rulerG.lineStyle(2, 0xffd27a, 0.95);
    rulerG.moveTo(a.x, a.y);
    rulerG.lineTo(b.x, b.y);
    rulerG.beginFill(0xffd27a, 0.95);
    rulerG.drawCircle(a.x, a.y, 3);
    rulerG.drawCircle(b.x, b.y, 3);
    rulerG.endFill();

    const cells = Math.hypot(to.x - from.x, to.y - from.y) / Math.max(1, cellPx());
    showMeasure(measureText(cells), (a.x + b.x) / 2, Math.min(a.y, b.y));
  }

  // ─────────────────────────── подсказка ───────────────────────────

  function updateHint() {
    const hint = document.getElementById('hint');
    if (!hint) return;
    let text = 'ЛКМ — жетон / панорама · колесо — масштаб · ПКМ — меню жетона';
    if (tools.measure) text = 'Линейка: протяните мышью — клетки, футы, метры';
    else if (tools.wall && isDm) text = 'Стена: протяните мышью (Shift — дверь) · ПКМ по стене — меню';
    else if (tools.brush) text = tools.eraser
      ? 'Ластик: проведите по рисункам'
      : 'Кисть: рисуйте мышью, отпустите — штрих сохранится';
    else if (tools.ping) text = 'Пинг: щёлкните по карте';
    else if (tools.shape) text = 'Фигура ' + tools.shape + ': щёлкните по карте';
    hint.textContent = text;
  }

  // ─────────────────────────── мышь ───────────────────────────

  function endDrag(e) {
    if (drag) {
      if (drag.kind === 'token') finishTokenDrag();
      else if (drag.kind === 'shape') finishShapeDrag();
      else if (drag.kind === 'stroke') finishStroke();
      else if (drag.kind === 'wall') finishWall();
      else if (drag.kind === 'ruler') finishRuler();
      else if (drag.kind === 'rubber') finishRubber();
    }
    drag = null;
    try { app.view.releasePointerCapture(e.pointerId); } catch (_) { }
  }

  app.view.addEventListener('pointerdown', e => {
    hideMenu();
    try { lastScenePointer = toScene(e); } catch (_) { }

    if (e.button === 2) {
      const sp = toScene(e);
      const tokenHit = hitToken(sp);
      if (tokenHit) { openTokenMenu(e, tokenHit.t); e.preventDefault(); return; }
      const wallHit = hitWall(sp);
      if (wallHit) { openWallMenu(e, wallHit); e.preventDefault(); return; }
      const shapeHit = hitShape(sp);
      if (shapeHit) { openShapeMenu(e, shapeHit); e.preventDefault(); return; }

      // Пустое место: у мастера — быстрые действия по этой точке
      if (isDm) {
        openMapMenu(e, sp);
        e.preventDefault();
        return;
      }

      e.preventDefault();
      return;
    }

    if (e.button !== 0) return;

    const p = screenPoint(e);
    const sp = screenToScene(p);

    // 1. Линейка
    if (tools.measure) {
      clearTimeout(window.__hmwtRulerTimer);
      drag = { kind: 'ruler', from: { x: sp.x, y: sp.y }, to: { x: sp.x, y: sp.y } };
      drawRuler(drag.from, drag.to);
      app.view.setPointerCapture(e.pointerId);
      e.preventDefault();
      return;
    }

    // 2. Стены и двери (только ведущий)
    if (tools.wall && isDm) {
      const start = snapWall(sp.x, sp.y);
      drag = { kind: 'wall', start, end: start, door: !!e.shiftKey };
      app.view.setPointerCapture(e.pointerId);
      e.preventDefault();
      return;
    }

    // 3. Кисть и ластик
    if (tools.brush) {
      drag = {
        kind: 'stroke',
        points: [{ x: sp.x, y: sp.y }],
        color: tools.brushColor,
        thickness: tools.brushThickness,
        eraser: tools.eraser
      };
      previewG.clear();
      app.view.setPointerCapture(e.pointerId);
      e.preventDefault();
      return;
    }

    // 4. Пинг
    if (tools.ping) {
      post({ type: 'ping', x: sp.x, y: sp.y });
      e.preventDefault();
      return;
    }

    // 5. Фигура: ставим по щелчку
    if (tools.shape) {
      post({ type: 'placeShape', x: sp.x, y: sp.y });
      e.preventDefault();
      return;
    }

    // 6. Жетон
    const tokenHit = hitToken(sp);
    if (tokenHit) {
      // Выделение нужно и для панели «Обзор»: ползунок в WPF двигает
      // именно выделенный жетон.
      selectedTokenId = tokenHit.t.id;
      post({ type: 'selectToken', id: tokenHit.t.id });

      // Клик по одиночному жетону снимает выделение группы,
      // Shift+клик — добавляет/убирает жетон из группы.
      if (e.shiftKey) {
        if (groupSel.has(tokenHit.t.id)) groupSel.delete(tokenHit.t.id);
        else groupSel.add(tokenHit.t.id);
        drawGroup();
      } else if (groupSel.size > 0 && !groupSel.has(tokenHit.t.id)) {
        clearGroup();
      }

      if (tokenHit.t.canMove !== false) {
        startTokenDrag(tokenHit, sp);
        app.view.setPointerCapture(e.pointerId);
        e.preventDefault();
        return;
      }
    }

    // 7. Фигура: тянем
    const shape = hitShape(sp);
    if (shape) {
      drag = {
        kind: 'shape', shape,
        dx: sp.x - shape.x, dy: sp.y - shape.y
      };
      shapeDisplay.set(shape.id, { x: shape.x, y: shape.y });
      app.view.setPointerCapture(e.pointerId);
      e.preventDefault();
      return;
    }

    // 8. Дверь: щелчок открывает/закрывает (ведущий)
    const wall = hitWall(sp);
    if (wall && wall.kind === 'door' && isDm) {
      post({ type: 'wallAction', id: wall.id, action: 'toggle' });
      e.preventDefault();
      return;
    }

    // 9. Панорама (Shift — выделение рамкой)
    if (e.shiftKey) {
      clearGroup();
      drag = { kind: 'rubber', from: { x: sp.x, y: sp.y }, to: { x: sp.x, y: sp.y } };
      app.view.setPointerCapture(e.pointerId);
      e.preventDefault();
      return;
    }

    // Клик по пустому месту снимает выделение группы
    clearGroup();
    drag = { kind: 'pan', sx: p.x, sy: p.y, wx: world.x, wy: world.y };
    app.view.setPointerCapture(e.pointerId);
    e.preventDefault();
  });

  function startTokenDrag(hit, sp) {
    const t = hit.t;
    log('drag token ' + t.id + ' from ' + Math.round(sp.x) + ',' + Math.round(sp.y));

    // Новый маршрут начинается с чистого следа: иначе линия соединяла
    // прошлую остановку с новой, и выходил путь, которым жетон не ходил.
    post({ type: 'trailClear', id: t.id });

    drag = {
      kind: 'token', id: hit.id, node: hit.node, token: t,
      offsetX: sp.x - t.x, offsetY: sp.y - t.y,
      path: [{ x: t.x, y: t.y }]
    };

    // Тянем один из выделенных — двигаем всю группу
    if (groupSel.has(t.id) && groupSel.size > 1) {
      drag.group = [];
      for (const id of groupSel) {
        const node = tokenNodes.get(id);
        const g = node && node._token;
        if (!g) continue;
        drag.group.push({ token: g, node: node, x: g.x, y: g.y });
        if (id !== t.id) post({ type: 'trailClear', id: id });
      }
      drag.groupStartX = t.x;
      drag.groupStartY = t.y;
    }

    lastLiveAt = 0;
    lastFogAt = 0;
  }

  function moveToken(sp) {
    const t = drag.token;
    let x = sp.x - drag.offsetX;
    let y = sp.y - drag.offsetY;
    // Опираемся на текущую клетку жетона: так ход по диагонали не «застревает»
    // на углу, а смена клетки происходит целым шагом по обеим осям.
    const snapped = snapPoint(x, y, t.x, t.y);
    x = snapped.x;
    y = snapped.y;

    x = Math.max(0, Math.min(mapW, x));
    y = Math.max(0, Math.min(mapH, y));

    t.x = x;
    t.y = y;

    // Группа едет вместе с ведущим жетоном (тем же шагом, без своей привязки)
    if (drag.group && drag.group.length > 1) {
      const dx = x - (drag.groupStartX || t.x);
      const dy = y - (drag.groupStartY || t.y);
      for (const item of drag.group) {
        if (item.token.id === t.id) continue;
        item.token.x = Math.max(0, Math.min(mapW, item.x + dx));
        item.token.y = Math.max(0, Math.min(mapH, item.y + dy));
        if (item.node) {
          item.node.position.set(item.token.x, item.token.y);
          item.node._targetX = item.token.x;
          item.node._targetY = item.token.y;
        }
      }
    }
    drag.node.position.set(x, y);
    drag.node._targetX = x;
    drag.node._targetY = y;
    drag.node._token = t;
    drag.node.visible = true;
    drag.node.alpha = 1;

    // Локальный обзор едет вместе с жетоном: туман и сектор обновляются
    // сразу, не дожидаясь ответа C#.
    for (const f of fovTokens) {
      if (f.id === t.id) { f.x = x; f.y = y; }
    }

    const last = drag.path[drag.path.length - 1];
    if (!last || Math.hypot(last.x - x, last.y - y) > 4) {
      drag.path.push({ x, y });
      buildTrails();
      trailMeasure(drag.path);
    }

    const now = performance.now();
    if (now - lastFogAt >= 45) {
      lastFogAt = now;
      buildFog();
      buildFov();
    }

    if (Date.now() - lastLiveAt >= 50) {
      lastLiveAt = Date.now();
      post({ type: 'moveTokenLive', id: t.id, x, y });
    }
  }

  function finishTokenDrag() {
    const t = drag.token;
    if (!t) return;

    // Группа: одним пакетом, чтобы ведущий сохранил и опубликовал один раз
    if (drag.group && drag.group.length > 1) {
      const moves = drag.group
        .filter(item => item.token)
        .map(item => ({
          id: item.token.id,
          x: Math.round(item.token.x * 100) / 100,
          y: Math.round(item.token.y * 100) / 100
        }));
      post({ type: 'moveTokens', moves: moves });
      buildFog();
      buildFov();
      drawGroup();
      return;
    }

    if (!drag.path || drag.path.length < 2) drag.path = [{ x: t.x, y: t.y }];
    trailMeasure(drag.path);
    hideMeasureLater();
    post({ type: 'moveToken', id: t.id, x: t.x, y: t.y, points: drag.path });
    buildFog();
    buildFov();
  }

  function finishShapeDrag() {
    const s = drag.shape;
    if (!s) return;
    post({ type: 'moveShape', id: s.id, x: s.x, y: s.y, commit: true });
  }

  function finishStroke() {
    if (!drag.points || drag.points.length === 0) return;
    previewG.clear();
    if (drag.points.length === 1) drag.points.push({ x: drag.points[0].x, y: drag.points[0].y });
    post({
      type: 'stroke',
      points: drag.points,
      color: drag.color,
      thickness: drag.thickness,
      eraser: !!drag.eraser
    });
  }

  function finishWall() {
    previewG.clear();
    const a = drag.start, b = drag.end;
    if (!a || !b) return;
    if (Math.abs(b.x - a.x) + Math.abs(b.y - a.y) < 8) return;
    post({
      type: 'wall',
      x1: a.x, y1: a.y, x2: b.x, y2: b.y,
      kind: drag.door ? 'door' : 'wall'
    });
  }

  function finishRuler() {
    clearTimeout(window.__hmwtRulerTimer);
    window.__hmwtRulerTimer = setTimeout(() => { rulerG.clear(); }, 6000);
    hideMeasureLater(6000);
  }

  // Последняя позиция курсора в координатах картинки: по ней TAB ставит пинг.
  let lastScenePointer = { x: 0, y: 0 };

  app.view.addEventListener('pointermove', e => {
    try { lastScenePointer = toScene(e); } catch (_) { }
    if (!drag) return;
    const sp = toScene(e);

    if (drag.kind === 'pan') {
      const p = screenPoint(e);
      world.position.set(drag.wx + (p.x - drag.sx), drag.wy + (p.y - drag.sy));
      saveView();
      return;
    }

    if (drag.kind === 'token') { moveToken(sp); return; }

    if (drag.kind === 'rubber') {
      drag.to = { x: sp.x, y: sp.y };
      previewG.clear();
      // previewG живёт в слое карты (sceneRoot), поэтому и координаты
      // должны быть в координатах картинки, без sceneToScreen — иначе
      // рамка уезжала вверх-вправо от курсора.
      const zoom = Math.max(0.05, world.scale.x || 1);
      const x1 = Math.min(drag.from.x, drag.to.x);
      const y1 = Math.min(drag.from.y, drag.to.y);
      const w = Math.abs(drag.to.x - drag.from.x);
      const h = Math.abs(drag.to.y - drag.from.y);
      previewG.lineStyle(2 / zoom, 0x7fe3ff, 0.9);
      previewG.beginFill(0x7fe3ff, 0.08);
      previewG.drawRect(x1, y1, w, h);
      previewG.endFill();
      return;
    }

    if (drag.kind === 'shape') {
      // Точка интереса идёт точно за курсором (она метит место), остальные
      // фигуры — по перекрестьям, чтобы стояли «по линейке».
      const isPoint = drag.shape.kind === 'point';
      const snapped = isPoint
        ? { x: sp.x - drag.dx, y: sp.y - drag.dy }
        : snapCross(sp.x - drag.dx, sp.y - drag.dy);
      drag.shape.x = Math.max(0, Math.min(mapW, snapped.x));
      drag.shape.y = Math.max(0, Math.min(mapH, snapped.y));
      shapeDisplay.set(drag.shape.id, { x: drag.shape.x, y: drag.shape.y });
      drawShapes();

      // Фигуру ведём: остальные получают только координаты и дорисовывают
      // движение плавно. Раньше она прыгала в новое место при отпускании.
      const nowLive = performance.now();
      if (!drag.lastLive || nowLive - drag.lastLive > 40) {
        drag.lastLive = nowLive;
        post({ type: 'moveShape', id: drag.shape.id, x: drag.shape.x, y: drag.shape.y, commit: false });
      }
      return;
    }

    if (drag.kind === 'stroke') {
      const pts = drag.points;
      const last = pts[pts.length - 1];
      if (Math.hypot(sp.x - last.x, sp.y - last.y) < 2) return;
      pts.push({ x: sp.x, y: sp.y });

      previewG.clear();
      if (drag.eraser) {
        previewG.lineStyle(Math.max(12, drag.thickness * 2), 0xff9b9b, 0.45);
      } else {
        const col = rgba(drag.color, { color: 0xe06a63, alpha: 1 });
        previewG.lineStyle(Math.max(1, drag.thickness), col.color, 0.9);
      }
      previewG.moveTo(pts[0].x, pts[0].y);
      for (let i = 1; i < pts.length; i++) previewG.lineTo(pts[i].x, pts[i].y);
      return;
    }

    if (drag.kind === 'wall') {
      drag.end = snapWall(sp.x, sp.y);
      previewG.clear();
      previewG.lineStyle(Math.max(3, cellPx() / 16), drag.door ? 0xcfa15a : 0x7fd9e8, 0.9);
      previewG.moveTo(drag.start.x, drag.start.y);
      previewG.lineTo(drag.end.x, drag.end.y);
      return;
    }

    if (drag.kind === 'ruler') {
      drag.to = { x: sp.x, y: sp.y };
      drawRuler(drag.from, drag.to);
    }
  });

  app.view.addEventListener('pointerup', endDrag);
  app.view.addEventListener('pointercancel', endDrag);

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
    saveView();
    e.preventDefault();
  }, { passive: false });

  window.addEventListener('keydown', e => {
    if (e.key === 'Escape') {
      hideMenu();
      if (drag) { drag = null; previewG.clear(); rulerG.clear(); }
      return;
    }

    // Q/E — поворот обзора выделенного жетона. Пока клавишу держат, угол
    // идёт непрерывно, а не шагами по 15°.
    const key = (e.key || '').toLowerCase();

    // TAB — пинг по карте в точке курсора (когда фокус у WebView,
    // до WPF эта клавиша не доходит).
    if (key === 'tab') {
      post({ type: 'ping', x: Math.round(lastScenePointer.x), y: Math.round(lastScenePointer.y) });
      e.preventDefault();
      return;
    }

    if (key !== 'q' && key !== 'e') return;
    if (e.ctrlKey || e.altKey || e.metaKey || e.repeat) return;

    startRotateFromKeyboard(screenTurn(key === 'e' ? 1 : -1));
    e.preventDefault();
  });

  window.addEventListener('keyup', e => {
    const key = (e.key || '').toLowerCase();
    if (key === 'q' || key === 'e') finishRotation();
  });

  // Потеряли фокус — перестаём крутить, иначе жетон будет вращаться вечно.
  window.addEventListener('blur', () => finishRotation());

  function selectedToken() {
    const tokens = (state && state.tokens) || [];
    if (selectedTokenId) {
      const found = tokens.find(t => t.id === selectedTokenId);
      if (found) return found;
    }
    const marked = tokens.find(t => t.selected);
    if (marked) { selectedTokenId = marked.id; return marked; }
    return tokens.find(t => t.canMove) || null;
  }

  function applyFacing(token, facing) {
    token.facing = ((facing % 360) + 360) % 360;

    // Тот же жетон может быть в перетаскивании: у drag свой объект, и без
    // синхронизации следующий снимок карты вернул бы старый угол.
    if (drag && drag.kind === 'token' && drag.token && drag.token.id === token.id) {
      drag.token.facing = token.facing;
      drag.node._token = token;
    }

    for (const f of fovTokens) {
      if (f.id !== token.id) continue;
      f.facing = token.facing;
      // Поворот уже анимируем сами — доводить нечего.
      f._target = token.facing;
      f._shown = token.facing;
      f._vel = 0;
      f._msgAt = performance.now();
    }
  }

  let rotateHold = null;
  let rotateRaf = 0;
  let rotateLast = 0;

  /** Поворот с клавиатуры, когда фокус ввода всё-таки у WebView. */
  function startRotateFromKeyboard(dir) {
    const token = selectedToken();
    if (!token || token.canMove === false) return;
    beginRotation(token, dir, 75);
  }

  /**
   * Поворот на заданный угол с анимацией — для пунктов меню ПКМ.
   * Раньше такой поворот просто переставлял угол, и конус прыгал.
   */
  function rotateBy(token, deg) {
    if (!token || !deg) return;
    if (rotateHold) finishRotation();

    const from = token.facing || 0;
    const dur = Math.max(240, Math.min(700, Math.abs(deg) * 8));

    rotateHold = {
      token: token,
      dir: deg > 0 ? 1 : -1,
      speed: 0,
      applied: Math.abs(deg),
      lastPost: 0,
      frames: 0,
      maxDt: 0,
      by: { from: from, to: from + deg, dur: dur, t0: performance.now() }
    };

    rotateLast = performance.now();
    if (!rotateRaf) rotateRaf = requestAnimationFrame(rotateByStep);
  }

  function rotateByStep(now) {
    const h = rotateHold;
    if (!h || !h.by) { rotateRaf = 0; return; }

    h.frames++;
    if (now - rotateLast > h.maxDt) h.maxDt = now - rotateLast;
    rotateLast = now;

    const k = Math.min(1, (now - h.by.t0) / h.by.dur);
    const e = 1 - Math.pow(1 - k, 3);              // плавное торможение
    applyFacing(h.token, h.by.from + (h.by.to - h.by.from) * e);

    buildFov();
    if (now - lastFogAt >= 40) {
      lastFogAt = now;
      buildFog();
    }

    if (now - h.lastPost >= 100) {
      h.lastPost = now;
      post({ type: 'rotate', id: h.token.id, facing: h.token.facing, commit: false });
    }

    if (k >= 1) {
      const facing = h.token.facing;
      rotateHold = null;
      rotateRaf = 0;
      buildFog();
      buildFov();
      post({ type: 'rotate', id: h.token.id, facing: facing, commit: true });
      return;
    }

    rotateRaf = requestAnimationFrame(rotateByStep);
  }

  function findToken(id) {
    const tokens = (state && state.tokens) || [];
    return tokens.find(t => t.id === id) || null;
  }

  /**
   * Начало поворота. C# присылает только «начали» и направление — саму
   * анимацию ведёт WebGL-рендерер, поэтому она не зависит от загрузки
   * UI-потока WPF и не дёргается.
   */
  function beginRotation(token, dir, speed, facing) {
    if (!token) return;
    if (rotateHold && rotateHold.token.id !== token.id) finishRotation();

    // C# присылает текущий угол: начинаем ровно с него, без скачка.
    if (typeof facing === 'number' && Number.isFinite(facing))
      applyFacing(token, facing);

    selectedTokenId = token.id;
    rotateHold = {
      token: token,
      dir: dir || 1,
      speed: speed > 0 ? speed : 75,
      applied: 0,
      lastPost: 0,
      frames: 0,
      maxDt: 0
    };

    rotateLast = performance.now();
    buildFov();
    buildFog();

    if (!rotateRaf) rotateRaf = requestAnimationFrame(rotateStep);
  }

  function rotateStep(now) {
    if (!rotateHold) { rotateRaf = 0; return; }

    const dt = Math.min(0.12, (now - rotateLast) / 1000);
    rotateLast = now;
    rotateHold.frames++;
    if (dt * 1000 > rotateHold.maxDt) rotateHold.maxDt = dt * 1000;

    const token = rotateHold.token;
    const step = rotateHold.dir * rotateHold.speed * dt;
    rotateHold.applied += Math.abs(step);
    applyFacing(token, (token.facing || 0) + step);

    buildFov();
    if (now - lastFogAt >= 40) {
      lastFogAt = now;
      buildFog();
    }

    // В C# — промежуточный угол (в базу пока не пишется), чтобы остальные
    // видели поворот и запоминалось разведанное.
    if (now - rotateHold.lastPost >= 100) {
      rotateHold.lastPost = now;
      post({ type: 'rotate', id: token.id, facing: token.facing, commit: false });
    }

    rotateRaf = requestAnimationFrame(rotateStep);
  }

  function finishRotation() {
    if (!rotateHold) return;
    const token = rotateHold.token;
    const applied = rotateHold.applied;
    const dir = rotateHold.dir;
    const frames = rotateHold.frames;
    const maxDt = rotateHold.maxDt;
    rotateHold = null;

    if (rotateRaf) { cancelAnimationFrame(rotateRaf); rotateRaf = 0; }
    if (!token) return;

    // Короткий тап: доводим до заметного шага, иначе поворота почти не видно.
    if (applied < 4) applyFacing(token, (token.facing || 0) + dir * (5 - applied));

    const facing = token.facing;
    buildFog();
    buildFov();
    log('rot stop frames=' + frames + ' maxdt=' + maxDt.toFixed(0) + 'ms');
    post({ type: 'rotate', id: token.id, facing: facing, commit: true });
  }

  window.addEventListener('contextmenu', e => e.preventDefault());
  window.addEventListener('pointerdown', e => {
    // Подменю — отдельный элемент поверх страницы, клик по нему не должен
    // закрывать меню (иначе состояния не переключить подряд).
    if (menuEl && menuEl.style.display === 'block' &&
        !menuEl.contains(e.target) &&
        !(menuSubEl && menuSubEl.contains(e.target)) &&
        e.target !== app.view) hideMenu();
  }, true);

  window.addEventListener('resize', () => {
    try { app.renderer.resize(window.innerWidth, window.innerHeight); } catch (_) { }
    // Не fit(): масштаб и панорама — выбор игрока, размер окна их не сбрасывает.
    keepViewOnResize();
  });
  setTimeout(() => {
    try { app.renderer.resize(window.innerWidth, window.innerHeight); } catch (_) { }
    if (hasFit) keepViewOnResize();
  }, 50);

  if (window.ResizeObserver) {
    // WebView прячется под оверлеями окна персонажа и возвращается — размер
    // при этом меняется. Автоподгонка тут сбрасывала зум у клиента.
    new ResizeObserver(() => { if (hasFit) keepViewOnResize(); })
      .observe(document.getElementById('stage'));
  }

  // ─────────────────────────── приём из C# ───────────────────────────

  function apply(payload) {
    let data = payload;
    if (typeof data === 'string') {
      try { data = JSON.parse(data); } catch (_) { return; }
    }
    if (!data || typeof data !== 'object') return;

    try {
      if (data.type === 'scene') applyScene(data);
      else if (data.type === 'fog') applyFogUpdate(data);
      else if (data.type === 'facing') applyFacingUpdate(data);
      else if (data.type === 'seenAdd') applySeenAdd(data);
      else if (data.type === 'tokenLive') applyTokenLive(data);
      else if (data.type === 'shapeLive') applyShapeLive(data);
      else if (data.type === 'viewFit') fit();
      else if (data.type === 'viewZoom') zoomBy(data.factor || 1);
      else if (data.type === 'rotateStart') beginRotation(findToken(data.id), data.dir, data.speed, data.facing);
      else if (data.type === 'rotateStop') finishRotation();
      else if (data.type === 'rotateBy') rotateBy(findToken(data.id), data.deg || 0);
      else if (data.type === 'pingAtCursor') {
        // Пинг от TAB: точку берём у курсора, отправляем в C# — он разошлёт
        // всем в комнате и вернёт эхо, которое и нарисует круги.
        post({ type: 'ping', x: Math.round(lastScenePointer.x), y: Math.round(lastScenePointer.y) });
      }
      else if (data.type === 'ping') addPing(data.x || 0, data.y || 0);
    } catch (err) {
      showError(err && err.stack || err);
    }
  }

  window.__hmwtApply = apply;

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

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', e => apply(e.data));
    window.chrome.webview.postMessage({ type: 'ready' });
  }

  log('renderer ready v1.3.9.2');
})();
