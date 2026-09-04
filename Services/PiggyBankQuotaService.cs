using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Reads Codex quota state through Codex's own local app-server. This is a
/// deliberately read-only integration: the only account RPC method allowed
/// here is account/rateLimits/read.
/// </summary>
public sealed class PiggyBankQuotaService
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(15);

    public async Task<PiggyBankSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var executable = await LocateCodexAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Codex CLI was not found.");

        var response = await ReadRateLimitsAsync(executable, cancellationToken).ConfigureAwait(false);
        return PiggyBankRateLimitParser.Parse(response, DateTimeOffset.UtcNow);
    }

    private static async Task<string?> LocateCodexAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var whereInfo = new ProcessStartInfo
        {
            FileName = "where.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        whereInfo.ArgumentList.Add("codex");

        try
        {
            var where = await RunProcessAsync(whereInfo, ProcessTimeout, cancellationToken).ConfigureAwait(false);
            if (where.ExitCode == 0)
            {
                foreach (var candidate in where.StandardOutput.Split(
                             new[] { '\r', '\n' },
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!candidates.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
                        candidates.Add(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or Win32Exception or TimeoutException)
        {
            // Fall through to the standard installation location below.
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var standardInstall = Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            if (!candidates.Any(existing => string.Equals(existing, standardInstall, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(standardInstall);
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var versionInfo = CreateCodexStartInfo(candidate, new[] { "--version" });
                var version = await RunProcessAsync(versionInfo, ProcessTimeout, cancellationToken).ConfigureAwait(false);
                var combined = $"{version.StandardOutput}\n{version.StandardError}";
                if (version.ExitCode == 0 && combined.Contains("codex", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch (Exception ex) when (ex is FileNotFoundException or Win32Exception or InvalidOperationException or TimeoutException)
            {
                // Try the next discovered Codex wrapper/executable.
            }
        }

        return null;
    }

    private static async Task<JsonElement> ReadRateLimitsAsync(string executable, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateCodexStartInfo(executable, new[] { "app-server", "--stdio" }),
            EnableRaisingEvents = true
        };

        if (!process.Start())
            throw new InvalidOperationException("The Codex app-server process could not be started.");

        var standardError = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(SessionTimeout);

        try
        {
            await SendRpcAsync(process, "initialize", 0, new
            {
                clientInfo = new
                {
                    name = "v_notch_piggy_bank",
                    title = "V-Notch Piggy Bank",
                    version = "1.0"
                }
            }, timeoutSource.Token).ConfigureAwait(false);

            var initialise = await ReadResponseAsync(process, 0, timeoutSource.Token).ConfigureAwait(false);
            EnsureNoRpcError(initialise, "initialisation");

            await SendRpcAsync(process, "initialized", null, new { }, timeoutSource.Token).ConfigureAwait(false);
            await SendRpcAsync(process, "account/rateLimits/read", 1, new { }, timeoutSource.Token).ConfigureAwait(false);

            var response = await ReadResponseAsync(process, 1, timeoutSource.Token).ConfigureAwait(false);
            EnsureNoRpcError(response, "rate-limit read");
            return response.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            throw new TimeoutException($"Codex app-server did not respond within {SessionTimeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (InvalidOperationException) { }

            if (!process.HasExited)
                await TerminateAsync(process).ConfigureAwait(false);

            _ = await standardError.ConfigureAwait(false);
        }
    }

    private static async Task SendRpcAsync(
        Process process,
        string method,
        int? id,
        object parameters,
        CancellationToken cancellationToken)
    {
        if (method is not ("initialize" or "initialized" or "account/rateLimits/read"))
            throw new InvalidOperationException($"RPC method is not permitted: {method}");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            if (id is not null) writer.WriteNumber("id", id.Value);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters);
            writer.WriteEndObject();
        }

        var line = Encoding.UTF8.GetString(stream.ToArray());
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadResponseAsync(Process process, int expectedId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw new InvalidOperationException($"Codex app-server exited before response id {expectedId} was received.");

            try
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("id", out var idElement) &&
                    idElement.TryGetInt32(out var responseId) && responseId == expectedId)
                    return message.Clone();
            }
            catch (JsonException)
            {
                // Ignore unrelated/malformed notification lines while waiting for our response.
            }
        }
    }

    private static void EnsureNoRpcError(JsonElement response, string operation)
    {
        if (!response.TryGetProperty("error", out var error)) return;
        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var value)
            ? value.GetString()
            : null;
        throw new InvalidOperationException($"Codex app-server {operation} failed: {message ?? "unknown error"}");
    }

    private static ProcessStartInfo CreateCodexStartInfo(string executablePath, IEnumerable<string> arguments)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var argumentList = arguments.ToArray();
        var extension = Path.GetExtension(fullPath);
        var isBatchWrapper = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = isBatchWrapper ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : fullPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (isBatchWrapper)
        {
            var command = $"\"{fullPath}\" {string.Join(" ", argumentList.Select(QuoteForCommandLine))}";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            foreach (var argument in argumentList) startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string QuoteForCommandLine(string value)
        => value.Length == 0
            ? "\"\""
            : value.Any(char.IsWhiteSpace) || value.Contains('"')
                ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                : value;

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("The child process could not be started.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            throw new TimeoutException($"Process did not exit within {timeout.TotalSeconds:0.#} seconds.");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task TerminateAsync(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return; }

        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

internal static class PiggyBankRateLimitParser
{
    public static PiggyBankSnapshot Parse(JsonElement response, DateTimeOffset fetchedAt)
    {
        var result = GetProperty(response, "result");
        var rateLimits = GetProperty(result, "rateLimits");
        var windows = new List<PiggyQuotaWindow>();

        if (rateLimits.ValueKind == JsonValueKind.Object)
        {
            AddWindow(windows, "primary", GetProperty(rateLimits, "primary"));
            AddWindow(windows, "secondary", GetProperty(rateLimits, "secondary"));
        }

        if (windows.Count == 0)
        {
            var byLimitId = GetProperty(result, "rateLimitsByLimitId");
            if (byLimitId.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in byLimitId.EnumerateObject())
                {
                    var primary = GetProperty(entry.Value, "primary");
                    var secondary = GetProperty(entry.Value, "secondary");
                    if (primary.ValueKind == JsonValueKind.Object || secondary.ValueKind == JsonValueKind.Object)
                    {
                        AddWindow(windows, $"{entry.Name}.primary", primary);
                        AddWindow(windows, $"{entry.Name}.secondary", secondary);
                    }
                    else if (GetProperty(entry.Value, "usedPercent").ValueKind != JsonValueKind.Undefined)
                    {
                        AddWindow(windows, entry.Name, entry.Value);
                    }
                }
            }
        }

        var fiveHour = windows.FirstOrDefault(window => window.WindowDurationMinutes == 300)
                       ?? windows.FirstOrDefault(window => window.Source.EndsWith("primary", StringComparison.OrdinalIgnoreCase));
        var weekly = windows.FirstOrDefault(window => window.WindowDurationMinutes == 10080)
                     ?? windows.FirstOrDefault(window => window.Source.EndsWith("secondary", StringComparison.OrdinalIgnoreCase));

        var resetObject = GetProperty(result, "rateLimitResetCredits");
        bool resetDataAvailable = resetObject.ValueKind == JsonValueKind.Object;
        var credits = ParseCredits(GetProperty(resetObject, "credits"));
        var reportedCount = GetInt(resetObject, "availableCount") ?? credits.Count;
        var availableCount = Math.Max(reportedCount, credits.Count);

        return new PiggyBankSnapshot(
            fetchedAt,
            fiveHour,
            weekly,
            credits,
            availableCount,
            Math.Max(availableCount - credits.Count, 0))
        {
            BankedResetDataAvailable = resetDataAvailable
        };
    }

    private static void AddWindow(ICollection<PiggyQuotaWindow> windows, string source, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var used = ClampPercent(GetDouble(value, "usedPercent") ?? 0);
        windows.Add(new PiggyQuotaWindow(
            used,
            100 - used,
            GetInt(value, "windowDurationMins"),
            ParseUnixSeconds(GetLong(value, "resetsAt")),
            source));
    }

    private static IReadOnlyList<PiggyBankedReset> ParseCredits(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<PiggyBankedReset>();

        var credits = new List<PiggyBankedReset>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var status = GetString(item, "status") ?? "unknown";
            var resetType = GetString(item, "resetType") ?? "unknown";
            if (!string.Equals(status, "available", StringComparison.OrdinalIgnoreCase) && status != "unknown") continue;
            if (!string.Equals(resetType, "codexRateLimits", StringComparison.OrdinalIgnoreCase) && resetType != "unknown") continue;

            var grantedAt = ParseUnixSeconds(GetLong(item, "grantedAt"));
            var expiresAt = ParseUnixSeconds(GetLong(item, "expiresAt"));
            var stableId = GetString(item, "id") ?? CreateDeterministicId(grantedAt, expiresAt, resetType);
            credits.Add(new PiggyBankedReset(
                stableId,
                GetString(item, "title") ?? "Codex reset",
                grantedAt,
                expiresAt,
                resetType,
                status));
        }

        return credits
            .OrderBy(credit => credit.ExpiresAt is null)
            .ThenBy(credit => credit.ExpiresAt)
            .ThenBy(credit => credit.GrantedAt)
            .ToArray();
    }

    private static string CreateDeterministicId(DateTimeOffset? grantedAt, DateTimeOffset? expiresAt, string resetType)
    {
        var input = $"{grantedAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? ""}|{expiresAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? ""}|{resetType}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"local-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static int ClampPercent(double value)
        => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);

    private static DateTimeOffset? ParseUnixSeconds(long? seconds)
    {
        if (seconds is null) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static JsonElement GetProperty(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;

    private static string? GetString(JsonElement parent, string name)
    {
        var value = GetProperty(parent, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? GetInt(JsonElement parent, string name)
    {
        var value = GetProperty(parent, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static long? GetLong(JsonElement parent, string name)
    {
        var value = GetProperty(parent, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static double? GetDouble(JsonElement parent, string name)
    {
        var value = GetProperty(parent, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }
}

internal static class PiggyBankFormatting
{
    private static readonly Color HealthyGreen = Color.FromRgb(48, 209, 88);
    private static readonly Color Amber = Color.FromRgb(255, 159, 10);
    private static readonly Color Orange = Color.FromRgb(255, 107, 53);
    private static readonly Color Red = Color.FromRgb(255, 69, 58);

    public static int ClampRemaining(double value)
        => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);

    public static Color QuotaColour(int remainingPercent)
    {
        var remaining = Math.Clamp(remainingPercent, 0, 100);
        if (remaining >= 70) return HealthyGreen;
        if (remaining >= 40) return Lerp(Amber, HealthyGreen, (remaining - 40) / 30d);
        if (remaining >= 15) return Lerp(Orange, Amber, (remaining - 15) / 25d);
        return Lerp(Red, Orange, remaining / 15d);
    }

    public static string FiveHourReset(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null) return "Reset time unavailable";
        var remaining = resetAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "Reset due now";
        if (remaining.TotalHours >= 24)
            return $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1)
            return $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"Resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    public static string WeeklyRemaining(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null) return "Time remaining unavailable";
        var remaining = resetAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "Reset due now";
        if (remaining.TotalDays >= 1)
        {
            var days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
            return $"{days} day{(days == 1 ? "" : "s")} remaining";
        }
        if (remaining.TotalHours >= 1)
            return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalHours))}h remaining";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m remaining";
    }

    public static int WeeklyRemainingDays(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null) return 0;
        var remaining = resetAt.Value - now;
        if (remaining <= TimeSpan.Zero) return 0;
        return Math.Clamp((int)Math.Ceiling(remaining.TotalDays), 1, 7);
    }

    public static string WeeklyReset(DateTimeOffset? resetAt)
        => resetAt is null ? "Reset time unavailable" : $"Resets {FormatLocal(resetAt.Value)}";

    public static string ResetExpiry(DateTimeOffset? expiresAt)
        => expiresAt is null ? "Expiry unavailable" : $"Expires {FormatLocal(expiresAt.Value)}";

    public static string ResetExpiryDate(DateTimeOffset? expiresAt)
    {
        if (expiresAt is null) return "Unavailable";
        var local = TimeZoneInfo.ConvertTime(expiresAt.Value, TimeZoneInfo.Local);
        var month = local.Month == 9
            ? "Sept"
            : local.ToString("MMM", CultureInfo.CurrentCulture);
        return $"{local:ddd} {local.Day} {month}";
    }

    public static string ResetExpiryTime(DateTimeOffset? expiresAt)
    {
        if (expiresAt is null) return string.Empty;
        return TimeZoneInfo.ConvertTime(expiresAt.Value, TimeZoneInfo.Local)
            .ToString("h:mm tt", CultureInfo.CurrentCulture)
            .Replace(" AM", " am", StringComparison.Ordinal)
            .Replace(" PM", " pm", StringComparison.Ordinal);
    }

    private static string FormatLocal(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local)
            .ToString("ddd d MMM, h:mm tt", CultureInfo.CurrentCulture)
            .Replace(" AM", " am", StringComparison.Ordinal)
            .Replace(" PM", " pm", StringComparison.Ordinal);

    private static Color Lerp(Color from, Color to, double t)
    {
        var amount = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
