# Penumbra Preview Manager (PPM) - FAQ & Configuration Guide

Welcome to the official FAQ and Configuration reference for **Penumbra Preview Manager (PPM)**. This guide explains all plugin settings, features, and troubleshooting tips.

---

## Settings & Configuration Breakdown

Open the settings window using `/ppm settings` in chat or via the Dalamud plugin manager.

### Display Options

- **Penumbra Preview Image Size** (`10% - 100%`)
  Controls the horizontal scale of the main mod preview card drawn inside Penumbra's Mod Settings panel. Adjust this slider to fit your UI scaling and font preferences.
- **Middle-Click Preview Zoom Size** (`10% - 300%`)
  Controls the zoom scale when holding **Middle-Click** on any preview card or option icon. This allows you to inspect high-resolution textures up close without permanently expanding the UI.
- **Main Preview Import Crop Method**
  Determines how main mod images (`preview.png`) are processed when imported via clipboard or file picker:
  - *No Crop (Preserve Aspect)*: Preserves exact original dimensions without cropping.
  - *16:9 Aspect Ratio*: Center-crops images to a widescreen 16:9 format.
  - *1:1 Aspect Ratio*: Center-crops images into a clean square format.
  - *4:3 Aspect Ratio*: Center-crops images into a classic 4:3 format.
- **Mod-Option Preview Crop Method**
  Determines how individual option thumbnail previews (in the `ppm/` folder) are cropped during import.
- **Option Preview Trigger Icon**
  Selects the icon style rendered next to mod options inside Penumbra:
  - *Eye / View Icon*: Renders a FontAwesome eye icon.
  - *Image Frame Icon*: Renders a classic image frame icon.

---

### Penumbra Window Quick Buttons

Customize which action buttons appear on the main preview bar inside Penumbra:

- **Show "Paste from Clipboard"**: Displays a button to instantly import and crop images copied to your system clipboard (`Ctrl+V` workflow).
- **Show "Search XMA"**: Displays a button to automatically search XIVModArchive in your default web browser for the active mod.
- **Show "Browse File"**: Displays a local file picker button to choose images from your hard drive.
- **Show "Copy Search Terms"**: Displays a button to quickly copy formatted mod search terms.
- **Show "Grab from URL"**: Displays an input field to fetch thumbnails directly from image URLs or XIVModArchive links.

---

### Integration Options

- **Automatically sync selection with Penumbra**
  When enabled, selecting any mod in Penumbra's mod list instantly updates PPM's active mod view.
- **Hide mod list column in Preview Manager**
  Hides the redundant left-hand mod list sidebar in PPM's standalone window, keeping the workspace extremely compact when auto-sync is active.
- **Force PPM previews for Heliosphere mods**
  By default, PPM automatically steps aside for mods managed by Heliosphere to avoid UI clutter. Enable this setting if you want PPM to force-draw preview cards on Heliosphere mods regardless.
- **Hide option previews from Penumbra File Redirections**
  Disables option preview icons inside Penumbra's advanced File Redirections / Advanced view if you don't like to see a bunch of unused files in there.

---

## Frequently Asked Questions

### How do option previews work portably?
Option previews are stored locally inside each mod's directory within a `ppm/` folder and registered in a `ppm.json` manifest. Because they reside inside the mod directory, moving your mod library, switching PCs, or sharing mod packages with friends preserves all option previews automatically.

### Why are my blue preview icons not showing up in Penumbra?
1. Ensure Penumbra is open and IPC is enabled.
2. Verify that option previews have been assigned in the PPM configuration panel.
3. If hot-reloading plugins, restart Penumbra or re-open the settings tab to refresh ImGui detours.

### What chat commands are available?
- `/ppm`: Toggles the main Penumbra Preview Manager window.
- `/ppm settings` or `/ppm config`: Opens the settings configuration window directly.
