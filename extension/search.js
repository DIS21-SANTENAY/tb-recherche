"use strict";

// ---------- Dialogue avec le moteur (TBRecherche.exe, "native messaging") ----------

let port = null;
let nextId = 1;
const pending = new Map();

function connect() {
  port = messenger.runtime.connectNative("tbrecherche");
  port.onMessage.addListener(onHostMessage);
  port.onDisconnect.addListener(p => {
    const err = (p && p.error && p.error.message) || "";
    for (const { rej } of pending.values()) rej(new Error("Moteur déconnecté"));
    pending.clear();
    port = null;
    showHostError(err);
  });
}

function call(cmd, args = {}) {
  if (!port) connect();
  const id = nextId++;
  return new Promise((res, rej) => {
    pending.set(id, { res, rej });
    port.postMessage({ id, cmd, ...args });
  });
}

function onHostMessage(m) {
  if (m.type === "progress") return onIndexProgress(m);
  if (m.type === "indexed") return onIndexed(m);
  const p = pending.get(m.id);
  if (!p) return;
  pending.delete(m.id);
  if (m.ok) p.res(m);
  else p.rej(Object.assign(new Error(m.error || "Erreur"), m));
}

function showHostError(detail) {
  const box = $("hostError");
  box.hidden = false;
  box.innerHTML = "<b>Le moteur TB Recherche ne répond pas.</b><br>" +
    "Lancez une fois <b>TBRecherche.exe</b> sur ce poste : il s'inscrit auprès de Thunderbird, puis rechargez cet onglet (F5)." +
    (detail ? `<br><span class="small">Détail : ${esc(detail)}</span>` : "");
  setIndex("Moteur indisponible", "", "error");
}

// ---------- Outils ----------

const $ = id => document.getElementById(id);
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const fr = "fr-FR";

const PALETTE = ["#B3264A", "#2F6FA8", "#3C8D6B", "#4E7D3A", "#7A4FB8", "#1F8A92", "#2E5F78", "#55607A", "#9A3B87"];
function colorFor(key) {
  let h = 2166136261 >>> 0;
  for (const ch of String(key || "").toLowerCase()) h = Math.imul(h ^ ch.charCodeAt(0), 16777619) >>> 0;
  return PALETTE[h % PALETTE.length];
}

