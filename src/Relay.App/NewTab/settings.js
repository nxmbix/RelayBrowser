// RELAY settings page. Runs as https://settings.relay/ (relay://settings) inside the browser.
// The host sends every setting as JSON; changes go back as small messages and the host
// replies with fresh state. Every string is rendered with textContent.
(function () {
  "use strict";

  // Refuse to run inside a frame: only the top-level page may talk to the browser.
  if (window.top !== window.self) { document.documentElement.textContent = ""; return; }

  const bridge = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);
  const root = document.documentElement;

  const SECTIONS = [
    { id: "general", title: "General" },
    { id: "appearance", title: "Appearance" },
    { id: "privacy", title: "Privacy and security" },
    { id: "blocking", title: "Ad blocking" },
    { id: "downloads", title: "Downloads" },
    { id: "about", title: "About RELAY" },
  ];

  let S = null;            // latest state from the browser
  const updaters = [];     // functions that refresh controls from S
  let current = null;      // shown section id
  let confirmAction = null;
  let toastTimer = 0;
  let loadedAll = false;

  function send(msg) { if (bridge) bridge.postMessage(msg); }
  const set = (key, value) => send({ type: "set", key: key, value: value });
  const bind = (fn) => { updaters.push(fn); if (S) fn(S); };

  // ---- DOM helpers ------------------------------------------------------------------
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  }
  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "ico");
    const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", "#i-" + name);
    svg.appendChild(use);
    return svg;
  }
  function button(label, onClick, cls) {
    const b = el("button", "btn" + (cls ? " " + cls : ""), label);
    b.addEventListener("click", onClick);
    return b;
  }
  const action = (label, type, cls) => button(label, () => send({ type: type }), cls);
  function iconButton(name, label, onClick) {
    const b = el("button", "icon-btn");
    b.title = label;
    b.setAttribute("aria-label", label);
    b.appendChild(icon(name));
    b.addEventListener("click", onClick);
    return b;
  }

  /** One setting row: label and hint on the left, controls on the right (or below with stack). */
  function item(label, hint, controls, opts) {
    opts = opts || {};
    const row = el("div", "item" + (opts.stack ? " stack" : ""));
    const text = el("div", "text");
    const l = el("div", "label" + (opts.strong ? " strong" : ""), label);
    text.appendChild(l);
    let h = null;
    if (hint !== undefined && hint !== null) {
      h = el("div", "hint" + (opts.hintClass ? " " + opts.hintClass : ""));
      if (typeof hint === "function") bind((s) => { h.textContent = hint(s); });
      else h.textContent = hint;
      text.appendChild(h);
    }
    row.appendChild(text);
    if (controls) {
      const c = opts.stack ? el("div") : el("div", "controls");
      for (const x of [].concat(controls)) if (x) c.appendChild(x);
      row.appendChild(c);
    }
    row.dataset.find = (label + " " + (typeof hint === "string" ? hint : "") + " " + (opts.keywords || "")).toLowerCase();
    if (opts.when) bind((s) => { row.hidden = !opts.when(s); });
    return row;
  }
  function card(title, rows) {
    const c = el("section", "card");
    if (title) c.appendChild(el("h2", "card-title", title));
    for (const r of rows) if (r) c.appendChild(r);
    return c;
  }

  // ---- Controls ------------------------------------------------------------------------
  function toggle(key) {
    const lab = el("label", "switch");
    const input = el("input");
    input.type = "checkbox";
    input.addEventListener("change", () => set(key, input.checked));
    lab.append(input, el("span"));
    bind((s) => { input.checked = !!s[key]; });
    return lab;
  }
  function select(key, optionsKey, numeric) {
    const sel = el("select");
    let built = "";
    sel.addEventListener("change", () => set(key, numeric ? Number(sel.value) : sel.value));
    bind((s) => {
      const opts = s[optionsKey] || [];
      const sig = JSON.stringify(opts);
      if (sig !== built) {
        sel.textContent = "";
        for (const o of opts) { const op = el("option", null, o.text); op.value = String(o.value); sel.appendChild(op); }
        built = sig;
      }
      sel.value = String(s[key]);
    });
    return sel;
  }
  function choices(key, list) {
    const box = el("div", "choices");
    const name = "c-" + key;
    const inputs = list.map(([value, text]) => {
      const lab = el("label", "choice");
      const input = el("input");
      input.type = "radio";
      input.name = name;
      input.value = value;
      input.addEventListener("change", () => { if (input.checked) set(key, value); });
      lab.append(input, el("span", null, text));
      box.appendChild(lab);
      return input;
    });
    bind((s) => { for (const i of inputs) i.checked = i.value === s[key]; });
    return box;
  }
  function segmented(key, list) {
    const box = el("div", "segmented");
    box.setAttribute("role", "radiogroup");
    const btns = list.map(([value, text]) => {
      const b = el("button", null, text);
      b.setAttribute("role", "radio");
      b.addEventListener("click", () => set(key, value));
      box.appendChild(b);
      return [value, b];
    });
    bind((s) => { for (const [v, b] of btns) { b.classList.toggle("on", v === s[key]); b.setAttribute("aria-checked", String(v === s[key])); } });
    return box;
  }
  /** A text box with a Save button; Enter saves too. The box isn't overwritten while focused. */
  function textSave(valueKey, errorKey, type, placeholder) {
    const wrap = el("div");
    const row = el("div", "row");
    const input = el("input", "input");
    input.type = "text";
    input.placeholder = placeholder || "";
    input.spellcheck = false;
    const save = () => send({ type: type, text: input.value });
    input.addEventListener("keydown", (e) => { if (e.key === "Enter") save(); });
    row.append(input, button("Save", save));
    const err = el("div", "error");
    wrap.append(row, err);
    bind((s) => {
      if (document.activeElement !== input) input.value = s[valueKey] || "";
      err.textContent = s[errorKey] || "";
      err.hidden = !s[errorKey];
    });
    return wrap;
  }
  function list(render, emptyText) {
    const box = el("div", "list");
    bind((s) => {
      box.textContent = "";
      const rows = render(s);
      if (!rows.length) box.appendChild(el("div", "list-empty", emptyText));
      else for (const r of rows) box.appendChild(r);
    });
    return box;
  }
  function listRow(main, side, buttons) {
    const r = el("div", "list-row");
    r.appendChild(el("div", "main", main));
    if (side) r.appendChild(el("div", "side", side));
    for (const b of buttons || []) r.appendChild(b);
    return r;
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
  const confirmed = (label, title, text, okLabel, type) =>
    button(label, () => ask(title, text, okLabel, () => send({ type: type })), "danger");

  function toast(message) {
    const t = $("toast");
    t.textContent = message;
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.hidden = true; }, 2800);
  }

  // ---- Sections ----------------------------------------------------------------------------
  const build = {
    general() {
      return [
        card(null, [
          item("Default browser", (s) => s.defaultBrowserStatus, (() => {
            const b = action("Make default", "make-default", "primary");
            bind((s) => { b.hidden = !!s.isDefaultBrowser; });
            return b;
          })(), { strong: true, keywords: "links apps" }),
          item("Import from another browser", "Copy bookmarks and history from Chrome, Edge, Brave, Opera, Vivaldi or Firefox in one click, or import a bookmarks file.",
            action("Import…", "import-data"), { strong: true, keywords: "bookmarks history" }),
        ]),
        card("When RELAY starts", [
          item("Open with", null, null, { stack: true, keywords: "startup restore session tabs homepage" }),
        ].map((r) => {
          r.appendChild(choices("startup", [
            ["NewTabPage", "The new tab page"],
            ["RestoreSession", "Continue where you left off (restore previous tabs)"],
            ["Homepage", "The homepage"],
          ]));
          r.querySelector(".text").hidden = true;
          return r;
        })),
        card("Homepage", [
          item("Homepage address", null, textSave("homepage", "homepageError", "homepage", "example.com"), { stack: true, keywords: "home page" }),
          item("Show the Home button in the toolbar", null, toggle("showHomeButton"), { keywords: "home" }),
        ]),
        card("Search", [
          item("Search engine used in the address bar", null, select("engine", "engines"), { keywords: "google bing duckduckgo" }),
          item("Custom search address", "Example: https://search.example.com/?q=%s — %s is replaced by what you type.",
            textSave("customEngine", "customEngineError", "custom-engine", "https://search.example.com/?q=%s"),
            { stack: true, when: (s) => s.isCustomEngine, keywords: "search engine template" }),
          item("Show search suggestions while typing", "What you type in the address bar is sent to your search engine to get suggestions.", toggle("searchSuggestions")),
        ]),
        card("Performance", [
          item("Put background tabs to sleep", "Sleeping tabs use no CPU and less memory, and wake instantly when you open them. Tabs playing audio stay awake.",
            select("sleepAfter", "sleepOptions", true), { keywords: "memory cpu" }),
          item("Memory saver: unload background tabs", "Unloaded tabs give all of their memory back and reload when you open them. Tabs playing audio, private tabs and tabs with a download running are kept. Press Shift+Esc to see what each tab uses.",
            select("discardAfter", "discardOptions", true), { keywords: "memory discard" }),
          item("Show a preview when hovering over a tab", null, toggle("tabHoverPreviews"), { keywords: "thumbnail" }),
        ]),
      ];
    },

    appearance() {
      const swatches = el("div", "swatches");
      bind((s) => {
        swatches.textContent = "";
        for (const a of s.accents || []) {
          const b = el("button", "swatch" + (a.name === s.accent ? " on" : ""));
          b.style.background = a.color;
          b.title = a.name;
          b.setAttribute("aria-label", a.name);
          b.appendChild(icon("check"));
          b.addEventListener("click", () => set("accent", a.name));
          swatches.appendChild(b);
        }
      });
      const hex = el("input", "input");
      hex.type = "text";
      hex.placeholder = "#FF5A1F";
      hex.maxLength = 7;
      hex.size = 9;
      const applyHex = () => send({ type: "accent-hex", text: hex.value });
      hex.addEventListener("keydown", (e) => { if (e.key === "Enter") applyHex(); });
      bind((s) => { if (document.activeElement !== hex) hex.value = (s.accent || "").startsWith("#") ? s.accent : ""; });

      const bg = el("div", "controls");
      bg.append(action("Choose image…", "choose-background"), action("Remove", "clear-background"));

      return [
        card("Theme", [
          item("Colour mode", null, segmented("themeMode", [["System", "Match Windows"], ["Light", "Light"], ["Dark", "Dark"]]), { keywords: "dark light theme" }),
          item("Accent colour", null, null, { stack: true, keywords: "color colour accent" }),
          item("Custom accent colour", "Type any colour as #RRGGBB, or pick one from the Windows colour picker.",
            [hex, button("Apply", applyHex), action("Pick any colour…", "pick-accent")], { keywords: "color colour hex" }),
        ].map((r, i) => { if (i === 1) r.appendChild(swatches); return r; })),
        card("Tabs and sidebar", [
          item("Tab size", null, segmented("tabDensity", [["Comfortable", "Comfortable"], ["Compact", "Compact"]]), { keywords: "density compact" }),
          item("Show site icons on tabs", null, toggle("showTabFavicons"), { keywords: "favicon" }),
          item("Vertical tabs", "Show tabs in a list on the left instead of along the top.", toggle("verticalTabs"), { keywords: "zen side tabs" }),
          item("Show the sidebar", "When off, the sidebar is hidden completely. Ctrl+H, Ctrl+J and the toolbar still open panels.", toggle("sidebarEnabled")),
          item("Show pinned tabs in the sidebar", "Pinned tabs sit in the sidebar for one-click access, and other tabs stay along the top. With vertical tabs, pinned tabs are always at the top of the tab list.",
            toggle("pinnedTabsInSidebar"), { keywords: "pin pinned quick", when: (s) => !s.verticalTabs && s.sidebarEnabled }),
        ]),
        card("Fonts and zoom", [
          item("Interface font", null, select("interfaceFont", "fonts"), { keywords: "font typeface" }),
          item("Interface text size", null, select("interfaceFontSize", "fontSizes", true), { keywords: "font size" }),
          item("Default page zoom", "Applies to new pages. Websites choose their own fonts.", select("defaultZoom", "zooms", true), { keywords: "zoom scale" }),
        ]),
        card("New tab page", [
          item("Clock and date", null, toggle("newTabShowClock"), { keywords: "new tab" }),
          item("Recently visited sites", null, toggle("newTabShowRecent"), { keywords: "new tab history" }),
          item("Bookmarks", null, toggle("newTabShowBookmarks"), { keywords: "new tab" }),
          item("Background", (s) => "Current: " + s.backgroundSummary + ". More presets are under Customize on the new tab page.", bg, { keywords: "new tab wallpaper image" }),
        ]),
      ];
    },

    privacy() {
      const cookieFilter = el("input", "input");
      cookieFilter.type = "text";
      cookieFilter.placeholder = "Filter sites";
      const cookieList = list((s) => {
        const q = cookieFilter.value.trim().toLowerCase();
        return (s.cookieSites || []).filter((c) => !q || c.domain.includes(q)).slice(0, 500).map((c) =>
          listRow(c.domain, c.count === 1 ? "1 cookie" : c.count + " cookies",
            [iconButton("close", "Delete cookies for this site", () => send({ type: "delete-cookies", domain: c.domain }))]));
      }, "No sites to show.");
      cookieFilter.addEventListener("input", refresh);

      const permissionList = list((s) => (s.permissions || []).map((p) => {
        const mk = (label, act) => { const b = button(label, () => send({ type: "permission", origin: p.origin, kind: p.kind, action: act }), "small"); return b; };
        return listRow(p.origin, p.kind + " · " + p.decision, [mk("Allow", "allow"), mk("Block", "block"),
          iconButton("close", "Forget this decision", () => send({ type: "permission", origin: p.origin, kind: p.kind, action: "forget" }))]);
      }), "No saved permissions.");

      const cookiesRow = item("Cookies and site data", (s) => s.cookieStatus, [
        action("Refresh", "refresh-cookies"),
        confirmed("Delete all", "Delete all cookies?", "You'll be signed out of most websites.", "Delete all", "delete-cookies"),
      ], { strong: true, keywords: "cookies site data" });
      const cookiesBox = item("", null, null, { stack: true, keywords: "cookies" });
      cookiesBox.querySelector(".text").hidden = true;
      cookiesBox.append(cookieFilter, cookieList);

      const permRow = item("Site permissions", "Decisions you asked RELAY to remember. Sites ask again after you remove a decision.", null, { stack: true, strong: true, keywords: "camera microphone location notifications" });
      permRow.appendChild(permissionList);

      return [
        card("RELAY passwords", [
          item("Saved passwords", (s) => (s.passwordCount === 1 ? "1 saved password" : s.passwordCount + " saved passwords") + ", encrypted for your Windows account. Click the key in the toolbar on a sign-in page to fill one.",
            [action("Import CSV…", "import-passwords"), action("Manage passwords", "open-passwords", "primary")], { keywords: "passwords logins relay://passwords" }),
          item("Offer to save passwords", "After you sign in to a site, RELAY asks whether to keep the password. Never in private windows.", toggle("offerToSavePasswords"), { keywords: "passwords" }),
        ]),
        card("Browsing data", [
          item("Clear browsing data", "History, cookies, cache, download history and autofill.", action("Clear browsing data…", "clear-data"), { keywords: "history cache delete" }),
          item("Clear history, cookies and cache when RELAY closes", "Previous tabs won't be restored while this is on.", toggle("clearDataOnExit")),
        ]),
        card("Passwords and forms in the browser engine", [
          item("Let the engine offer to save passwords", "Saved passwords are kept by the browser engine, encrypted with your Windows account. RELAY never stores passwords in plain text. Never saved in private windows.", toggle("passwordAutosave"), { keywords: "autosave" }),
          item("Fill in forms automatically", "Addresses and similar details.", toggle("generalAutofill"), { keywords: "autofill address" }),
          item("Passwords saved by the engine", "The browser engine doesn't let apps list these, so RELAY can't show them. For a full password manager, use a dedicated one (such as Bitwarden or 1Password).",
            confirmed("Remove saved passwords", "Remove all saved passwords?", "Passwords the browser engine saved for websites will be deleted. You'll need to type them again.", "Remove", "clear-engine-passwords")),
        ]),
        card("Private browsing", [
          item("Allow search suggestions in private windows", "Private windows use a separate session. History, cookies and site data from them are deleted when the last private window closes. Websites, your network and your internet provider can still see your activity.", toggle("privateSearchSuggestions"), { keywords: "incognito" }),
        ]),
        card(null, [cookiesRow, cookiesBox]),
        card(null, [permRow]),
        card(null, [
          item("Security", "RELAY keeps the engine's protections on: invalid certificates are always blocked, Microsoft Defender SmartScreen checks downloads, and web pages can't reach RELAY's own code. Files that can run programs always ask before they're kept or opened.", null, { strong: true, keywords: "certificates smartscreen" }),
        ]),
      ];
    },

    blocking() {
      const lists = list((s) => (s.filterLists || []).map((l) => {
        const r = el("div", "list-row");
        const t = el("div", "main");
        t.appendChild(el("div", null, l.name));
        t.appendChild(el("div", "hint", l.description + " · " + l.updated));
        t.style.whiteSpace = "normal";
        const lab = el("label", "switch");
        const input = el("input");
        input.type = "checkbox";
        input.checked = !!l.enabled;
        input.addEventListener("change", () => send({ type: "filter-list", id: l.id, value: input.checked }));
        lab.append(input, el("span"));
        r.append(t, lab);
        return r;
      }), "Loading lists…");

      const allowed = list((s) => (s.allowedSites || []).map((site) =>
        listRow(site, null, [iconButton("close", "Turn blocking back on for this site", () => send({ type: "remove-allowed", site: site }))])),
      "None. Use the shield in the address bar to turn blocking off for a site.");

      const rules = el("textarea");
      rules.spellcheck = false;
      rules.placeholder = "||ads.example.com^\nexample.com##.banner";
      bind((s) => { if (document.activeElement !== rules) rules.value = s.customRules || ""; });

      const listsItem = item("Filter lists", "Community-maintained lists, refreshed automatically every few days.", action("Update now", "update-lists"), { strong: true, keywords: "easylist easyprivacy" });
      const listsBox = item("", null, null, { stack: true, keywords: "filter lists easylist" });
      listsBox.querySelector(".text").hidden = true;
      listsBox.appendChild(lists);

      const allowedItem = item("Sites where blocking is off", null, null, { stack: true, strong: true, keywords: "allow whitelist exceptions" });
      allowedItem.appendChild(allowed);

      const rulesItem = item("Your own filters", "One rule per line in Adblock Plus syntax, for example ||ads.example.com^ or example.com##.banner", null, { stack: true, strong: true, keywords: "custom rules adblock" });
      const saveRow = el("div", "row");
      saveRow.appendChild(button("Save filters", () => send({ type: "save-rules", text: rules.value })));
      rulesItem.append(rules, saveRow);

      return [
        card(null, [
          item("Block ads and trackers", "Stops ad and tracking requests before they load, which also makes pages faster. Turn it off for a single site with the shield in the address bar.", toggle("blockerEnabled"), { strong: true, keywords: "adblock" }),
          item("", null, null, { keywords: "blocked count status" }),
        ].map((r, i) => {
          if (i === 1) {
            r.querySelector(".label").remove();
            const t = r.querySelector(".text");
            const total = el("div", "hint accent");
            const status = el("div", "hint");
            t.append(total, status);
            bind((s) => { total.textContent = s.blockedTotal || ""; status.textContent = s.blockerStatus || ""; });
          }
          return r;
        })),
        card(null, [listsItem, listsBox]),
        card(null, [allowedItem]),
        card(null, [rulesItem]),
        card(null, [item("How blocking works", "RELAY blocks network requests and hides page elements using filter lists. It doesn't run scripts inside websites, so ads served from the same address as a video or page (some YouTube ads, for example) can still appear.", null)]),
      ];
    },

    downloads() {
      return [
        card(null, [
          item("Location", (s) => s.downloadDirectory, [action("Open", "open-downloads"), action("Change…", "change-downloads")], { strong: true, hintClass: "mono", keywords: "folder path" }),
          item("Ask where to save each file", null, toggle("askWhereToSave"), { keywords: "save as" }),
          item("Show a notice when downloads start and finish", null, toggle("downloadNotifications"), { keywords: "notification" }),
        ]),
      ];
    },

    about() {
      const head = el("div", "about-head");
      head.appendChild(document.querySelector(".nav-head .mark").cloneNode(true));
      const info = el("div");
      info.appendChild(el("div", "about-name", "RELAY"));
      const v = el("div", "hint");
      const engine = el("div", "hint");
      const sdk = el("div", "hint");
      const net = el("div", "hint");
      info.append(v, engine, sdk, net);
      head.appendChild(info);
      bind((s) => {
        v.textContent = "Version " + s.appVersion;
        engine.textContent = "Browser engine: Microsoft Edge WebView2 " + s.engineVersion;
        sdk.textContent = "WebView2 SDK: " + s.sdkVersion;
        net.textContent = ".NET runtime: " + s.dotNetVersion;
      });
      const headItem = el("div", "item");
      headItem.dataset.find = "version about relay engine webview";
      headItem.appendChild(head);

      const licenses = el("pre", "licenses");
      licenses.hidden = true;
      const show = button("Show licenses", () => send({ type: "licenses" }));
      licenses.id = "licenses";
      const licItem = item("Open-source licenses and acknowledgements", null, show, { strong: true, keywords: "third party notices" });
      const licBox = el("div", "item stack");
      licBox.dataset.find = "licenses";
      licBox.appendChild(licenses);

      return [
        card(null, [headItem]),
        card(null, [
          item("Your data", (s) => s.storageMode, action("Open folder", "open-data-folder"), { strong: true, keywords: "profile folder portable" }),
          item("Data folder", (s) => s.dataFolder, null, { hintClass: "mono", keywords: "path" }),
        ]),
        card(null, [licItem, licBox]),
      ];
    },
  };

  // ---- Layout, navigation and search ---------------------------------------------------------
  const sectionEls = {};
  const navLinks = {};

  function buildAll() {
    const content = $("content");
    for (const sec of SECTIONS) {
      const wrap = el("section");
      wrap.dataset.section = sec.id;
      const h = el("h1", "section-title", sec.title);
      wrap.appendChild(h);
      for (const c of build[sec.id]()) wrap.appendChild(c);
      wrap.hidden = true;
      content.appendChild(wrap);
      sectionEls[sec.id] = wrap;

      const a = el("a");
      a.href = "#" + sec.id;
      a.appendChild(icon(sec.id));
      a.appendChild(el("span", null, sec.title));
      $("nav-links").appendChild(a);
      navLinks[sec.id] = a;
    }
    const empty = el("p", "empty-find", "No settings match your search.");
    empty.id = "empty-find";
    empty.hidden = true;
    content.appendChild(empty);
  }

  function sectionFromHash() {
    const id = decodeURIComponent(location.hash.replace(/^#/, "")).toLowerCase();
    return SECTIONS.some((s) => s.id === id) ? id : "general";
  }

  function show(id) {
    current = id;
    $("find").value = "";
    applyFind();
    for (const s of SECTIONS) {
      sectionEls[s.id].hidden = s.id !== id;
      navLinks[s.id].classList.toggle("active", s.id === id);
      if (s.id === id) navLinks[s.id].setAttribute("aria-current", "page"); else navLinks[s.id].removeAttribute("aria-current");
    }
    document.title = (SECTIONS.find((s) => s.id === id) || SECTIONS[0]).title + " · Settings";
    window.scrollTo(0, 0);
    send({ type: "load", section: id });
  }

  function applyFind() {
    const q = $("find").value.trim().toLowerCase();
    let any = false;
    for (const s of SECTIONS) {
      const wrap = sectionEls[s.id];
      if (!q) {
        wrap.hidden = s.id !== current;
        for (const r of wrap.querySelectorAll("[data-find]")) r.style.display = "";
        for (const c of wrap.querySelectorAll(".card")) c.style.display = "";
        continue;
      }
      let sectionHit = false;
      for (const c of wrap.querySelectorAll(".card")) {
        const title = (c.querySelector(".card-title") || { textContent: "" }).textContent.toLowerCase();
        let cardHit = false;
        for (const r of c.querySelectorAll(":scope > [data-find]")) {
          const hit = title.includes(q) || r.dataset.find.includes(q);
          r.style.display = hit ? "" : "none";
          cardHit = cardHit || hit;
        }
        c.style.display = cardHit ? "" : "none";
        sectionHit = sectionHit || cardHit;
      }
      wrap.hidden = !sectionHit;
      any = any || sectionHit;
    }
    $("empty-find").hidden = !q || any;
    for (const s of SECTIONS) navLinks[s.id].classList.toggle("active", !q && s.id === current);
    // Search covers every section, so make sure the live lists are loaded once.
    if (q && !loadedAll) { loadedAll = true; send({ type: "load", section: "privacy" }); send({ type: "load", section: "blocking" }); }
  }

  function refresh() { if (S) for (const fn of updaters) fn(S); }

  // ---- Events ------------------------------------------------------------------------------
  window.addEventListener("hashchange", () => show(sectionFromHash()));
  $("find").addEventListener("input", applyFind);
  $("confirm-cancel").addEventListener("click", closeConfirm);
  $("confirm-ok").addEventListener("click", () => { const run = confirmAction; closeConfirm(); if (run) run(); });
  $("confirm").addEventListener("click", (e) => { if (e.target === e.currentTarget) closeConfirm(); });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      if (!$("confirm").hidden) closeConfirm();
      else if ($("find").value) { $("find").value = ""; applyFind(); }
    } else if (e.ctrlKey && e.key.toLowerCase() === "f") {
      e.preventDefault();
      $("find").focus();
    }
  });

  buildAll();
  show(sectionFromHash());

  if (bridge) {
    bridge.addEventListener("message", (e) => {
      const m = e.data;
      if (!m || typeof m !== "object") return;
      switch (m.type) {
        case "state":
          S = m;
          root.dataset.theme = m.theme;
          root.dataset.private = String(!!m.private);
          root.style.setProperty("--accent", m.accentColor || "#1F7A6D");
          refresh();
          document.body.classList.remove("loading");
          break;
        case "toast":
          if (typeof m.message === "string") toast(m.message);
          break;
        case "licenses": {
          const pre = $("licenses");
          pre.textContent = typeof m.text === "string" ? m.text : "";
          pre.hidden = false;
          break;
        }
      }
    });
    send({ type: "ready" });
    send({ type: "load", section: current });
  } else {
    document.body.classList.remove("loading");
  }
})();
