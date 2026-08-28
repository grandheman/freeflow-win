using System;
using System.IO;
using System.Reflection;
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
                var command = StartupCommand();
                if (command is null) return false;

                key.SetValue(ValueName, command);
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

    /// <summary>
    /// Builds the command line that relaunches this app exactly the way it is
    /// currently running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are two launch shapes, and registering the wrong one silently breaks
    /// launch-at-login:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// Normal: the process is FreeFlow.exe, so the path alone is enough.
    /// </item>
    /// <item>
    /// Under the shared runtime: the process is dotnet.exe and the app is an
    /// argument. Registering just the host path would launch dotnet with no
    /// assembly, which prints usage text and exits. This matters because running
    /// through the Microsoft-signed host is how the app starts on machines with
    /// Smart App Control enabled and no code-signing certificate.
    /// </item>
    /// </list>
    /// </remarks>
    private static string? StartupCommand()
    {
        try
        {
            var hostPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(hostPath)) return null;

            var hostName = Path.GetFileNameWithoutExtension(hostPath);
            if (!hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                // Launched through the app host. Quoted so a path with spaces
                // parses as a single argument.
                return $"\"{hostPath}\"";
            }

            // Launched through the shared runtime, so the managed assembly has to
            // be passed along explicitly.
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(assemblyPath)) return null;

            return $"\"{hostPath}\" \"{assemblyPath}\"";
        }
        catch (Exception)
        {
            return null;
        }
    }
}
