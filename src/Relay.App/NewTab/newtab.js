// RELAY new tab page. Runs as https://newtab.relay/ inside the browser.
// All data arrives as JSON from the host; every string is rendered with textContent.
(function () {
  "use strict";

  // Refuse to run inside a frame: only the top-level page may talk to the browser.
  if (window.top !== window.self) { document.documentElement.textContent = ""; return; }

  const bridge = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);
  const root = document.documentElement;
  let state = null;
  let menuTarget = null;
  let editingId = 0;

  const BACKGROUNDS = [
    { id: "none", tone: null, label: "None" },
    { id: "dawn", tone: "paper", label: "Dawn", color: "linear-gradient(160deg,#F7E9E1,#ECE6F3,#E3ECF2)" },
    { id: "mist", tone: "paper", label: "Mist", color: "linear-gradient(180deg,#E9EEF1,#F4F6F7)" },
    { id: "sand", tone: "paper", label: "Sand", color: "linear-gradient(170deg,#F3EDE3,#EEE8DD)" },
    { id: "dusk", tone: "ink", label: "Dusk", color: "linear-gradient(165deg,#1E2335,#33263A)" },
    { id: "forest", tone: "ink", label: "Forest", color: "linear-gradient(170deg,#17251F,#1E2F27)" },
    { id: "slate", tone: "ink", label: "Slate", color: "linear-gradient(170deg,#22262B,#2B3036)" },
  ];

  function send(msg) { if (bridge) bridge.postMessage(msg); }

  // ---- Monogram tiles (no third-party favicon lookups) ----------------------------
  function hue(text) {
    let h = 0;
    for (let i = 0; i < text.length; i++) h = (h * 31 + text.charCodeAt(i)) >>> 0;
    return h % 360;
  }
  function mono(host, title) {
    const el = document.createElement("span");
    el.className = "mono";
    const base = (host || title || "?").replace(/^www\./, "");
    el.textContent = base.charAt(0).toUpperCase();
    el.style.background = "hsl(" + hue(base) + " 32% 46%)";
    return el;
  }

  // ---- Rendering -----------------------------------------------------------------
  function applyTheme(s) {
    root.dataset.theme = s.theme;
    root.dataset.private = String(!!s.private);
    root.style.setProperty("--accent", s.accent || "#1F7A6D");

    const backdrop = $("backdrop");
    backdrop.className = "";
    backdrop.style.backgroundImage = "";
    delete root.dataset.bg;
    delete root.dataset.tone;
    if (!s.private && s.background && s.background !== "none") {
      if (s.background.indexOf("https://assets.relay/") === 0) {
        backdrop.className = "image";
        backdrop.style.backgroundImage = 'url("' + s.background.replace(/["\\]/g, "") + '")';
        root.dataset.tone = "ink";
      } else {
        const preset = BACKGROUNDS.find((b) => b.id === s.background);
        if (preset) { root.dataset.bg = preset.id; if (preset.tone) root.dataset.tone = preset.tone; }
      }
    }
  }

  function renderTiles(links) {
    const list = $("quick-links");
    list.textContent = "";
    links.forEach((l) => {
      const li = document.createElement("li");
      li.className = "tile";
      li.draggable = true;
      li.dataset.id = String(l.id);
      const a = document.createElement("a");
      a.href = l.url;
      a.title = l.url;
      a.addEventListener("click", (e) => { e.preventDefault(); send({ type: "open", url: l.url }); });
      a.appendChild(mono(l.host, l.title));
      const label = document.createElement("span");
      label.className = "label";
      label.textContent = l.title;
      a.appendChild(label);
      li.appendChild(a);

      const more = document.createElement("button");
      more.type = "button";
      more.className = "more";
      more.setAttribute("aria-label", "Options for " + l.title);
      more.textContent = "⋯";
      more.addEventListener("click", (e) => { e.stopPropagation(); openMenu(more, l); });
      li.appendChild(more);

      wireDrag(li);
      list.appendChild(li);
    });

    if (links.length < 24) {
      const li = document.createElement("li");
      li.className = "tile";
      const add = document.createElement("button");
      add.type = "button";
      add.className = "add";
      const m = document.createElement("span");
      m.className = "mono";
      m.textContent = "+";
      add.appendChild(m);
      const label = document.createElement("span");
      label.className = "label";
      label.textContent = "Add shortcut";
      add.appendChild(label);
      add.addEventListener("click", () => openDialog(null));
      li.appendChild(add);
      list.appendChild(li);
    }
  }

  function renderRows(id, items, emptyText) {
    const ul = $(id);
    ul.textContent = "";
    if (!items.length) {
      const p = document.createElement("li");
      p.className = "empty";
      p.textContent = emptyText;
      ul.appendChild(p);
      return;
    }
    items.forEach((it) => {
      const li = document.createElement("li");
      const a = document.createElement("a");
      a.href = it.url;
      a.title = it.url;
      a.addEventListener("click", (e) => { e.preventDefault(); send({ type: "open", url: it.url }); });
      a.appendChild(mono(it.host, it.title));
      const t = document.createElement("span"); t.className = "t"; t.textContent = it.title; a.appendChild(t);
      const h = document.createElement("span"); h.className = "h"; h.textContent = it.host; a.appendChild(h);
      li.appendChild(a);
      ul.appendChild(li);
    });
  }

  function render(s) {
    state = s;
    applyTheme(s);
    $("private-badge").hidden = !s.private;
    $("private-note").hidden = !s.private;
    $("clock").hidden = !s.showClock;
    $("q").placeholder = "Search with " + s.engine + " or enter address";
    renderTiles(s.quickLinks || []);
    const blocked = Number(s.blockedTotal) || 0;
    $("blocked").hidden = blocked <= 0;
    $("blocked-text").textContent = blocked.toLocaleString() + (blocked === 1 ? " ad or tracker blocked" : " ads and trackers blocked") + " by RELAY";

    const showRecent = !s.private && (s.recent || []).length > 0;
    $("recent-block").hidden = !showRecent;
    if (showRecent) renderRows("recent", s.recent, "");
    const showBookmarks = (s.bookmarks || []).length > 0;
    $("bookmarks-block").hidden = !showBookmarks;
    if (showBookmarks) renderRows("bookmarks", s.bookmarks, "");

    renderSwatches();
    document.querySelectorAll("[data-toggle]").forEach((cb) => {
      const k = cb.dataset.toggle;
      cb.checked = k === "clock" ? s.showClock : k === "recent" ? !!s.showRecent : !!s.showBookmarks;
    });
    document.body.classList.remove("loading");
  }

  function renderSwatches() {
    const box = $("swatches");
    box.textContent = "";
    BACKGROUNDS.forEach((b) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "swatch" + (b.id === "none" ? " none" : "");
      btn.title = b.label;
      btn.setAttribute("aria-label", b.label);
      btn.setAttribute("aria-pressed", String(state && state.background === b.id));
      if (b.color) btn.style.background = b.color;
      btn.addEventListener("click", () => send({ type: "background", value: b.id }));
      box.appendChild(btn);
    });
  }

  // ---- Clock -----------------------------------------------------------------------
  function tick() {
    const now = new Date();
    $("time").textContent = now.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
    $("date").textContent = now.toLocaleDateString(undefined, { weekday: "long", month: "long", day: "numeric" });
  }
  tick();
  setInterval(tick, 10000);

  // ---- Search ----------------------------------------------------------------------
  $("search").addEventListener("submit", (e) => {
    e.preventDefault();
    const text = $("q").value.trim();
    if (text) send({ type: "navigate", text: text });
  });

  // ---- Tile menu -------------------------------------------------------------------
  function openMenu(anchor, link) {
    menuTarget = link;
    const menu = $("tile-menu");
    menu.hidden = false;
    const r = anchor.getBoundingClientRect();
    const left = Math.min(r.left, window.innerWidth - menu.offsetWidth - 8);
    menu.style.left = Math.max(8, left) + "px";
    menu.style.top = (r.bottom + 4) + "px";
    menu.querySelector("button").focus();
  }
  function closeMenu() { $("tile-menu").hidden = true; menuTarget = null; }
  $("tile-menu").addEventListener("click", (e) => {
    const action = e.target.closest("button") && e.target.closest("button").dataset.action;
    const link = menuTarget;
    closeMenu();
    if (!link) return;
    if (action === "edit") openDialog(link);
    if (action === "remove") send({ type: "quicklink.remove", id: link.id });
  });

  // ---- Add / edit dialog -----------------------------------------------------------
  function openDialog(link) {
    editingId = link ? link.id : 0;
    $("dlg-title").textContent = link ? "Edit shortcut" : "Add shortcut";
    $("dlg-name").value = link ? link.title : "";
    $("dlg-url").value = link ? link.url : "";
    $("dlg-error").hidden = true;
    $("dialog-scrim").hidden = false;
    (link ? $("dlg-name") : $("dlg-url")).focus();
  }
  function closeDialog() { $("dialog-scrim").hidden = true; }
  $("dlg-cancel").addEventListener("click", closeDialog);
  $("dialog-scrim").addEventListener("click", (e) => { if (e.target === $("dialog-scrim")) closeDialog(); });
  $("link-dialog").addEventListener("submit", (e) => {
    e.preventDefault();
    const url = $("dlg-url").value.trim();
    if (!url) { showError("Enter a web address."); return; }
    send({ type: "quicklink.save", id: editingId, title: $("dlg-name").value.trim(), url: url });
    closeDialog();
  });
  function showError(text) { const el = $("dlg-error"); el.textContent = text; el.hidden = false; }

  // ---- Drag to reorder -------------------------------------------------------------
  let dragged = null;
  function wireDrag(li) {
    li.addEventListener("dragstart", (e) => {
      dragged = li;
      li.classList.add("dragging");
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData("text/plain", li.dataset.id);
    });
    li.addEventListener("dragend", () => {
      li.classList.remove("dragging");
      document.querySelectorAll(".drop-before").forEach((x) => x.classList.remove("drop-before"));
      dragged = null;
    });
    li.addEventListener("dragover", (e) => {
      if (!dragged || dragged === li) return;
      e.preventDefault();
      document.querySelectorAll(".drop-before").forEach((x) => x.classList.remove("drop-before"));
      li.classList.add("drop-before");
    });
    li.addEventListener("drop", (e) => {
      e.preventDefault();
      if (!dragged || dragged === li) return;
      li.parentNode.insertBefore(dragged, li);
      const ids = Array.from(document.querySelectorAll("#quick-links .tile[data-id]")).map((x) => Number(x.dataset.id));
      send({ type: "quicklink.reorder", ids: ids });
    });
  }
  // Dropping on the "Add" tile area moves to the end.
  $("quick-links").addEventListener("dragover", (e) => { if (dragged) e.preventDefault(); });
  $("quick-links").addEventListener("drop", (e) => {
    if (!dragged || e.target.closest(".tile[data-id]")) return;
    e.preventDefault();
    const addTile = $("quick-links").lastElementChild;
    $("quick-links").insertBefore(dragged, addTile && !addTile.dataset.id ? addTile : null);
    const ids = Array.from(document.querySelectorAll("#quick-links .tile[data-id]")).map((x) => Number(x.dataset.id));
    send({ type: "quicklink.reorder", ids: ids });
  });

  // ---- Customize panel -------------------------------------------------------------
  $("customize").addEventListener("click", (e) => { e.stopPropagation(); $("panel").hidden = !$("panel").hidden; });
  $("panel").addEventListener("click", (e) => e.stopPropagation());
  document.querySelectorAll("[data-toggle]").forEach((cb) =>
    cb.addEventListener("change", () => send({ type: "toggle", key: cb.dataset.toggle, value: cb.checked })));
  document.querySelectorAll("[data-settings]").forEach((b) =>
    b.addEventListener("click", () => send({ type: "settings", section: b.dataset.settings })));

  document.addEventListener("click", () => { closeMenu(); $("panel").hidden = true; });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") { closeMenu(); closeDialog(); $("panel").hidden = true; }
  });

  // ---- Bridge ----------------------------------------------------------------------
  if (bridge) {
    bridge.addEventListener("message", (e) => {
      const msg = e.data;
      if (!msg || typeof msg !== "object") return;
      if (msg.type === "state") render(msg);
      else if (msg.type === "error") { openDialog(null); showError(String(msg.message || "Something went wrong.")); }
    });
    send({ type: "ready" });
  } else {
    document.body.classList.remove("loading");
  }
})();
