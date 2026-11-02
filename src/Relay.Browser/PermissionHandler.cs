using Microsoft.Web.WebView2.Core;
using Relay.Core.Models;
using Relay.Core.Navigation;

namespace Relay.Browser;

/// <summary>
/// Decides website permission requests (camera, location, …): applies saved decisions,
/// otherwise asks the user. Private windows never save decisions.
/// </summary>
public sealed class PermissionHandler(BrowserServices services)
{
    public static string Describe(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "Use your microphone",
        CoreWebView2PermissionKind.Camera => "Use your camera",
        CoreWebView2PermissionKind.Geolocation => "Know your location",
        CoreWebView2PermissionKind.Notifications => "Show notifications",
        CoreWebView2PermissionKind.OtherSensors => "Use motion and light sensors",
        CoreWebView2PermissionKind.ClipboardRead => "See text and images copied to the clipboard",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "Download multiple files automatically",
        CoreWebView2PermissionKind.FileReadWrite => "Read and edit files",
        CoreWebView2PermissionKind.Autoplay => "Play media automatically",
        CoreWebView2PermissionKind.LocalFonts => "Use fonts installed on this computer",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "Control MIDI devices",
        CoreWebView2PermissionKind.WindowManagement => "Manage windows on all your displays",
        _ => "Use a device capability",
    };

    public static IReadOnlyList<string> KnownKinds { get; } =
        ["Camera", "Microphone", "Geolocation", "Notifications", "ClipboardRead", "MultipleAutomaticDownloads", "OtherSensors", "Autoplay", "LocalFonts", "FileReadWrite", "WindowManagement"];

    public async void Handle(CoreWebView2PermissionRequestedEventArgs e, bool isPrivate)
    {
        var deferral = e.GetDeferral();
        try
        {
            // RELAY manages decisions itself so they appear in Settings and can be revoked.
            try { e.SavesInProfile = false; } catch { /* older runtime */ }

            var origin = UrlDisplay.Origin(e.Uri);
            var kind = e.PermissionKind.ToString();

            if (!isPrivate && origin.Length > 0)
            {
                var saved = await services.Permissions.GetAsync(origin, kind);
                if (saved == PermissionDecision.Allow) { e.State = CoreWebView2PermissionState.Allow; return; }
                if (saved == PermissionDecision.Block) { e.State = CoreWebView2PermissionState.Deny; return; }
            }

            var (result, remember) = await services.Ui.AskPermissionAsync(
                new PermissionPrompt(origin.Length > 0 ? origin : e.Uri, kind, Describe(e.PermissionKind)),
                canRemember: !isPrivate && origin.Length > 0);

            switch (result)
            {
                case PermissionPromptResult.Allow:
                    e.State = CoreWebView2PermissionState.Allow;
                    break;
                case PermissionPromptResult.Block:
                    e.State = CoreWebView2PermissionState.Deny;
                    break;
                default:
                    e.State = CoreWebView2PermissionState.Deny;
                    remember = false;
                    break;
            }

            if (remember && !isPrivate && origin.Length > 0)
                await services.Permissions.SetAsync(origin, kind, result == PermissionPromptResult.Allow ? PermissionDecision.Allow : PermissionDecision.Block);
        }
        catch (Exception ex)
        {
            services.Paths.LogError("Permission request", ex);
            try { e.State = CoreWebView2PermissionState.Deny; } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
