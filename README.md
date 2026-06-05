# Penumbra Preview Manager (PPM)

A premium companion plugin for FINAL FANTASY XIV (Dalamud) that solves native preview limitations, enabling high-quality **16:9** visual preview cards directly inside your **Penumbra** Mod Settings window!

---

## Features

- **True GUI Injection**: Communicates natively with Penumbra's IPC system (`PreSettingsTabBarDraw`) to render custom preview images natively inside Penumbra's details pane (above the Mod settings tabs and below the title).
- **Mod Option Previews**: Support for configuring and displaying preview thumbnails for individual options in settings groups (supporting both multi-selection checkbox lists and single-selection combo dropdowns).
- **Native ImGui Detour Overlay**: detours native `cimgui.dll` calls (`igCheckbox`, `igBeginCombo`, `igSelectable_Bool`, `igSelectable_BoolPtr`) to display a sky-blue FontAwesome eye/image icon next to options with previews, showing preview tooltips instantly when hovered.
- **Portability-First Manifests**: Manages individual option previews via a localized `ppm.json` manifest and a `ppm/` folder inside each mod's directory. Moving or sharing the mod folder preserves all option previews!
- **Consolidated UI Sub-Panel**: Select and configure option preview images using a single sub-panel below a divider in the PPM window (supports clipboard pasting, local file picking, URL grabbing, and unassigned image scanning).
- **Unassigned Image Scanner**: Scans the `ppm/` directory for orphan image files (`.png`, `.jpg`, `.jpeg`, `.webp`, `.bmp`, `.gif`) and displays a live thumbnail preview container for easy assignment.
- **Space-Saving UI Modes**: Added an option to completely hide the left mod list column when Penumbra auto-sync is enabled, keeping the PPM window extremely compact.
- **Clipboard & Paste Support**: Instantly copy and center-crop clipboard images to a perfect 16:9 PNG (`preview.png`) in one click.
- **Smart Image Grabbing**: Fetch and extract thumbnails directly from image URLs or XIVModArchive (XMA) mod links.
- **descriptive NSFW/Restricted Handling**: If an image cannot be automatically imported due to XMA NSFW/Restricted access controls, the plugin halts cleanly and walks you through standard manual copy-paste instructions.
- **One-Click XMA Search**: Built-in search button that automatically URL-escapes mod name/author and searches XIVModArchive in your browser.
- **Heliosphere Auto-Avoidance**: Scans and detects if a mod is managed by Heliosphere, automatically disabling duplicate previews or buttons to keep your Mod Settings page 100% clean and clutter-free!
- **Image Sizing Slider**: Built-in configuration slider allowing you to customize the preview width (from 10% to 100%, keeping aspect ratio) to perfectly match your UI scaling.
- **Interactive Controls**: Left-click a preview image inside Penumbra to jump directly to its manager page, or hold **Right-Click** to overlay a gorgeous full-scale zoom.

---

## Installation

To install the **Penumbra Preview Manager** using my custom repository:

1. Launch FINAL FANTASY XIV and open Dalamud Settings using `/xlsettings` in chat.
2. Navigate to the **Experimental** tab.
3. Under **Custom Plugin Repositories**, paste the following URL:
   `https://raw.githubusercontent.com/Tomok2404/TomokPlugins/main/pluginmaster.json`
4. Click the **`+`** button to add the repository, then click **Save and Close**.
5. Open the Plugin Installer using `/xlplugins` in chat.
6. Search for **Penumbra Preview Manager** and click **Install**!

---

## For Mod Creators

### Main Mod Previews
Either manually add a `preview.png` to your `.pmp` file (with WinRAR) - or use the Penumbra "Export Mod" feature. As long as the `preview.png` image ends up in the Mod folder (same level as the `default_mod.json`), the plugin will detect it on clicking **Refresh Mod List**.

### Mod Option Previews (Portability)
If you want to package option preview images alongside your mod for others to use:
* Individual option preview assignments are tracked inside a `ppm.json` manifest file and the image files are stored in a `ppm/` folder at the root of your mod folder.
* Filenames inside the `ppm/` directory use a deterministic and filesystem-safe `ppm_groupname_optionname_hash.png` format to ensure compatibility.
* Simply distribute the `ppm.json` manifest and the `ppm/` folder inside your mod package. Other users of the Penumbra Preview Manager plugin will automatically see your option previews in-game!

---

## Developer / Building Instructions

If you wish to build the source code manually:

### Prerequisites
- .NET 8.0 SDK or higher.
- FINAL FANTASY XIV, XIVLauncher, and Dalamud installed to default directories.

### Steps
1. Open the solution file `PenumbraPreviewManager.sln` in your C# IDE of choice (e.g. Visual Studio, Rider).
2. Set configuration to `Release` and build the project.
3. The packaged plugin `.zip` folder will be automatically generated at:
   `PenumbraPreviewManager/bin/x64/Release/PenumbraPreviewManager/latest.zip`

---

## AI Disclosure / Collaboration Note

> [!NOTE]
> This plugin was co-authored, coded, and polished with the assistance of agentic AI coding assistants (Google DeepMind's Antigravity). All design aesthetics, custom IPC integration, and robust exception frameworks were developed through collaborative pair programming.

---

## Special Thanks & Credits
- **[Heliosphere Team](https://github.com/heliosphere-xiv/plugin)**: A very special thank you and credit to the Heliosphere team! The native Penumbra window IPC hook integration, image zooming mechanics, and double-rendering prevention logic in this plugin were inspired by and adapted from Heliosphere's elegant open-source C# plugin implementation.
