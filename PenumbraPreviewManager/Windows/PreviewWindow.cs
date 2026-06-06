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
    private readonly Dictionary<string, (string Group, string Option)> selectedOptionsCache = new();

    public ModInfo? SelectedMod
    {
        get => selectedMod;
        set
        {
            if (selectedMod != value)
            {
                // Cache the selection of the previous mod before switching
                if (selectedMod != null)
                {
                    selectedOptionsCache[selectedMod.FolderName] = (selectedGroupName, selectedOptionName);
                }

                selectedMod = value;
                localImagePathInput = string.Empty;
                grabUrlInput = value?.Website ?? string.Empty;
                statusMessage = string.Empty;

                // Load the cached option previews UI state or default
                if (value != null && selectedOptionsCache.TryGetValue(value.FolderName, out var cached))
                {
                    selectedGroupName = cached.Group;
                    selectedOptionName = cached.Option;
                }
                else
                {
                    selectedGroupName = string.Empty;
                    selectedOptionName = string.Empty;
                }

                optionLocalImagePathInput = string.Empty;
                optionGrabUrlInput = string.Empty;
                optionStatusMessage = string.Empty;
                selectedUnassignedIndex = 0;
            }
        }
    }
    
    // UI input buffers
    private string localImagePathInput = string.Empty;
    private string grabUrlInput = string.Empty;
    private string statusMessage = string.Empty;
    private bool isWorking = false;

    // Option previews UI state
    private string selectedGroupName = string.Empty;
    private string selectedOptionName = string.Empty;
    private string optionLocalImagePathInput = string.Empty;
    private string optionGrabUrlInput = string.Empty;
    private string optionStatusMessage = string.Empty;
    private bool optionIsWorking = false;
    private int selectedUnassignedIndex = 0;



    public PreviewWindow(Plugin plugin)
        : base("Penumbra Preview Manager###PPM_Main")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        
        Size = new Vector2(850, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
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

        if (plugin.Configuration.AutoSyncSelection && plugin.Configuration.HideModList)
        {
            DrawModDetails();
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
            if (plugin.Configuration.AutoSyncSelection && plugin.Configuration.HideModList)
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "Mod list is hidden because auto-sync is enabled. Please select a mod inside Penumbra to load it here.");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "Select a mod from the list to manage its preview.");
            }
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
            
            var texture = Plugin.TextureProvider.GetFromFile(plugin.GetBustedImagePath(selectedMod.PreviewImagePath!)).GetWrapOrDefault();
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

        // Option Previews Area below a divider
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Mod Option Previews");

        // Fetch settings groups from Penumbra via our IPC helper
        var settings = plugin.GetAvailableSettings(selectedMod.FolderName);
        if (settings == null || settings.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "This mod does not have any setting groups configured in Penumbra.");
            return;
        }

        // Group selector dropdown
        var groupNames = settings.Keys.ToArray();
        
        // Ensure selectedGroupName is valid, else default to first group
        if (string.IsNullOrEmpty(selectedGroupName) || !settings.ContainsKey(selectedGroupName))
        {
            selectedGroupName = groupNames[0];
            selectedOptionName = string.Empty;
        }

        int groupIndex = Array.IndexOf(groupNames, selectedGroupName);
        ImGui.TextUnformatted("Select Option Group:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##OptionGroupSelector", ref groupIndex, groupNames, groupNames.Length))
        {
            selectedGroupName = groupNames[groupIndex];
            selectedOptionName = string.Empty;
            optionLocalImagePathInput = string.Empty;
            optionGrabUrlInput = string.Empty;
            optionStatusMessage = string.Empty;
            if (selectedMod != null)
            {
                selectedOptionsCache[selectedMod.FolderName] = (selectedGroupName, selectedOptionName);
            }
        }

        // Option selector dropdown
        var options = settings[selectedGroupName].Options;
        if (options == null || options.Length == 0)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "No options available in this group.");
            return;
        }

        if (string.IsNullOrEmpty(selectedOptionName) || !options.Contains(selectedOptionName))
        {
            selectedOptionName = options[0];
            optionLocalImagePathInput = string.Empty;
            optionGrabUrlInput = string.Empty;
            optionStatusMessage = string.Empty;
        }

        int optionIndex = Array.IndexOf(options, selectedOptionName);
        ImGui.Spacing();
        ImGui.TextUnformatted("Select Option:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##OptionSelector", ref optionIndex, options, options.Length))
        {
            selectedOptionName = options[optionIndex];
            optionLocalImagePathInput = string.Empty;
            optionGrabUrlInput = string.Empty;
            optionStatusMessage = string.Empty;
            if (selectedMod != null)
            {
                selectedOptionsCache[selectedMod.FolderName] = (selectedGroupName, selectedOptionName);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Load the manifest to see if a preview image is registered for this option
        var manifest = plugin.LoadOptionManifest(selectedMod!.FullPath);
        var key = $"{selectedGroupName}/{selectedOptionName}";
        string? imagePath = null;
        bool hasOptionPreview = manifest.OptionImages.TryGetValue(key, out imagePath) && !string.IsNullOrEmpty(imagePath) && File.Exists(Path.Combine(selectedMod.FullPath, imagePath));

        if (hasOptionPreview)
        {
            var fullImagePath = Path.Combine(selectedMod.FullPath, imagePath!);
            ImGui.TextColored(new Vector4(0.2f, 0.9f, 0.2f, 1f), $"✓ Preview Image Found for [{selectedOptionName}]:");
            
            var texture = Plugin.TextureProvider.GetFromFile(plugin.GetBustedImagePath(fullImagePath)).GetWrapOrDefault();
            if (texture != null)
            {
                var colWidth = ImGui.GetContentRegionAvail().X;
                float aspect = 1f; // 1:1 default for options
                if (texture.Width > 0 && texture.Height > 0)
                {
                    aspect = (float)texture.Width / texture.Height;
                }
                var drawHeight = colWidth / aspect;
                
                // Cap height to make it fit nicely
                if (drawHeight > 180)
                {
                    drawHeight = 180;
                    colWidth = drawHeight * aspect;
                }
                
                ImGui.Image(texture.Handle, new Vector2(colWidth, drawHeight));
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "[Image file found but could not be loaded into game texture wrapper]");
            }

            if (ImGui.Button("Remove Option Preview Image##RemoveOptionImg"))
            {
                plugin.ClearOptionImage(selectedMod, selectedGroupName, selectedOptionName);
                optionStatusMessage = "Option preview image removed.";
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.2f, 1f), $"✗ No Preview Image Found for [{selectedOptionName}]");
            
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.1f, 0.1f, 0.1f, 0.5f));
            using (var optionAddHelp = ImRaii.Child("NoOptionPreviewHelp", new Vector2(0, 45), true, ImGuiWindowFlags.NoScrollbar))
            {
                if (optionAddHelp.Success)
                {
                    string cropText = plugin.Configuration.OptionCrop switch
                    {
                        OptionCropAspect.Aspect16_9 => "16:9",
                        OptionCropAspect.NoCrop => "No Crop (Preserve Aspect)",
                        OptionCropAspect.Aspect1_1 or _ => "1:1 Square"
                    };
                    ImGui.TextWrapped($"Add a preview image for the option '{selectedOptionName}' (default crop: {cropText}).");

                }
            }
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();

        // 1. Grab Option Image from URL
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Grab Option Image from URL");
        ImGui.InputTextWithHint("##OptionGrabUrl", "Paste URL (e.g. XIVModArchive or direct image link)", ref optionGrabUrlInput, 500);
        ImGui.SameLine();

        if (optionIsWorking)
        {
            ImGui.TextUnformatted("Grabbing...");
        }
        else
        {
            if (ImGui.Button("Grab & Crop Option Image##GrabOptionBtn"))
            {
                if (!string.IsNullOrEmpty(optionGrabUrlInput))
                {
                    optionIsWorking = true;
                    optionStatusMessage = "Downloading and processing option image... please wait.";
                    
                    var targetUrl = optionGrabUrlInput;
                    var targetGroup = selectedGroupName;
                    var targetOption = selectedOptionName;
                    var targetMod = selectedMod;
                    
                    Task.Run(async () =>
                    {
                        try
                        {
                            var result = await plugin.GrabOptionImageFromUrlAsync(targetMod, targetGroup, targetOption, targetUrl);
                            optionIsWorking = false;
                            if (result == GrabResult.Success)
                            {
                                optionStatusMessage = "Successfully downloaded, cropped, and saved option preview!";
                            }
                            else if (result == GrabResult.NsfwRestricted)
                            {
                                optionStatusMessage = "Download failed: Content is NSFW/Restricted on XIVModArchive.\n\n" +
                                                      "Please save the image manually, copy it to clipboard, and click 'Paste Option Image' below.";
                            }
                            else
                            {
                                optionStatusMessage = "Failed to grab option image. Please verify the URL.";
                            }
                        }
                        catch (Exception ex)
                        {
                            optionIsWorking = false;
                            optionStatusMessage = $"Error: {ex.Message}";
                        }
                    });
                }
                else
                {
                    optionStatusMessage = "Please enter a valid URL.";
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Set Option Local Image File / Clipboard");
        ImGui.InputTextWithHint("##OptionLocalPath", "Paste local path...", ref optionLocalImagePathInput, 500);
        ImGui.SameLine();

        if (ImGui.Button("Browse...##BrowseOptionImg"))
        {
            plugin.FileDialogManager.OpenFileDialog(
                "Select Option Preview Image", 
                "Image Files{.png,.jpg,.jpeg,.webp,.bmp,.gif}", 
                (success, path) =>
                {
                    if (success)
                    {
                        optionLocalImagePathInput = path;
                    }
                });
        }
        ImGui.SameLine();

        if (ImGui.Button("Set Option Image##SetOptionBtn"))
        {
            if (File.Exists(optionLocalImagePathInput))
            {
                try
                {
                    using (var image = System.Drawing.Image.FromFile(optionLocalImagePathInput))
                    {
                        var relativePath = plugin.SaveOptionImage(selectedMod, selectedGroupName, selectedOptionName, image);
                        if (relativePath != null)
                        {
                            optionStatusMessage = "Option image set successfully!";
                            optionLocalImagePathInput = string.Empty;
                        }
                        else
                        {
                            optionStatusMessage = "Failed to process and save option image.";
                        }
                    }
                }
                catch (Exception ex)
                {
                    optionStatusMessage = $"Error: {ex.Message}";
                }
            }
            else
            {
                optionStatusMessage = "File does not exist.";
            }
        }

        if (ImGui.Button("Paste Option Image from Clipboard##PasteOptionBtn"))
        {
            try
            {
                using (var clipboardImage = ClipboardHelper.GetImageFromClipboard())
                {
                    if (clipboardImage != null)
                    {
                        var relativePath = plugin.SaveOptionImage(selectedMod, selectedGroupName, selectedOptionName, clipboardImage);
                        if (relativePath != null)
                        {
                            optionStatusMessage = "Image successfully pasted from clipboard, cropped/scaled, and registered!";
                        }
                        else
                        {
                            optionStatusMessage = "Failed to save option image.";
                        }
                    }
                    else
                    {
                        optionStatusMessage = "No image found in clipboard! Please copy an image first.";
                    }
                }
            }
            catch (Exception ex)
            {
                optionStatusMessage = $"Clipboard paste failed: {ex.Message}";
            }
        }

        // 3. Assign from Unassigned Images in ppm/ folder
        var assignedImages = manifest.OptionImages.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unassignedImages = new List<string>();
        var ppmDir = Path.Combine(selectedMod.FullPath, "ppm");
        if (Directory.Exists(ppmDir))
        {
            try
            {
                var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
                var files = Directory.GetFiles(ppmDir)
                    .Where(file => allowedExtensions.Contains(Path.GetExtension(file)));
                foreach (var file in files)
                {
                    var relativePath = Path.Combine("ppm", Path.GetFileName(file)).Replace('\\', '/');
                    if (!assignedImages.Contains(relativePath))
                    {
                        unassignedImages.Add(relativePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to scan ppm folder: {ex.Message}");
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Assign Unassigned Image from ppm/");

        if (unassignedImages.Count > 0)
        {
            if (selectedUnassignedIndex >= unassignedImages.Count)
            {
                selectedUnassignedIndex = 0;
            }
            
            var displayNames = unassignedImages.Select(p => Path.GetFileName(p)).ToArray();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##UnassignedCombo", ref selectedUnassignedIndex, displayNames, displayNames.Length))
            {
                // Unassigned image selection changed
            }
            
            // Show preview of selected unassigned image
            var chosenPath = unassignedImages[selectedUnassignedIndex];
            var fullImagePath = Path.Combine(selectedMod.FullPath, chosenPath);
            var texture = Plugin.TextureProvider.GetFromFile(plugin.GetBustedImagePath(fullImagePath)).GetWrapOrDefault();
            if (texture != null)
            {
                ImGui.Spacing();
                var colWidth = ImGui.GetContentRegionAvail().X;
                float aspect = 1f;
                if (texture.Width > 0 && texture.Height > 0)
                {
                    aspect = (float)texture.Width / texture.Height;
                }
                var drawHeight = colWidth / aspect;
                if (drawHeight > 180)
                {
                    drawHeight = 180;
                    colWidth = drawHeight * aspect;
                }
                ImGui.Image(texture.Handle, new Vector2(colWidth, drawHeight));
            }
            
            ImGui.Spacing();
            if (ImGui.Button("Assign Selected Image##AssignUnassignedBtn"))
            {
                // Clear old preview image (handles deletion of old asset if registered)
                plugin.ClearOptionImage(selectedMod, selectedGroupName, selectedOptionName);
                
                // Set the chosen path in manifest
                var optionManifest = plugin.LoadOptionManifest(selectedMod.FullPath);
                var optionKey = $"{selectedGroupName}/{selectedOptionName}";
                optionManifest.OptionImages[optionKey] = chosenPath;
                plugin.SaveOptionManifest(selectedMod.FullPath, optionManifest);
                
                optionStatusMessage = $"Assigned image '{displayNames[selectedUnassignedIndex]}' to '{selectedOptionName}'!";
                selectedUnassignedIndex = 0;
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "No unassigned images found in ppm/ folder.");
        }

        if (!string.IsNullOrEmpty(optionStatusMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), optionStatusMessage);
        }

    }
}
