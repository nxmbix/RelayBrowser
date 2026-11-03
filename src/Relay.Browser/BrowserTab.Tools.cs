using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Relay.Core.Navigation;

namespace Relay.Browser;

public enum PipResult { Started, Stopped, NoVideo, Failed }

/// <summary>Page tools: reader mode, picture-in-picture and screenshots.</summary>
public sealed partial class BrowserTab
{
    private string? _readerPayload;
    private string? _readerOriginalUrl;

    public bool IsReaderPage => UrlDisplay.IsReader(Url);

    /// <summary>Extracts the article and opens it in RELAY's reader view. Returns false when the page isn't an article.</summary>
    public async Task<bool> EnterReaderAsync()
    {
        if (Core is not { } core || IsNewTabPage || IsReaderPage) return false;
        try
        {
            var raw = await core.ExecuteScriptAsync(ReaderExtractor.Script);
            // ExecuteScriptAsync returns the JSON encoding of the returned string.
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json is null) return false;
            var node = JsonNode.Parse(json);
            if (node?["ok"]?.GetValue<bool>() != true) return false;
            node["type"] = "article";
            _readerPayload = node.ToJsonString();
            _readerOriginalUrl = core.Source;
            core.Navigate(UrlDisplay.ReaderUrl);
            return true;
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Reader mode", ex);
            return false;
        }
    }

    public void ExitReader()
    {
        if (!IsReaderPage) return;
        if (Core?.CanGoBack == true) Core.GoBack();
        else if (_readerOriginalUrl is not null) Navigate(_readerOriginalUrl);
    }

    /// <summary>The address shown while the reader view is open: the article's own address.</summary>
    public string? ReaderOriginalUrl => IsReaderPage ? _readerOriginalUrl : null;

    private void OnReaderMessage(string json)
    {
        string? type = null;
        try { type = JsonNode.Parse(json)?["type"]?.GetValue<string>(); } catch { }
        switch (type)
        {
            case "ready" when _readerPayload is not null:
                Core?.PostWebMessageAsJson(_readerPayload);
                break;
            case "exit":
                ExitReader();
                break;
        }
    }

    private const string FillScript = """
        (function (o, u, p) {
          try {
            if (location.origin !== o) return false;
            const vis = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
            const pw = [...document.querySelectorAll('input[type=password]')].find(e => vis(e) && !e.disabled && !e.readOnly);
            if (!pw) return false;
            const scope = pw.form || document;
            let user = null;
            for (const f of scope.querySelectorAll('input')) {
              if (!vis(f) || f.disabled || !/^(text|email|tel|)$/i.test(f.getAttribute('type') || '')) continue;
              if (f.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING) user = f;
            }
            const set = (el, v) => {
              Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v);
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            if (user && u) set(user, u);
            set(pw, p);
            return true;
          } catch (e) { return false; }
        })
        """;

    /// <summary>
    /// Fills a saved login into the top-level page, only if it is still on <paramref name="origin"/> (checked here and again in the page).
    /// Runs only when the user picks the login; never automatically, never in private tabs or subframes.
    /// </summary>
    public async Task<bool> FillLoginAsync(string origin, string username, string password)
    {
        if (Core is not { } core || IsPrivate || Relay.Core.Passwords.PasswordOrigin.From(core.Source) != origin) return false;
        try
        {
            var js = FillScript + "(" + JsonSerializer.Serialize(origin) + "," + JsonSerializer.Serialize(username) + "," + JsonSerializer.Serialize(password) + ")";
            return await core.ExecuteScriptAsync(js) == "true";
        }
        catch (Exception ex) { _s.Paths.LogError("Fill login", ex); return false; }
    }

    /// <summary>Toggles picture-in-picture for the main video on the page.</summary>
    public async Task<PipResult> TogglePictureInPictureAsync()
    {
        if (Core is not { } core) return PipResult.Failed;
        try
        {
            // Runtime.evaluate with userGesture lets the request count as a user action, which browsers require for PiP.
            var args = JsonSerializer.Serialize(new { expression = ReaderExtractor.PictureInPictureScript, userGesture = true, awaitPromise = true, returnByValue = true });
            var result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
            var node = JsonNode.Parse(result);
            if (node?["exceptionDetails"] is not null) return PipResult.Failed;
            return node?["result"]?["value"]?.GetValue<string>() switch
            {
                "ok" => PipResult.Started,
                "exited" => PipResult.Stopped,
                "novideo" => PipResult.NoVideo,
                _ => PipResult.Failed,
            };
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Picture-in-picture", ex);
            return PipResult.Failed;
        }
    }

    /// <summary>Captures the visible area, or the whole page (up to 16,000 px tall), as PNG bytes.</summary>
    public async Task<byte[]?> CaptureScreenshotAsync(bool fullPage)
    {
        if (Core is not { } core) return null;
        try
        {
            if (!fullPage)
            {
                using var ms = new MemoryStream();
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return ms.ToArray();
            }
            var metrics = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
            var size = metrics?["cssContentSize"] ?? metrics?["contentSize"];
            var w = Math.Ceiling(size?["width"]?.GetValue<double>() ?? 0);
            var h = Math.Ceiling(size?["height"]?.GetValue<double>() ?? 0);
            if (w <= 0 || h <= 0) return await CaptureScreenshotAsync(false);
            h = Math.Min(h, 16000);
            var args = JsonSerializer.Serialize(new
            {
                format = "png",
                captureBeyondViewport = true,
                clip = new { x = 0, y = 0, width = w, height = h, scale = 1 },
            });
            var shot = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args));
            var data = shot?["data"]?.GetValue<string>();
            return data is null ? null : Convert.FromBase64String(data);
        }
        catch (Exception ex)
        {
            _s.Paths.LogError("Screenshot", ex);
            return null;
        }
    }
}
