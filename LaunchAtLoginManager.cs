using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PrimeDictate;

internal static class LaunchAtLoginManager
{
    private const string ShortcutFileName = "PrimeDictate.lnk";
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

    private static readonly string[] CurrentUserScopeSwitches =
    [
        "--current-user",
        "--scope=current-user",
        "--scope=user",
        "/CurrentUser"
    ];

    private static readonly string[] AllUsersScopeSwitches =
    [
        "--all-users",
        "--scope=all-users",
        "--scope=machine",
        "/AllUsers"
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
                var scope = ResolveCommandLineScope(args);
                if (scope == LaunchAtLoginScope.Disabled)
                {
                    throw new InvalidOperationException("Launch-at-login enable requires a user or all-users scope.");
                }

                Apply(scope);
                AppLog.Info($"Launch at login enabled for {FormatScope(scope)}.");
            }
            else
            {
                if (TryResolveCommandLineScope(args, out var scope))
                {
                    if (scope == LaunchAtLoginScope.AllUsers)
                    {
                        DisableAllUsers();
                    }
                    else
                    {
                        DisableCurrentUser();
                    }
                }
                else
                {
                    Apply(LaunchAtLoginScope.Disabled);
                }

                AppLog.Info(TryResolveCommandLineScope(args, out var loggedScope) && loggedScope == LaunchAtLoginScope.AllUsers
                    ? "Launch at login disabled for all users."
                    : "Launch at login disabled.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Launch-at-login command failed: {ex.Message}");
            exitCode = 1;
        }

