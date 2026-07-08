using System.Diagnostics;
using RideManager.Core;
using RideManager.Utils;

namespace RideManager.Actuators;

/// <summary>
/// Plays pre-recorded warning audio through the system default speaker.
/// </summary>
public sealed class SystemSpeakerNotifier : ISpeakerNotifier
{
    private readonly ActuatorEndpointOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private DateTimeOffset _lastPlayedAt = DateTimeOffset.MinValue;
    private SafetyRiskLevel _lastPlayedRisk = SafetyRiskLevel.Normal;
    private Process? _activePlayer;
    private bool _reportedMissingPlayer;
    private bool _reportedMissingAsset;

    /// <summary>
    /// Creates a system speaker notifier.
    /// </summary>
    public SystemSpeakerNotifier(ActuatorEndpointOptions options, TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Plays the configured warning or danger audio for the current decision.
    /// </summary>
    public Task NotifyAsync(SafetyDecision decision, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || decision.RiskLevel == SafetyRiskLevel.Normal)
        {
            return Task.CompletedTask;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!ShouldPlay(decision.RiskLevel, now))
            {
                return Task.CompletedTask;
            }

            var plan = SpeakerNotificationPlan.Create(_options, decision.RiskLevel);
            if (plan is null)
            {
                return Task.CompletedTask;
            }

            if (!File.Exists(plan.AssetPath))
            {
                ReportMissingAssetOnce(plan.AssetPath);
                return Task.CompletedTask;
            }

            var player = SpeakerPlayerResolver.Resolve(_options.PlayerCommand);
            if (player is null)
            {
                ReportMissingPlayerOnce();
                return Task.CompletedTask;
            }

            StopActivePlayer();
            _activePlayer = StartPlayer(player, plan.AssetPath);
            _lastPlayedAt = now;
            _lastPlayedRisk = decision.RiskLevel;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Determines whether the current decision should produce another audio prompt.
    /// </summary>
    private bool ShouldPlay(SafetyRiskLevel riskLevel, DateTimeOffset now)
    {
        var minInterval = TimeSpan.FromSeconds(Math.Max(_options.MinIntervalSeconds, 0.0));
        return riskLevel > _lastPlayedRisk || now - _lastPlayedAt >= minInterval;
    }

    /// <summary>
    /// Starts the selected player process.
    /// </summary>
    private static Process? StartPlayer(SpeakerPlayer player, string assetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = player.Command,
            UseShellExecute = false
        };

        foreach (var argument in player.ArgumentsBeforePath)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(assetPath);
        foreach (var argument in player.ArgumentsAfterPath)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo);
    }

    /// <summary>
    /// Stops a still-running audio process before playing a more recent prompt.
    /// </summary>
    private void StopActivePlayer()
    {
        if (_activePlayer is null)
        {
            return;
        }

        try
        {
            if (!_activePlayer.HasExited)
            {
                _activePlayer.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _activePlayer.Dispose();
            _activePlayer = null;
        }
    }

    /// <summary>
    /// Reports missing audio assets once.
    /// </summary>
    private void ReportMissingAssetOnce(string assetPath)
    {
        if (_reportedMissingAsset)
        {
            return;
        }

        _reportedMissingAsset = true;
        Console.WriteLine($"Speaker audio asset not found: {assetPath}");
    }

    /// <summary>
    /// Reports missing audio players once.
    /// </summary>
    private void ReportMissingPlayerOnce()
    {
        if (_reportedMissingPlayer)
        {
            return;
        }

        _reportedMissingPlayer = true;
        Console.WriteLine("Speaker is enabled but no system audio player was found. Install aplay, paplay, ffplay, or configure actuators.speaker.player_command.");
    }
}

/// <summary>
/// Represents the audio file selected for one safety decision.
/// </summary>
internal sealed record SpeakerNotificationPlan(SafetyRiskLevel RiskLevel, string AssetPath)
{
    /// <summary>
    /// Creates a notification plan from actuator options and risk level.
    /// </summary>
    public static SpeakerNotificationPlan? Create(ActuatorEndpointOptions options, SafetyRiskLevel riskLevel)
    {
        if (riskLevel == SafetyRiskLevel.Normal)
        {
            return null;
        }

        var fileName = riskLevel == SafetyRiskLevel.Danger
            ? options.DangerFile
            : options.WarningFile;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var assetPath = Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(options.AssetDirectory, fileName);
        return new SpeakerNotificationPlan(riskLevel, assetPath);
    }
}

/// <summary>
/// Resolves a system audio player command for the current host.
/// </summary>
internal static class SpeakerPlayerResolver
{
    private static readonly SpeakerPlayer[] LinuxPlayers =
    {
        new("aplay", Array.Empty<string>(), Array.Empty<string>()),
        new("paplay", Array.Empty<string>(), Array.Empty<string>()),
        new("ffmpeg", new[] { "-re", "-i" }, new[] { "-ac", "2", "-ar", "48000", "-sample_fmt", "s16", "-f", "alsa", "plughw:1,0" }),
        new("ffplay", new[] { "-nodisp", "-autoexit", "-loglevel", "quiet" }, Array.Empty<string>())
    };

    /// <summary>
    /// Resolves a configured player command or a known system default.
    /// </summary>
    public static SpeakerPlayer? Resolve(string configuredCommand)
    {
        if (!string.IsNullOrWhiteSpace(configuredCommand))
        {
            return ResolveConfiguredCommand(configuredCommand);
        }

        if (OperatingSystem.IsMacOS() && FindExecutable("afplay") is { } afplay)
        {
            return new SpeakerPlayer(afplay, Array.Empty<string>(), Array.Empty<string>());
        }

        foreach (var player in LinuxPlayers)
        {
            if (FindExecutable(player.Command) is { } command)
            {
                return player with { Command = command };
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a configured player command, optionally using {asset} as the audio-file placeholder.
    /// </summary>
    private static SpeakerPlayer? ResolveConfiguredCommand(string configuredCommand)
    {
        var tokens = SplitCommandLine(configuredCommand);
        if (tokens.Count == 0)
        {
            return null;
        }

        var assetIndex = tokens.FindIndex(token => string.Equals(token, "{asset}", StringComparison.Ordinal));
        if (assetIndex < 0)
        {
            return new SpeakerPlayer(tokens[0], tokens.Skip(1).ToArray(), Array.Empty<string>());
        }

        if (assetIndex == 0)
        {
            return null;
        }

        return new SpeakerPlayer(
            tokens[0],
            tokens.Skip(1).Take(assetIndex - 1).ToArray(),
            tokens.Skip(assetIndex + 1).ToArray());
    }

    /// <summary>
    /// Splits a simple shell-style command into process arguments.
    /// </summary>
    private static List<string> SplitCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;

        foreach (var character in commandLine)
        {
            if (character == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (character == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inSingleQuote && !inDoubleQuote)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Finds an executable on PATH.
    /// </summary>
    private static string? FindExecutable(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// Represents a command-line audio player.
/// </summary>
internal sealed record SpeakerPlayer(
    string Command,
    IReadOnlyList<string> ArgumentsBeforePath,
    IReadOnlyList<string> ArgumentsAfterPath);
