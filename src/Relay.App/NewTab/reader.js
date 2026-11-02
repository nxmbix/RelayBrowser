// RELAY reader view. Receives plain-text blocks from the browser and renders them with textContent only.
(function () {
  "use strict";
  if (window.top !== window.self) { document.documentElement.textContent = ""; return; }

  const bridge = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);
  const body = document.body;

  // Reading preferences live in this page's own storage.
  const prefs = { size: 20, font: "serif", theme: "light" };
  try { Object.assign(prefs, JSON.parse(localStorage.getItem("relay-reader") || "{}")); } catch (e) { }
  if (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches && !localStorage.getItem("relay-reader")) prefs.theme = "dark";

  function apply() {
    prefs.size = Math.min(30, Math.max(15, prefs.size));
    document.documentElement.style.setProperty("--size", prefs.size + "px");
    body.dataset.font = prefs.font;
    body.dataset.theme = prefs.theme;
    document.querySelectorAll("[data-font]").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.font === prefs.font)));
    document.querySelectorAll("[data-theme]").forEach((b) => { if (b.tagName === "BUTTON") b.setAttribute("aria-pressed", String(b.dataset.theme === prefs.theme)); });
    try { localStorage.setItem("relay-reader", JSON.stringify(prefs)); } catch (e) { }
  }
  apply();

  document.querySelectorAll("[data-size]").forEach((b) => b.addEventListener("click", () => { prefs.size += Number(b.dataset.size); apply(); }));
  document.querySelectorAll("button[data-font]").forEach((b) => b.addEventListener("click", () => { prefs.font = b.dataset.font; apply(); }));
  document.querySelectorAll("button[data-theme]").forEach((b) => b.addEventListener("click", () => { prefs.theme = b.dataset.theme; apply(); }));
  $("exit").addEventListener("click", () => bridge && bridge.postMessage({ type: "exit" }));
  document.addEventListener("keydown", (e) => { if (e.key === "Escape" && bridge) bridge.postMessage({ type: "exit" }); });

  function el(tag, text, cls) {
    const n = document.createElement(tag);
    if (text) n.textContent = text;
    if (cls) n.className = cls;
    return n;
  }

  function render(a) {
    document.title = a.title || "Reader view";
    $("title").textContent = a.title || "";
    $("site").textContent = a.site || "";
    if (a.lang) document.documentElement.lang = String(a.lang).slice(0, 20);
    const minutes = Math.max(1, Math.round((Number(a.words) || 0) / 230));
    $("meta").textContent = [a.byline, minutes + " min read"].filter(Boolean).join(" · ");

    const content = $("content");
    content.textContent = "";
    let list = null;
    const title = (a.title || "").trim();
    (a.blocks || []).forEach((b, i) => {
      if (b.t !== "li") list = null;
      switch (b.t) {
        case "h":
          if (i < 3 && b.x.trim() === title) return; // the title is already shown
          content.appendChild(el("h2", b.x));
          break;
        case "p": content.appendChild(el("p", b.x)); break;
        case "q": content.appendChild(el("blockquote", b.x)); break;
        case "pre": content.appendChild(el("pre", b.x)); break;
        case "li":
          if (!list) { list = el("ul"); content.appendChild(list); }
          list.appendChild(el("li", b.x));
          break;
        case "img": {
          if (typeof b.s !== "string" || b.s.indexOf("https://") !== 0) break;
          const fig = el("figure");
          const img = el("img");
          img.src = b.s;
          img.alt = b.a || "";
          img.loading = "lazy";
          img.referrerPolicy = "no-referrer";
          fig.appendChild(img);
          if (b.a) fig.appendChild(el("figcaption", b.a));
          content.appendChild(fig);
          break;
        }
      }
    });
    body.classList.remove("loading");
  }

  if (!bridge) { $("empty").hidden = false; body.classList.remove("loading"); return; }
  bridge.addEventListener("message", (e) => {
    const msg = e.data;
    if (msg && msg.type === "article") render(msg);
  });
  bridge.postMessage({ type: "ready" });
  setTimeout(() => { if (body.classList.contains("loading")) { $("empty").hidden = false; body.classList.remove("loading"); } }, 2500);
})();