        return true;
    }

    internal static LaunchAtLoginScope GetConfiguredScope()
    {
        if (IsAllUsersConfigured())
        {
            return LaunchAtLoginScope.AllUsers;
        }

        if (IsCurrentUserConfigured())
        {
            return LaunchAtLoginScope.CurrentUser;
        }

        return LaunchAtLoginScope.Disabled;
    }

    internal static bool TryApply(LaunchAtLoginScope scope, out string error)
    {
        try
        {
            Apply(scope);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static void Apply(LaunchAtLoginScope scope)
    {
        switch (scope)
        {
            case LaunchAtLoginScope.AllUsers:
                EnableAllUsers();
                DisableCurrentUser();
                break;
            case LaunchAtLoginScope.CurrentUser:
                DisableAllUsers();
                EnableCurrentUser();
                break;
            case LaunchAtLoginScope.Disabled:
            case LaunchAtLoginScope.NotConfigured:
                DisableAllUsers();
                DisableCurrentUser();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown launch-at-login scope.");
        }
    }

    internal static void Enable() => Apply(IsAdministrator()
        ? LaunchAtLoginScope.AllUsers
        : LaunchAtLoginScope.CurrentUser);

    internal static void Disable() => Apply(LaunchAtLoginScope.Disabled);

    private static void EnableCurrentUser()
    {
        CreateStartupShortcut(LaunchAtLoginScope.CurrentUser);
        DeleteRunValue(Registry.CurrentUser);
    }

    private static void EnableAllUsers()
    {
        if (IsStartupShortcutEnabled(LaunchAtLoginScope.AllUsers) && !IsMachineRunValueEnabled())
        {
            return;
        }

        if (!IsAdministrator())
        {
            RunElevatedHelper(enable: true, LaunchAtLoginScope.AllUsers);
            return;
        }

        CreateStartupShortcut(LaunchAtLoginScope.AllUsers);
        DeleteRunValue(Registry.LocalMachine);
    }

    private static void DisableCurrentUser()
    {
        DeleteStartupShortcut(LaunchAtLoginScope.CurrentUser);
        DeleteRunValue(Registry.CurrentUser);
    }

    private static void DisableAllUsers()
    {
        if (!IsAllUsersConfigured())
        {
            return;
        }

        if (!IsAdministrator())
        {
            RunElevatedHelper(enable: false, LaunchAtLoginScope.AllUsers);
            return;
        }

        DeleteStartupShortcut(LaunchAtLoginScope.AllUsers);
        DeleteRunValue(Registry.LocalMachine);
    }

    private static LaunchAtLoginScope ResolveCommandLineScope(string[] args)
    {
        if (TryResolveCommandLineScope(args, out var scope))
        {
            return scope;
        }

        return IsAdministrator()
            ? LaunchAtLoginScope.AllUsers
            : LaunchAtLoginScope.CurrentUser;
    }

    private static bool TryResolveCommandLineScope(string[] args, out LaunchAtLoginScope scope)
    {
        if (HasAnySwitch(args, AllUsersScopeSwitches))
        {
            scope = LaunchAtLoginScope.AllUsers;
            return true;
        }

        if (HasAnySwitch(args, CurrentUserScopeSwitches))
        {
            scope = LaunchAtLoginScope.CurrentUser;
            return true;
        }

        scope = LaunchAtLoginScope.NotConfigured;
        return false;
    }

    private static void RunElevatedHelper(bool enable, LaunchAtLoginScope scope)
    {
        if (scope != LaunchAtLoginScope.AllUsers)
        {
            throw new InvalidOperationException("Only all-users startup changes require elevation.");
        }

        var executablePath = GetExecutablePath();
        var arguments = enable
            ? "--enable-launch-at-login --scope=all-users"
            : "--disable-launch-at-login --scope=all-users";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            }) ?? throw new InvalidOperationException("Could not start the elevated startup shortcut helper.");

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The elevated startup shortcut helper failed with exit code {process.ExitCode}.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Administrator approval is required to update startup for all users.", ex);
        }
    }

    private static void CreateStartupShortcut(LaunchAtLoginScope scope)
    {
        var shortcutPath = GetStartupShortcutPath(scope);
        var shortcutDirectory = Path.GetDirectoryName(shortcutPath)
            ?? throw new InvalidOperationException("Startup shortcut directory is invalid.");
        var executablePath = GetExecutablePath();

        Directory.CreateDirectory(shortcutDirectory);
        CreateWindowsShortcut(
            shortcutPath,
            executablePath,
            FromLoginSwitch,
            Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            $"{executablePath},0",
            "Start PrimeDictate when Windows starts.");
    }

    private static void DeleteStartupShortcut(LaunchAtLoginScope scope)
    {
        var shortcutPath = GetStartupShortcutPath(scope);
        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }

    private static bool IsCurrentUserConfigured() =>
        IsStartupShortcutEnabled(LaunchAtLoginScope.CurrentUser) || IsUserRunValueEnabled();

    private static bool IsAllUsersConfigured() =>
        IsStartupShortcutEnabled(LaunchAtLoginScope.AllUsers) || IsMachineRunValueEnabled();

    private static bool IsStartupShortcutEnabled(LaunchAtLoginScope scope) =>
        File.Exists(GetStartupShortcutPath(scope));

    private static string GetStartupShortcutPath(LaunchAtLoginScope scope)
    {
        var folder = scope switch
        {
            LaunchAtLoginScope.CurrentUser => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            LaunchAtLoginScope.AllUsers => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Startup shortcut scope must be current user or all users.")
        };

        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException($"Windows did not return a Startup folder for {FormatScope(scope)}.");
        }

        return Path.Combine(folder, ShortcutFileName);
    }

    private static void CreateWindowsShortcut(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string iconLocation,
        string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable, so PrimeDictate could not create a startup shortcut.");
        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create the Windows shortcut helper.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath])
                ?? throw new InvalidOperationException("Could not create the startup shortcut.");

            SetComProperty(shortcut, "TargetPath", targetPath);
            SetComProperty(shortcut, "Arguments", arguments);
            SetComProperty(shortcut, "WorkingDirectory", workingDirectory);
            SetComProperty(shortcut, "IconLocation", iconLocation);
            SetComProperty(shortcut, "Description", description);
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, binder: null, target: shortcut, args: null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void SetComProperty(object target, string propertyName, object value) =>
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target: target,
            args: [value]);

    private static void DeleteRunValue(RegistryKey root)
    {
        using var key = root.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static bool IsUserRunValueEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return IsPrimeDictateRunValue(key?.GetValue(RunValueName) as string);
    }

    private static bool IsMachineRunValueEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKeyPath, writable: false);
        return IsPrimeDictateRunValue(key?.GetValue(RunValueName) as string);
    }

    private static bool IsPrimeDictateRunValue(string? configured) =>
        configured?.Contains("PrimeDictate", StringComparison.OrdinalIgnoreCase) == true;

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

    private static string FormatScope(LaunchAtLoginScope scope) =>
        scope switch
        {
            LaunchAtLoginScope.AllUsers => "all users",
            LaunchAtLoginScope.CurrentUser => "the current user",
            LaunchAtLoginScope.Disabled => "disabled",
            _ => "the configured user"
        };

    private static bool HasAnySwitch(IEnumerable<string> args, IReadOnlyList<string> switches) =>
        args.Any(arg => switches.Any(candidate => string.Equals(arg, candidate, StringComparison.OrdinalIgnoreCase)));
}
