using System.Text.Json.Serialization;

namespace Relay.Core.Settings;

public enum StartupBehavior { NewTabPage, RestoreSession, Homepage }
public enum ThemeMode { System, Light, Dark }
public enum TabDensity { Comfortable, Compact }

/// <summary>All persisted user preferences. Serialized to settings.json.</summary>
public sealed class RelaySettings
{
    // General
    public StartupBehavior Startup { get; set; } = StartupBehavior.NewTabPage;
    public string Homepage { get; set; } = "https://www.wikipedia.org/";
    public bool ShowHomeButton { get; set; }
    public string SearchEngineId { get; set; } = "google";
    /// <summary>False until the user picks an engine on the first-run choice screen.</summary>
    public bool SearchEngineChosen { get; set; }
    /// <summary>The one-time "import from another browser" offer was shown.</summary>
    public bool ImportOffered { get; set; }
    public string? CustomSearchTemplate { get; set; }
    public bool SearchSuggestions { get; set; } = true;

    // Appearance
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string Accent { get; set; } = "Teal";
    public TabDensity TabDensity { get; set; } = TabDensity.Comfortable;
    public bool ShowTabFavicons { get; set; } = true;
    public string InterfaceFont { get; set; } = "Segoe UI Variable Text";
    public double InterfaceFontSize { get; set; } = 13;
    public double DefaultZoom { get; set; } = 1.0;
    public bool SidebarVisible { get; set; }
    public bool SidebarEnabled { get; set; } = true;
    /// <summary>Background tabs sleep after this many minutes (0 = never).</summary>
    public int SleepTabsAfterMinutes { get; set; } = 15;
    /// <summary>Background tabs are unloaded completely (their memory is freed) after this many minutes (0 = never).</summary>
    public int DiscardTabsAfterMinutes { get; set; } = 60;
    public bool TabHoverPreviews { get; set; } = true;
    public bool VerticalTabs { get; set; }
    public bool VerticalTabsCollapsed { get; set; }

    // New tab page
    public bool NewTabShowClock { get; set; } = true;
    public bool NewTabShowRecent { get; set; } = true;
    public bool NewTabShowBookmarks { get; set; } = true;
    /// <summary>"none", a preset id ("dawn", "mist", …) or "custom" for <see cref="NewTabBackgroundImage"/>.</summary>
    public string NewTabBackground { get; set; } = "none";
    public string? NewTabBackgroundImage { get; set; }

    // Privacy
    public bool PasswordAutosave { get; set; }
    public bool GeneralAutofill { get; set; } = true;
    public bool ClearDataOnExit { get; set; }
    public bool PrivateSearchSuggestions { get; set; }

    // Ad & tracker blocking
    public bool BlockerEnabled { get; set; } = true;
    /// <summary>Ids of enabled filter lists (see BlockerService.Lists).</summary>
    public List<string> BlockerLists { get; set; } = ["easylist", "easyprivacy"];
    public string BlockerCustomRules { get; set; } = string.Empty;
    /// <summary>Sites (base domains) where blocking is turned off.</summary>
    public List<string> BlockerAllowedSites { get; set; } = [];
    public DateTime? BlockerLastUpdated { get; set; }
    public long BlockedTotal { get; set; }

    // Downloads
    public string? DownloadDirectory { get; set; }
    public bool AskWhereToSave { get; set; }
    public bool DownloadNotifications { get; set; } = true;

    // Window
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public static IReadOnlyList<string> AccentNames { get; } = ["Teal", "Cobalt", "Iris", "Amber", "Rose", "Graphite"];

    public RelaySettings Clone()
    {
        var c = (RelaySettings)MemberwiseClone();
        c.BlockerLists = [.. BlockerLists];
        c.BlockerAllowedSites = [.. BlockerAllowedSites];
        return c;
    }
}
