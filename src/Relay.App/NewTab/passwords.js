// RELAY passwords page. Runs as https://passwords.relay/ (relay://passwords) inside the browser.
// The host sends the list of logins without passwords; a password arrives only after the user
// clicks "show", and copying happens in the browser so the page never sees the copied value.
(function () {
  "use strict";

  // Refuse to run inside a frame: only the top-level page may talk to the browser.
  if (window.top !== window.self) { document.documentElement.textContent = ""; return; }

  const bridge = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);
  const root = document.documentElement;
  const HIDE_AFTER_MS = 30000;

  let logins = [];
  const revealed = new Map(); // id -> password (or null when it can't be decrypted)
  const hideTimers = new Map();
  let menuFor = null;
  let confirmAction = null;
  let toastTimer = 0;

  function send(msg) { if (bridge) bridge.postMessage(msg); }

  // ---- Helpers -------------------------------------------------------------------
  function hue(text) {
    let h = 0;
    for (let i = 0; i < text.length; i++) h = (h * 31 + text.charCodeAt(i)) >>> 0;
    return h % 360;
  }
  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "ico");
    const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", "#i-" + name);
    svg.appendChild(use);
    return svg;
  }
  function iconButton(name, label, onClick) {
    const b = document.createElement("button");
    b.className = "icon-btn";
    b.title = label;
    b.setAttribute("aria-label", label);
    b.appendChild(icon(name));
    b.addEventListener("click", onClick);
    return b;
  }
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  }

  // ---- Rendering -----------------------------------------------------------------
  function applyTheme(s) {
    root.dataset.theme = s.theme;
    root.dataset.private = String(!!s.private);
    root.style.setProperty("--accent", s.accent || "#1F7A6D");
  }

  function render() {
    const q = $("filter").value.trim().toLowerCase();
    const shown = q ? logins.filter((l) => l.host.toLowerCase().includes(q) || l.origin.toLowerCase().includes(q) || l.username.toLowerCase().includes(q)) : logins;

    const n = logins.length;
    $("count").textContent = n === 0 ? "No saved passwords" : n === 1 ? "1 saved password" : n + " saved passwords";
    $("empty").hidden = n > 0;
    $("filter").closest(".search").hidden = n === 0;
    $("list").hidden = shown.length === 0;
    $("no-match").hidden = n === 0 || shown.length > 0;

    const list = $("list");
    list.textContent = "";
    let group = null;
    let lastHost = null;
    for (const l of shown) {
      const host = l.host || l.origin;
      const first = host !== lastHost;
      if (first) { group = el("div", "group"); list.appendChild(group); lastHost = host; }
      group.appendChild(row(l, host, first));
    }
  }

  function row(l, host, first) {
    const r = el("div", first ? "row" : "row cont");

    const m = el("span", "mono", host.replace(/^www\./, "").charAt(0).toUpperCase() || "?");
    m.style.background = "hsl(" + hue(host) + " 32% 46%)";
    r.appendChild(m);

    const who = el("div", "who");
    who.appendChild(el("div", "site", host));
    who.appendChild(el("div", l.username ? "user" : "user none", l.username || "No username"));
    who.title = l.origin;
    r.appendChild(who);

    const has = revealed.has(l.id);
    const pw = revealed.get(l.id);
    const secret = el("div", "secret");
    if (!has) { secret.classList.add("masked"); secret.textContent = "••••••••••"; }
    else if (pw === null) { secret.classList.add("error"); secret.textContent = "Saved by another Windows account or PC"; }
    else { secret.classList.add("shown"); secret.textContent = pw; }
    r.appendChild(secret);

    const tools = el("div", "tools");
    tools.appendChild(iconButton(has ? "eye-off" : "eye", has ? "Hide password" : "Show password", () => toggleReveal(l.id)));
    tools.appendChild(iconButton("copy", "Copy password", () => send({ type: "copy-password", id: l.id })));
    tools.appendChild(iconButton("more", "More", (e) => openRowMenu(e.currentTarget, l)));
    r.appendChild(tools);
    return r;
  }

  function toggleReveal(id) {
    if (revealed.has(id)) { hide(id); render(); return; }
    send({ type: "reveal", id: id });
  }
  function hide(id) {
    revealed.delete(id);
    clearTimeout(hideTimers.get(id));
    hideTimers.delete(id);
  }

  // ---- Menus and dialogs -----------------------------------------------------------
  function openMenu(anchor, items) {
    const menu = $("menu");
    menu.textContent = "";
    for (const it of items) {
      if (it === "-") { menu.appendChild(document.createElement("hr")); continue; }
      const b = el("button", it.danger ? "danger" : "", it.label);
      b.setAttribute("role", "menuitem");
      b.addEventListener("click", () => { closeMenu(); it.run(); });
      menu.appendChild(b);
    }
    menu.hidden = false;
    const a = anchor.getBoundingClientRect();
    const w = menu.offsetWidth, h = menu.offsetHeight;
    menu.style.left = Math.max(8, Math.min(a.right - w, window.innerWidth - w - 8)) + "px";
    menu.style.top = (a.bottom + 4 + h > window.innerHeight ? a.top - h - 4 : a.bottom + 4) + "px";
    menuFor = anchor;
    menu.querySelector("button").focus();
  }
  function closeMenu() { $("menu").hidden = true; menuFor = null; }

  function openRowMenu(anchor, l) {
    if (menuFor === anchor) { closeMenu(); return; }
    const items = [];
    if (l.username) items.push({ label: "Copy username", run: () => send({ type: "copy-username", id: l.id }) });
    if (/^https?:/i.test(l.origin)) items.push({ label: "Open " + (l.host || l.origin), run: () => send({ type: "open", id: l.id }) });
    items.push("-");
    items.push({
      label: "Delete", danger: true,
      run: () => ask("Delete this password?", "The login for " + (l.username || "this site") + " on " + (l.host || l.origin) + " will be removed from RELAY.", "Delete",
        () => send({ type: "delete", id: l.id })),
    });
    openMenu(anchor, items);
  }

  function ask(title, text, okLabel, onOk) {
    $("confirm-title").textContent = title;
    $("confirm-text").textContent = text;
    $("confirm-ok").textContent = okLabel;
    confirmAction = onOk;
    $("confirm").hidden = false;
    $("confirm-cancel").focus();
  }
  function closeConfirm() { $("confirm").hidden = true; confirmAction = null; }

  function toast(message) {
    const t = $("toast");
    t.textContent = message;
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.hidden = true; }, 2600);
  }

  // ---- Events ----------------------------------------------------------------------
  $("filter").addEventListener("input", render);
  $("import").addEventListener("click", () => send({ type: "import" }));
  $("empty-import").addEventListener("click", () => send({ type: "import" }));
  $("offer").addEventListener("change", (e) => send({ type: "offer-to-save", value: e.target.checked }));
  $("page-more").addEventListener("click", (e) => {
    if (menuFor === e.currentTarget) { closeMenu(); return; }
    openMenu(e.currentTarget, [
      { label: "Import passwords (CSV)…", run: () => send({ type: "import" }) },
      "-",
      {
        label: "Remove all passwords", danger: true,
        run: () => ask("Remove all saved passwords?", "Every password saved in RELAY will be deleted. This can't be undone.", "Remove all",
          () => send({ type: "clear" })),
      },
    ]);
  });
  $("confirm-cancel").addEventListener("click", closeConfirm);
  $("confirm-ok").addEventListener("click", () => { const run = confirmAction; closeConfirm(); if (run) run(); });
  $("confirm").addEventListener("click", (e) => { if (e.target === e.currentTarget) closeConfirm(); });

  document.addEventListener("click", (e) => {
    if (menuFor && !$("menu").contains(e.target) && !menuFor.contains(e.target)) closeMenu();
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      if (!$("confirm").hidden) closeConfirm();
      else if (menuFor) closeMenu();
      else if ($("filter").value) { $("filter").value = ""; render(); }
    } else if ((e.ctrlKey && e.key.toLowerCase() === "f") || (e.key === "/" && document.activeElement === document.body)) {
      e.preventDefault();
      $("filter").focus();
    }
  });
  // Hide shown passwords when the tab goes to the background.
  document.addEventListener("visibilitychange", () => {
    if (document.hidden && revealed.size) { for (const id of [...revealed.keys()]) hide(id); render(); }
  });

  if (bridge) {
    bridge.addEventListener("message", (e) => {
      const m = e.data;
      if (!m || typeof m !== "object") return;
      switch (m.type) {
        case "state": {
          applyTheme(m);
          $("offer").checked = !!m.offerToSave;
          logins = Array.isArray(m.logins) ? m.logins : [];
          const ids = new Set(logins.map((l) => l.id));
          for (const id of [...revealed.keys()]) if (!ids.has(id)) hide(id);
          render();
          document.body.classList.remove("loading");
          break;
        }
        case "secret":
          revealed.set(m.id, typeof m.password === "string" ? m.password : null);
          clearTimeout(hideTimers.get(m.id));
          hideTimers.set(m.id, setTimeout(() => { hide(m.id); render(); }, HIDE_AFTER_MS));
          render();
          break;
        case "toast":
          if (typeof m.message === "string") toast(m.message);
          break;
      }
    });
    send({ type: "ready" });
  } else {
    document.body.classList.remove("loading");
    render();
  }
})();
