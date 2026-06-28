using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;


namespace PenumbraPreviewManager;

internal class PenumbraWindowIntegration : IDisposable
{
    private readonly Plugin plugin;
    private EventSubscriber<string, float, float>? preSettingsTabBarDrawEvent;
    private EventSubscriber<string>? preSettingsDrawEvent;
    private EventSubscriber<string>? postSettingsDrawEvent;

    public bool IsDrawingPenumbraSettings { get; private set; }
    public string? ActiveDrawingModPath { get; private set; }
    private IReadOnlyDictionary<string, (string[] Options, Penumbra.Api.Enums.GroupType Type)>? activeModSettings;

    // Drawing performance cache fields
    private string? lastDrawDirectory;
    public ModInfo? ActiveDrawingMod { get; private set; }
    private Dictionary<string, string>? activeModOptionToGroup;
    private Dictionary<string, List<string>>? activeModOptionToGroups;
    private string? currentActiveGroupContext;

    private string grabUrlInput = string.Empty;
    private ModInfo? activePopupMod;


    public PenumbraWindowIntegration(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Register()
    {
        try
        {
            preSettingsTabBarDrawEvent = PreSettingsTabBarDraw.Subscriber(Plugin.PluginInterface, PreSettingsTabBarDrawCallback);
            preSettingsDrawEvent = PreSettingsDraw.Subscriber(Plugin.PluginInterface, PreSettingsDrawCallback);
            postSettingsDrawEvent = PostSettingsDraw.Subscriber(Plugin.PluginInterface, PostSettingsDrawCallback);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to subscribe to Penumbra draw IPC events: {ex.Message}");
        }
    }

    public void Unregister()
    {
        preSettingsTabBarDrawEvent?.Dispose();
        preSettingsTabBarDrawEvent = null;
        preSettingsDrawEvent?.Dispose();
        preSettingsDrawEvent = null;
        postSettingsDrawEvent?.Dispose();
        postSettingsDrawEvent = null;
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

        // Automatically sync the selected mod in our Preview Manager if enabled
        if (plugin.Configuration.AutoSyncSelection)
        {
            plugin.PreviewWindow.SelectedMod = mod;
        }

        // If the mod is managed by Heliosphere and the plugin is active, let Heliosphere handle the preview and UI drawing to prevent double drawing
        // (unless the user has enabled the manual override).
        if (mod.IsHeliosphereManaged && plugin.IsHeliosphereActive() && !plugin.Configuration.DisableHeliosphereBypass) return;

        if (mod.HasPreview)
        {
            var texture = Plugin.TextureProvider.GetFromFile(plugin.GetBustedImagePath(mod.PreviewImagePath!)).GetWrapOrDefault();
            if (texture != null)
            {
                var scale = plugin.Configuration.PreviewImageSizePercent / 100f;
                var colWidth = width * scale;
                float aspect = 16f / 9f;
                if (texture.Width > 0 && texture.Height > 0)
                {
                    aspect = (float)texture.Width / texture.Height;
                }
                var drawHeight = colWidth / aspect;
                
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
                    ImGui.TextUnformatted("Left-click: Open in Preview Manager.\nMiddle-click: Hold to zoom.");
                    ImGui.EndTooltip();
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    plugin.PreviewWindow.OpenModPage(mod);
                }

                if (ImGui.IsItemHovered() && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
                {
                    var winSize = ImGuiHelpers.MainViewport.WorkSize;
                    var zoomScale = plugin.Configuration.MiddleClickZoomPercent / 100f;
                    var imgSize = new Vector2(texture.Width * zoomScale, texture.Height * zoomScale);

                    if (imgSize.X > winSize.X || imgSize.Y > winSize.Y)
                    {
                        var ratio = Math.Min(winSize.X / imgSize.X, winSize.Y / imgSize.Y);
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
            // Mod has no preview! Draw nice button(s) where the image usually sits
            var buttons = new List<(string Label, Action Action, string Tooltip)>();

            // 1. Open Preview Manager (always first)
            buttons.Add(("Open Preview Manager", () => plugin.PreviewWindow.OpenModPage(mod), "Open the main Preview Manager window for this mod."));

            // 2. Paste from Clipboard
            if (plugin.Configuration.ShowClipboardButtonInPenumbra)
            {
                buttons.Add(("Paste Clipboard", () => 
                {
                    try
                    {
                        using var clipboardImage = ClipboardHelper.GetImageFromClipboard();
                        if (clipboardImage != null)
                        {
                            var targetPath = Path.Combine(mod.FullPath, "preview.png");
                            plugin.SaveImageFromBitmap(clipboardImage, targetPath, plugin.Configuration.CropOption);
                            mod.PreviewImagePath = targetPath;
                            plugin.UpdateModTag(mod, true);
                        }
                        else
                        {
                            Plugin.Log.Warning("No image found in clipboard to paste.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error($"Failed to paste clipboard image: {ex}");
                    }
                }, "Directly paste and crop an image from your clipboard."));
            }

            // 3. Search on XMA
            if (plugin.Configuration.ShowXmaButtonInPenumbra)
            {
                buttons.Add(("Search XMA", () =>
                {
                    var searchTerms = mod.FolderName;
                    if (!string.IsNullOrEmpty(mod.Author) && mod.Author != "Unknown")
                    {
                        searchTerms += " " + mod.Author;
                    }
                    var escapedTerms = Uri.EscapeDataString(searchTerms);
                    var searchUrl = $"https://www.xivmodarchive.com/search?sortby=rank&sortorder=desc&basic_text={escapedTerms}&dt_compat=1&types=";
                    try
                    {
                        Process.Start(new ProcessStartInfo(searchUrl) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error($"Failed to open XMA search URL: {ex.Message}");
                    }
                }, "Search for this mod on XIVModArchive."));
            }

            // 4. Browse Local Image
            if (plugin.Configuration.ShowBrowseButtonInPenumbra)
            {
                buttons.Add(("Browse File", () =>
                {
                    plugin.FileDialogManager.OpenFileDialog(
                        "Select Preview Image", 
                        "Image Files{.png,.jpg,.jpeg,.webp,.bmp,.gif}", 
                        (success, path) =>
                        {
                            if (success)
                            {
                                try
                                {
                                    var targetPath = Path.Combine(mod.FullPath, "preview.png");
                                    plugin.CropAndScaleImage(path, targetPath, plugin.Configuration.CropOption);
                                    mod.PreviewImagePath = targetPath;
                                    plugin.UpdateModTag(mod, true);
                                }
                                catch (Exception ex)
                                {
                                    Plugin.Log.Error($"Failed to set local image from browse: {ex}");
                                }
                            }
                        });
                }, "Browse your local files for a preview image."));
            }

            // 5. Copy Search Terms
            if (plugin.Configuration.ShowCopySearchButtonInPenumbra)
            {
                buttons.Add(("Copy Search Terms", () =>
                {
                    var copyText = mod.FolderName;
                    if (!string.IsNullOrEmpty(mod.Author) && mod.Author != "Unknown")
                    {
                        copyText += " " + mod.Author;
                    }
                    ImGui.SetClipboardText(copyText);
                }, "Copy search terms (folder name and author) to clipboard."));
            }

            // 6. Grab from URL
            if (plugin.Configuration.ShowGrabUrlButtonInPenumbra)
            {
                buttons.Add(("Grab from URL", () =>
                {
                    activePopupMod = mod;
                    grabUrlInput = mod.Website;
                    ImGui.OpenPopup("Grab URL Popup##PPM_GrabUrlPopup");
                }, "Download and crop a preview image from a XIVModArchive or direct link."));
            }

            // Draw buttons in rows of 2
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var btnWidth = (width - spacing) / 2f;

            for (int i = 0; i < buttons.Count; i++)
            {
                bool isLastOdd = (i == buttons.Count - 1) && (buttons.Count % 2 != 0);
                float currentWidth = isLastOdd ? width : btnWidth;

                if (i > 0 && i % 2 != 0)
                {
                    ImGui.SameLine();
                }
                
                if (ImGui.Button($"{buttons[i].Label}##PPM_IntBtn_{i}", new Vector2(currentWidth, 30)))
                {
                    buttons[i].Action();
                }
                
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(buttons[i].Tooltip);
                }
            }
        }

        // Render the URL grab popup if it is open
        if (ImGui.BeginPopup("Grab URL Popup##PPM_GrabUrlPopup"))
        {
            ImGui.TextUnformatted($"Grab Preview for: {activePopupMod?.Name}");
            ImGui.InputTextWithHint("##GrabUrlInput", "https://xivmodarchive.com/mod/...", ref grabUrlInput, 500);
            ImGui.Spacing();
            
            if (ImGui.Button("Grab & Scale Image"))
            {
                if (activePopupMod != null && !string.IsNullOrEmpty(grabUrlInput))
                {
                    var targetMod = activePopupMod;
                    var url = grabUrlInput;
                    Task.Run(async () =>
                    {
                        var result = await plugin.GrabImageFromUrlAsync(targetMod, url);
                        if (result == GrabResult.NsfwRestricted)
                        {
                            Plugin.ChatGui.PrintError(
                                "Automatic import failed: This mod is NSFW/Restricted on XIVModArchive. " +
                                "To add a preview manually, copy the image from your browser and click 'Paste Clipboard'.", 
                                "PenumbraPreviewManager");
                        }
                        else if (result == GrabResult.FailedGeneric)
                        {
                            Plugin.ChatGui.PrintError(
                                "Failed to grab image. Please check the URL or try a direct image link.", 
                                "PenumbraPreviewManager");
                        }
                    });
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void PreSettingsDrawCallback(string directory)
    {
        ActiveDrawingModPath = directory;
        IsDrawingPenumbraSettings = true;
        
        if (directory != lastDrawDirectory)
        {
            lastDrawDirectory = directory;
            var folderName = Path.GetFileName(directory);
            activeModSettings = plugin.GetAvailableSettings(folderName);
            
            ActiveDrawingMod = plugin.Mods.FirstOrDefault(m => string.Equals(m.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (ActiveDrawingMod != null && activeModSettings != null)
            {
                activeModOptionToGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                activeModOptionToGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in activeModSettings)
                {
                    var groupName = kvp.Key;
                    foreach (var option in kvp.Value.Options)
                    {
                        if (!activeModOptionToGroup.ContainsKey(option))
                        {
                            activeModOptionToGroup[option] = groupName;
                        }

                        if (!activeModOptionToGroups.TryGetValue(option, out var gList))
                        {
                            gList = new List<string>();
                            activeModOptionToGroups[option] = gList;
                        }
                        gList.Add(groupName);
                    }
                }
            }
            else
            {
                activeModOptionToGroup = null;
                activeModOptionToGroups = null;
            }
        }
    }

    private void PostSettingsDrawCallback(string directory)
    {
        IsDrawingPenumbraSettings = false;
        ActiveDrawingModPath = null;
    }

    public void ClearDrawCache()
    {
        lastDrawDirectory = null;
        ActiveDrawingMod = null;
        activeModSettings = null;
        activeModOptionToGroup = null;
        activeModOptionToGroups = null;
        currentActiveGroupContext = null;
    }

    private string ResolveGroupForOption(string label, string optionName)
    {
        if (ActiveDrawingMod == null || activeModOptionToGroup == null || !activeModOptionToGroup.TryGetValue(optionName, out var defaultGroup))
        {
            return string.Empty;
        }

        if (activeModOptionToGroups == null || !activeModOptionToGroups.TryGetValue(optionName, out var gList) || gList.Count <= 1)
        {
            if (activeModOptionToGroups != null && activeModOptionToGroups.TryGetValue(optionName, out var singleList) && singleList.Count == 1)
            {
                currentActiveGroupContext = singleList[0];
            }
            return defaultGroup;
        }

        if (!string.IsNullOrEmpty(currentActiveGroupContext) && gList.Contains(currentActiveGroupContext, StringComparer.OrdinalIgnoreCase))
        {
            return currentActiveGroupContext;
        }

        var labelMatch = gList.FirstOrDefault(g => label.Contains(g, StringComparison.OrdinalIgnoreCase));
        if (labelMatch != null)
        {
            currentActiveGroupContext = labelMatch;
            return labelMatch;
        }

        return defaultGroup;
    }

    public void OnCheckboxDraw(string label)
    {
        if (ActiveDrawingMod == null || activeModOptionToGroup == null) return;

        var optionName = label;
        var hashIndex = label.IndexOf("##");
        if (hashIndex >= 0)
        {
            optionName = label.Substring(0, hashIndex);
        }

        var targetGroup = ResolveGroupForOption(label, optionName);
        if (!string.IsNullOrEmpty(targetGroup))
        {
            var key = $"{targetGroup}/{optionName}";
            var validPaths = plugin.GetValidOptionImagePaths(ActiveDrawingMod);
            if (validPaths.TryGetValue(key, out var fullImagePath))
            {
                DrawPreviewTriggerIcon(fullImagePath, optionName);
            }
        }
    }

    public void OnBeginComboDraw(string label, string previewValue)
    {
        if (ActiveDrawingMod == null || activeModSettings == null) return;

        var groupName = label;
        var hashIndex = label.IndexOf("##");
        if (hashIndex >= 0)
        {
            groupName = label.Substring(0, hashIndex);
        }

        if (!string.IsNullOrEmpty(groupName) && activeModSettings.TryGetValue(groupName, out var groupInfo) && groupInfo.Type == Penumbra.Api.Enums.GroupType.Single)
        {
            var key = $"{groupName}/{previewValue}";
            var validPaths = plugin.GetValidOptionImagePaths(ActiveDrawingMod);
            if (validPaths.TryGetValue(key, out var fullImagePath))
            {
                DrawPreviewTriggerIcon(fullImagePath, previewValue);
            }
        }
    }

    public void OnSelectableDraw(string label)
    {
        if (ActiveDrawingMod == null || activeModOptionToGroup == null) return;

        var optionName = label;
        var hashIndex = label.IndexOf("##");
        if (hashIndex >= 0)
        {
            optionName = label.Substring(0, hashIndex);
        }

        // Capture whether the selectable list item is hovered before drawing the icon
        bool isSelectableHovered = ImGui.IsItemHovered();

        var targetGroup = ResolveGroupForOption(label, optionName);
        if (!string.IsNullOrEmpty(targetGroup))
        {
            var key = $"{targetGroup}/{optionName}";
            var validPaths = plugin.GetValidOptionImagePaths(ActiveDrawingMod);
            if (validPaths.TryGetValue(key, out var fullImagePath))
            {
                DrawPreviewTriggerIcon(fullImagePath, optionName, isSelectableHovered);
            }
        }
    }

    private void DrawPreviewTriggerIcon(string fullImagePath, string optionName, bool forceShowTooltip = false)
    {
        var icon = plugin.Configuration.OptionIcon == OptionIconStyle.Eye ? FontAwesomeIcon.Eye : FontAwesomeIcon.Image;
        ImGui.SameLine();
        
        ImGui.PushFont(UiBuilder.IconFont);
        
        // Render trigger icon as a premium sky blue colored icon next to the option checkbox/dropdown
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 0.8f), icon.ToIconString());
        ImGui.PopFont();

        bool isIconHovered = ImGui.IsItemHovered();

        if (forceShowTooltip || isIconHovered)
        {
            ShowPreviewTooltip(fullImagePath, optionName);
        }

        // Trigger large preview overlay when holding Middle-Click on the icon
        if (isIconHovered && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            var texture = Plugin.TextureProvider.GetFromFile(plugin.GetBustedImagePath(fullImagePath)).GetWrapOrDefault();
            if (texture != null)
            {
                var winSize = ImGuiHelpers.MainViewport.WorkSize;
                var zoomScale = plugin.Configuration.MiddleClickZoomPercent / 100f;
                var imgSize = new Vector2(texture.Width * zoomScale, texture.Height * zoomScale);

                // Scale image down to fit viewport if it exceeds screen dimensions
                if (imgSize.X > winSize.X * 0.9f || imgSize.Y > winSize.Y * 0.9f)
                {
                    var ratio = Math.Min((winSize.X * 0.9f) / imgSize.X, (winSize.Y * 0.9f) / imgSize.Y);
                    imgSize *= ratio;
                }

                var min = new Vector2(winSize.X / 2 - imgSize.X / 2, winSize.Y / 2 - imgSize.Y / 2);
                var max = new Vector2(winSize.X / 2 + imgSize.X / 2, winSize.Y / 2 + imgSize.Y / 2);

                // Draw background dimming overlay
                ImGui.GetForegroundDrawList().AddRectFilled(Vector2.Zero, winSize, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.5f)));
                // Draw the preview image centered
                ImGui.GetForegroundDrawList().AddImage(texture.Handle, min, max);
            }
        }
    }



    private void ShowPreviewTooltip(string fullImagePath, string optionName)
    {
        var texture = Plugin.TextureProvider.GetFromFile(plugin.GetBustedImagePath(fullImagePath)).GetWrapOrDefault();
        if (texture != null)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), optionName);
            ImGui.Separator();
            
            float aspect = 1f;
            if (texture.Width > 0 && texture.Height > 0)
            {
                aspect = (float)texture.Width / texture.Height;
            }
            
            float drawWidth = 250f;
            float drawHeight = 250f / aspect;
            
            ImGui.Image(texture.Handle, new Vector2(drawWidth, drawHeight));
            ImGui.EndTooltip();
        }
    }
}