function splitSender(s) {
  const first = String(s || "").split(", ")[0].trim();
  const m = first.match(/^(.*)<([^>]+)>$/);
  if (m) return { name: m[1].trim().replace(/^["']|["']$/g, ""), addr: m[2] };
  return { name: first, addr: first };
}

function initials(name) {
  const parts = String(name).split(/[\s.\-_@]+/).filter(p => /[\p{L}\p{N}]/u.test(p[0] || ""));
  if (!parts.length) return "?";
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

function smartDate(iso) {
  if (!iso) return "";
  const d = new Date(iso), today = new Date(); today.setHours(0, 0, 0, 0);
  const day = new Date(d); day.setHours(0, 0, 0, 0);
  const diff = Math.round((today - day) / 86400000);
  if (diff === 0) return d.toLocaleTimeString(fr, { hour: "2-digit", minute: "2-digit" });
  if (diff === 1) return "Hier";
  if (diff < 7) { const w = d.toLocaleDateString(fr, { weekday: "long" }); return w[0].toUpperCase() + w.slice(1); }
  if (d.getFullYear() === today.getFullYear()) return d.toLocaleDateString(fr, { day: "numeric", month: "short" });
  return d.toLocaleDateString(fr);
}

const longDate = iso => iso ? new Date(iso).toLocaleString(fr, { weekday: "long", day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit" }) : "";

function fileSize(b) {
  if (!b) return "";
  if (b < 1024) return b + " o";
  if (b < 1048576) return Math.round(b / 1024) + " Ko";
  return (b / 1048576).toFixed(1).replace(".", ",") + " Mo";
}

function fileBadge(name) {
  const ext = (String(name).match(/\.([^.]+)$/) || [, ""])[1].toLowerCase();
  const c = ({ pdf: "#D33A2C", doc: "#2B5CB8", docx: "#2B5CB8", odt: "#2B5CB8", rtf: "#2B5CB8", xls: "#1E7D4A", xlsx: "#1E7D4A", ods: "#1E7D4A", csv: "#1E7D4A",
    ppt: "#A8326E", pptx: "#A8326E", odp: "#A8326E", jpg: "#7A4FB8", jpeg: "#7A4FB8", png: "#7A4FB8", gif: "#7A4FB8", heic: "#7A4FB8",
    zip: "#6B5B3E", rar: "#6B5B3E", "7z": "#6B5B3E", eml: "#1F8A92", msg: "#1F8A92" })[ext] || "#6E6A66";
  return { label: (ext || "?").slice(0, 4).toUpperCase(), color: c };
}

// Repli des accents caractère par caractère : même longueur que le texte d'origine
function fold(s) {
  let o = "";
  for (let i = 0; i < s.length; i++) {
    const d = s[i].normalize("NFD")[0];
    const l = d.toLowerCase();
    o += l.length === 1 ? l : d;
  }
  return o;
}
const isWord = c => /[\p{L}\p{N}]/u.test(c);

/** Texte échappé avec les termes surlignés (début de mot, sans accents). */
function highlightText(text, terms) {
  text = String(text || "");
  if (!terms.length) return esc(text);
  const f = fold(text), hits = [];
  for (const t of terms) {
    let i = 0;
    while ((i = f.indexOf(t, i)) >= 0) { if (i === 0 || !isWord(f[i - 1])) hits.push([i, t.length]); i += t.length; }
  }
  hits.sort((a, b) => a[0] - b[0]);
  let out = "", pos = 0;
  for (const [s, len] of hits) {
    if (s < pos) continue;
    out += esc(text.slice(pos, s)) + "<mark>" + esc(text.slice(s, s + len)) + "</mark>";
    pos = s + len;
  }
  return out + esc(text.slice(pos));
}

/** Surlignage dans le document du message (iframe), sur les nœuds texte. */
function highlightDocument(doc, terms) {
  if (!terms.length || !doc.body) return;
  const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT, {
    acceptNode: n => n.parentElement && /^(SCRIPT|STYLE|TITLE)$/.test(n.parentElement.tagName) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT
  });
  const nodes = [];
  while (walker.nextNode()) nodes.push(walker.currentNode);
  let total = 0;
  for (const node of nodes) {
    const text = node.nodeValue, f = fold(text), hits = [];
    for (const t of terms) { let i = 0; while ((i = f.indexOf(t, i)) >= 0) { if (i === 0 || !isWord(f[i - 1])) hits.push([i, t.length]); i += t.length; } }
    if (!hits.length) continue;
    hits.sort((a, b) => a[0] - b[0]);
    const frag = doc.createDocumentFragment();
    let pos = 0;
    for (const [s, len] of hits) {
      if (s < pos) continue;
      if (s > pos) frag.appendChild(doc.createTextNode(text.slice(pos, s)));
      const m = doc.createElement("mark");
      m.className = "tbr" + (total === 0 ? " first" : "");
      m.textContent = text.slice(s, s + len);
      frag.appendChild(m);
      pos = s + len;
      total++;
    }
    if (pos < text.length) frag.appendChild(doc.createTextNode(text.slice(pos)));
    node.parentNode.replaceChild(frag, node);
    if (total > 3000) break;
  }
  // Défilement de l'aperçu seul (scrollIntoView ferait aussi défiler la page de recherche)
  const first = doc.querySelector("mark.tbr");
  const win = doc.defaultView;
  if (first && win) {
    const y = first.getBoundingClientRect().top + win.scrollY - win.innerHeight / 2;
    win.scrollTo(0, Math.max(0, y));
  }
}

let toastTimer = 0;
function toast(msg) {
  const t = $("toast");
  t.textContent = msg;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.hidden = true, 3500);
}

// ---------- État ----------

const state = {
  account: null,
  period: 0,
  sort: "date",
  hits: [],
  terms: [],
  selected: -1,
  current: null,       // aperçu affiché
  remoteAllowed: new Set(),
};

const PERIODS = [
  { label: "Toute la période" },
  { label: "Aujourd'hui", days: 0 },
  { label: "7 derniers jours", days: 6 },
  { label: "30 derniers jours", days: 29 },
  { label: "12 derniers mois", months: 12 },
  { label: "Dates précises…", custom: true },
];

const isoDay = d => d.toISOString().slice(0, 10);

function periodRange() {
  const p = PERIODS[state.period];
  if (p.custom) return { from: $("from").value || null, to: $("to").value || null };
  if (p.days != null) { const d = new Date(); d.setDate(d.getDate() - p.days); return { from: isoDay(d), to: null }; }
  if (p.months) { const d = new Date(); d.setMonth(d.getMonth() - p.months); return { from: isoDay(d), to: null }; }
  return { from: null, to: null };
}

// ---------- Colonne latérale ----------

function renderPeriods() {
  $("periods").innerHTML = PERIODS.map((p, i) => `<li data-i="${i}" class="${i === state.period ? "on" : ""}"><span class="lbl">${esc(p.label)}</span></li>`).join("");
  $("customDates").hidden = !PERIODS[state.period].custom;
}

async function loadAccounts() {
  try {
    const r = await call("accounts");
    const total = r.accounts.reduce((s, a) => s + a.count, 0);
    const items = [{ name: null, label: "Tous les comptes", count: total, color: "#E6E7EA" },
      ...r.accounts.map(a => ({ name: a.name, label: a.name, count: a.count, color: colorFor("compte:" + a.name) }))];
    $("accounts").innerHTML = items.map(a =>
      `<li data-name="${esc(a.name ?? "")}" class="${(a.name ?? null) === state.account ? "on" : ""}">
         <span class="dot" style="background:${a.color}"></span><span class="lbl">${esc(a.label)}</span>
         <span class="num">${a.count.toLocaleString(fr)}</span></li>`).join("");
  } catch { /* moteur indisponible : message déjà affiché */ }
}

$("accounts").addEventListener("click", e => {
  const li = e.target.closest("li");
  if (!li) return;
  state.account = li.dataset.name || null;
  for (const x of $("accounts").children) x.classList.toggle("on", x === li);
  runSearch();
});

$("periods").addEventListener("click", e => {
  const li = e.target.closest("li");
  if (!li) return;
  state.period = +li.dataset.i;
  renderPeriods();
  runSearch();
});

for (const id of ["from", "to", "attachOnly"]) $(id).addEventListener("change", () => runSearch());

$("sort").addEventListener("click", e => {
  const b = e.target.closest("button");
  if (!b) return;
  state.sort = b.dataset.sort;
  for (const x of $("sort").children) x.classList.toggle("on", x === b);
  runSearch();
});

// ---------- Recherche ----------

let searchSeq = 0;
let debounce = 0;

$("q").addEventListener("input", () => {
  $("clear").hidden = !$("q").value;
  clearTimeout(debounce);
  debounce = setTimeout(runSearch, 200);
});

$("clear").addEventListener("click", () => { $("q").value = ""; $("clear").hidden = true; $("q").focus(); runSearch(); });

$("q").addEventListener("keydown", e => {
  if (e.key === "Enter") { e.preventDefault(); clearTimeout(debounce); runSearch(); }
  else if (e.key === "Escape") { $("q").value = ""; $("clear").hidden = true; runSearch(); }
  else if (e.key === "ArrowDown" || e.key === "ArrowUp") { e.preventDefault(); moveSelection(e.key === "ArrowDown" ? 1 : -1); }
});

async function runSearch() {
  const seq = ++searchSeq;
  const range = periodRange();
  const progressTimer = setTimeout(() => {
    if (seq !== searchSeq) return;
    $("searchProgress").hidden = false;
    $("info").textContent = "Recherche en cours…";
  }, 100);
  try {
    const r = await call("search", {
      text: $("q").value, account: state.account, from: range.from, to: range.to,
      attachOnly: $("attachOnly").checked, sort: state.sort, limit: 300,
    });
    if (seq !== searchSeq) return;
    state.hits = r.hits;
    state.terms = r.terms.map(fold).filter(t => t.length > 0);
    renderResults(r);
  } catch (e) {
    if (seq !== searchSeq) return;
    $("count").textContent = "Recherche incomplète";
    $("info").textContent = e.message;
  } finally {
    clearTimeout(progressTimer);
    if (seq === searchSeq) $("searchProgress").hidden = true;
  }
}

function renderResults(r) {
  const n = r.hits.length;
  $("count").textContent = n === 0 ? "Aucun résultat" : r.limited ? `${n}+ messages` : `${n.toLocaleString(fr)} message${n > 1 ? "s" : ""}`;
  const inPj = r.hits.filter(h => h.inAttachment).length;
  $("info").textContent = `en ${r.ms} ms` + (inPj ? `  ·  ${inPj} trouvé${inPj > 1 ? "s" : ""} dans des pièces jointes` : "") + (r.limited ? "  ·  affinez la recherche" : "");
  $("empty").hidden = n > 0;
  $("results").hidden = n === 0;

  const keepId = state.current && state.current.dbId;
  $("results").innerHTML = r.hits.map((h, i) => {
    const s = splitSender(h.sender);
    const name = s.name || s.addr || "(expéditeur inconnu)";
    const parts = h.folder.split("/");
    const folder = parts.length > 2 ? "… / " + parts.slice(-2).join(" / ") : parts.join(" / ");
    const snippet = String(h.snippet || "").replace(/[-_=*~#.]{4,}/g, " ").trim();
    return `<div class="card" data-i="${i}">
      <div class="avatar" style="background:${colorFor(s.addr || name)}">${esc(initials(name))}</div>
      <div style="min-width:0">
        <div class="top"><span class="who">${highlightText(name, state.terms)}</span><span class="when">${esc(smartDate(h.date))}</span></div>
        <div class="subj">${highlightText(h.subject || "(sans objet)", state.terms)}</div>
        <div class="snip">${highlightText(snippet, state.terms)}</div>
        <div class="meta">
          <span class="pill"><i style="background:${colorFor("compte:" + h.account)}"></i>${esc(h.account)}</span>
          <span class="folder">${esc(folder)}</span>
          ${h.inAttachment ? '<span class="in-pj">dans une PJ</span>' : ""}
          ${h.attach > 0 ? '<span class="clip" title="Pièces jointes"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 11.5l-7.8 7.8a5 5 0 0 1-7.1-7.1l8.1-8.1a3.3 3.3 0 0 1 4.7 4.7l-8 8a1.6 1.6 0 0 1-2.3-2.3l7.3-7.3"/></svg></span>' : ""}
        </div>
      </div>
    </div>`;
  }).join("");

  const again = keepId != null ? r.hits.findIndex(h => h.id === keepId) : -1;
  if (again >= 0) select(again, { keepPreview: true });
  else if (n > 0) select(0);
  else { state.selected = -1; clearReader(); }
}

$("results").addEventListener("click", e => {
  const card = e.target.closest(".card");
  if (card) select(+card.dataset.i);
});
$("results").addEventListener("dblclick", e => { if (e.target.closest(".card")) openInThunderbird(); });
$("results").addEventListener("keydown", e => {
  if (e.key === "ArrowDown" || e.key === "ArrowUp") { e.preventDefault(); moveSelection(e.key === "ArrowDown" ? 1 : -1); }
  else if (e.key === "Enter") { e.preventDefault(); openInThunderbird(); }
});

function moveSelection(delta) {
  if (!state.hits.length) return;
  select(Math.max(0, Math.min(state.hits.length - 1, state.selected + delta)));
}

/** Fait défiler la liste seule : scrollIntoView ferait aussi glisser toute la page hors de l'onglet. */
function revealCard(card) {
  const box = $("results");
  const r = card.getBoundingClientRect(), b = box.getBoundingClientRect();
  if (r.top < b.top) box.scrollTop -= b.top - r.top + 6;
  else if (r.bottom > b.bottom) box.scrollTop += r.bottom - b.bottom + 6;
}

let previewTimer = 0;
function select(i, { keepPreview = false } = {}) {
  state.selected = i;
  const cards = $("results").children;
  for (const c of cards) c.classList.toggle("on", +c.dataset.i === i);
  if (cards[i]) revealCard(cards[i]);
  clearTimeout(previewTimer);
  if (keepPreview && state.current && state.current.dbId === state.hits[i].id) {
    previewTimer = setTimeout(() => loadPreview(state.hits[i]), 120); // nouveau surlignage
    return;
  }
  previewTimer = setTimeout(() => loadPreview(state.hits[i]), 120);
}

// ---------- Lecture ----------

let previewSeq = 0;

async function loadPreview(hit) {
  const seq = ++previewSeq;
  try {
    const r = await call("preview", { message: hit.id, terms: state.terms, allowRemote: state.remoteAllowed.has(hit.id) });
    if (seq !== previewSeq) return;
    state.current = { dbId: hit.id, hit, data: r };
    renderReader(hit, r);
  } catch (e) {
    if (seq !== previewSeq) return;
    clearReader();
    $("readerHint").textContent = "Ce message a été déplacé ou compacté par Thunderbird. Cliquez sur « Actualiser ». (" + e.message + ")";
  }
}

function renderReader(hit, r) {
  $("readerEmpty").hidden = true;
  $("reader").hidden = false;
  $("rDot").style.background = colorFor("compte:" + r.account);
  $("rAccount").textContent = r.account;
  $("rFolder").textContent = r.folder.split("/").join("  ›  ");
  $("rSubject").textContent = r.subject || "(sans objet)";
  const name = r.fromName || r.fromAddress || "(expéditeur inconnu)";
  $("rFromName").textContent = name;
  $("rFromAddr").textContent = r.fromAddress;
  $("rAvatar").textContent = initials(name);
  $("rAvatar").style.background = colorFor(r.fromAddress || name);
  $("rDate").textContent = longDate(r.date);
  const to = "À  " + (r.to || "(non renseigné)") + (r.cc ? "     Cc  " + r.cc : "");
  $("rTo").textContent = to;
  $("rTo").title = to;
  $("remoteBtn").hidden = !(r.hasRemote && !state.remoteAllowed.has(hit.id));
  $("rNote").hidden = !r.trimmed;
  $("rNote").textContent = r.trimmed ? "Message volumineux : les images intégrées ne sont pas affichées dans l'aperçu. Ouvrez-le dans Thunderbird pour tout voir." : "";

  $("rAtts").innerHTML = r.attachments.map(a => {
    const b = fileBadge(a.name);
    return `<button class="att${a.match ? " match" : ""}" data-index="${a.index}" title="${esc(a.name)}">
      <span class="badge" style="background:${b.color}">${esc(b.label)}</span>
      <span style="min-width:0"><span class="n">${esc(a.name)}</span>
      <span class="s">${esc(fileSize(a.size))}${a.match ? `${a.size ? "  ·  " : ""}<em>contient la recherche</em>` : ""}</span></span>
    </button>`;
  }).join("");

  const frame = $("frame");
  frame.onload = () => {
    const doc = frame.contentDocument;
    if (!doc) return;
    // Liens : ouverts dans le navigateur par défaut, jamais dans l'aperçu
    doc.addEventListener("click", ev => {
      const a = ev.target.closest && ev.target.closest("a[href]");
      if (!a) return;
      ev.preventDefault();
      const url = a.href;
      if (/^(https?:|mailto:)/i.test(url)) messenger.windows.openDefaultBrowser(url);
    });
    highlightDocument(doc, state.terms);
  };
  frame.srcdoc = r.html;
}

function clearReader() {
  state.current = null;
  $("reader").hidden = true;
  $("readerEmpty").hidden = false;
  $("readerHint").textContent = "Son contenu s'affichera ici, avec vos mots surlignés.";
}

$("remoteBtn").addEventListener("click", () => {
  if (!state.current) return;
  state.remoteAllowed.add(state.current.dbId);
  loadPreview(state.current.hit);
});

$("rAtts").addEventListener("click", async e => {
  const b = e.target.closest(".att");
  if (!b || !state.current) return;
  try { await call("openAttachment", { message: state.current.dbId, index: +b.dataset.index }); }
  catch (err) { toast("Impossible d'ouvrir la pièce jointe : " + err.message); }
});

// ---------- Ouverture dans Thunderbird ----------

// messages.query ne consulte aucun index : il lit un par un tous les messages des dossiers
// demandés. Sans dossier précis, il parcourt tous les comptes (des minutes sur un gros profil).
// On cherche donc d'abord dans le dossier que le moteur connaît, puis dans le compte.

let tbAccounts = null;

/** Identifiant Thunderbird ("account3") du compte qui porte ce nom dans l'index. */
async function tbAccountId(name) {
  if (!tbAccounts) tbAccounts = await messenger.accounts.list(false);
  const a = tbAccounts.find(x => x.name === name);
  return a ? a.id : null;
}

/** Première page de résultats, puis arrêt du parcours que Thunderbird poursuivrait en arrière-plan. */
async function queryMessages(info) {
  const list = await messenger.messages.query(info);
  if (list && list.id) messenger.messages.abortList(list.id).catch(() => {});
  return (list && list.messages) || [];
}

/** Retrouve le message dans Thunderbird par son Message-ID : dossier exact, puis compte, puis partout. */
async function findTbMessage(hit) {
  if (!hit.msgid) return null;
  const accountId = await tbAccountId(hit.account);
  if (accountId) {
    try {
      // Identifiant de dossier Thunderbird : "<compte>:/<chemin>", le chemin commençant par "/"
      const inFolder = await queryMessages({ headerMessageId: hit.msgid, folderId: `${accountId}://${hit.folder}` });
      if (inFolder.length) return inFolder[0];
    } catch { /* nom de dossier différent sur le disque : on élargit au compte */ }
    const inAccount = await queryMessages({ headerMessageId: hit.msgid, accountId });
    if (inAccount.length) return pickFolder(inAccount, hit);
  }
  return pickFolder(await queryMessages({ headerMessageId: hit.msgid }), hit);
}

function pickFolder(msgs, hit) {
  if (!msgs.length) return null;
  const want = hit.folder.toLowerCase();
  return msgs.find(m => m.folder && decodeURIComponent(m.folder.path || "").toLowerCase().replace(/^\//, "").endsWith(want)) || msgs[0];
}

/** Recherche du message avec un message d'attente si elle dépasse une demi-seconde. */
async function findWithNotice(hit) {
  const t = setTimeout(() => toast("Recherche du message dans Thunderbird…"), 500);
  try { return await findTbMessage(hit); }
  finally { clearTimeout(t); }
}

async function openInThunderbird() {
  const hit = state.hits[state.selected];
  if (!hit) return;
  try {
    const m = await findWithNotice(hit);
    if (!m) return toast("Message introuvable dans Thunderbird (supprimé ou dossier non synchronisé).");
    await messenger.messageDisplay.open({ messageId: m.id, location: "tab", active: true });
  } catch (e) {
    toast("Ouverture impossible : " + e.message);
  }
}

async function showInFolder() {
  const hit = state.hits[state.selected];
  if (!hit) return;
  try {
    const m = await findWithNotice(hit);
    if (!m) return toast("Message introuvable dans Thunderbird (supprimé ou dossier non synchronisé).");
    const tabs = await messenger.mailTabs.query({ currentWindow: true });
    const tab = tabs.find(t => t.active) || tabs[0] || await messenger.mailTabs.create({});
    // Manifest V3 : l'onglet courrier s'identifie par tabId (id n'existe qu'en V2 et vaut null ici,
    // ce qui visait l'onglet actif, c'est-à-dire la page de recherche elle-même).
    const tabId = tab.tabId ?? tab.id;
    // L'onglet courrier passe d'abord au premier plan : sa liste de messages doit être chargée.
    await messenger.tabs.update(tabId, { active: true });
    // En Manifest V3, seul displayedFolderId est accepté (displayedFolder est réservé à la V2).
    await messenger.mailTabs.update(tabId, { displayedFolderId: m.folder.id });
    await messenger.mailTabs.setSelectedMessages(tabId, [m.id]);
  } catch (e) {
    toast("Affichage impossible : " + e.message);
  }
}

$("openBtn").addEventListener("click", openInThunderbird);
$("folderBtn").addEventListener("click", showInFolder);

// ---------- Index ----------

function setIndex(status, detail, mode = "") {
  $("indexStatus").textContent = status;
  $("indexDetail").textContent = detail;
  $("indexDot").className = "dot" + (mode ? " " + mode : "");
}

async function refreshStatus(suffix = "") {
  try {
    const s = await call("status");
    setIndex(`${s.messages.toLocaleString(fr)} messages`, suffix || `À jour à ${new Date().toLocaleTimeString(fr, { hour: "2-digit", minute: "2-digit" })}`);
  } catch { }
}

async function startIndex(full) {
  $("refresh").disabled = $("rebuild").disabled = true;
  setIndex("Recherche des nouveautés…", "", "busy");
  try {
    const r = await call("index", { full });
    if (!r.started) setIndex("Indexation déjà en cours", "", "busy");
  } catch (e) {
    setIndex(e.busy ? "Indexation en cours ailleurs" : "Erreur d'indexation", e.busy ? "L'application TB Recherche indexe déjà." : e.message, e.busy ? "busy" : "error");
    $("refresh").disabled = $("rebuild").disabled = false;
  }
}

let lastLiveRefresh = 0;
function onIndexProgress(m) {
  $("indexBar").hidden = false;
  $("indexBar").firstElementChild.style.width = (m.percent || 0) + "%";
  setIndex(m.percent != null ? `Indexation ${Math.round(m.percent)} %` : "Indexation…", `${m.messages.toLocaleString(fr)} messages lus, ${m.filesDone}/${m.filesTotal} dossiers`, "busy");
  // Pendant une longue indexation, les résultats se complètent au fil de l'eau
  if (Date.now() - lastLiveRefresh > 20000) { lastLiveRefresh = Date.now(); runSearch(); }
}

async function onIndexed(m) {
  $("indexBar").hidden = true;
  $("refresh").disabled = $("rebuild").disabled = false;
  if (m.busy) return setIndex("Indexation en cours ailleurs", "L'application TB Recherche indexe déjà.", "busy");
  const t = new Date().toLocaleTimeString(fr, { hour: "2-digit", minute: "2-digit" });
  await refreshStatus(`À jour à ${t}` + (m.changed ? `  ·  +${m.added} / -${m.removed}` : ""));
  if (m.changed) { loadAccounts(); runSearch(); }
}

$("refresh").addEventListener("click", () => startIndex(false));
$("rebuild").addEventListener("click", () => {
  if (confirm("Reconstruire entièrement l'index ?\n\nAvec le texte des pièces jointes, cela prend une quinzaine de minutes. La recherche reste utilisable pendant ce temps.")) startIndex(true);
});

// ---------- Aide, raccourcis ----------

$("helpBtn").addEventListener("click", e => { e.stopPropagation(); $("help").hidden = !$("help").hidden; });
document.addEventListener("click", e => { if (!$("help").hidden && !e.target.closest("#help")) $("help").hidden = true; });
document.addEventListener("keydown", e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "f") { e.preventDefault(); $("q").focus(); $("q").select(); }
});

// ---------- Démarrage ----------

renderPeriods();
connect();
(async () => {
  await refreshStatus();
  await loadAccounts();
  await runSearch();
  startIndex(false);
  setInterval(() => { if ($("refresh").disabled === false) startIndex(false); }, 5 * 60 * 1000);
})();
