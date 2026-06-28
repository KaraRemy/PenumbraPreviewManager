using Dalamud.Configuration;
using System;

namespace PenumbraPreviewManager;

public enum CropAspect
{
    NoCrop,
    Aspect16_9,
    Aspect1_1,
    Aspect4_3
}

public enum OptionCropAspect
{
    Aspect1_1,
    Aspect16_9,
    NoCrop
}


public enum OptionIconStyle
{
    Image,
    Eye
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public int PreviewImageSizePercent { get; set; } = 100;
    public int MiddleClickZoomPercent { get; set; } = 100;
    public CropAspect CropOption { get; set; } = CropAspect.NoCrop;
    public bool ShowClipboardButtonInPenumbra { get; set; } = true;
    public bool ShowXmaButtonInPenumbra { get; set; } = true;
    public bool ShowBrowseButtonInPenumbra { get; set; } = false;
    public bool ShowCopySearchButtonInPenumbra { get; set; } = false;
    public bool ShowGrabUrlButtonInPenumbra { get; set; } = false;
    public bool AutoSyncSelection { get; set; } = true;
    public bool HideModList { get; set; } = true;
    public bool DisableHeliosphereBypass { get; set; } = false;
    public bool HideOptionPreviewsFromPenumbra { get; set; } = false;

    // Option previews configurations
    public OptionCropAspect OptionCrop { get; set; } = OptionCropAspect.NoCrop;
    public OptionIconStyle OptionIcon { get; set; } = OptionIconStyle.Eye;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

