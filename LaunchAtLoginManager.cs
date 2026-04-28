using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace PrimeDictate;

internal static class LaunchAtLoginManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PrimeDictate";
    internal const string FromLoginSwitch = "--from-login";

    private static readonly string[] EnableSwitches =
    [
        "--enable-launch-at-login",
        "--launch-at-login",
        "/EnableLaunchAtLogin"
    ];

    private static readonly string[] DisableSwitches =
    [
        "--disable-launch-at-login",
        "--no-launch-at-login",
        "/DisableLaunchAtLogin"
    ];

    internal static bool TryHandleCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        var enableRequested = HasAnySwitch(args, EnableSwitches);
        var disableRequested = HasAnySwitch(args, DisableSwitches);
        if (!enableRequested && !disableRequested)
        {
            return false;
        }

        if (enableRequested && disableRequested)
        {
            AppLog.Error("Launch-at-login command line switches conflict.");
            exitCode = 2;
            return true;
        }

        try
        {
            if (enableRequested)
            {
                Enable();
                AppLog.Info("Launch at login enabled.");
            }
            else
            {
                Disable();
                AppLog.Info("Launch at login disabled.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Launch-at-login command failed: {ex.Message}");
            exitCode = 1;
        }

        return true;
    }

    internal static void Enable()
    {
        if (IsMachineRunValueEnabled())
        {
            return;
        }

        if (IsAdministrator())
        {
            SetRunValue(Registry.LocalMachine, GetExecutablePath());
            return;
        }

        SetRunValue(Registry.CurrentUser, GetExecutablePath());
    }

    internal static void Disable()
    {
        DeleteRunValue(Registry.CurrentUser);
        if (!IsMachineRunValueEnabled())
        {
            return;
        }

        if (!IsAdministrator())
        {
            throw new InvalidOperationException("PrimeDictate is configured machine-wide. Run this command as administrator to disable launch at login.");
        }

        DeleteRunValue(Registry.LocalMachine);
    }

    private static string BuildRunCommand(string executablePath) => $"\"{executablePath}\" {FromLoginSwitch}";

    private static void SetRunValue(RegistryKey root, string executablePath)
    {
        using var key = root.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows Run registry key.");
        key.SetValue(RunValueName, BuildRunCommand(executablePath), RegistryValueKind.String);
    }

    private static void DeleteRunValue(RegistryKey root)
    {
        using var key = root.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static bool IsMachineRunValueEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string configured &&
            configured.Contains("PrimeDictate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string GetExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        return Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not resolve PrimeDictate executable path.");
    }

    private static bool HasAnySwitch(IEnumerable<string> args, IReadOnlyList<string> switches) =>
        args.Any(arg => switches.Any(candidate => string.Equals(arg, candidate, StringComparison.OrdinalIgnoreCase)));
}
