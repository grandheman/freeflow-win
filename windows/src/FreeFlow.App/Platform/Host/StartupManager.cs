using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace FreeFlow.App.Platform.Host;

/// <summary>
/// Controls whether FreeFlow launches when the user signs in.
/// </summary>
/// <remarks>
/// <para>
/// Windows replacement for <c>SMAppService</c>. The per-user <c>Run</c> key is used
/// rather than a scheduled task because it needs no elevation, is what the Startup
/// apps page in Settings shows and lets the user override, and runs the app at the
/// normal integrity level the keyboard hook requires.
/// </para>
/// <para>
/// Windows itself can disable a Run entry through Task Manager. When it does,
/// <see cref="IsEnabled"/> still reports true because the registry value is present;
/// that mismatch is the user's explicit choice and the app does not fight it.
/// </para>
/// </remarks>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FreeFlow";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null) return false;

            if (enabled)
            {
                var executablePath = ExecutablePath();
                if (executablePath is null) return false;

                // Quoted so a path containing spaces is parsed as one argument.
                key.SetValue(ValueName, $"\"{executablePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ExecutablePath()
    {
        try
        {
            // MainModule gives the real .exe path, which Assembly.Location does not
            // for a single-file published build.
            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return Environment.ProcessPath;
        }
    }
}
