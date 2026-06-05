using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;

namespace PenumbraPreviewManager;

internal class ImGuiHookManager : IDisposable
{
    private readonly Plugin plugin;
    private readonly PenumbraWindowIntegration integration;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte CheckboxDelegate(IntPtr label, IntPtr v);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte BeginComboDelegate(IntPtr label, IntPtr previewValue, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte SelectableBoolDelegate(IntPtr label, byte selected, int flags, Vector2 size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte SelectableBoolPtrDelegate(IntPtr label, IntPtr pSelected, int flags, Vector2 size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte RadioButtonBoolDelegate(IntPtr label, byte active);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte RadioButtonIntPtrDelegate(IntPtr label, IntPtr v, int vButton);

    private Hook<CheckboxDelegate>? checkboxHook;
    private Hook<BeginComboDelegate>? beginComboHook;
    private Hook<SelectableBoolDelegate>? selectableBoolHook;
    private Hook<SelectableBoolPtrDelegate>? selectableBoolPtrHook;
    private Hook<RadioButtonBoolDelegate>? radioButtonBoolHook;
    private Hook<RadioButtonIntPtrDelegate>? radioButtonIntPtrHook;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public ImGuiHookManager(Plugin plugin, PenumbraWindowIntegration integration)
    {
        this.plugin = plugin;
        this.integration = integration;
    }

    public void Initialize()
    {
        try
        {
            var moduleName = "cimgui.dll";
            var moduleHandle = GetModuleHandle(moduleName);
            if (moduleHandle == IntPtr.Zero)
            {
                var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .FirstOrDefault(m => m.ModuleName.Contains("cimgui", StringComparison.OrdinalIgnoreCase));
                if (module != null)
                {
                    moduleHandle = module.BaseAddress;
                }
            }

            if (moduleHandle == IntPtr.Zero)
            {
                Plugin.Log.Error("Could not find cimgui module handle for hooking.");
                return;
            }

            var igCheckboxAddr = GetProcAddress(moduleHandle, "igCheckbox");
            var igBeginComboAddr = GetProcAddress(moduleHandle, "igBeginCombo");
            var igSelectableBoolAddr = GetProcAddress(moduleHandle, "igSelectable_Bool");
            var igSelectableBoolPtrAddr = GetProcAddress(moduleHandle, "igSelectable_BoolPtr");

            if (igCheckboxAddr != IntPtr.Zero)
            {
                checkboxHook = Plugin.GameInteropProvider.HookFromAddress<CheckboxDelegate>(igCheckboxAddr, CheckboxDetour);
                checkboxHook.Enable();
            }
            else
            {
                Plugin.Log.Warning("Failed to resolve address for igCheckbox.");
            }

            if (igBeginComboAddr != IntPtr.Zero)
            {
                beginComboHook = Plugin.GameInteropProvider.HookFromAddress<BeginComboDelegate>(igBeginComboAddr, BeginComboDetour);
                beginComboHook.Enable();
            }
            else
            {
                Plugin.Log.Warning("Failed to resolve address for igBeginCombo.");
            }

            if (igSelectableBoolAddr != IntPtr.Zero)
            {
                selectableBoolHook = Plugin.GameInteropProvider.HookFromAddress<SelectableBoolDelegate>(igSelectableBoolAddr, SelectableBoolDetour);
                selectableBoolHook.Enable();
            }
            else
            {
                Plugin.Log.Warning("Failed to resolve address for igSelectable_Bool.");
            }

            if (igSelectableBoolPtrAddr != IntPtr.Zero)
            {
                selectableBoolPtrHook = Plugin.GameInteropProvider.HookFromAddress<SelectableBoolPtrDelegate>(igSelectableBoolPtrAddr, SelectableBoolPtrDetour);
                selectableBoolPtrHook.Enable();
            }
            else
            {
                Plugin.Log.Warning("Failed to resolve address for igSelectable_BoolPtr.");
            }

            var igRadioButtonBoolAddr = GetProcAddress(moduleHandle, "igRadioButton_Bool");
            var igRadioButtonIntPtrAddr = GetProcAddress(moduleHandle, "igRadioButton_IntPtr");

            if (igRadioButtonBoolAddr != IntPtr.Zero)
            {
                radioButtonBoolHook = Plugin.GameInteropProvider.HookFromAddress<RadioButtonBoolDelegate>(igRadioButtonBoolAddr, RadioButtonBoolDetour);
                radioButtonBoolHook.Enable();
            }
            else
            {
                Plugin.Log.Warning("Failed to resolve address for igRadioButton_Bool.");
            }

            if (igRadioButtonIntPtrAddr != IntPtr.Zero)
            {
                radioButtonIntPtrHook = Plugin.GameInteropProvider.HookFromAddress<RadioButtonIntPtrDelegate>(igRadioButtonIntPtrAddr, RadioButtonIntPtrDetour);
                radioButtonIntPtrHook.Enable();
            }
            else
            {
                Plugin.Log.Warning("Failed to resolve address for igRadioButton_IntPtr.");
            }

            Plugin.Log.Information("ImGui native hooks initialized successfully.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to initialize ImGui native hooks: {ex}");
        }
    }

    public void Dispose()
    {
        checkboxHook?.Dispose();
        beginComboHook?.Dispose();
        selectableBoolHook?.Dispose();
        selectableBoolPtrHook?.Dispose();
        radioButtonBoolHook?.Dispose();
        radioButtonIntPtrHook?.Dispose();
    }

    private byte CheckboxDetour(IntPtr labelPtr, IntPtr v)
    {
        var originalRet = checkboxHook != null ? checkboxHook.Original(labelPtr, v) : (byte)0;

        try
        {
            if (integration.IsDrawingPenumbraSettings && integration.ActiveDrawingModPath != null)
            {
                var label = GetUtf8String(labelPtr);
                if (!string.IsNullOrEmpty(label))
                {
                    integration.OnCheckboxDraw(label);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in CheckboxDetour: {ex}");
        }

        return originalRet;
    }

    private byte BeginComboDetour(IntPtr labelPtr, IntPtr previewValuePtr, int flags)
    {
        var originalRet = beginComboHook != null ? beginComboHook.Original(labelPtr, previewValuePtr, flags) : (byte)0;

        try
        {
            if (integration.IsDrawingPenumbraSettings && integration.ActiveDrawingModPath != null)
            {
                var label = GetUtf8String(labelPtr);
                var previewValue = GetUtf8String(previewValuePtr);
                if (!string.IsNullOrEmpty(label))
                {
                    integration.OnBeginComboDraw(label, previewValue);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in BeginComboDetour: {ex}");
        }

        return originalRet;
    }

    private byte SelectableBoolDetour(IntPtr labelPtr, byte selected, int flags, Vector2 size)
    {
        var originalRet = selectableBoolHook != null ? selectableBoolHook.Original(labelPtr, selected, flags, size) : (byte)0;

        try
        {
            if (integration.IsDrawingPenumbraSettings && integration.ActiveDrawingModPath != null)
            {
                var label = GetUtf8String(labelPtr);
                if (!string.IsNullOrEmpty(label))
                {
                    integration.OnSelectableDraw(label);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in SelectableBoolDetour: {ex}");
        }

        return originalRet;
    }

    private byte SelectableBoolPtrDetour(IntPtr labelPtr, IntPtr pSelected, int flags, Vector2 size)
    {
        var originalRet = selectableBoolPtrHook != null ? selectableBoolPtrHook.Original(labelPtr, pSelected, flags, size) : (byte)0;

        try
        {
            if (integration.IsDrawingPenumbraSettings && integration.ActiveDrawingModPath != null)
            {
                var label = GetUtf8String(labelPtr);
                if (!string.IsNullOrEmpty(label))
                {
                    integration.OnSelectableDraw(label);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in SelectableBoolPtrDetour: {ex}");
        }

        return originalRet;
    }

    private byte RadioButtonBoolDetour(IntPtr labelPtr, byte active)
    {
        var originalRet = radioButtonBoolHook != null ? radioButtonBoolHook.Original(labelPtr, active) : (byte)0;

        try
        {
            if (integration.IsDrawingPenumbraSettings && integration.ActiveDrawingModPath != null)
            {
                var label = GetUtf8String(labelPtr);
                if (!string.IsNullOrEmpty(label))
                {
                    integration.OnCheckboxDraw(label);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in RadioButtonBoolDetour: {ex}");
        }

        return originalRet;
    }

    private byte RadioButtonIntPtrDetour(IntPtr labelPtr, IntPtr v, int vButton)
    {
        var originalRet = radioButtonIntPtrHook != null ? radioButtonIntPtrHook.Original(labelPtr, v, vButton) : (byte)0;

        try
        {
            if (integration.IsDrawingPenumbraSettings && integration.ActiveDrawingModPath != null)
            {
                var label = GetUtf8String(labelPtr);
                if (!string.IsNullOrEmpty(label))
                {
                    integration.OnCheckboxDraw(label);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in RadioButtonIntPtrDetour: {ex}");
        }

        return originalRet;
    }

    private static string GetUtf8String(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;
        int len = 0;
        while (Marshal.ReadByte(ptr, len) != 0) len++;
        byte[] buffer = new byte[len];
        Marshal.Copy(ptr, buffer, 0, len);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }
}
