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
using Dalamud.Interface.ImGuiFileDialog;
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

public class OptionManifest
{
    public int Version { get; set; } = 1;
    public Dictionary<string, string> OptionImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;


    private const string CommandName = "/ppm";
    private const string AltCommandName = "/preview";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PenumbraPreviewManager");
    private ConfigWindow ConfigWindow { get; init; }
    public PreviewWindow PreviewWindow { get; init; }
    private PenumbraWindowIntegration PenumbraWindowIntegration { get; init; }
    public FileDialogManager FileDialogManager { get; } = new();
    internal ImGuiHookManager ImGuiHookManager { get; }




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
    
    private bool isHeliosphereActive = false;
    private DateTime lastHeliosphereCheck = DateTime.MinValue;

    // Cache dictionaries for performance optimization
    private readonly Dictionary<string, OptionManifest> manifestCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> validPathsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string BustedPath, long LastWriteTicks)> bustedPathCache = new(StringComparer.OrdinalIgnoreCase);

    public Plugin()
    {
        ClearTempCache();
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Set standard browser User-Agent to avoid Cloudflare 403 Forbidden blocks on XIVModArchive and other static servers
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        ConfigWindow = new ConfigWindow(this);
        PreviewWindow = new PreviewWindow(this);
        PenumbraWindowIntegration = new PenumbraWindowIntegration(this);
        ImGuiHookManager = new ImGuiHookManager(this, PenumbraWindowIntegration);
        ImGuiHookManager.Initialize();

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(PreviewWindow);


        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Penumbra Preview Manager UI"
        };
        CommandManager.AddHandler(CommandName, commandInfo);
        CommandManager.AddHandler(AltCommandName, commandInfo);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += DrawFileDialog;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        PluginInterface.UiBuilder.DisableGposeUiHide = true;

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
        PluginInterface.UiBuilder.Draw -= DrawFileDialog;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ImGuiHookManager.Dispose();
        PenumbraWindowIntegration.Dispose();
        ConfigWindow.Dispose();
        PreviewWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(AltCommandName);
        httpClient.Dispose();

        ClearTempCache();
    }

    private void OnCommand(string command, string args)
    {
        PreviewWindow.Toggle();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => PreviewWindow.Toggle();
    private void DrawFileDialog() => FileDialogManager.Draw();

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
            // Clear caching systems to prevent stale cache entries
            ClearManifestCache();
            PenumbraWindowIntegration?.ClearDrawCache();

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
                        if (string.Equals(folderName, ".git", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(folderName, ".github", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(folderName, ".vs", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(folderName, ".vscode", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(folderName, ".idea", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(folderName, "__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;

                        // Clean up legacy .ppm_cache_ files from previous sessions to keep mod directories pristine
                        try
                        {
                            foreach (var cacheFile in Directory.GetFiles(dir, ".ppm_cache_*"))
                            {
                                try { File.Delete(cacheFile); } catch { }
                            }
                            var ppmSubdir = Path.Combine(dir, "ppm");
                            if (Directory.Exists(ppmSubdir))
                            {
                                foreach (var cacheFile in Directory.GetFiles(ppmSubdir, ".ppm_cache_*"))
                                {
                                    try { File.Delete(cacheFile); } catch { }
                                }
                            }
                        }
                        catch { }

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
    /// Checks if the Heliosphere plugin is active and loaded, with a 5-second throttle.
    /// </summary>
    public bool IsHeliosphereActive()
    {
        var now = DateTime.UtcNow;
        if ((now - lastHeliosphereCheck).TotalSeconds > 5)
        {
            lastHeliosphereCheck = now;
            try
            {
                isHeliosphereActive = CommandManager.Commands.ContainsKey("/heliosphere") || IsHeliosphereLoaded();
            }
            catch
            {
                isHeliosphereActive = false;
            }
        }
        return isHeliosphereActive;
    }

    private bool IsHeliosphereLoaded()
    {
        try
        {
            var installedPluginsProp = PluginInterface.GetType().GetProperty("InstalledPlugins", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (installedPluginsProp == null) return false;

            var installedPlugins = installedPluginsProp.GetValue(PluginInterface) as System.Collections.IEnumerable;
            if (installedPlugins == null) return false;

            foreach (var plugin in installedPlugins)
            {
                var nameProp = plugin.GetType().GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                               ?? plugin.GetType().GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var name = nameProp?.GetValue(plugin) as string;
                if (string.Equals(name, "Heliosphere", StringComparison.OrdinalIgnoreCase))
                {
                    var isLoadedProp = plugin.GetType().GetProperty("IsLoaded", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                       ?? plugin.GetType().GetProperty("IsLoaded", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (isLoadedProp != null)
                    {
                        return (bool)isLoadedProp.GetValue(plugin)!;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to check if Heliosphere is loaded: {ex}");
        }
        return false;
    }

    /// <summary>
    /// Gets available option settings for a mod folder name from Penumbra.
    /// </summary>
    public IReadOnlyDictionary<string, (string[] Options, Penumbra.Api.Enums.GroupType Type)>? GetAvailableSettings(string modFolderName)
    {
        try
        {
            var ipc = new GetAvailableModSettings(PluginInterface);
            return ipc.Invoke(modFolderName, modFolderName);
        }
        catch (Exception ex)
        {
            Log.Debug($"Penumbra.GetAvailableModSettings failed for {modFolderName}: {ex.Message}");
            return null;
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
                    CropAndScaleImage(tempFile, targetPath, Configuration.CropOption);
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
    /// Grabs an image from a URL or XIVModArchive website and sets it as an option preview.
    /// </summary>
    public async Task<GrabResult> GrabOptionImageFromUrlAsync(ModInfo mod, string groupName, string optionName, string url)
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
                    Log.Warning($"Option Grab image failed due to NSFW/Restricted access control: {httpEx.Message}");
                    return GrabResult.NsfwRestricted;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Option Grab image HTML fetch failed: {ex.Message}");
                    return GrabResult.FailedGeneric;
                }
                
                var match = Regex.Match(html, @"<meta\s+property=[""']og:image[""']\s+content=[""']([^""']+)[""']");
                if (match.Success)
                {
                    targetImageUrl = match.Groups[1].Value;
                }
                else
                {
                    match = Regex.Match(html, @"<meta\s+name=[""']twitter:image[""']\s+content=[""']([^""']+)[""']");
                    if (match.Success)
                    {
                        targetImageUrl = match.Groups[1].Value;
                    }
                    else
                    {
                        var staticMatch = Regex.Match(html, @"https?:\\?/\\?/static\.xivmodarchive\.com\\?/mod-images\\?/[a-fA-F0-9\-]+\.(?:jpg|jpeg|png|webp)", RegexOptions.IgnoreCase);
                        if (staticMatch.Success)
                        {
                            targetImageUrl = staticMatch.Value.Replace("\\", "");
                        }
                        else
                        {
                            Log.Warning("No images found on the XIVModArchive page for option grab.");
                            return GrabResult.NsfwRestricted;
                        }
                    }
                }
            }

            // 2. Download the image
            var bytes = await httpClient.GetByteArrayAsync(targetImageUrl);
            var tempFile = Path.Combine(Path.GetTempPath(), $"ppm_option_download_{Guid.NewGuid()}.tmp");
            await File.WriteAllBytesAsync(tempFile, bytes);

            // 3. Process, crop, and save
            var success = await Task.Run(() =>
            {
                try
                {
                    using (var img = System.Drawing.Image.FromFile(tempFile))
                    {
                        var relativePath = SaveOptionImage(mod, groupName, optionName, img);
                        return relativePath != null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Option image processing failed: {ex.Message}");
                    return false;
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            if (success)
            {
                return GrabResult.Success;
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Grab option image from URL failed: {ex.Message}");
        }

        return GrabResult.FailedGeneric;
    }


    /// <summary>
    /// Crops and scales a local image to a target aspect ratio and saves it as PNG.
    /// </summary>
    public void CropAndScaleImage(string sourcePath, string targetPath, CropAspect cropOption)
    {
        using (var originalImage = System.Drawing.Image.FromFile(sourcePath))
        {
            SaveImageFromBitmap(originalImage, targetPath, cropOption);
        }
    }

    /// <summary>
    /// Crops, scales, and saves a System.Drawing.Image directly to a target path based on CropAspect.
    /// </summary>
    public void SaveImageFromBitmap(System.Drawing.Image originalImage, string targetPath, CropAspect cropOption)
    {
        int targetWidth, targetHeight;
        bool shouldCrop = true;

        switch (cropOption)
        {
            case CropAspect.Aspect16_9:
                targetWidth = 800;
                targetHeight = 450;
                break;
            case CropAspect.Aspect1_1:
                targetWidth = 600;
                targetHeight = 600;
                break;
            case CropAspect.Aspect4_3:
                targetWidth = 800;
                targetHeight = 600;
                break;
            case CropAspect.NoCrop:
            default:
                shouldCrop = false;
                targetWidth = originalImage.Width;
                targetHeight = originalImage.Height;
                int maxSize = 1024;
                if (targetWidth > maxSize || targetHeight > maxSize)
                {
                    float aspect = (float)targetWidth / targetHeight;
                    if (aspect > 1f)
                    {
                        targetWidth = maxSize;
                        targetHeight = (int)(maxSize / aspect);
                    }
                    else
                    {
                        targetHeight = maxSize;
                        targetWidth = (int)(maxSize * aspect);
                    }
                }
                break;
        }

        int cropWidth = originalImage.Width;
        int cropHeight = originalImage.Height;
        int cropX = 0;
        int cropY = 0;

        if (shouldCrop)
        {
            float targetAspect = (float)targetWidth / targetHeight;
            float sourceAspect = (float)originalImage.Width / originalImage.Height;
            
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
        }

        using (var bitmap = new System.Drawing.Bitmap(targetWidth, targetHeight))
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            
            g.DrawImage(originalImage, 
                new System.Drawing.Rectangle(0, 0, targetWidth, targetHeight), 
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

    /// <summary>
    /// Loads the ppm.json manifest for a given mod path.
    /// </summary>
    public OptionManifest LoadOptionManifest(string modFullPath)
    {
        if (manifestCache.TryGetValue(modFullPath, out var cached))
        {
            return cached;
        }

        var manifestPath = Path.Combine(modFullPath, "ppm.json");
        OptionManifest manifest;
        if (File.Exists(manifestPath))
        {
            try
            {
                var text = File.ReadAllText(manifestPath);
                var decoded = JsonConvert.DeserializeObject<OptionManifest>(text);
                if (decoded != null)
                {
                    if (decoded.OptionImages != null)
                    {
                        decoded.OptionImages = new Dictionary<string, string>(decoded.OptionImages, StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        decoded.OptionImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                    manifest = decoded;
                }
                else
                {
                    manifest = new OptionManifest();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load ppm.json in {modFullPath}: {ex.Message}");
                manifest = new OptionManifest();
            }
        }
        else
        {
            manifest = new OptionManifest();
        }

        manifestCache[modFullPath] = manifest;
        return manifest;
    }

    /// <summary>
    /// Saves the ppm.json manifest for a given mod path.
    /// </summary>
    public void SaveOptionManifest(string modFullPath, OptionManifest manifest)
    {
        var manifestPath = Path.Combine(modFullPath, "ppm.json");
        try
        {
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            File.WriteAllText(manifestPath, json);
            
            // Update cache
            manifestCache[modFullPath] = manifest;
            InvalidateValidPathsCache(modFullPath);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save ppm.json in {modFullPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all option preview paths that physically exist on disk. Used to eliminate hot-path File.Exists lookups.
    /// </summary>
    public Dictionary<string, string> GetValidOptionImagePaths(ModInfo mod)
    {
        if (validPathsCache.TryGetValue(mod.FullPath, out var cached))
        {
            return cached;
        }

        var validPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifest = LoadOptionManifest(mod.FullPath);
        if (manifest?.OptionImages != null)
        {
            foreach (var kvp in manifest.OptionImages)
            {
                var fullImagePath = Path.Combine(mod.FullPath, kvp.Value);
                if (File.Exists(fullImagePath))
                {
                    validPaths[kvp.Key] = fullImagePath;
                }
            }
        }

        validPathsCache[mod.FullPath] = validPaths;
        return validPaths;
    }

    /// <summary>
    /// Invalidates the valid paths cache for a modified mod path.
    /// </summary>
    public void InvalidateValidPathsCache(string modFullPath)
    {
        validPathsCache.Remove(modFullPath);
    }

    /// <summary>
    /// Clears manifest and valid path caches entirely.
    /// </summary>
    public void ClearManifestCache()
    {
        manifestCache.Clear();
        validPathsCache.Clear();
        bustedPathCache.Clear();
    }

    /// <summary>
    /// Generates a safe, readable filename for an option preview based on group and option names.
    /// </summary>
    public string GenerateSafeOptionFilename(string groupName, string optionName)
    {
        var combined = $"{groupName}_{optionName}";
        
        // Replace invalid filesystem characters and spaces with underscores
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedChars = combined.Select(c => 
        {
            if (invalidChars.Contains(c) || char.IsWhiteSpace(c))
                return '_';
            return c;
        }).ToArray();
        
        var sanitized = new string(sanitizedChars).ToLowerInvariant();
        
        // Remove duplicate/consecutive underscores
        sanitized = Regex.Replace(sanitized, @"_+", "_").Trim('_');

        // Generate a 4-character deterministic hash of the original group/option key to guarantee uniqueness
        var rawKey = $"{groupName}/{optionName}";
        uint hash = 2166136261;
        foreach (char c in rawKey)
        {
            hash = (hash ^ c) * 16777619;
        }
        var hashStr = (hash & 0xFFFF).ToString("x4");

        // Truncate name if too long to prevent path overflow (leave room for hash and extension)
        var maxNameLen = 45;
        if (sanitized.Length > maxNameLen)
        {
            sanitized = sanitized.Substring(0, maxNameLen).Trim('_');
        }

        return $"ppm_{sanitized}_{hashStr}.png";
    }

    /// <summary>
    /// Saves an image for a specific option group and name, returning the relative path or null.
    /// </summary>
    public string? SaveOptionImage(ModInfo mod, string groupName, string optionName, System.Drawing.Image image)
    {
        // First delete old image if any
        ClearOptionImage(mod, groupName, optionName);

        var ppmDir = Path.Combine(mod.FullPath, "ppm");
        if (!Directory.Exists(ppmDir))
        {
            Directory.CreateDirectory(ppmDir);
        }

        // Generate a safe readable filename
        var fileName = GenerateSafeOptionFilename(groupName, optionName);
        var targetPath = Path.Combine(ppmDir, fileName);


        // Crop and scale based on OptionCrop setting
        var cropOption = CropAspect.Aspect1_1;
        if (Configuration.OptionCrop == OptionCropAspect.Aspect16_9)
        {
            cropOption = CropAspect.Aspect16_9;
        }
        else if (Configuration.OptionCrop == OptionCropAspect.NoCrop)
        {
            cropOption = CropAspect.NoCrop;
        }


        try
        {
            SaveImageFromBitmap(image, targetPath, cropOption);
            
            // Register in manifest
            var manifest = LoadOptionManifest(mod.FullPath);
            var key = $"{groupName}/{optionName}";
            manifest.OptionImages[key] = Path.Combine("ppm", fileName).Replace('\\', '/');
            SaveOptionManifest(mod.FullPath, manifest);

            return manifest.OptionImages[key];
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save option image for mod {mod.Name}, group {groupName}, option {optionName}: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Clears the image for a specific option group and name, deleting the file if it exists.
    /// </summary>
    public void ClearOptionImage(ModInfo mod, string groupName, string optionName)
    {
        var manifest = LoadOptionManifest(mod.FullPath);
        var key = $"{groupName}/{optionName}";
        if (manifest.OptionImages.TryGetValue(key, out var relativePath))
        {
            var fullPath = Path.Combine(mod.FullPath, relativePath);
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to delete old option image {fullPath}: {ex.Message}");
                }
            }
            manifest.OptionImages.Remove(key);
            SaveOptionManifest(mod.FullPath, manifest);
        }
    }

    /// <summary>
    /// Creates a cache-busted copy of the image file in the system temp directory if it has changed,
    /// resolving caching issues in Dalamud's TextureProvider while keeping mod directories clean.
    /// </summary>
    public string GetBustedImagePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;

        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;

            // Return cached busted path immediately if it's already resolved and valid
            if (bustedPathCache.TryGetValue(path, out var cached) && cached.LastWriteTicks == lastWrite)
            {
                return cached.BustedPath;
            }

            var cacheDir = Path.Combine(Path.GetTempPath(), "PenumbraPreviewManagerCache");
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            var fileDir = Path.GetDirectoryName(path) ?? string.Empty;
            uint pathHash = 2166136261;
            foreach (char c in fileDir)
            {
                pathHash = (pathHash ^ c) * 16777619;
            }
            var pathHashStr = (pathHash & 0xFFFF).ToString("x4");

            var nameWithoutExt = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var cachePath = Path.Combine(cacheDir, $"ppm_{pathHashStr}_{nameWithoutExt}_{lastWrite}{ext}");

            if (!File.Exists(cachePath))
            {
                // Clean up previous cache-busted copies for this specific file in the temp directory
                var searchPattern = $"ppm_{pathHashStr}_{nameWithoutExt}_*{ext}";
                foreach (var oldFile in Directory.GetFiles(cacheDir, searchPattern))
                {
                    try
                    {
                        File.Delete(oldFile);
                    }
                    catch
                    {
                        // File might be locked/in use by Dalamud, ignore
                    }
                }

                // Copy the updated file to the new cache-buster path
                File.Copy(path, cachePath, true);
            }

            bustedPathCache[path] = (cachePath, lastWrite);
            return cachePath;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to create cache-busted image copy in temp: {ex.Message}");
            return path;
        }
    }

    /// <summary>
    /// Clears all files in the central temporary cache directory to free up disk space.
    /// </summary>
    public void ClearTempCache()
    {
        try
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), "PenumbraPreviewManagerCache");
            if (Directory.Exists(cacheDir))
            {
                foreach (var file in Directory.GetFiles(cacheDir))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // File might be in use/locked, skip
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to clear temporary cache: {ex.Message}");
        }
    }
}


