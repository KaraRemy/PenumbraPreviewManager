using System;
using System.IO;
using System.Numerics;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Textures;

namespace PenumbraPreviewManager.Windows;

public class PreviewWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string searchText = string.Empty;
    private ModInfo? selectedMod;

    public ModInfo? SelectedMod
    {
        get => selectedMod;
        set
        {
            if (selectedMod != value)
            {
                selectedMod = value;
                localImagePathInput = string.Empty;
                grabUrlInput = value?.Website ?? string.Empty;
                statusMessage = string.Empty;
            }
        }
    }
    
    // UI input buffers
    private string localImagePathInput = string.Empty;
    private string grabUrlInput = string.Empty;
    private string statusMessage = string.Empty;
    private bool isWorking = false;

    public PreviewWindow(Plugin plugin)
        : base("Penumbra Preview Manager###PPM_Main")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        
        Size = new Vector2(850, 600);
        this.plugin = plugin;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(1, 1),
            Click = _ => plugin.ToggleConfigUi(),
            ShowTooltip = () => ImGui.SetTooltip("Settings")
        });
    }

    public void Dispose() { }

    public void OpenModPage(ModInfo mod)
    {
        SelectedMod = mod;
        IsOpen = true;
    }

    public override void PreDraw()
    {
    }

    public override void Draw()
    {
        // Title Bar & Options
        if (ImGui.Button("Refresh Mod List"))
        {
            Task.Run(() => plugin.ScanModsAsync());
        }

        ImGui.Separator();

        if (plugin.IsScanning)
        {
            ImGui.TextUnformatted("Scanning Penumbra mod directories... Please wait.");
            return;
        }

        if (plugin.Mods.Count == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "No mods detected or Penumbra Mod Directory is not configured!");
            ImGui.TextUnformatted($"Mod Directory Path: {plugin.DetectedModDirectory ?? "Unknown"}");
            return;
        }

        // Layout: Left search list and right details (if not docked/wide, otherwise stacked if window size is narrow)
        var windowWidth = ImGui.GetWindowWidth();
        var useSplitLayout = windowWidth > 650;

        if (useSplitLayout)
        {
            // Left pane: 220px width
            using (var leftChild = ImRaii.Child("LeftPane", new Vector2(220, 0), true))
            {
                if (leftChild.Success)
                {
                    DrawModList();
                }
            }

            ImGui.SameLine();

            // Right pane: remaining width
            using (var rightChild = ImRaii.Child("RightPane", new Vector2(0, 0), true))
            {
                if (rightChild.Success)
                {
                    DrawModDetails();
                }
            }
        }
        else
        {
            // Stacked layout for compact sidebar view
            using (var topChild = ImRaii.Child("TopPane", new Vector2(0, 200), true))
            {
                if (topChild.Success)
                {
                    DrawModList();
                }
            }
            
            ImGui.Separator();

            using (var bottomChild = ImRaii.Child("BottomPane", new Vector2(0, 0), true))
            {
                if (bottomChild.Success)
                {
                    DrawModDetails();
                }
            }
        }
    }

    private void DrawModList()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Search:");
        ImGui.SameLine();
        ImGui.InputText("##SearchMods", ref searchText, 100);

        ImGui.Separator();

        var filteredMods = plugin.Mods;
        if (!string.IsNullOrEmpty(searchText))
        {
            filteredMods = plugin.Mods
                .Where(m => m.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) || 
                            m.FolderName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        using (var listChild = ImRaii.Child("ModListContainer", Vector2.Zero, false))
        {
            if (listChild.Success)
            {
                foreach (var mod in filteredMods)
                {
                    var isSelected = mod == selectedMod;
                    
                    // Show a dot indicator for preview image presence
                    // Green for preview exists, gray for missing
                    var dotColor = mod.HasPreview ? new Vector4(0.2f, 0.9f, 0.2f, 1f) : new Vector4(0.5f, 0.5f, 0.5f, 1f);
                    
                    ImGui.PushStyleColor(ImGuiCol.Text, dotColor);
                    ImGui.TextUnformatted("●");
                    ImGui.PopStyleColor();
                    
                    ImGui.SameLine();
                    
                    if (ImGui.Selectable($"{mod.Name}##{mod.FolderName}", isSelected))
                    {
                        SelectedMod = mod;
                    }
                }
            }
        }
    }

    private void DrawModDetails()
    {
        if (selectedMod == null)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "Select a mod from the list to manage its preview.");
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 4));
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), selectedMod.Name);
        
        if (!string.IsNullOrEmpty(selectedMod.Author))
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Author: {selectedMod.Author}");
        }

        if (!string.IsNullOrEmpty(selectedMod.Version))
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Version: {selectedMod.Version}");
        }
        ImGui.PopStyleVar();

        if (ImGui.Button("Copy Search Terms (Folder Name & Author)##ClipboardCopy"))
        {
            var copyText = selectedMod.FolderName;
            if (!string.IsNullOrEmpty(selectedMod.Author) && selectedMod.Author != "Unknown")
            {
                copyText += " " + selectedMod.Author;
            }
            ImGui.SetClipboardText(copyText);
            statusMessage = $"Copied search terms: \"{copyText}\" to clipboard!";
        }

        if (ImGui.Button("Search for Mod on XMA##XMASearch"))
        {
            var searchTerms = selectedMod.FolderName;
            if (!string.IsNullOrEmpty(selectedMod.Author) && selectedMod.Author != "Unknown")
            {
                searchTerms += " " + selectedMod.Author;
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
        }

        // Folder & explorer button
        ImGui.TextUnformatted($"Folder: {selectedMod.FolderName}");
        ImGui.SameLine();
        if (ImGui.Button("Open Folder##Explorer"))
        {
            try
            {
                Process.Start("explorer.exe", selectedMod.FullPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to open mod folder: {ex.Message}");
            }
        }

        ImGui.Separator();

        // Image Display
        if (selectedMod.HasPreview)
        {
            ImGui.TextColored(new Vector4(0.2f, 0.9f, 0.2f, 1f), "✓ Preview Image Found:");
            
            var texture = Plugin.TextureProvider.GetFromFile(selectedMod.PreviewImagePath!).GetWrapOrDefault();
            if (texture != null)
            {
                // Display the image beautifully fit to the column width, keeping its actual aspect ratio
                var colWidth = ImGui.GetContentRegionAvail().X;
                float aspect = 16f / 9f;
                if (texture.Width > 0 && texture.Height > 0)
                {
                    aspect = (float)texture.Width / texture.Height;
                }
                var drawHeight = colWidth / aspect;
                if (drawHeight > 250)
                {
                    drawHeight = 250;
                    colWidth = drawHeight * aspect;
                }
                
                ImGui.Image(texture.Handle, new Vector2(colWidth, drawHeight));
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "[Image file found but could not be loaded into game texture wrapper]");
            }

            if (ImGui.Button("Remove Preview Image", new Vector2(0, 0)))
            {
                try
                {
                    if (File.Exists(selectedMod.PreviewImagePath))
                    {
                        File.Delete(selectedMod.PreviewImagePath);
                    }
                    selectedMod.PreviewImagePath = null;
                    plugin.UpdateModTag(selectedMod, false);
                    statusMessage = "Preview image removed.";
                }
                catch (Exception ex)
                {
                    statusMessage = $"Failed to delete: {ex.Message}";
                }
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.2f, 1f), "✗ No Preview Image Found");
            
            // Helpful tutorial indicator
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.1f, 0.1f, 0.1f, 0.5f));
            using (var addHelp = ImRaii.Child("NoPreviewHelp", new Vector2(0, 60), true, ImGuiWindowFlags.NoScrollbar))
            {
                if (addHelp.Success)
                {
                    ImGui.TextWrapped("Add an image to display a preview for this mod. It will automatically crop and scale it into a high-quality PNG preview according to your settings!");
                }
            }
            ImGui.PopStyleColor();
        }

        ImGui.Separator();

        // 1. Grab Image from URL (XIVModArchive / Direct Link)
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Grab Image from URL / XIVModArchive");
        ImGui.InputTextWithHint("##GrabUrl", "Paste URL (e.g. https://xivmodarchive.com/...)", ref grabUrlInput, 500);
        ImGui.SameLine();
        
        if (isWorking)
        {
            ImGui.TextUnformatted("Grabbing...");
        }
        else
        {
            if (ImGui.Button("Grab & Scale Image"))
            {
                if (!string.IsNullOrEmpty(grabUrlInput))
                {
                    isWorking = true;
                    statusMessage = "Grabbing mod preview image... please wait.";
                    
                    Task.Run(async () =>
                    {
                        var result = await plugin.GrabImageFromUrlAsync(selectedMod, grabUrlInput);
                        isWorking = false;
                        if (result == GrabResult.Success)
                        {
                            statusMessage = "Successfully downloaded, cropped, and scaled preview image!";
                        }
                        else if (result == GrabResult.NsfwRestricted)
                        {
                            statusMessage = "Automatic import cancelled: This mod is NSFW/Restricted on XIVModArchive.\n\n" +
                                            "To add a preview manually:\n" +
                                            "1. Click the 'Search for Mod on XMA' button above to open the mod page in your browser.\n" +
                                            "2. Find the mod image on the webpage, right-click it, and select 'Copy Image'.\n" +
                                            "3. Click 'Paste Image from Clipboard' below to crop and set it instantly!";
                        }
                        else
                        {
                            statusMessage = "Failed to grab image. Please check the URL or try a direct image link.";
                        }
                    });
                }
                else
                {
                    statusMessage = "Please enter a valid URL first.";
                }
            }
        }

        // 2. Set Local File Image / Clipboard
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Set Local Image File / Clipboard");
        ImGui.InputTextWithHint("##LocalImagePath", "Paste local path (e.g. C:\\image.png)", ref localImagePathInput, 500);
        ImGui.SameLine();

        if (ImGui.Button("Browse...##BrowseLocalImage"))
        {
            plugin.FileDialogManager.OpenFileDialog(
                "Select Preview Image", 
                "Image Files{.png,.jpg,.jpeg,.webp,.bmp,.gif}", 
                (success, path) =>
                {
                    if (success)
                    {
                        localImagePathInput = path;
                    }
                });
        }
        ImGui.SameLine();
        
        if (ImGui.Button("Set Local Image"))
        {
            if (File.Exists(localImagePathInput))
            {
                try
                {
                    var targetPath = Path.Combine(selectedMod.FullPath, "preview.png");
                    plugin.CropAndScaleImage(localImagePathInput, targetPath, plugin.Configuration.CropOption);
                    selectedMod.PreviewImagePath = targetPath;
                    plugin.UpdateModTag(selectedMod, true);
                    statusMessage = "Local image cropped, scaled, and set successfully!";
                    localImagePathInput = string.Empty;
                }
                catch (Exception ex)
                {
                    statusMessage = $"Failed to process image: {ex.Message}";
                }
            }
            else
            {
                statusMessage = "File does not exist. Please check the path.";
            }
        }

        if (ImGui.Button("Paste Image from Clipboard"))
        {
            try
            {
                using (var clipboardImage = ClipboardHelper.GetImageFromClipboard())
                {
                    if (clipboardImage != null)
                    {
                        var targetPath = Path.Combine(selectedMod.FullPath, "preview.png");
                        plugin.SaveImageFromBitmap(clipboardImage, targetPath, plugin.Configuration.CropOption);
                        selectedMod.PreviewImagePath = targetPath;
                        plugin.UpdateModTag(selectedMod, true);
                        statusMessage = "Image successfully pasted from clipboard, cropped/scaled according to settings, and set!";
                    }
                    else
                    {
                        statusMessage = "No image found in clipboard! Please copy an image first.";
                    }
                }
            }
            catch (Exception ex)
            {
                statusMessage = $"Clipboard paste failed: {ex.Message}";
                Plugin.Log.Error($"Clipboard paste failed: {ex}");
            }
        }

        // Status Messages
        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), statusMessage);
        }
    }
}
