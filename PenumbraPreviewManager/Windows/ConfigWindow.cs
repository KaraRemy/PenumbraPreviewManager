using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PenumbraPreviewManager.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Penumbra Preview Manager Settings###PPM_Config")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(400, 480);
        SizeCondition = ImGuiCond.Always;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw() { }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Display Options");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Penumbra Preview Image Size:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"{configuration.PreviewImageSizePercent}%");

        var sizePercent = configuration.PreviewImageSizePercent;
        if (ImGui.SliderInt("##ImageSizeSlider", ref sizePercent, 10, 100, "%d%%"))
        {
            configuration.PreviewImageSizePercent = sizePercent;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Main Preview Import Crop Method:");
        
        var cropNames = new[] { "No Crop (Preserve Aspect)", "16:9 Aspect Ratio", "1:1 Aspect Ratio (Square)", "4:3 Aspect Ratio" };
        int cropIndex = (int)configuration.CropOption;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##CropCombo", ref cropIndex, cropNames, cropNames.Length))
        {
            configuration.CropOption = (CropAspect)cropIndex;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Mod-Option Preview Crop Method:");
        
        var optionCropNames = new[] { "1:1 Aspect Ratio (Square)", "16:9 Aspect Ratio", "No Crop (Preserve Aspect)" };
        int optionCropIndex = (int)configuration.OptionCrop;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##OptionCropCombo", ref optionCropIndex, optionCropNames, optionCropNames.Length))

        {
            configuration.OptionCrop = (OptionCropAspect)optionCropIndex;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Option Preview Trigger Icon:");
        
        var optionIconNames = new[] { "Image Frame Icon", "Eye / View Icon" };
        int optionIconIndex = (int)configuration.OptionIcon;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##OptionIconCombo", ref optionIconIndex, optionIconNames, optionIconNames.Length))
        {
            configuration.OptionIcon = (OptionIconStyle)optionIconIndex;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Penumbra Window Quick Buttons:");
        
        var showClipboard = configuration.ShowClipboardButtonInPenumbra;
        if (ImGui.Checkbox("Show \"Paste from Clipboard\"", ref showClipboard))
        {
            configuration.ShowClipboardButtonInPenumbra = showClipboard;
            configuration.Save();
        }

        var showXma = configuration.ShowXmaButtonInPenumbra;
        if (ImGui.Checkbox("Show \"Search XMA\"", ref showXma))
        {
            configuration.ShowXmaButtonInPenumbra = showXma;
            configuration.Save();
        }

        var showBrowse = configuration.ShowBrowseButtonInPenumbra;
        if (ImGui.Checkbox("Show \"Browse File\"", ref showBrowse))
        {
            configuration.ShowBrowseButtonInPenumbra = showBrowse;
            configuration.Save();
        }

        var showCopy = configuration.ShowCopySearchButtonInPenumbra;
        if (ImGui.Checkbox("Show \"Copy Search Terms\"", ref showCopy))
        {
            configuration.ShowCopySearchButtonInPenumbra = showCopy;
            configuration.Save();
        }

        var showGrab = configuration.ShowGrabUrlButtonInPenumbra;
        if (ImGui.Checkbox("Show \"Grab from URL\"", ref showGrab))
        {
            configuration.ShowGrabUrlButtonInPenumbra = showGrab;
            configuration.Save();
        }

        ImGui.Spacing();
        var autoSync = configuration.AutoSyncSelection;
        if (ImGui.Checkbox("Automatically sync selection with Penumbra", ref autoSync))
        {
            configuration.AutoSyncSelection = autoSync;
            if (!autoSync)
            {
                configuration.HideModList = false;
            }
            configuration.Save();
        }

        if (configuration.AutoSyncSelection)
        {
            var hideModList = configuration.HideModList;
            if (ImGui.Checkbox("Hide mod list column in Preview Manager", ref hideModList))
            {
                configuration.HideModList = hideModList;
                configuration.Save();
            }
        }
    }
}
