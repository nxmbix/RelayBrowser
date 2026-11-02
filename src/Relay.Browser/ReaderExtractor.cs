namespace Relay.Browser;

/// <summary>
/// RELAY's own article extractor, run in a page only when the user asks for reader mode.
/// It returns plain text blocks (never HTML), so nothing from the page can execute in the reader view.
/// </summary>
public static class ReaderExtractor
{
    public const string Script = """
(() => {
  const SKIP = 'nav,aside,footer,header,form,button,noscript,script,style,svg,iframe,[role=navigation],[role=complementary],[aria-hidden=true],.ad,.ads,.advert,.share,.social,.related,.comments,.newsletter,.promo';
  const textOf = (el) => (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
  const isSkipped = (el) => !!el.closest(SKIP);

  // Pick the container with the most paragraph text.
  let best = document.querySelector('article') || document.querySelector('main') || document.querySelector('[role=main]');
  let bestScore = 0;
  const score = (el) => [...el.querySelectorAll('p')].filter(p => !isSkipped(p)).reduce((n, p) => n + Math.min(textOf(p).length, 1200), 0);
  if (best) bestScore = score(best);
  const parents = new Map();
  for (const p of document.querySelectorAll('p')) {
    if (isSkipped(p)) continue;
    const len = textOf(p).length;
    if (len < 40) continue;
    const a = p.parentElement, b = a && a.parentElement;
    if (a) parents.set(a, (parents.get(a) || 0) + len);
    if (b) parents.set(b, (parents.get(b) || 0) + len * 0.6);
  }
  for (const [el, s] of parents) if (s > bestScore * 1.2) { best = el; bestScore = s; }
  if (!best || bestScore < 500) return JSON.stringify({ ok: false });

  const blocks = [];
  const seen = new Set();
  const walker = document.createTreeWalker(best, NodeFilter.SHOW_ELEMENT);
  let words = 0;
  for (let el = walker.currentNode; el && blocks.length < 2500; el = walker.nextNode()) {
    if (el !== best && isSkipped(el)) continue;
    const tag = el.tagName;
    if (/^H[1-4]$/.test(tag)) {
      const t = textOf(el); if (t && t.length < 300) blocks.push({ t: 'h', x: t });
    } else if (tag === 'P') {
      if (el.closest('blockquote, li')) continue;
      const t = textOf(el); if (t.length > 1) { blocks.push({ t: 'p', x: t }); words += t.split(' ').length; }
    } else if (tag === 'BLOCKQUOTE') {
      const t = textOf(el); if (t) blocks.push({ t: 'q', x: t });
    } else if (tag === 'LI') {
      if (el.closest('nav')) continue;
      const t = textOf(el); if (t && t.length < 1500) blocks.push({ t: 'li', x: t });
    } else if (tag === 'PRE') {
      const t = (el.textContent || '').trim(); if (t) blocks.push({ t: 'pre', x: t.slice(0, 20000) });
    } else if (tag === 'IMG') {
      const src = el.currentSrc || el.src || '';
      const w = el.naturalWidth || el.width || 0;
      if (/^https:\/\//.test(src) && w >= 160 && !seen.has(src)) { seen.add(src); blocks.push({ t: 'img', s: src, a: (el.alt || '').slice(0, 300) }); }
    }
  }
  const meta = (n) => { const m = document.querySelector(`meta[property="${n}"],meta[name="${n}"]`); return m ? (m.content || '').trim() : ''; };
  const h1 = document.querySelector('h1');
  return JSON.stringify({
    ok: blocks.length > 2,
    title: (meta('og:title') || (h1 && textOf(h1)) || document.title || '').slice(0, 300),
    byline: (meta('author') || meta('article:author') || '').slice(0, 200),
    site: (meta('og:site_name') || location.hostname).slice(0, 120),
    lang: document.documentElement.lang || '',
    url: location.href,
    words,
    blocks
  });
})()
""";

    /// <summary>Finds the largest playing-capable video and toggles picture-in-picture.</summary>
    public const string PictureInPictureScript = """
(async () => {
  if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return 'exited'; }
  const vids = [...document.querySelectorAll('video')].filter(v => v.readyState > 0 && !v.disablePictureInPicture);
  vids.sort((a, b) => (b.clientWidth * b.clientHeight) - (a.clientWidth * a.clientHeight));
  if (!vids.length) return 'novideo';
  await vids[0].requestPictureInPicture();
  return 'ok';
})()
""";
}
