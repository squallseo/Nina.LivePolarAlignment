using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace Nina.LivePolarAlignment;

/// <summary>Plugin entry point. Alignment capture and correction UI are planned.</summary>
[Export(typeof(IPluginManifest))]
public sealed class Plugin : PluginBase {
    [ImportingConstructor]
    public Plugin() { }
}
