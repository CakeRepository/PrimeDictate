using System.Diagnostics;

namespace PrimeDictate;

internal readonly record struct VoiceShellCommandResult(int? ProcessId);

internal static class VoiceShellCommandRunner
{
    public static VoiceShellCommandResult Run(VoiceShellCommand? shellCommand)
    {
        if (shellCommand is null)
        {
            throw new ArgumentNullException(nameof(shellCommand));
        }

        var command = shellCommand.Command.Trim();
        if (command.Length == 0)
        {
            throw new InvalidOperationException("Voice command has no command prompt command configured.");
        }

        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
        {
            commandProcessor = "cmd.exe";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = commandProcessor,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the command prompt process.");
        return new VoiceShellCommandResult(process.Id);
    }
}
