namespace Relay.Browser;

/// <summary>Notices sign-in form submits on the top-level page so RELAY can offer to save the login.</summary>
public static class LoginCapture
{
    public const string Script = """
(() => {
  if (window !== window.top || window.__relayLogin || !window.chrome || !window.chrome.webview) return;
  window.__relayLogin = true;
  const vis = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
  let last = 0;
  const capture = (scope) => {
    try {
      const pw = [...(scope || document).querySelectorAll('input[type=password]')].find(e => vis(e) && e.value);
      if (!pw || Date.now() - last < 1500) return;
      last = Date.now();
      let user = '';
      for (const f of (pw.form || document).querySelectorAll('input')) {
        if (!vis(f) || !/^(text|email|tel|)$/i.test(f.getAttribute('type') || '')) continue;
        if ((f.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING) && f.value) user = f.value;
      }
      window.chrome.webview.postMessage({ type: 'relay-login', u: user, p: pw.value });
    } catch (e) { }
  };
  document.addEventListener('submit', e => capture(e.target), true);
  document.addEventListener('click', e => {
    const b = e.target && e.target.closest && e.target.closest('button,input[type=submit],[role=button]');
    if (b && (b.type === 'submit' || /log ?in|sign ?in|continue|next/i.test(b.textContent || b.value || ''))) capture(b.form || document);
  }, true);
  document.addEventListener('keydown', e => { if (e.key === 'Enter' && e.target && e.target.type === 'password') capture(e.target.form || document); }, true);
})();
""";
}
