using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace PenumbraPreviewManager;

internal class PenumbraWindowIntegration : IDisposable
{
    private readonly Plugin plugin;
    private EventSubscriber<string, float, float>? preSettingsTabBarDrawEvent;

    public PenumbraWindowIntegration(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Register()
    {
        try
        {
            preSettingsTabBarDrawEvent = PreSettingsTabBarDraw.Subscriber(Plugin.PluginInterface, PreSettingsTabBarDrawCallback);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to subscribe to Penumbra PreSettingsTabBarDraw IPC: {ex.Message}");
        }
    }

    public void Unregister()
    {
        preSettingsTabBarDrawEvent?.Dispose();
        preSettingsTabBarDrawEvent = null;
    }

    public void Dispose()
    {
        Unregister();
    }

    private void PreSettingsTabBarDrawCallback(string directory, float width, float titleWidth)
    {
        if (string.IsNullOrEmpty(directory)) return;

        var folderName = Path.GetFileName(directory);
        
        // Find the mod in memory
        var mod = plugin.Mods.FirstOrDefault(m => string.Equals(m.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        if (mod == null) return;

        // If the mod is managed by Heliosphere, let Heliosphere handle the preview and UI drawing to prevent double drawing
        if (mod.IsHeliosphereManaged) return;

        if (mod.HasPreview)
        {
            var texture = Plugin.TextureProvider.GetFromFile(mod.PreviewImagePath!).GetWrapOrDefault();
            if (texture != null)
            {
                var scale = plugin.Configuration.PreviewImageSizePercent / 100f;
                var colWidth = width * scale;
                var drawHeight = colWidth * 9f / 16f; // perfect 16:9 aspect ratio
                
                // Center the image inside the column width
                var offsetX = (width - colWidth) / 2f;
                if (offsetX > 0)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
                }
                
                ImGui.Image(texture.Handle, new Vector2(colWidth, drawHeight));
                
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Left-click: Open in Preview Manager.\nRight-click: Hold to zoom.");
                    ImGui.EndTooltip();
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    plugin.PreviewWindow.OpenModPage(mod);
                }

                if (ImGui.IsItemHovered() && ImGui.IsMouseDown(ImGuiMouseButton.Right))
                {
                    var winSize = ImGuiHelpers.MainViewport.WorkSize;
                    var imgSize = new Vector2(texture.Width, texture.Height);

                    if (imgSize.X > winSize.X || imgSize.Y > winSize.Y)
                    {
                        var ratio = Math.Min(winSize.X / texture.Width, winSize.Y / texture.Height);
                        imgSize *= ratio;
                    }

                    var min = new Vector2(winSize.X / 2 - imgSize.X / 2, winSize.Y / 2 - imgSize.Y / 2);
                    var max = new Vector2(winSize.X / 2 + imgSize.X / 2, winSize.Y / 2 + imgSize.Y / 2);

                    ImGui.GetForegroundDrawList().AddImage(texture.Handle, min, max);
                }
            }
        }
        else
        {
            // Mod has no preview! Draw a nice button where the image usually sits
            if (ImGui.Button("Add Preview Image##PPM_Add", new Vector2(width, 30)))
            {
                plugin.PreviewWindow.OpenModPage(mod);
            }
        }
    }
}
