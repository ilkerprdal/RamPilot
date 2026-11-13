// RamPilot control panel — page side. Data comes from the C# bridge (docs/IPC.md, protocol v2 Status).
// Every user-visible string comes from i18n.js; engine data is inserted with textContent only.
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const isNum = (v) => typeof v === 'number' && Number.isFinite(v);
  const isStr = (v) => typeof v === 'string' && v.length > 0;

  // ---------- Language ----------
  const I18N = window.RP_I18N;
  const params = new URLSearchParams(location.search);
  const LANG_KEY = 'rampilot.lang';
  const supported = (l) => (l === 'tr' || l === 'en' ? l : null);

  function storedLang() {
    try { return supported(localStorage.getItem(LANG_KEY)); } catch { return null; }
  }
  function initialLang() {
    // --lang flag (testing) > manual choice > Windows display language > browser language.
    return supported(params.get('lang')) || storedLang() || supported(params.get('osLang'))
      || (String(navigator.language || '').toLowerCase().startsWith('tr') ? 'tr' : 'en');
  }

  let lang = initialLang();
  const has = (key) => Object.prototype.hasOwnProperty.call(I18N.en, key);
  function t(key, vars) {
    const table = I18N[lang] || I18N.en;
    let s = Object.prototype.hasOwnProperty.call(table, key) ? table[key] : (has(key) ? I18N.en[key] : key);
    if (vars) s = s.replace(/\{(\w+)\}/g, (_, k) => (vars[k] === undefined || vars[k] === null ? '—' : String(vars[k])));
    return s;
  }

  let nf0, nf1, nf2, nfAxis, timeHM, timeHMS;
  function buildFormatters() {
    const loc = t('locale');
    nf0 = new Intl.NumberFormat(loc, { maximumFractionDigits: 0 });
    nf1 = new Intl.NumberFormat(loc, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    nf2 = new Intl.NumberFormat(loc, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    nfAxis = new Intl.NumberFormat(loc, { maximumFractionDigits: 2 });
    timeHM = new Intl.DateTimeFormat(loc, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
    timeHMS = new Intl.DateTimeFormat(loc, { hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
  }

  function applyStatic() {
    document.documentElement.lang = lang;
    document.title = t('doc.title');
    for (const el of document.querySelectorAll('[data-i18n]')) el.textContent = t(el.dataset.i18n);
    for (const el of document.querySelectorAll('[data-i18n-title]')) el.title = t(el.dataset.i18nTitle);
    for (const el of document.querySelectorAll('[data-i18n-aria-label]')) el.setAttribute('aria-label', t(el.dataset.i18nAriaLabel));
    for (const b of document.querySelectorAll('.lang-toggle .seg-btn')) b.setAttribute('aria-pressed', String(b.dataset.lang === lang));
  }

  function setLang(next) {
    if (!supported(next) || next === lang) return;
    lang = next;
    try { localStorage.setItem(LANG_KEY, next); } catch { /* storage unavailable: session only */ }
    buildFormatters();
    applyStatic();
    eventsSig = '';
    renderConn();
    render();
  }
  for (const b of document.querySelectorAll('.lang-toggle .seg-btn')) b.addEventListener('click', () => setLang(b.dataset.lang));

  // ---------- Formatting ----------
  /** Formats MB as [number, unit]: 612 MB, 1.24 GB, 15.6 GB. */
  function mbParts(mb) {
    if (!isNum(mb)) return ['—', ''];
    if (Math.abs(mb) >= 1000) {
      const gb = mb / 1024;
      return [(Math.abs(gb) >= 10 ? nf1 : nf2).format(gb), 'GB'];
    }
    return [nf0.format(Math.round(mb)), 'MB'];
  }
  const fmtMB = (mb) => { const [n, u] = mbParts(mb); return u ? `${n} ${u}` : n; };
  const fmtGB1 = (mb) => (isNum(mb) ? `${nf1.format(mb / 1024)} GB` : '—');
  const fmtPct = (v) => (isNum(v) ? t('fmt.pct', { v: nf0.format(v) }) : '—');
  const fmtTime = (unix) => (isNum(unix) ? timeHMS.format(new Date(unix * 1000)) : isStr(unix) ? unix : '');
  function fmtUptime(s) {
    if (!isNum(s)) return '—';
    s = Math.max(0, Math.floor(s));
    if (s < 60) return t('up.s', { s });
    const m = Math.floor(s / 60), h = Math.floor(m / 60), d = Math.floor(h / 24);
    if (d > 0) return t('up.d', { d, h: h % 24 });
    if (h > 0) return t('up.h', { h, m: m % 60 });
    return t('up.m', { m });
  }
  const stateKey = (s) => (has(`state.${s}`) ? s : null);
  const errText = (code, detail) => (isStr(code) && has(`err.${code}`) ? t(`err.${code}`, { detail }) : t('err.unknown', { code: code || '?' }));

  // ---------- Bridge ----------
  const demo = params.has('demo');
  const bridge = window.chrome && window.chrome.webview;
  const send = (msg) => { if (bridge) bridge.postMessage(msg); };

  let status = null;
  let conn = 'connecting';      // connecting | up | down | starting | mismatch
  let engineMsg = null;         // last engine envelope (error, detail, protocol info)
  let quitRequested = false;
  let startRequestedAt = 0;
  let lastUpdate = 0;
  const pendingApps = new Map(); // exe -> { enabled, at }
  let pendingAutostart = null;   // { enabled, at }
  let pendingPause = null;       // { paused, at }
  const PENDING_MS = 4000;

  if (bridge) {
    bridge.addEventListener('message', (e) => {
      const m = e.data;
      if (!m || typeof m !== 'object') return;
      if (m.type === 'status' && m.data && typeof m.data === 'object') onStatus(m.data);
      else if (m.type === 'engine') onEngine(m);
      else if (m.type === 'result') onResult(m);
    });
  }

  function onStatus(data) {
    status = data;
    lastUpdate = Date.now();
    if (conn !== 'up') { conn = 'up'; quitRequested = false; engineMsg = null; }
    render();
  }

  function onEngine(m) {
    const state = m.state;
    if (state === 'connecting' && conn === 'up') return;
    if (state === 'down' && conn === 'starting' && !m.error && Date.now() - startRequestedAt < 15000) return;
    conn = ['connecting', 'down', 'starting', 'mismatch'].includes(state) ? state : 'down';
    engineMsg = m;
    renderConn();
  }

  function onResult(m) {
    if (m.cmd === 'setApp') pendingApps.clear();
    if (m.cmd === 'setAutostart') pendingAutostart = null;
    if (m.cmd === 'pause' || m.cmd === 'resume') pendingPause = null;
    if (!m.ok) {
      toast(t('toast.failed', { reason: errText(m.error) }));
      if (m.cmd === 'quit') quitRequested = false;
      if (status) render();
    }
  }

  // ---------- Connection view ----------
  let connectingTimer = 0;
  function renderConn() {
    const pill = $('statePill');
    const up = conn === 'up';
    let pillState = conn;
    let text = t(`conn.${conn}`);
    if (up) {
      const paused = pendingPause ? pendingPause.paused : !!(status && status.paused);
      pillState = paused ? 'paused' : 'active';
      text = t(paused ? 'conn.paused' : 'conn.active');
    }
    pill.dataset.state = pillState;
    $('stateText').textContent = text;
    $('app').dataset.conn = conn;
    for (const id of ['btnPause', 'swAutostart', 'btnQuit']) $(id).disabled = !up;

    const overlay = $('overlay');
    if (up) {
      overlay.hidden = true;
      clearTimeout(connectingTimer);
      connectingTimer = 0;
      return;
    }
    if (conn === 'connecting') {
      // The first connection is usually instant; only show the overlay after a short delay.
      if (!connectingTimer && overlay.hidden) {
        connectingTimer = setTimeout(() => { connectingTimer = 0; if (conn === 'connecting') showOverlay(); }, 900);
      }
      return;
    }
    showOverlay();
  }

  function showOverlay() {
    const overlay = $('overlay');
    const title = $('downTitle'), body = $('downText'), hint = $('downHint'), btn = $('btnStart');
    overlay.dataset.variant = conn;
    btn.disabled = false;
    if (conn === 'connecting') {
      title.textContent = t('overlay.connectingTitle');
      body.textContent = t('overlay.connectingBody');
      hint.textContent = '';
    } else if (conn === 'mismatch') {
      const m = engineMsg || {};
      title.textContent = t('overlay.mismatchTitle');
      body.textContent = t('overlay.mismatchBody', {
        panel: isNum(m.panelProtocol) ? m.panelProtocol : 2,
        engine: isNum(m.engineProtocol) ? m.engineProtocol : t('overlay.unknown'),
        version: isStr(m.engineVersion) ? m.engineVersion : t('overlay.unknown'),
      });
      hint.textContent = t('overlay.mismatchHint');
    } else if (conn === 'starting') {
      const late = Date.now() - startRequestedAt > 15000;
      title.textContent = t(late ? 'overlay.lateTitle' : 'overlay.startingTitle');
      body.textContent = t(late ? 'overlay.lateBody' : 'overlay.startingBody');
      btn.disabled = !late;
      btn.textContent = t(late ? 'overlay.retry' : 'overlay.starting');
      hint.textContent = '';
    } else {
      title.textContent = t(quitRequested ? 'overlay.quitTitle' : 'overlay.downTitle');
      body.textContent = t(quitRequested ? 'overlay.quitBody' : 'overlay.downBody');
      btn.textContent = t('overlay.start');
      hint.textContent = engineMsg && isStr(engineMsg.error) ? errText(engineMsg.error, engineMsg.detail) : t('overlay.retryHint');
    }
    overlay.hidden = false;
  }

  setInterval(() => {
    if (conn === 'starting') showOverlay();
    if (conn === 'up' && lastUpdate && Date.now() - lastUpdate > 1500) renderFootNote();
  }, 1000);

  $('btnStart').addEventListener('click', () => {
    conn = 'starting';
    startRequestedAt = Date.now();
    engineMsg = null;
    send({ panel: 'startEngine' });
    renderConn();
  });

  // ---------- Main render ----------
  function render() {
    renderConn();
    if (!status) return;
    const s = status;
    const eng = s.engine || {};

    $('uptime').textContent = fmtUptime(s.uptimeSec);
    $('engineMem').textContent = isNum(eng.wsMb) ? `${nf1.format(eng.wsMb)} MB` : '—';
    $('engineMemItem').title = isNum(eng.wsMb) && isNum(eng.privateMb)
      ? t('meta.engineMemTip', { ws: `${nf1.format(eng.wsMb)} MB`, priv: `${nf1.format(eng.privateMb)} MB` }) : '';
    $('engineVersion').textContent = isStr(s.engineVersion) ? `v${s.engineVersion}` : '';

    renderSavings(s);
    renderRam(s.system || {});
    renderGovernor(s.governor || {});
    drawCharts();
    if (!$('histTableWrap').hidden) renderTable();
    renderEvents(Array.isArray(s.events) ? s.events : []);
    renderApps(Array.isArray(s.apps) ? s.apps : []);
    renderFooter(s);
  }

  function setSegments(bar, parts) {
    const visible = [];
    for (const seg of bar.children) {
      const v = Math.max(0, parts[seg.dataset.key] || 0);
      seg.style.flexGrow = String(v);
      seg.hidden = !(v > 0);
      seg.dataset.value = String(v);
      seg.classList.remove('only');
      if (v > 0) visible.push(seg);
    }
    if (visible.length === 1) visible[0].classList.add('only');
  }

  function renderSavings(s) {
    const sv = s.savings || {};
    const reclaimed = isNum(sv.reclaimedMb) ? Math.max(0, sv.reclaimedMb) : null;
    const cost = isNum(sv.storeCostMb) ? Math.max(0, sv.storeCostMb) : null;
    let net = isNum(sv.netMb) ? Math.max(0, sv.netMb) : null;
    if (net === null && reclaimed !== null && cost !== null) net = Math.max(0, reclaimed - cost);

    const [n, u] = mbParts(net ?? 0);
    $('heroNum').textContent = net === null ? '—' : n;
    $('heroUnit').textContent = net === null ? '' : u;
    $('bdReclaimed').textContent = fmtMB(reclaimed);
    $('bdCost').textContent = cost === null ? '—' : t('fmt.minus', { v: fmtMB(cost) });
    $('bdNet').textContent = fmtMB(net);

    const bar = $('savingsBar');
    setSegments(bar, { net: net || 0, cost: cost || 0 });
    bar.setAttribute('aria-label', t('net.barLabel', { reclaimed: fmtMB(reclaimed), net: fmtMB(net), cost: fmtMB(cost) }));

    const paused = pendingPause ? pendingPause.paused : !!s.paused;
    $('heroNote').textContent = paused ? t('net.pausedSub') : t('net.note');
  }

  function renderRam(sys) {
    const total = sys.totalMb, avail = sys.availMb, comp = isNum(sys.compressionMb) ? sys.compressionMb : 0;
    const aside = [];
    if (isNum(sys.loadPct)) aside.push(t('ram.load', { v: fmtPct(sys.loadPct) }));
    if (isNum(sys.commitPct)) aside.push(t('ram.commit', { v: fmtPct(sys.commitPct) }));
    $('ramLoad').textContent = aside.join(' · ');
    if (!isNum(total) || !isNum(avail) || total <= 0) return;
    const used = Math.max(0, total - avail);
    const usedOnly = Math.max(0, used - comp);
    $('ramUsed').textContent = t('ram.inUse', { v: fmtGB1(used) });
    $('ramTotal').textContent = t('ram.of', { v: fmtGB1(total) });
    setSegments($('ramBar'), { used: usedOnly, comp, avail });
    for (const seg of $('ramBar').children) seg.dataset.share = String(Number(seg.dataset.value) / total);
    $('lgUsed').textContent = fmtGB1(usedOnly);
    $('lgComp').textContent = fmtMB(comp);
    $('lgAvail').textContent = fmtGB1(avail);
    $('ramBar').setAttribute('aria-label', t('ram.barLabel', { total: fmtGB1(total), used: fmtGB1(usedOnly), comp: fmtMB(comp), avail: fmtGB1(avail) }));
  }

  function renderGovernor(g) {
    const level = ['low', 'medium', 'high'].includes(g.pressure) ? g.pressure : 'unknown';
    $('governor').dataset.level = level;
    $('govLevel').textContent = level === 'unknown' ? '—' : t(`gov.level.${level}`);
    $('govHint').textContent = t(`gov.hint.${level}`);
    const aggr = isNum(g.aggressiveness) ? Math.min(1, Math.max(0, g.aggressiveness)) : null;
    $('govAggr').textContent = aggr === null ? '—' : fmtPct(Math.round(aggr * 100));
    $('aggrFill').style.width = `${aggr === null ? 0 : (aggr * 100).toFixed(1)}%`;
  }

  // Tooltips on segmented bars
  const SEG_INFO = {
    used: ['ram.used', '--s-used'], comp: ['ram.compressedTip', '--s-comp'], avail: ['ram.available', '--s-avail'],
    net: ['net.net', '--s-net'], cost: ['net.storeCost', '--s-comp'],
  };
  for (const seg of document.querySelectorAll('.segbar .seg')) {
    seg.addEventListener('pointermove', (e) => {
      const v = Number(seg.dataset.value);
      const [key, color] = SEG_INFO[seg.dataset.key];
      const share = seg.dataset.share ? ` · ${fmtPct(Math.round(Number(seg.dataset.share) * 100))}` : '';
      showTooltip(e.clientX, e.clientY, null, [{ v: fmtMB(v) + share, name: t(key), color: `var(${color})`, box: true }]);
    });
    seg.addEventListener('pointerleave', hideTooltip);
  }

  // ---------- Charts (two small multiples sharing one time axis; never a dual y-axis) ----------
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const WINDOW_SEC = 30 * 60;
  const M = { l: 50, r: 62, t: 8 };
  let hoverT = null;
  const geo = {};
  const SERIES = [
    { key: 'avail', field: 'availMb', el: 'chartAvail', zero: false, xAxis: false, name: 'chart.avail', color: '--s-avail' },
    { key: 'net', field: 'netMb', el: 'chartNet', zero: true, xAxis: true, name: 'chart.net', color: '--s-net' },
  ];

  function niceStep(raw) {
    const exp = Math.pow(10, Math.floor(Math.log10(raw)));
    const f = raw / exp;
    return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * exp;
  }

  /** Clean axis for a value range: MB or GB, about three gridlines. */
  function axisFor(min, max, zero) {
    const useGB = max >= 1000;
    const div = useGB ? 1024 : 1;
    let lo = (zero ? 0 : min) / div, hi = max / div;
    const minSpan = useGB ? 0.25 : 50;
    if (hi - lo < minSpan) {
      const c = (hi + lo) / 2;
      lo = zero ? 0 : c - minSpan / 2;
      hi = zero ? minSpan : c + minSpan / 2;
    }
    const step = niceStep((hi - lo) / 2);
    lo = zero ? 0 : Math.floor(lo / step) * step;
    hi = Math.ceil(hi / step) * step;
    if (hi === lo) hi = lo + step;
    const ticks = [];
    for (let v = lo; v <= hi + step * 1e-6; v += step) ticks.push(v);
    return { lo: lo * div, hi: hi * div, ticks: ticks.map((v) => ({ mb: v * div, label: `${nfAxis.format(v)} ${useGB ? 'GB' : 'MB'}` })) };
  }

  function el(tag, attrs, parent) {
    const n = document.createElementNS(SVG_NS, tag);
    for (const k in attrs) n.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(n);
    return n;
  }

  const historyPoints = () => (status && Array.isArray(status.history) ? status.history : []).filter((p) => p && isNum(p.t));

  function drawCharts() {
    const hist = historyPoints();
    const enough = hist.length >= 2;
    $('chartEmpty').hidden = enough;
    for (const s of SERIES) $(s.el).hidden = !enough;
    if (!enough) return;
    const tMax = hist[hist.length - 1].t;
    const tMin = tMax - WINDOW_SEC;
    const pts = hist.filter((p) => p.t >= tMin);
    for (const s of SERIES) drawOne(s, $(s.el).querySelector('svg'), pts, tMin, tMax);
    drawHover();
  }

  function drawOne(series, svg, pts, tMin, tMax) {
    const { key, field, zero, xAxis } = series;
    const W = Math.max(200, svg.clientWidth || svg.parentElement.clientWidth);
    const H = xAxis ? 96 : 74;
    svg.style.height = `${H}px`;
    svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
    svg.replaceChildren();
    delete geo[key];
    const plotB = xAxis ? H - 22 : H - 6;
    const vals = pts.map((p) => p[field]).filter(isNum);
    if (!vals.length) return;
    const ax = axisFor(Math.min(...vals), Math.max(...vals), zero);
    const x = (tt) => M.l + ((tt - tMin) / (tMax - tMin)) * (W - M.l - M.r);
    const y = (v) => plotB - ((v - ax.lo) / (ax.hi - ax.lo)) * (plotB - M.t);
    geo[key] = { svg, pts, field, x, y, plotB, W, tMin, tMax };

    for (const tk of ax.ticks) {
      const yy = Math.round(y(tk.mb)) + 0.5;
      el('line', { x1: M.l, x2: W - M.r, y1: yy, y2: yy, class: tk.mb === ax.lo && zero ? 'baseline' : 'grid' }, svg);
      el('text', { x: M.l - 8, y: yy + 4, 'text-anchor': 'end', class: 'tick' }, svg).textContent = tk.label;
    }
    if (xAxis) {
      for (let tt = Math.ceil(tMin / 300) * 300; tt <= tMax; tt += 300) {
        const xx = x(tt);
        if (xx < M.l + 14 || xx > W - M.r - 14) continue;
        el('text', { x: xx, y: H - 5, 'text-anchor': 'middle', class: 'tick' }, svg).textContent = timeHM.format(new Date(tt * 1000));
      }
    }

    // The line breaks over gaps longer than 25 s; missing data is never interpolated.
    let d = '', area = '', prev = null, segStart = null;
    const base = y(ax.lo).toFixed(1);
    const closeArea = () => { if (segStart !== null && prev) area += `L${x(prev.t).toFixed(1)},${base}L${x(segStart).toFixed(1)},${base}Z`; };
    for (const p of pts) {
      const v = p[field];
      if (!isNum(v)) continue;
      const px = x(p.t).toFixed(1), py = y(v).toFixed(1);
      if (!prev || p.t - prev.t > 25) {
        if (zero) closeArea();
        d += `M${px},${py}`;
        if (zero) { area += `M${px},${py}`; segStart = p.t; }
      } else {
        d += `L${px},${py}`;
        if (zero) area += `L${px},${py}`;
      }
      prev = p;
    }
    if (zero) { closeArea(); el('path', { d: area, class: `area ${key}` }, svg); }
    el('path', { d, class: `line ${key}` }, svg);

    const last = pts[pts.length - 1];
    if (isNum(last[field])) {
      el('circle', { cx: x(last.t), cy: y(last[field]), r: 4, class: `end-dot ${key}` }, svg);
      el('text', { x: x(last.t) + 9, y: y(last[field]) + 4, class: 'end-label' }, svg).textContent = fmtMB(last[field]);
    }
    el('g', { class: 'hover-layer' }, svg);
  }

  function nearestPoint(tt) {
    const g = geo.net || geo.avail;
    if (!g) return null;
    let best = null, bd = Infinity;
    for (const p of g.pts) { const dd = Math.abs(p.t - tt); if (dd < bd) { bd = dd; best = p; } }
    return best;
  }

  let lastPointer = null;
  function drawHover(clientPos) {
    const p = hoverT === null ? null : nearestPoint(hoverT);
    for (const s of SERIES) {
      const g = geo[s.key];
      const layer = g && g.svg.querySelector('.hover-layer');
      if (!layer) continue;
      layer.replaceChildren();
      if (!p) continue;
      const xx = Math.round(g.x(p.t)) + 0.5;
      el('line', { x1: xx, x2: xx, y1: M.t - 4, y2: g.plotB, class: 'crosshair' }, layer);
      if (isNum(p[g.field])) el('circle', { cx: g.x(p.t), cy: g.y(p[g.field]), r: 4.5, class: `hover-dot ${s.key}` }, layer);
    }
    if (!p) { hideTooltip(); return; }
    let cx, cy;
    if (clientPos) { cx = clientPos.x; cy = clientPos.y; } else {
      const g = geo.avail || geo.net;
      const r = g.svg.getBoundingClientRect();
      cx = r.left + g.x(p.t);
      cy = r.top + 30;
    }
    showTooltip(cx, cy, fmtTime(p.t), SERIES.map((s) => ({ v: fmtMB(p[s.field]), name: t(s.name), color: `var(${s.color})` })));
  }

  const chartsEl = $('charts');
  chartsEl.addEventListener('pointermove', (e) => {
    const g = geo.avail || geo.net;
    if (!g) return;
    const r = g.svg.getBoundingClientRect();
    const px = Math.min(Math.max(e.clientX - r.left, M.l), g.W - M.r);
    hoverT = g.tMin + ((px - M.l) / (g.W - M.l - M.r)) * (g.tMax - g.tMin);
    lastPointer = { x: e.clientX, y: e.clientY };
    drawHover(lastPointer);
  });
  chartsEl.addEventListener('pointerleave', () => { hoverT = null; lastPointer = null; drawHover(); });
  chartsEl.addEventListener('keydown', (e) => {
    const g = geo.net || geo.avail;
    if (!g || !g.pts.length) return;
    const ts = g.pts.map((p) => p.t);
    let i = hoverT === null ? ts.length - 1 : ts.indexOf(nearestPoint(hoverT).t);
    if (e.key === 'ArrowLeft') i = Math.max(0, i - 1);
    else if (e.key === 'ArrowRight') i = Math.min(ts.length - 1, i + 1);
    else if (e.key === 'Home') i = 0;
    else if (e.key === 'End') i = ts.length - 1;
    else if (e.key === 'Escape') { hoverT = null; drawHover(); return; } else return;
    e.preventDefault();
    hoverT = ts[i];
    lastPointer = null;
    drawHover();
  });
  chartsEl.addEventListener('blur', () => { if (!lastPointer) { hoverT = null; drawHover(); } });
  new ResizeObserver(() => { if (status) drawCharts(); }).observe(chartsEl);

  function setView(table) {
    $('viewChart').setAttribute('aria-pressed', String(!table));
    $('viewTable').setAttribute('aria-pressed', String(table));
    $('charts').hidden = table;
    $('histTableWrap').hidden = !table;
    if (table) renderTable(); else drawCharts();
  }
  $('viewChart').addEventListener('click', () => setView(false));
  $('viewTable').addEventListener('click', () => setView(true));

  function renderTable() {
    const hist = historyPoints();
    const rows = [];
    for (let i = hist.length - 1; i >= 0; i -= 6) { // one row per minute, newest first
      const p = hist[i];
      const tr = document.createElement('tr');
      for (const [txt, cls] of [[fmtTime(p.t), ''], [fmtMB(p.availMb), 'num'], [fmtMB(p.netMb), 'num']]) {
        const td = document.createElement('td');
        td.textContent = txt;
        if (cls) td.className = cls;
        tr.appendChild(td);
      }
      rows.push(tr);
    }
    if (!rows.length) {
      const tr = document.createElement('tr'), td = document.createElement('td');
      td.colSpan = 3;
      td.textContent = t('table.noData');
      tr.appendChild(td);
      rows.push(tr);
    }
    $('histTable').replaceChildren(...rows);
  }

  // ---------- Tooltip ----------
  const tip = $('tooltip');
  function showTooltip(cx, cy, title, rows) {
    tip.replaceChildren();
    if (title) {
      const tt = document.createElement('div');
      tt.className = 'tt-title';
      tt.textContent = title;
      tip.appendChild(tt);
    }
    for (const r of rows) {
      const row = document.createElement('div');
      row.className = 'tt-row';
      const k = document.createElement('span');
      k.className = 'line-key';
      k.style.background = r.color;
      if (r.box) { k.style.width = '10px'; k.style.height = '10px'; k.style.borderRadius = '3px'; }
      const v = document.createElement('span');
      v.className = 'tt-val';
      v.textContent = r.v;
      const n = document.createElement('span');
      n.className = 'tt-name';
      n.textContent = r.name;
      row.append(k, v, n);
      tip.appendChild(row);
    }
    tip.hidden = false;
    const w = tip.offsetWidth, h = tip.offsetHeight;
    let x = cx + 14, y = cy - h - 10;
    if (x + w > window.innerWidth - 8) x = cx - w - 14;
    if (y < 8) y = cy + 16;
    tip.style.left = `${Math.max(8, x)}px`;
    tip.style.top = `${y}px`;
  }
  function hideTooltip() { tip.hidden = true; }

  // ---------- Events (structured codes, localized here) ----------
  function appName(exe) {
    if (!isStr(exe)) return '—';
    const a = status && Array.isArray(status.apps) ? status.apps.find((x) => x && x.exe === exe) : null;
    return a && isStr(a.name) ? a.name : exe;
  }
  const roleName = (r) => (has(`role.${r}`) ? t(`role.${r}`) : String(r));
  const reasonName = (r) => (!isStr(r) ? '—' : has(`reason.${r}`) ? t(`reason.${r}`) : r);

  /** Returns { text, unknown }. Unknown codes and missing fields degrade to a generic line or dashes. */
  function eventText(ev) {
    const code = isStr(ev.code) ? ev.code : '';
    if (!code || !has(`ev.${code}`) || code === 'unknown' || code === 'who') {
      return { text: t('ev.unknown', { code: code || '?' }), unknown: true };
    }
    const app = appName(ev.app);
    const who = isStr(ev.role) ? t('ev.who', { app, role: roleName(ev.role) }) : app;
    return {
      text: t(`ev.${code}`, {
        app, who,
        cap: isNum(ev.capMb) ? fmtMB(ev.capMb) : null,
        prev: isNum(ev.prevCapMb) ? fmtMB(ev.prevCapMb) : null,
        reason: reasonName(ev.reason),
        count: isNum(ev.count) ? nf0.format(ev.count) : null,
        detail: isStr(ev.detail) ? ev.detail : null,
      }),
      unknown: false,
    };
  }

  const KINDS = ['cap', 'free', 'warn', 'info'];
  let eventsSig = '';
  const seenEvents = new Set();
  let eventsInitialized = false;
  function renderEvents(events) {
    const last = events[events.length - 1];
    const sig = `${lang}|${events.length}|${last ? `${last.t}${last.code}${last.app}${last.capMb}` : ''}`;
    $('eventsCount').textContent = events.length ? t('events.count', { n: events.length }) : '';
    if (sig === eventsSig) return;
    eventsSig = sig;
    const items = [];
    for (let i = events.length - 1; i >= 0; i--) {
      const ev = events[i];
      if (!ev || typeof ev !== 'object') continue;
      const key = JSON.stringify([ev.t, ev.code, ev.app, ev.role, ev.capMb, ev.prevCapMb, ev.reason]);
      const kind = KINDS.includes(ev.kind) ? ev.kind : 'info';
      const { text, unknown } = eventText(ev);
      const li = document.createElement('li');
      li.className = 'event';
      li.dataset.kind = kind;
      li.dataset.unknown = String(unknown);
      if (eventsInitialized && !seenEvents.has(key)) li.classList.add('event-new');
      seenEvents.add(key);
      const time = document.createElement('span');
      time.className = 'event-time';
      time.textContent = fmtTime(ev.t);
      const mark = document.createElement('span');
      mark.className = 'event-mark';
      mark.setAttribute('role', 'img');
      mark.setAttribute('aria-label', t(`kind.${kind}`));
      mark.title = t(`kind.${kind}`);
      const tx = document.createElement('span');
      tx.className = 'event-text';
      tx.textContent = text;
      li.append(time, mark, tx);
      items.push(li);
    }
    if (!items.length) {
      const li = document.createElement('li');
      li.className = 'empty-note';
      li.textContent = t('events.empty');
      items.push(li);
    }
    $('events').replaceChildren(...items);
    eventsInitialized = true;
    if (seenEvents.size > 500) seenEvents.clear();
  }

  // ---------- Apps ----------
  const AUDIO_ICON = 'M2.5 6.2h2.2L8 3.5v9L4.7 9.8H2.5zM10.5 5.8a3 3 0 0 1 0 4.4M12.3 4a5.5 5.5 0 0 1 0 8';
  const rows = new Map(); // exe -> row refs
  function makeTag(cls, withIcon) {
    const tag = document.createElement('span');
    tag.className = `tag ${cls}`;
    if (withIcon) {
      const svg = document.createElementNS(SVG_NS, 'svg');
      svg.setAttribute('viewBox', '0 0 16 16');
      svg.setAttribute('aria-hidden', 'true');
      el('path', { d: AUDIO_ICON }, svg);
      tag.appendChild(svg);
    }
    const label = document.createElement('span');
    tag.appendChild(label);
    tag.hidden = true;
    return { tag, label };
  }

  function makeRow(exe) {
    const div = (cls) => { const d = document.createElement('div'); d.className = cls; return d; };
    const span = (cls) => { const s = document.createElement('span'); if (cls) s.className = cls; return s; };
    const row = div('app-row apps-grid');
    row.setAttribute('role', 'listitem');
    const id = div('app-id');
    const name = div('app-name');
    const nameText = span('app-name-text');
    const fg = makeTag('tag-fg', false);
    const audio = makeTag('tag-audio', true);
    name.append(nameText, audio.tag, fg.tag);
    const sub = div('app-exe');
    id.append(name, sub);

    const badge = span('badge');
    const dot = span('badge-dot');
    dot.setAttribute('aria-hidden', 'true');
    const badgeText = span('');
    badge.append(dot, badgeText);

    const priv = span('cell-val');
    const cap = span('cell-val');
    const savedCell = div('saved-cell');
    const savedLine = div('saved-line');
    const saved = span('cell-val');
    const savedPct = span('saved-pct');
    savedLine.append(savedPct, saved);
    const meter = div('meter');
    meter.setAttribute('aria-hidden', 'true');
    const fill = div('meter-fill');
    meter.appendChild(fill);
    savedCell.append(savedLine, meter);

    const sw = document.createElement('button');
    sw.type = 'button';
    sw.className = 'switch';
    sw.setAttribute('role', 'switch');
    sw.appendChild(span('switch-thumb'));
    sw.addEventListener('click', () => {
      const cur = sw.getAttribute('aria-checked') === 'true';
      pendingApps.set(exe, { enabled: !cur, at: Date.now() });
      sw.setAttribute('aria-checked', String(!cur));
      sw.dataset.pending = 'true';
      send({ cmd: 'setApp', exe, enabled: !cur });
    });

    row.append(id, badge, priv, cap, savedCell, sw);
    return { el: row, nameText, fg, audio, sub, badge, badgeText, priv, cap, saved, savedPct, fill, sw };
  }

  function renderApps(apps) {
    const list = $('appList');
    const order = (a) => (!a.running ? 2 : !a.enabled ? 1 : 0);
    const sorted = apps.filter((a) => a && isStr(a.exe)).map((a, i) => ({ a, i }))
      .sort((p, q) => order(p.a) - order(q.a) || p.i - q.i).map((p) => p.a);

    list.querySelector(':scope > .empty-note')?.remove();
    const alive = new Set();
    let managed = 0, notRunning = 0, disabled = 0;
    sorted.forEach((a, idx) => {
      alive.add(a.exe);
      let r = rows.get(a.exe);
      if (!r) { r = makeRow(a.exe); rows.set(a.exe, r); }
      if (list.children[idx] !== r.el) list.insertBefore(r.el, list.children[idx] || null);

      const pend = pendingApps.get(a.exe);
      if (pend && (pend.enabled === !!a.enabled || Date.now() - pend.at > PENDING_MS)) pendingApps.delete(a.exe);
      const stillPending = pendingApps.has(a.exe);
      const enabled = stillPending ? pendingApps.get(a.exe).enabled : !!a.enabled;
      if (!a.running) notRunning++; else if (!a.enabled) disabled++; else managed++;

      const name = isStr(a.name) ? a.name : a.exe;
      r.nameText.textContent = name;
      r.fg.label.textContent = t('apps.focused');
      r.fg.tag.hidden = !(a.running && a.focused);
      r.audio.label.textContent = t('apps.audio');
      r.audio.tag.hidden = !(a.running && a.audio);
      r.audio.tag.title = t('apps.audioTip');
      r.sub.textContent = a.running && isNum(a.procCount) && a.procCount > 0
        ? t(a.procCount === 1 ? 'apps.procs1' : 'apps.procs', { exe: a.exe, n: nf0.format(a.procCount) }) : a.exe;

      const sk = stateKey(a.state);
      r.badge.dataset.state = sk || 'unknown';
      r.badgeText.textContent = sk ? t(`state.${sk}`) : (isStr(a.state) ? a.state : t('state.unknown'));
      r.badge.title = sk ? t(`stateTip.${sk}`) : '';

      r.el.dataset.dim = String(!a.running || !a.enabled);
      r.priv.textContent = a.running && isNum(a.privateWsMb) ? fmtMB(a.privateWsMb) : '—';
      r.priv.title = a.running && isNum(a.wsMb) ? t('apps.wsTip', { v: fmtMB(a.wsMb) }) : '';
      r.priv.classList.toggle('cell-muted', !a.running);
      const hasCap = a.running && isNum(a.capMb) && a.capMb > 0;
      r.cap.textContent = hasCap ? fmtMB(a.capMb) : '—';
      r.cap.classList.toggle('cell-muted', !hasCap);

      const rec = isNum(a.reclaimedMb) ? Math.max(0, a.reclaimedMb) : 0;
      const show = a.running && a.enabled;
      r.saved.textContent = show ? fmtMB(rec) : '—';
      r.saved.classList.toggle('cell-muted', !show || rec < 1);
      // Share of what the app would use without a cap: reclaimed / (current + reclaimed).
      const priv = isNum(a.privateWsMb) ? a.privateWsMb : 0;
      const share = show && rec > 0 && priv + rec > 0 ? Math.min(1, rec / (priv + rec)) : 0;
      r.savedPct.textContent = share >= 0.01 ? t('fmt.minus', { v: fmtPct(Math.round(share * 100)) }) : '';
      r.savedPct.title = share > 0 ? t('apps.pctTip') : '';
      r.fill.style.width = `${(share * 100).toFixed(1)}%`;

      r.sw.setAttribute('aria-checked', String(enabled));
      r.sw.setAttribute('aria-label', t('apps.switchAria', { app: name }));
      r.sw.title = t(enabled ? 'apps.switchOn' : 'apps.switchOff');
      r.sw.dataset.pending = String(stillPending);
      r.sw.disabled = conn !== 'up';
    });
    for (const [exe, r] of rows) if (!alive.has(exe)) { r.el.remove(); rows.delete(exe); }

    const parts = [t('apps.summaryManaged', { n: managed })];
    if (disabled) parts.push(t('apps.summaryDisabled', { n: disabled }));
    if (notRunning) parts.push(t('apps.summaryNotRunning', { n: notRunning }));
    $('appsSummary').textContent = sorted.length ? parts.join(' · ') : '';
    if (!sorted.length) {
      const p = document.createElement('p');
      p.className = 'empty-note';
      p.textContent = t('apps.empty');
      list.replaceChildren(p);
    }
  }

  // ---------- Footer ----------
  function renderFooter(s) {
    if (pendingPause && (pendingPause.paused === !!s.paused || Date.now() - pendingPause.at > PENDING_MS)) pendingPause = null;
    const paused = pendingPause ? pendingPause.paused : !!s.paused;
    $('btnPauseText').textContent = t(paused ? 'footer.resume' : 'footer.pause');
    $('pauseIconPath').setAttribute('d', paused ? 'M5.5 3.5 12.5 8l-7 4.5z' : 'M5 3.5v9M11 3.5v9');

    if (pendingAutostart && (pendingAutostart.enabled === !!s.autostart || Date.now() - pendingAutostart.at > PENDING_MS)) pendingAutostart = null;
    $('swAutostart').setAttribute('aria-checked', String(pendingAutostart ? pendingAutostart.enabled : !!s.autostart));
    $('swAutostart').dataset.pending = String(!!pendingAutostart);
    renderFootNote();
  }

  function renderFootNote() {
    if (!status) return;
    const paused = pendingPause ? pendingPause.paused : !!status.paused;
    let note = t(paused ? 'footer.pausedNote' : 'footer.live');
    if (Date.now() - lastUpdate > 1500) note = t('footer.stale');
    note += ` · ${timeHMS.format(new Date(lastUpdate))}`;
    if (demo) note += ` · ${t('footer.demo')}`;
    $('footNote').textContent = note;
  }

  $('btnPause').addEventListener('click', () => {
    if (!status) return;
    const paused = pendingPause ? pendingPause.paused : !!status.paused;
    pendingPause = { paused: !paused, at: Date.now() };
    send({ cmd: paused ? 'resume' : 'pause' });
    render();
  });

  $('swAutostart').addEventListener('click', () => {
    if (!status) return;
    const cur = $('swAutostart').getAttribute('aria-checked') === 'true';
    pendingAutostart = { enabled: !cur, at: Date.now() };
    send({ cmd: 'setAutostart', enabled: !cur });
    renderFooter(status);
  });

  const dlg = $('confirmQuit');
  $('btnQuit').addEventListener('click', () => { dlg.returnValue = ''; dlg.showModal(); });
  dlg.addEventListener('close', () => {
    if (dlg.returnValue !== 'ok') return;
    quitRequested = true;
    send({ cmd: 'quit' });
  });

  // ---------- Toast ----------
  let toastTimer = 0;
  function toast(text) {
    const el2 = $('toast');
    el2.textContent = text;
    el2.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el2.hidden = true; }, 4500);
  }

  // ---------- Start ----------
  buildFormatters();
  applyStatic();
  if (demo) $('demoTag').hidden = false;
  renderConn();
  send({ panel: 'ready' });
})();
