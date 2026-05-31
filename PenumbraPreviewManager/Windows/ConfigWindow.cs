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

        Size = new Vector2(400, 110);
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


    }
}
