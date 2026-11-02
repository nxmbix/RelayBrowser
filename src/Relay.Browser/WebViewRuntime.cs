using Microsoft.Web.WebView2.Core;

namespace Relay.Browser;

/// <summary>Detects the Evergreen WebView2 Runtime. RELAY never installs it on its own.</summary>
public static class WebViewRuntime
{
    public const string DownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>Returns the installed runtime version, or null when it isn't available.</summary>
    public static string? GetInstalledVersion()
    {
        try
        {
            var v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch (WebView2RuntimeNotFoundException) { return null; }
        catch (Exception) { return null; }
    }
}
