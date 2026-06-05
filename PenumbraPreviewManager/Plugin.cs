using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using PenumbraPreviewManager.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace PenumbraPreviewManager;

public class ModInfo
{
    public string FolderName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public List<string> ModTags { get; set; } = new();
    
    public string? PreviewImagePath { get; set; }
    public bool HasPreview => !string.IsNullOrEmpty(PreviewImagePath);
    public bool IsHeliosphereManaged { get; set; } = false;
}

public enum GrabResult
{
    Success,
    FailedGeneric,
    NsfwRestricted
}

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/ppm";
    private const string AltCommandName = "/preview";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PenumbraPreviewManager");
    private ConfigWindow ConfigWindow { get; init; }
    public PreviewWindow PreviewWindow { get; init; }
    private PenumbraWindowIntegration PenumbraWindowIntegration { get; init; }

    // IPC subscribers to keep mod list synced in real-time
    private EventSubscriber<string>? modAddedSubscriber;
    private EventSubscriber<string>? modDeletedSubscriber;
    private EventSubscriber<string, string>? modMovedSubscriber;
    private EventSubscriber<string, bool>? modDirectoryChangedSubscriber;

    // In-memory list of mods
    public List<ModInfo> Mods { get; private set; } = new();
    public bool IsScanning { get; private set; } = false;
    private readonly object scanLock = new();
    private bool scanQueued = false;
    public string? DetectedModDirectory { get; private set; }
    
    private readonly HttpClient httpClient = new();

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Set standard browser User-Agent to avoid Cloudflare 403 Forbidden blocks on XIVModArchive and other static servers
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        ConfigWindow = new ConfigWindow(this);
        PreviewWindow = new PreviewWindow(this);
        PenumbraWindowIntegration = new PenumbraWindowIntegration(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(PreviewWindow);

        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Penumbra Preview Manager UI"
        };
        CommandManager.AddHandler(CommandName, commandInfo);
        CommandManager.AddHandler(AltCommandName, commandInfo);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        PenumbraWindowIntegration.Register();

        // Register to Penumbra IPC events for automatic synchronization
        try
        {
            modAddedSubscriber = ModAdded.Subscriber(PluginInterface, mod => Task.Run(() => ScanModsAsync()));
            modDeletedSubscriber = ModDeleted.Subscriber(PluginInterface, mod => Task.Run(() => ScanModsAsync()));
            modMovedSubscriber = ModMoved.Subscriber(PluginInterface, (from, to) => Task.Run(() => ScanModsAsync()));
            modDirectoryChangedSubscriber = ModDirectoryChanged.Subscriber(PluginInterface, (_, _) => Task.Run(() => ScanModsAsync()));
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to subscribe to Penumbra mod lifecycle IPC events: {ex.Message}");
        }

        // Run initial scan in background
        Task.Run(() => ScanModsAsync());
    }

    public void Dispose()
    {
        modAddedSubscriber?.Dispose();
        modDeletedSubscriber?.Dispose();
        modMovedSubscriber?.Dispose();
        modDirectoryChangedSubscriber?.Dispose();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        PenumbraWindowIntegration.Dispose();
        ConfigWindow.Dispose();
        PreviewWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(AltCommandName);
        httpClient.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        PreviewWindow.Toggle();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => PreviewWindow.Toggle();

    /// <summary>
    /// Gets the Penumbra mod directory by trying IPC first, then falling back to parsing the config file.
    /// </summary>
    public string? GetModDirectory()
    {
        // 1. Try IPC
        try
        {
            var getDirSubscriber = PluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
            if (getDirSubscriber != null)
            {
                var dir = getDirSubscriber.InvokeFunc();
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    DetectedModDirectory = dir;
                    return dir;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Penumbra IPC GetModDirectory failed, falling back to config parsing. Error: {ex.Message}");
        }

        // 2. Fall back to config reading
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var configPath = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "Penumbra.json");
            if (File.Exists(configPath))
            {
                var content = File.ReadAllText(configPath);
                var match = Regex.Match(content, @"""ModDirectory""\s*:\s*""([^""]+)""");
                if (match.Success)
                {
                    var path = Regex.Unescape(match.Groups[1].Value);
                    if (Directory.Exists(path))
                    {
                        DetectedModDirectory = path;
                        return path;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to parse Penumbra.json: {ex.Message}");
        }

        return DetectedModDirectory;
    }

    /// <summary>
    /// Scans the mod directory for mods and previews
    /// </summary>
    public async Task ScanModsAsync()
    {
        lock (scanLock)
        {
            if (IsScanning)
            {
                scanQueued = true;
                return;
            }
            IsScanning = true;
        }

        try
        {
            bool runAgain;
            do
            {
                var modDir = GetModDirectory();
                if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir))
                {
                    Log.Warning("Penumbra mod directory not detected or does not exist.");
                    Mods = new List<ModInfo>();
                    return;
                }

                var scannedMods = new List<ModInfo>();
                var directories = Directory.GetDirectories(modDir);

                await Task.Run(() =>
                {
                    foreach (var dir in directories)
                    {
                        var folderName = Path.GetFileName(dir);
                        if (folderName.StartsWith(".") || folderName.StartsWith("_")) continue;

                        var modInfo = new ModInfo
                        {
                            FolderName = folderName,
                            FullPath = dir,
                            Name = folderName,
                            IsHeliosphereManaged = File.Exists(Path.Combine(dir, "heliosphere.json"))
                        };

                        // Parse meta.json if it exists
                        var metaPath = Path.Combine(dir, "meta.json");
                        if (File.Exists(metaPath))
                        {
                            try
                            {
                                var jsonText = File.ReadAllText(metaPath);
                                var obj = JsonConvert.DeserializeObject<JObject>(jsonText);
                                if (obj != null)
                                {
                                    modInfo.Name = obj.Value<string>("Name") ?? folderName;
                                    modInfo.Author = obj.Value<string>("Author") ?? "Unknown";
                                    modInfo.Description = obj.Value<string>("Description") ?? string.Empty;
                                    modInfo.Version = obj.Value<string>("Version") ?? string.Empty;
                                    modInfo.Website = obj.Value<string>("Website") ?? string.Empty;
                                    
                                    var tagsToken = obj["ModTags"];
                                    if (tagsToken != null)
                                    {
                                        modInfo.ModTags = tagsToken.ToObject<List<string>>() ?? new List<string>();
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Debug($"Failed to parse meta.json in {folderName}: {ex.Message}");
                            }
                        }

                        // Check for preview images
                        var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp" };
                        var previewNames = new[] { "preview", "cover" };
                        
                        foreach (var name in previewNames)
                        {
                            foreach (var ext in imageExtensions)
                            {
                                var checkPath = Path.Combine(dir, $"{name}{ext}");
                                if (File.Exists(checkPath))
                                {
                                    modInfo.PreviewImagePath = checkPath;
                                    break;
                                }
                            }
                            if (modInfo.HasPreview) break;
                        }

                        scannedMods.Add(modInfo);
                    }
                });

                // Sort alphabetically by name
                Mods = scannedMods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
                
                lock (scanLock)
                {
                    runAgain = scanQueued;
                    if (runAgain)
                    {
                        scanQueued = false;
                    }
                }
            } while (runAgain);
        }
        catch (Exception ex)
        {
            Log.Error($"Error scanning mods: {ex.Message}");
        }
        finally
        {
            lock (scanLock)
            {
                IsScanning = false;
            }
        }
    }

    /// <summary>
    /// Adds or removes the preview tag in Penumbra meta.json and tells Penumbra to reload the mod
    /// </summary>
    public void UpdateModTag(ModInfo mod, bool hasPreview)
    {
        var metaPath = Path.Combine(mod.FullPath, "meta.json");
        if (!File.Exists(metaPath)) return;

        try
        {
            var jsonText = File.ReadAllText(metaPath);
            var obj = JsonConvert.DeserializeObject<JObject>(jsonText);
            if (obj == null) return;

            var tagsToken = obj["ModTags"];
            var tags = tagsToken?.ToObject<List<string>>() ?? new List<string>();

            const string tagHas = "has_preview";
            const string tagNo = "no_preview";

            tags.Remove(tagHas);
            tags.Remove(tagNo);

            if (hasPreview)
            {
                tags.Add(tagHas);
            }
            else
            {
                tags.Add(tagNo);
            }

            obj["ModTags"] = JArray.FromObject(tags);
            File.WriteAllText(metaPath, JsonConvert.SerializeObject(obj, Formatting.Indented));
            
            // Reload the mod via Penumbra IPC
            ReloadModInPenumbra(mod.FolderName);
            
            // Update in-memory mod tag
            mod.ModTags = tags;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to update tag for mod {mod.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks Penumbra to reload the mod so that our tag updates take effect.
    /// </summary>
    public void ReloadModInPenumbra(string modFolderName)
    {
        try
        {
            var reloadSubscriber = PluginInterface.GetIpcSubscriber<string, object>("Penumbra.ReloadMod");
            reloadSubscriber?.InvokeAction(modFolderName);
        }
        catch (Exception ex)
        {
            Log.Debug($"Penumbra.ReloadMod IPC call failed (Penumbra might not be loaded): {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to grab an image from a URL or XIVModArchive website and sets it as the preview
    /// </summary>
    public async Task<GrabResult> GrabImageFromUrlAsync(ModInfo mod, string url)
    {
        try
        {
            var targetImageUrl = url;

            // 1. If it's a XIVModArchive link, parse it to find the primary image URL
            if (url.Contains("xivmodarchive.com"))
            {
                string html;
                try
                {
                    html = await httpClient.GetStringAsync(url);
                }
                catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.Forbidden || httpEx.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.Warning($"Grab image failed due to NSFW/Restricted access control: {httpEx.Message}");
                    return GrabResult.NsfwRestricted;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Grab image HTML fetch failed: {ex.Message}");
                    return GrabResult.FailedGeneric;
                }
                
                // Regex for og:image meta tag
                var match = Regex.Match(html, @"<meta\s+property=[""']og:image[""']\s+content=[""']([^""']+)[""']");
                if (match.Success)
                {
                    targetImageUrl = match.Groups[1].Value;
                }
                else
                {
                    // Fallback to twitter:image
                    match = Regex.Match(html, @"<meta\s+name=[""']twitter:image[""']\s+content=[""']([^""']+)[""']");
                    if (match.Success)
                    {
                        targetImageUrl = match.Groups[1].Value;
                    }
                    else
                    {
                        // Fallback to searching the entire HTML for any mod-images static link (handles raw, JSON, and double-escaped slashes)
                        var staticMatch = Regex.Match(html, @"https?:\\?/\\?/static\.xivmodarchive\.com\\?/mod-images\\?/[a-fA-F0-9\-]+\.(?:jpg|jpeg|png|webp)", RegexOptions.IgnoreCase);
                        if (staticMatch.Success)
                        {
                            targetImageUrl = staticMatch.Value.Replace("\\", "");
                        }
                        else
                        {
                            Log.Warning("No images found on the XIVModArchive page. It might be NSFW/Restricted.");
                            return GrabResult.NsfwRestricted;
                        }
                    }
                }
            }

            // 2. Download the image
            var bytes = await httpClient.GetByteArrayAsync(targetImageUrl);
            var tempFile = Path.Combine(Path.GetTempPath(), $"ppm_download_{Guid.NewGuid()}.tmp");
            await File.WriteAllBytesAsync(tempFile, bytes);

            // 3. Scale, crop, and save
            var targetPath = Path.Combine(mod.FullPath, "preview.png");
            var success = await Task.Run(() =>
            {
                try
                {
                    CropAndScaleImage(tempFile, targetPath, 800, 450); // Perfect 16:9 preview size
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Image processing failed: {ex.Message}");
                    return false;
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            if (success)
            {
                mod.PreviewImagePath = targetPath;
                UpdateModTag(mod, true);
                return GrabResult.Success;
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Grab image from URL failed: {ex.Message}");
        }

        return GrabResult.FailedGeneric;
    }

    /// <summary>
    /// Crops and scales a local image to a target width and height (aspect ratio) and saves it as PNG.
    /// </summary>
    public void CropAndScaleImage(string sourcePath, string targetPath, int width, int height)
    {
        using (var originalImage = System.Drawing.Image.FromFile(sourcePath))
        {
            SaveImageFromBitmap(originalImage, targetPath, width, height);
        }
    }

    /// <summary>
    /// Crops, scales, and saves a System.Drawing.Image directly to a target path.
    /// </summary>
    public void SaveImageFromBitmap(System.Drawing.Image originalImage, string targetPath, int width, int height)
    {
        float targetAspect = (float)width / height;
        float sourceAspect = (float)originalImage.Width / originalImage.Height;
        
        int cropWidth = originalImage.Width;
        int cropHeight = originalImage.Height;
        int cropX = 0;
        int cropY = 0;
        
        if (sourceAspect > targetAspect)
        {
            cropWidth = (int)(originalImage.Height * targetAspect);
            cropX = (originalImage.Width - cropWidth) / 2;
        }
        else
        {
            cropHeight = (int)(originalImage.Width / targetAspect);
            cropY = (originalImage.Height - cropHeight) / 2;
        }
        
        using (var bitmap = new System.Drawing.Bitmap(width, height))
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            
            g.DrawImage(originalImage, 
                new System.Drawing.Rectangle(0, 0, width, height), 
                new System.Drawing.Rectangle(cropX, cropY, cropWidth, cropHeight), 
                System.Drawing.GraphicsUnit.Pixel);
                
            // Ensure output directory exists
            var dir = Path.GetDirectoryName(targetPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bitmap.Save(targetPath, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
