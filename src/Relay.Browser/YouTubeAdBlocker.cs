namespace Relay.Browser;

/// <summary>
/// Removes YouTube's video ads. YouTube serves its ads from the same addresses as its videos, so request
/// blocking can't catch them. This script runs before YouTube's own code and strips the ad schedule out of
/// the player data; if an ad still slips through it is skipped right away.
/// </summary>
public static class YouTubeAdBlocker
{
    public static bool IsYouTubeHost(string host)
    {
        host = host.ToLowerInvariant();
        return host is "youtube.com" or "youtube-nocookie.com"
               || host.EndsWith(".youtube.com", StringComparison.Ordinal)
               || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
    }

    public const string Script = """
(() => {
  if (!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname) || window.__relayYt) return;
  window.__relayYt = true;
  const KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];
  const prune = (o, depth) => {
    if (!o || typeof o !== 'object' || depth > 3) return o;
    try {
      for (const k of KEYS) if (Object.prototype.hasOwnProperty.call(o, k)) delete o[k];
      if (o.playerResponse) prune(o.playerResponse, depth + 1);
      if (Array.isArray(o)) for (const x of o) prune(x, depth + 1);
      else if (o.response) prune(o.response, depth + 1);
    } catch (e) { }
    return o;
  };
  const nativeParse = JSON.parse;
  JSON.parse = function () { return prune(nativeParse.apply(this, arguments), 0); };
  const nativeJson = Response.prototype.json;
  Response.prototype.json = function () { return nativeJson.apply(this, arguments).then(r => prune(r, 0)); };
  for (const name of ['ytInitialPlayerResponse', 'playerResponse']) {
    let value;
    try {
      Object.defineProperty(window, name, { configurable: true, get: () => value, set: v => { value = prune(v, 0); } });
    } catch (e) { }
  }
  // Fallback: skip any ad that still starts playing, and hide ad slots in the page.
  const SKIP = '.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button,.videoAdUiSkipButton';
  const tick = () => {
    try {
      const player = document.querySelector('.html5-video-player.ad-showing');
      if (!player) return;
      const btn = document.querySelector(SKIP);
      if (btn) btn.click();
      const v = player.querySelector('video');
      if (v && isFinite(v.duration) && v.duration > 0) v.currentTime = v.duration;
    } catch (e) { }
  };
  setInterval(tick, 300);
  const css = 'ytd-ad-slot-renderer,ytd-in-feed-ad-layout-renderer,ytd-banner-promo-renderer,ytd-promoted-sparkles-web-renderer,' +
    'ytd-player-legacy-desktop-watch-ads-renderer,ytd-companion-slot-renderer,#player-ads,#masthead-ad,.ytp-ad-overlay-container,' +
    'ytd-rich-item-renderer:has(> #content > ytd-ad-slot-renderer){display:none!important}';
  const addStyle = () => {
    const s = document.createElement('style');
    s.setAttribute('data-relay', 'yt');
    s.textContent = css;
    (document.head || document.documentElement).appendChild(s);
  };
  if (document.documentElement) addStyle(); else document.addEventListener('DOMContentLoaded', addStyle, { once: true });
})();
""";
}
