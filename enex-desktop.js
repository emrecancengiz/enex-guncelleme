/* Enex POS — masaüstü köprüsü
   claude.ai artifact ortamındaki window.claude.use("db" / "downloads" / "assets" / "user")
   yeteneklerini bilgisayarda çalışırken sağlar. Veriler tarayıcı profilinin IndexedDB
   deposunda (program klasoru\profil) tutulur. */
(function () {
  "use strict";
  if (window.claude && window.claude.use) return;

  const IDB_NAME = "enexpos_desktop", IDB_VER = 1;
  const rid = () => (crypto.randomUUID ? crypto.randomUUID().replace(/-/g, "").slice(0, 20) : Math.random().toString(36).slice(2, 12) + Date.now().toString(36));
  const clone = v => (v === undefined ? undefined : (typeof structuredClone === "function" ? structuredClone(v) : JSON.parse(JSON.stringify(v))));
  const err = (code, message) => Object.assign(new Error(message || code), {code});

  /* ---------- IndexedDB ---------- */
  let idbP = null;
  function idb() {
    if (idbP) return idbP;
    idbP = new Promise((res, rej) => {
      const r = indexedDB.open(IDB_NAME, IDB_VER);
      r.onupgradeneeded = () => {
        const d = r.result;
        if (!d.objectStoreNames.contains("docs")) d.createObjectStore("docs", {keyPath: "path"});
        if (!d.objectStoreNames.contains("assets")) d.createObjectStore("assets", {keyPath: "id"});
      };
      r.onsuccess = () => res(r.result);
      r.onerror = () => rej(r.error);
    });
    return idbP;
  }
  const tx = (store, mode, fn) => idb().then(d => new Promise((res, rej) => {
    const t = d.transaction(store, mode), s = t.objectStore(store);
    let out; try { out = fn(s); } catch (e) { rej(e); return; }
    t.oncomplete = () => res(out && out.result !== undefined ? out.result : out);
    t.onerror = () => rej(t.error); t.onabort = () => rej(t.error || err("unavailable", "işlem iptal"));
  }));
  const getAll = store => tx(store, "readonly", s => s.getAll());

  /* ---------- ağ ortak verisi (Enex Hizmet, ana kasa) ----------
     localStorage.enex_sync = {on, url, key, role}. Açıkken okumalar yerel kopyadan, yazmalar ana kasadaki depoya gider;
     değişiklikler uzun yoklama ile tüm kasalara dağıtılır. Ana kasaya ulaşılamazsa yazma "unavailable" hatası verir,
     program bu kayıtları kendi kuyruğunda bekletip bağlantı gelince gönderir. */
  let SY = null; try { SY = JSON.parse(localStorage.getItem("enex_sync") || "null"); } catch (e) {}
  const REMOTE = !!(SY && SY.on && SY.url && SY.key);
  const syncSt = {on: REMOTE, role: SY && SY.role, url: SY && SY.url, state: REMOTE ? "baglaniyor" : "kapali", seq: +(localStorage.getItem("enex_sync_seq") || 0), err: null, at: 0, pulled: 0};
  const syncEmit = () => { try { window.dispatchEvent(new CustomEvent("enexsync", {detail: Object.assign({}, syncSt)})); } catch (e) {} };
  const unavailable = m => err("unavailable", m || "Ana kasaya ulaşılamıyor (network)");
  async function rq(path, opt, ms) {
    const ctl = new AbortController(), t = setTimeout(() => ctl.abort(), ms || 8000);
    let r;
    try { r = await fetch(SY.url.replace(/\/+$/, "") + path, Object.assign({cache: "no-store", signal: ctl.signal, headers: {"X-Enex-Key": SY.key}}, opt || {})); }
    catch (e) { syncSt.state = "kopuk"; syncSt.err = "Ana kasaya ulaşılamıyor"; syncEmit(); throw unavailable(); }
    finally { clearTimeout(t); }
    let j = null; try { j = await r.json(); } catch (e) {}
    if (r.status === 401) { syncSt.state = "hata"; syncSt.err = "Ağ anahtarı yanlış"; syncEmit(); throw err("permission_denied", "Ağ anahtarı yanlış"); }
    if (r.status === 409 && j && j.v != null) return Object.assign({conflict: true}, j);
    if (!r.ok || !j) { if (r.status === 409) { syncSt.state = "hata"; syncSt.err = (j && j.err) || "Ana kasa hazır değil"; syncEmit(); } throw unavailable((j && j.err) || ("HTTP " + r.status)); }
    if (syncSt.state !== "bagli" && syncSt.state !== "esitleniyor") { syncSt.state = "bagli"; syncSt.err = null; syncEmit(); }
    return j;
  }
  const enc = encodeURIComponent;

  /* ---------- belge deposu (bellekte + IndexedDB) ---------- */
  const docs = new Map();            // path -> data (plain object)
  const leases = new Map();          // path -> {holder, until}
  let persistQ = Promise.resolve();
  const persist = fn => { persistQ = persistQ.catch(() => {}).then(fn); return persistQ; };

  const SEG = /^[A-Za-z0-9_\-.~:@+]+$/;
  function splitPath(p, wantDoc) {
    if (typeof p !== "string") throw new TypeError("yol metin olmalı");
    const segs = p.split("/").filter(s => s.length);
    if (!segs.length) throw new TypeError("boş yol");
    for (const s of segs) if (!SEG.test(s) || s === "." || s === "..") throw new TypeError("geçersiz yol parçası: " + s);
    if (wantDoc && segs.length % 2 !== 0) throw new TypeError("belge yolu çift sayıda parça içermeli (" + segs.length + "): " + p);
    if (!wantDoc && segs.length % 2 !== 1) throw new TypeError("koleksiyon yolu tek sayıda parça içermeli (" + segs.length + "): " + p);
    return segs;
  }
  const colOf = path => path.slice(0, path.lastIndexOf("/"));
  const idOf = path => path.slice(path.lastIndexOf("/") + 1);

  const META = {fromCache: false, hasPendingWrites: false};
  function docSnap(path) {
    const d = docs.get(path), data = d === undefined ? undefined : clone(d);
    return {id: idOf(path), exists: d !== undefined, data: () => data, metadata: META};
  }

  /* dinleyiciler */
  const listeners = new Set();   // {kind:"doc", path, next} | {kind:"query", col, q, next, last:Map}
  let notifyT = null; const dirtyDocs = new Set(), dirtyCols = new Set();
  function touched(path) { dirtyDocs.add(path); dirtyCols.add(colOf(path)); if (!notifyT) notifyT = setTimeout(flushNotify, 0); }
  function flushNotify() {
    notifyT = null;
    const dd = new Set(dirtyDocs), dc = new Set(dirtyCols); dirtyDocs.clear(); dirtyCols.clear();
    for (const l of [...listeners]) {
      try {
        if (l.kind === "doc") { if (dd.has(l.path)) l.next(docSnap(l.path)); }
        else if (dc.has(l.col)) deliverQuery(l);
      } catch (e) { console.error("onSnapshot dinleyici hatası", e); }
    }
  }

  function cmp(a, b) {
    if (a === b) return 0;
    if (a === undefined || a === null) return 1; if (b === undefined || b === null) return -1;
    if (typeof a === "number" && typeof b === "number") return a - b;
    return String(a) < String(b) ? -1 : 1;
  }
  function matches(data, f) {
    const v = data[f.field], w = f.value;
    switch (f.op) {
      case "==": return v === w || (v != null && w != null && JSON.stringify(v) === JSON.stringify(w));
      case "!=": return v !== w;
      case "<": return v !== undefined && cmp(v, w) < 0;
      case "<=": return v !== undefined && cmp(v, w) <= 0;
      case ">": return v !== undefined && cmp(v, w) > 0;
      case ">=": return v !== undefined && cmp(v, w) >= 0;
      case "in": return Array.isArray(w) && w.includes(v);
      case "not-in": return Array.isArray(w) && !w.includes(v);
      case "array-contains": return Array.isArray(v) && v.includes(w);
      default: return false;
    }
  }
  function runQuery(col, q) {
    const prefix = col + "/", out = [];
    for (const [path, data] of docs) {
      if (!path.startsWith(prefix) || path.indexOf("/", prefix.length) !== -1) continue;
      let ok = true; for (const f of q.filters) if (!matches(data, f)) { ok = false; break; }
      if (ok) out.push(path);
    }
    if (q.order) {
      const [field, dir] = q.order, m = dir === "desc" ? -1 : 1;
      out.sort((a, b) => {
        const x = docs.get(a)[field], y = docs.get(b)[field];
        if (x == null && y == null) return a < b ? -1 : 1;
        if (x == null) return 1; if (y == null) return -1;
        const r = cmp(x, y); return r === 0 ? (a < b ? -1 : 1) : r * m;
      });
    } else out.sort();
    return q.lim ? out.slice(0, q.lim) : out;
  }
  function querySnap(paths, prev) {
    const snaps = paths.map(docSnap);
    const changes = [];
    const cur = new Map(paths.map((p, i) => [p, i]));
    if (prev) {
      for (const [p, i] of prev) if (typeof i === "number" && !cur.has(p)) changes.push({type: "removed", doc: prev.get(p + "\u0000snap") || docSnap(p), oldIndex: i, newIndex: -1});
    }
    paths.forEach((p, i) => {
      if (!prev || !prev.has(p)) changes.push({type: "added", doc: snaps[i], oldIndex: -1, newIndex: i});
      else if (JSON.stringify(docs.get(p)) !== prev.get(p + "\u0000json")) changes.push({type: "modified", doc: snaps[i], oldIndex: prev.get(p), newIndex: i});
    });
    const state = new Map();
    paths.forEach((p, i) => { state.set(p, i); state.set(p + "\u0000json", JSON.stringify(docs.get(p))); state.set(p + "\u0000snap", snaps[i]); });
    return {snap: {docs: snaps, size: snaps.length, empty: !snaps.length, docChanges: () => changes, metadata: META}, state};
  }
  function deliverQuery(l) {
    const r = querySnap(runQuery(l.col, l.q), l.last); l.last = r.state; l.next(r.snap);
  }

  function subscribe(l) {
    listeners.add(l);
    setTimeout(() => { if (listeners.has(l)) { try { l.kind === "doc" ? l.next(docSnap(l.path)) : deliverQuery(l); } catch (e) { console.error(e); } } }, 0);
    return () => listeners.delete(l);
  }

  async function localSet(path, data) {
    const v = clone(data); docs.set(path, v); touched(path);
    await persist(() => tx("docs", "readwrite", s => s.put({path, data: v})));
  }
  async function localDel(path) {
    if (docs.delete(path)) touched(path);
    await persist(() => tx("docs", "readwrite", s => s.delete(path)));
  }
  async function remotePut(path, data, ifVer) {
    const body = data == null ? "" : JSON.stringify(data);
    return rq("/sync/put?p=" + enc(path) + (ifVer != null ? "&if=" + ifVer : "") + (data == null ? "&del=1" : ""), {method: "POST", body, headers: {"X-Enex-Key": SY.key, "Content-Type": "text/plain;charset=utf-8"}});
  }
  async function remoteGet(path) { return rq("/sync/get?p=" + enc(path)); }
  async function writeDoc(path, data) {
    if (REMOTE) { const r = await remotePut(path, data); if (r.conflict) throw err("aborted", "çakışma"); }
    await localSet(path, data);
  }
  // uzak modda birleştirerek güncelle: sunucudaki güncel hâli al, birleştir, sürüm tutuyorsa yaz (tutmuyorsa tekrar)
  async function remoteMerge(path, patch, mustExist) {
    for (let i = 0; i < 8; i++) {
      const cur = await remoteGet(path);
      if (cur.d == null && mustExist) throw err("invalid_argument", "güncellenecek belge yok: " + path);
      const merged = deepMerge(clone(cur.d || {}), patch);
      const r = await remotePut(path, merged, cur.v || 0);
      if (!r.conflict) { await localSet(path, merged); return; }
      await new Promise(res => setTimeout(res, 40 + Math.random() * 120));
    }
    throw err("busy", "Kayıt şu an başka kasada güncelleniyor");
  }
  function deepMerge(dst, src) {
    for (const [k, v] of Object.entries(src)) {
      if (v && typeof v === "object" && !Array.isArray(v) && dst[k] && typeof dst[k] === "object" && !Array.isArray(dst[k])) deepMerge(dst[k], v);
      else dst[k] = clone(v);
    }
    return dst;
  }
  function docRef(path) {
    splitPath(path, true);
    return {
      id: idOf(path), path,
      async get() {
        if (REMOTE) { try { const r = await remoteGet(path); if (r.d == null) { if (docs.has(path)) await localDel(path); } else await localSet(path, r.d); } catch (e) { if (e.code === "permission_denied") throw e; } }   // ulaşılamazsa yerel kopya
        return docSnap(path);
      },
      async set(data) {
        if (!data || typeof data !== "object" || Array.isArray(data)) throw err("invalid_argument", "belge gövdesi nesne olmalı");
        await writeDoc(path, data);
      },
      async update(data) {
        if (!data || typeof data !== "object" || Array.isArray(data)) throw err("invalid_argument", "belge gövdesi nesne olmalı");
        if (REMOTE) return remoteMerge(path, data, true);
        if (!docs.has(path)) throw err("invalid_argument", "güncellenecek belge yok: " + path);
        await writeDoc(path, deepMerge(clone(docs.get(path)), data));
      },
      async delete() {
        if (REMOTE) { await remotePut(path, null); }
        await localDel(path);
      },
      async acquire(opt) {
        if (REMOTE) {
          const ttl = Math.min(600000, Math.max(1000, +(opt && opt.ttlMs) || 30000)), holder = String((opt && opt.holder) || "");
          const r = await rq("/sync/lease?p=" + enc(path) + "&h=" + enc(holder) + "&ttl=" + ttl, {method: "POST"});
          if (!r.ok) return {acquired: false, expiresAt: new Date(Date.now() + 1000).toISOString()};
          if (opt && opt.data && typeof opt.data === "object") await remoteMerge(path, opt.data, false);
          return {acquired: true, version: 1, holder, expiresAt: new Date(Date.now() + ttl).toISOString()};
        }
        const now = Date.now(), ttl = Math.min(600000, Math.max(1000, +(opt && opt.ttlMs) || 30000)), holder = String((opt && opt.holder) || "");
        const cur = leases.get(path);
        if (cur && cur.until > now && cur.holder !== holder) return {acquired: false, expiresAt: new Date(cur.until).toISOString()};
        leases.set(path, {holder, until: now + ttl});
        if (opt && opt.data && typeof opt.data === "object") await writeDoc(path, deepMerge(clone(docs.get(path) || {}), opt.data));
        return {acquired: true, version: 1, holder, expiresAt: new Date(now + ttl).toISOString()};
      },
      onSnapshot(next, error) { return subscribe({kind: "doc", path, next: next || (() => {}), error}); },
      collection(sub) { return colRef(path + "/" + sub); },
    };
  }
  function makeQuery(col, q) {
    return {
      path: col,
      where(field, op, value) { return makeQuery(col, {filters: [...q.filters, {field, op, value}], order: q.order, lim: q.lim}); },
      orderBy(field, dir) { return makeQuery(col, {filters: q.filters, order: [field, dir || "asc"], lim: q.lim}); },
      limit(n) { return makeQuery(col, {filters: q.filters, order: q.order, lim: Math.max(1, Math.min(1000, +n || 1000))}); },
      async get() { return querySnap(runQuery(col, q)).snap; },
      onSnapshot(next, error) { return subscribe({kind: "query", col, q, next: next || (() => {}), error, last: null}); },
    };
  }
  function colRef(col) {
    splitPath(col, false);
    const q = makeQuery(col, {filters: [], order: null, lim: 0});
    q.doc = id => docRef(col + "/" + (id == null ? rid() : id));
    q.add = async data => { const r = docRef(col + "/" + rid()); await r.set(data); return r; };
    return q;
  }


  /* ilk açılışta (veritabanı boşsa) yanındaki enex-ilk-veri.js dosyasındaki deneme verisi yüklenir */
  function loadSeedScript() {
    return new Promise(res => {
      if (window.__enexSeed) return res(window.__enexSeed);
      const sc = document.createElement("script"); sc.src = "enex-ilk-veri.js?" + Date.now();
      sc.onload = () => res(window.__enexSeed || null); sc.onerror = () => res(null);
      (document.head || document.documentElement).appendChild(sc);
    });
  }
  async function seedFirstRun() {
    try {
      const seed = await loadSeedScript(); if (!seed || !seed.collections) return;
      const rows = [];
      for (const [col, m] of Object.entries(seed.collections)) for (const [id, data] of Object.entries(m || {})) { const path = col + "/" + id; docs.set(path, data); rows.push({path, data}); }
      await tx("docs", "readwrite", s => { for (const r of rows) s.put(r); });
      console.info("Enex POS: ilk veri yüklendi —", rows.length, "kayıt");
      window.__enexSeeded = rows.length;
    } catch (e) { console.error("İlk veri yüklenemedi", e); }
  }
  /* ana kasadan değişiklikleri çek: önce birikenler (sayfa sayfa), sonra uzun yoklama */
  async function applyChanges(ch) {
    if (!ch.length) return;
    for (const c of ch) { if (c.d == null) docs.delete(c.p); else docs.set(c.p, c.d); touched(c.p); }
    await persist(() => tx("docs", "readwrite", s => { for (const c of ch) { if (c.d == null) s.delete(c.p); else s.put({path: c.p, data: c.d}); } }));
  }
  async function pullOnce(wait) {
    const j = await rq("/sync/changes?since=" + syncSt.seq + "&wait=" + (wait || 0), null, (wait || 0) * 1000 + 15000);
    if (j.seq < syncSt.seq) { syncSt.seq = 0; }   // ana kasadaki depo sıfırlanmış: baştan
    await applyChanges(j.ch || []);
    syncSt.pulled += (j.ch || []).length;
    if ((j.ch || []).length) { syncSt.seq = j.last; localStorage.setItem("enex_sync_seq", String(syncSt.seq)); }
    syncSt.at = Date.now(); return j;
  }
  async function initialPull() {
    syncSt.state = "esitleniyor"; syncEmit();
    for (let i = 0; i < 10000; i++) { const j = await pullOnce(0); syncEmit(); if (!j.more) break; }
    syncSt.state = "bagli"; syncEmit();
  }
  let pulling = false;
  async function pullLoop() {
    if (pulling) return; pulling = true;
    for (;;) {
      try { const j = await pullOnce(20); if (j.more) continue; if (syncSt.state !== "bagli") { syncSt.state = "bagli"; syncSt.err = null; syncEmit(); } }
      catch (e) { if (syncSt.state !== "hata") { syncSt.state = "kopuk"; syncEmit(); } await new Promise(r => setTimeout(r, 5000)); }
    }
  }
  let dbReady = null;
  function loadDb() {
    if (dbReady) return dbReady;
    dbReady = (async () => {
      if (REMOTE && localStorage.getItem("enex_sync_reset") === "1") {   // ağa yeni katıldı: bu bilgisayardaki eski veriler ortak veriyle değiştirilir
        await tx("docs", "readwrite", s => s.clear()); localStorage.removeItem("enex_sync_reset"); localStorage.setItem("enex_sync_seq", "0"); syncSt.seq = 0;
      }
      const rows = await getAll("docs");
      for (const r of rows) docs.set(r.path, r.data);
      if (REMOTE) {
        if (!syncSt.seq || !rows.length) { try { await initialPull(); } catch (e) { console.warn("Ortak veri alınamadı, yerel kopya ile açılıyor", e); } }
        pullLoop();
      } else if (!rows.some(r => r.path.startsWith("products/"))) await seedFirstRun();
      return {doc: docRef, collection: colRef};
    })().catch(e => { console.error("Veritabanı açılamadı", e); return null; });
    return dbReady;
  }

  /* ---------- varlıklar (yedek dosyaları) ---------- */
  const assets = {
    async upload(blob, opt) {
      const id = rid(), type = (opt && opt.type) || blob.type || "application/octet-stream";
      await tx("assets", "readwrite", s => s.put({id, blob, type, size: blob.size, at: Date.now()}));
      return {id, url: "/_blob/" + id, sizeBytes: blob.size, contentType: type};
    },
    async list() { const rows = await getAll("assets"); return {assets: rows.map(r => ({id: r.id, url: "/_blob/" + r.id, sizeBytes: r.size, contentType: r.type})), usage: {usedBytes: rows.reduce((a, r) => a + r.size, 0)}}; },
    async delete(id) { await tx("assets", "readwrite", s => s.delete(id)); },
  };
  const _fetch = window.fetch.bind(window);
  window.fetch = function (url, init) {
    const u = typeof url === "string" ? url : (url && url.url) || "";
    const m = /(?:^|\/)_blob\/([A-Za-z0-9]+)$/.exec(u);
    if (!m) return _fetch(url, init);
    return tx("assets", "readonly", s => s.get(m[1])).then(r => r ? new Response(r.blob, {status: 200, headers: {"Content-Type": r.type}}) : new Response("", {status: 404}));
  };

  /* ---------- indirme ---------- */
  const downloads = {
    async save(o) {
      const data = o.data instanceof Blob ? o.data : new Blob([typeof o.data === "string" ? o.data : (o.data || "")], {type: o.type || "application/octet-stream"});
      const a = document.createElement("a"); a.href = URL.createObjectURL(data); a.download = o.filename || "dosya";
      document.body.appendChild(a); a.click(); setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 4000);
      return {saved: true};
    },
  };

  /* ---------- kullanıcı ---------- */
  const user = {
    async id() { return "owner"; }, async me() { return {id: "owner", name: "Yönetici"}; },
    async isOwner() { return true; }, async canEdit() { return true; }, async can() { return true; },
    async profiles(ids) { const o = {}; for (const i of ids || []) o[i] = {id: i, name: i === "owner" ? "Yönetici" : ""}; return o; },
    async search() { return []; },
  };


  /* pencere / görev çubuğu simgesi: Enex POS logosu */
  try {
    const head = document.head || document.documentElement;
    for (const [rel, href, type] of [["icon", "enex-192.png", "image/png"], ["shortcut icon", "enex.ico", "image/x-icon"], ["apple-touch-icon", "enex-192.png", "image/png"]]) {
      const l = document.createElement("link"); l.rel = rel; l.href = href; if (type) l.type = type; head.appendChild(l);
    }
  } catch (e) {}
  const caps = {db: loadDb, assets: async () => assets, downloads: async () => downloads, user: async () => user};
  window.claude = Object.freeze({ use: async name => (caps[name] ? caps[name]() : null) });
  window.__enexDesktop = {version: 2, exportAll: async () => ({docs: Object.fromEntries(docs)}), sync: () => Object.assign({}, syncSt),
    exportLines: () => [...docs].map(([p, d]) => p + "\t" + JSON.stringify(d)).join("\n")};
})();
