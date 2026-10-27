using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Settings;

namespace StabilityMatrix.Core.Services;

public enum RemoteModelPublishState
{
    Pending,
    Running,
    Ready,
    Failed,
}

public sealed record RemoteModelPublishEntry(
    Guid Id,
    string LocalPath,
    string RelativeCategoryPath,
    string Sha256,
    RemoteModelPublishState State,
    DateTimeOffset UpdatedAt,
    string? Error = null
);

public interface IRemoteModelTransfer
{
    Task PublishAsync(
        FileInfo localFile,
        string sha256,
        RemoteModelPublishingSettings settings,
        string relativeCategoryPath,
        CancellationToken cancellationToken = default
    );
}

public interface IRemoteModelPublisher
{
    event EventHandler<RemoteModelPublishEntry>? PublicationReady;

    Task<IReadOnlyList<RemoteModelPublishEntry>> ReadJournalAsync(
        CancellationToken cancellationToken = default
    );
    Task EnqueueCompletedDownloadAsync(
        TrackedDownload download,
        CancellationToken cancellationToken = default
    );
    Task RetryIncompleteAsync(CancellationToken cancellationToken = default);
}

public sealed class RemoteModelPublisher(
    ILogger<RemoteModelPublisher> logger,
    ISettingsManager settingsManager,
    IRemoteModelTransfer transfer
) : IRemoteModelPublisher
{
    public event EventHandler<RemoteModelPublishEntry>? PublicationReady;

    private readonly SemaphoreSlim journalLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors",
        ".ckpt",
        ".pt",
        ".pth",
        ".bin",
        ".gguf",
    };

    public async Task EnqueueCompletedDownloadAsync(
        TrackedDownload download,
        CancellationToken cancellationToken = default
    )
    {
        var settings = settingsManager.Settings.RemoteModelPublishing;
        if (!settings.IsEnabled)
        {
            return;
        }

        await journalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await CreateEntryAsync(download, cancellationToken).ConfigureAwait(false);
            await UpsertJournalEntryUnsafeAsync(entry, cancellationToken).ConfigureAwait(false);
            await PublishEntryUnsafeAsync(entry, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            journalLock.Release();
        }
    }

    public async Task RetryIncompleteAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsManager.Settings.RemoteModelPublishing;
        if (!settings.IsEnabled)
        {
            return;
        }

        await journalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadJournalUnsafeAsync(cancellationToken).ConfigureAwait(false);
            foreach (
                var entry in entries.Where(e =>
                    e.State
                        is RemoteModelPublishState.Pending
                            or RemoteModelPublishState.Running
                            or RemoteModelPublishState.Failed
                )
            )
            {
                if (!File.Exists(entry.LocalPath))
                {
                    await UpsertJournalEntryUnsafeAsync(
                            entry with
                            {
                                State = RemoteModelPublishState.Failed,
                                UpdatedAt = DateTimeOffset.UtcNow,
                                Error = "Local source file no longer exists.",
                            },
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    continue;
                }

                await PublishEntryUnsafeAsync(entry, settings, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            journalLock.Release();
        }
    }

    public async Task<IReadOnlyList<RemoteModelPublishEntry>> ReadJournalAsync(
        CancellationToken cancellationToken = default
    )
    {
        await journalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadJournalUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            journalLock.Release();
        }
    }

    private async Task<RemoteModelPublishEntry> CreateEntryAsync(
        TrackedDownload download,
        CancellationToken cancellationToken
    )
    {
        if (download.AutoExtractArchive)
        {
            throw new InvalidOperationException(
                "Archive/extracted downloads are not eligible for remote publication."
            );
        }

        if (download.ContextAction is not (ModelPostDownloadContextAction or CivitPostDownloadContextAction))
        {
            throw new InvalidOperationException(
                "Download is not a model download with a supported completion context."
            );
        }

        var localPath = Path.GetFullPath(Path.Combine(download.DownloadDirectory, download.FileName));
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("Completed download file is missing", localPath);
        }

        var relativeCategoryPath = GetSafeRelativeCategoryPath(settingsManager.ModelsDirectory, localPath);
        var sha256 = await ComputeSha256Async(localPath, cancellationToken).ConfigureAwait(false);
        return new RemoteModelPublishEntry(
            download.Id,
            localPath,
            relativeCategoryPath,
            sha256,
            RemoteModelPublishState.Pending,
            DateTimeOffset.UtcNow
        );
    }

    private async Task PublishEntryUnsafeAsync(
        RemoteModelPublishEntry entry,
        RemoteModelPublishingSettings settings,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await UpsertJournalEntryUnsafeAsync(
                    entry with
                    {
                        State = RemoteModelPublishState.Running,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            await transfer
                .PublishAsync(
                    new FileInfo(entry.LocalPath),
                    entry.Sha256,
                    settings,
                    entry.RelativeCategoryPath,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var readyEntry = entry with
            {
                State = RemoteModelPublishState.Ready,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = null,
            };
            await UpsertJournalEntryUnsafeAsync(readyEntry, cancellationToken).ConfigureAwait(false);
            PublicationReady?.Invoke(this, readyEntry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Remote model publication failed for {File}",
                Path.GetFileName(entry.LocalPath)
            );
            await UpsertJournalEntryUnsafeAsync(
                    entry with
                    {
                        State = RemoteModelPublishState.Failed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Error = SanitizeFailure(ex),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static string SanitizeFailure(Exception ex) =>
        ex switch
        {
            RemoteProcessException remote => remote.Message,
            InvalidOperationException => ex.Message,
            FileNotFoundException => ex.Message,
            _ => ex.GetType().Name,
        };

    private async Task<IReadOnlyList<RemoteModelPublishEntry>> ReadJournalUnsafeAsync(
        CancellationToken cancellationToken
    )
    {
        var file = GetJournalPath();
        if (!File.Exists(file))
        {
            return [];
        }

        await using var stream = File.OpenRead(file);
        return await JsonSerializer
                .DeserializeAsync<List<RemoteModelPublishEntry>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
    }

    private async Task UpsertJournalEntryUnsafeAsync(
        RemoteModelPublishEntry entry,
        CancellationToken cancellationToken
    )
    {
        var entries = (await ReadJournalUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var index = entries.FindIndex(e => e.Id == entry.Id);
        if (index >= 0)
        {
            entries[index] = entry;
        }
        else
        {
            entries.Add(entry);
        }

        var max = Math.Max(1, settingsManager.Settings.RemoteModelPublishing.MaxJournalEntries);
        entries = entries.OrderByDescending(e => e.UpdatedAt).Take(max).ToList();

        var file = GetJournalPath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer
                .SerializeAsync(stream, entries, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(temp, file, true);
    }

    private string GetJournalPath() =>
        Path.Combine(settingsManager.LibraryDir, ".remote-model-publishing.json");

    internal static string GetSafeRelativeCategoryPath(string modelsDirectory, string localPath)
    {
        var modelsRoot =
            Path.GetFullPath(modelsDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullLocalPath = Path.GetFullPath(localPath);
        if (!fullLocalPath.StartsWith(modelsRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Remote publication only supports files inside the model library."
            );
        }

        var relative = Path.GetRelativePath(modelsRoot, fullLocalPath);
        var parts = relative.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries
        );
        if (parts.Length != 2 || !Enum.TryParse<SharedFolderType>(parts[0], true, out var category))
        {
            throw new InvalidOperationException(
                "Remote publication requires a model file directly inside an allowlisted model category."
            );
        }

        if (!IsAllowedCategory(category))
        {
            throw new InvalidOperationException("Remote publication category is not allowlisted.");
        }

        var fileName = parts[1];
        ValidateSafeFileName(fileName);
        if (!AllowedExtensions.Contains(Path.GetExtension(fileName)))
        {
            throw new InvalidOperationException("Remote publication file extension is not allowlisted.");
        }

        EnsureNoSymlinkInPath(modelsRoot, fullLocalPath);

        return ToComfyCategory(category) + "/" + fileName;
    }

    private static bool IsAllowedCategory(SharedFolderType category) =>
        category
            is SharedFolderType.StableDiffusion
                or SharedFolderType.Lora
                or SharedFolderType.LyCORIS
                or SharedFolderType.VAE
                or SharedFolderType.ApproxVAE
                or SharedFolderType.TextEncoders
                or SharedFolderType.ControlNet
                or SharedFolderType.ESRGAN
                or SharedFolderType.RealESRGAN
                or SharedFolderType.SwinIR
                or SharedFolderType.ClipVision
                or SharedFolderType.DiffusionModels;

    internal static string ToComfyCategory(SharedFolderType sharedFolderType) =>
        sharedFolderType switch
        {
            SharedFolderType.Lora or SharedFolderType.LyCORIS => "loras",
            SharedFolderType.VAE or SharedFolderType.ApproxVAE => "vae",
            SharedFolderType.TextEncoders => "text_encoders",
            SharedFolderType.ControlNet => "controlnet",
            SharedFolderType.ESRGAN or SharedFolderType.RealESRGAN or SharedFolderType.SwinIR =>
                "upscale_models",
            SharedFolderType.ClipVision => "clip_vision",
            SharedFolderType.DiffusionModels => "diffusion_models",
            _ => "checkpoints",
        };

    internal static void ValidateSafeFileName(string fileName)
    {
        if (
            string.IsNullOrWhiteSpace(fileName)
            || fileName != Path.GetFileName(fileName)
            || fileName.Contains("..", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException("Unsafe remote model file name.");
        }
    }

    private static void EnsureNoSymlinkInPath(string root, string fullPath)
    {
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(root, fullPath)) ?? string.Empty;
        var cursor = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (
            var part in relativeDirectory.Split(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            cursor = Path.Combine(cursor, part);
            if (new DirectoryInfo(cursor).LinkTarget is not null)
            {
                throw new InvalidOperationException("Remote publication refuses symlinked model paths.");
            }
        }

        if (new FileInfo(fullPath).LinkTarget is not null)
        {
            throw new InvalidOperationException("Remote publication refuses symlinked model files.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed partial class RSyncRemoteModelTransfer : IRemoteModelTransfer
{
    private static readonly Regex SafeDestinationRegex = SafeDestinationPattern();

    public async Task PublishAsync(
        FileInfo localFile,
        string sha256,
        RemoteModelPublishingSettings settings,
        string relativeCategoryPath,
        CancellationToken cancellationToken = default
    )
    {
        if (!localFile.Exists)
        {
            throw new FileNotFoundException("Local model file is missing", localFile.FullName);
        }

        var destination = ParseDestination(settings.Destination);
        var safeRelative = ValidateRelativeRemotePath(relativeCategoryPath);
        var stagingName = ValidateRemoteSegment(settings.StagingDirectoryName, allowLeadingDot: true);
        // A different publisher must not mutate or move this attempt's in-flight bytes.
        var stagingRelative = $"{stagingName}/{sha256}/{Guid.NewGuid():N}/{safeRelative}";

        var stagingPath = CombineRemotePath(destination.RootPath, stagingRelative);
        var finalPath = CombineRemotePath(destination.RootPath, safeRelative);
        var stagingDir = RemoteDirectoryName(stagingPath);
        var finalDir = RemoteDirectoryName(finalPath);

        await RunProcessAsync(
                "ssh",
                SshArgs(destination.Host, "mkdir", "-p", stagingDir, finalDir),
                cancellationToken
            )
            .ConfigureAwait(false);
        await RunProcessAsync(
                "rsync",
                [
                    "--partial",
                    "--inplace",
                    "--checksum",
                    "--",
                    localFile.FullName,
                    $"{destination.Host}:{stagingPath}",
                ],
                cancellationToken
            )
            .ConfigureAwait(false);

        var stagedHash = ParseSha256(
            await RunProcessAsync(
                    "ssh",
                    SshArgs(destination.Host, "sha256sum", stagingPath),
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
        if (!string.Equals(stagedHash, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Remote checksum validation failed.");
        }

        var publishScript = GetAtomicPublishScript();

        await RunProcessAsync(
                "ssh",
                SshArgs(destination.Host, "sh", "-s", "--", sha256, stagingPath, finalPath, finalDir),
                cancellationToken,
                publishScript
            )
            .ConfigureAwait(false);
    }

    internal static (string Host, string RootPath) ParseDestination(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !SafeDestinationRegex.IsMatch(destination))
        {
            throw new InvalidOperationException("Remote model destination is not configured or is unsafe.");
        }

        var separator = destination.IndexOf(':', StringComparison.Ordinal);
        if (
            separator <= 0
            || separator == destination.Length - 1
            || destination.IndexOf(':', separator + 1) >= 0
        )
        {
            throw new InvalidOperationException("Remote model destination must be host:/absolute/path.");
        }

        var host = destination[..separator];
        var root = destination[(separator + 1)..].TrimEnd('/');
        if (host.StartsWith("-", StringComparison.Ordinal) || !root.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Remote model destination must be host:/absolute/path.");
        }

        ValidateRemoteAbsolutePath(root);
        return (host, root);
    }

    internal static string ValidateRelativeRemotePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsafe remote relative path.");
        }

        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException(
                "Remote relative path must include exactly a category and file name."
            );
        }

        foreach (var part in parts)
        {
            ValidateRemoteSegment(part, allowLeadingDot: false);
        }

        return string.Join("/", parts);
    }

    private static string ValidateRemoteAbsolutePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            ValidateRemoteSegment(part, allowLeadingDot: false);
        }
        return path;
    }

    private static string ValidateRemoteSegment(string segment, bool allowLeadingDot)
    {
        if (
            string.IsNullOrWhiteSpace(segment)
            || segment is "." or ".."
            || segment.Contains("..", StringComparison.Ordinal)
            || (!allowLeadingDot && segment.StartsWith(".", StringComparison.Ordinal))
            || segment.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
        )
        {
            throw new InvalidOperationException("Unsafe remote path segment.");
        }
        return segment;
    }

    private static string GetAtomicPublishScript() =>
        "set -eu\n"
        + "expected=\"$1\"\nstaged=\"$2\"\nfinal=\"$3\"\nfinaldir=\"$4\"\n"
        + "mkdir -p -- \"$finaldir\"\n"
        + "if [ -e \"$final\" ]; then\n"
        + "  actual=$(sha256sum -- \"$final\" | awk '{print $1}')\n"
        + "  if [ \"$actual\" = \"$expected\" ]; then\n"
        + "    rm -f -- \"$staged\"\n"
        + "    exit 0\n"
        + "  fi\n"
        + "  exit 23\n"
        + "fi\n"
        + "mv -n -- \"$staged\" \"$final\"\n"
        + "actual=$(sha256sum -- \"$final\" | awk '{print $1}')\n"
        + "[ \"$actual\" = \"$expected\" ]\n";

    private static IReadOnlyList<string> SshArgs(string host, params string[] remoteArgs)
    {
        var args = new List<string> { "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", host };
        args.AddRange(remoteArgs);
        return args;
    }

    private static string CombineRemotePath(string root, string relative) =>
        root.TrimEnd('/') + "/" + relative;

    private static string RemoteDirectoryName(string remotePath) => remotePath[..remotePath.LastIndexOf('/')];

    private static string? ParseSha256(string output) =>
        output.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private static async Task<string> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null
    )
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = standardInput is not null,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process =
            Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        try
        {
            if (standardInput is not null)
            {
                await process
                    .StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new RemoteProcessException(fileName, process.ExitCode);
            }
            return stdout;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cancellation cleanup.
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.@-]+:/[A-Za-z0-9_./-]+$")]
    private static partial Regex SafeDestinationPattern();
}

public sealed class RemoteProcessException(string fileName, int exitCode)
    : Exception($"{fileName} failed with exit code {exitCode}.");
