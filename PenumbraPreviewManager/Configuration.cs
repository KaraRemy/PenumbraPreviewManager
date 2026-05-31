using Dalamud.Configuration;
using System;

namespace PenumbraPreviewManager;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public int PreviewImageSizePercent { get; set; } = 100;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
