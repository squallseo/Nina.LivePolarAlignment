using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using Nina.LivePolarAlignment.UI;
namespace Nina.LivePolarAlignment;

[Export(typeof(IPluginManifest))]
public sealed class Plugin : PluginBase, INotifyPropertyChanged {
    internal static readonly Guid SettingsId = new("a1968bf0-8fc3-4605-bf07-b29799485257");
    private readonly IProfileService profiles;
    [ImportingConstructor]
    public Plugin(IProfileService profiles) {
        this.profiles = profiles;
        UiText.Configure(() => profiles.ActiveProfile.ApplicationSettings?.Culture);
    }
    public bool EnableDebugRecording {
        get => ReadDebugRecording(profiles);
        set {
            profiles.ActiveProfile.PluginSettings.SetValue(SettingsId, nameof(EnableDebugRecording), value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnableDebugRecording)));
        }
    }
    internal static bool ReadDebugRecording(IProfileService profiles) =>
        profiles.ActiveProfile.PluginSettings?.TryGetValue(SettingsId, nameof(EnableDebugRecording), out bool enabled) == true && enabled;
    public event PropertyChangedEventHandler? PropertyChanged;
}
