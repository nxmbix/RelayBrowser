using Microsoft.Web.WebView2.Core;
using Relay.Core;
using Relay.Core.Settings;

namespace Relay.Browser;

/// <summary>
/// Owns the single shared <see cref="CoreWebView2Environment"/> (one browser process tree)
/// and hands out controller options for the normal and private profiles.
/// </summary>
public sealed class BrowserEnvironment
{
    public const string NormalProfile = "Default";
    public const string PrivateProfile = "Private";

    private readonly AppPaths _paths;
    private Task<CoreWebView2Environment>? _creating;

    public BrowserEnvironment(AppPaths paths) => _paths = paths;

    public CoreWebView2Environment? Current { get; private set; }

    /// <summary>Raised when the browser process died and every tab must be recreated.</summary>
    public event EventHandler? Reset;

    public Task<CoreWebView2Environment> GetAsync()
    {
        return _creating ??= CreateAsync();
    }

    private async Task<CoreWebView2Environment> CreateAsync()
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = false,
            // Lets Chromium honour Windows' own proxy/certificate configuration; no flags that weaken security.
            AllowSingleSignOnUsingOSPrimaryAccount = false,
        };
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _paths.WebProfile, options);
            env.BrowserProcessExited += OnBrowserProcessExited;
            Current = env;
            return env;
        }
        catch
        {
            _creating = null; // allow a retry
            throw;
        }
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        // Ignore normal shutdown; recreate after crashes.
        if (e.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Failed)
        {
            if (Current is not null) Current.BrowserProcessExited -= OnBrowserProcessExited;
            Current = null;
            _creating = null;
            Reset?.Invoke(this, EventArgs.Empty);
        }
    }

    public CoreWebView2ControllerOptions CreateControllerOptions(CoreWebView2Environment env, bool isPrivate)
    {
        var o = env.CreateCoreWebView2ControllerOptions();
        o.ProfileName = isPrivate ? PrivateProfile : NormalProfile;
        o.IsInPrivateModeEnabled = isPrivate;
        return o;
    }

    /// <summary>Applies profile-level preferences (shared by every tab of that profile).</summary>
    public static void ApplyProfileSettings(CoreWebView2Profile profile, RelaySettings s, bool isDark, string downloadDir)
    {
        try
        {
            profile.PreferredColorScheme = s.Theme switch
            {
                ThemeMode.Light => CoreWebView2PreferredColorScheme.Light,
                ThemeMode.Dark => CoreWebView2PreferredColorScheme.Dark,
                _ => isDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light,
            };
            profile.DefaultDownloadFolderPath = downloadDir;
            // Chromium stores saved passwords encrypted with Windows DPAPI; RELAY never stores them itself.
            profile.IsPasswordAutosaveEnabled = s.PasswordAutosave && !profile.IsInPrivateModeEnabled;
            profile.IsGeneralAutofillEnabled = s.GeneralAutofill && !profile.IsInPrivateModeEnabled;
        }
        catch (Exception) { /* older runtimes may not support every property */ }
    }
}
