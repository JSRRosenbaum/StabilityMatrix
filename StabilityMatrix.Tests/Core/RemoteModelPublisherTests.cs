using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Tests.Core;

[TestClass]
public class RemoteModelPublisherTests
{
    [TestMethod]
    public void RelativeCategoryPath_MapsKnownModelCategoriesToComfyLibrary()
    {
        var (modelsRoot, modelPath) = CreateModelFile(SharedFolderType.Lora, "cat.safetensors");

        var relative = RemoteModelPublisher.GetSafeRelativeCategoryPath(modelsRoot, modelPath);

        Assert.AreEqual("loras/cat.safetensors", relative);
    }

    [TestMethod]
    public void RelativeCategoryPath_FailsClosedForOutsidePathArchiveAndUnsupportedCategory()
    {
        var (modelsRoot, _) = CreateModelFile(SharedFolderType.Lora, "cat.safetensors");
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cat.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "outside");

        Assert.ThrowsException<InvalidOperationException>(() =>
            RemoteModelPublisher.GetSafeRelativeCategoryPath(modelsRoot, outside)
        );

        var (_, archivePath) = CreateModelFile(SharedFolderType.Lora, "cat.zip");
        Assert.ThrowsException<InvalidOperationException>(() =>
            RemoteModelPublisher.GetSafeRelativeCategoryPath(modelsRoot, archivePath)
        );

        var unsupportedPath = Path.Combine(
            modelsRoot,
            SharedFolderType.BackgroundRemoval.ToString(),
            "model.safetensors"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(unsupportedPath)!);
        File.WriteAllText(unsupportedPath, "unsupported");
        Assert.ThrowsException<InvalidOperationException>(() =>
            RemoteModelPublisher.GetSafeRelativeCategoryPath(modelsRoot, unsupportedPath)
        );
    }

    [TestMethod]
    public void DestinationValidation_RequiresSafeAbsoluteRemotePathAndRejectsTraversal()
    {
        var parsed = RSyncRemoteModelTransfer.ParseDestination("backend:/srv/model-library");
        Assert.AreEqual("backend", parsed.Host);
        Assert.AreEqual("/srv/model-library", parsed.RootPath);

        Assert.ThrowsException<InvalidOperationException>(() =>
            RSyncRemoteModelTransfer.ParseDestination("backend:relative")
        );
        Assert.ThrowsException<InvalidOperationException>(() =>
            RSyncRemoteModelTransfer.ParseDestination("backend:/srv/model-library;rm")
        );
        Assert.ThrowsException<InvalidOperationException>(() =>
            RSyncRemoteModelTransfer.ParseDestination("backend:/srv/model-library/../models")
        );
    }

    [TestMethod]
    public void RemotePathValidation_RejectsTraversalHiddenAndNestedFinalPaths()
    {
        Assert.AreEqual(
            "checkpoints/model.safetensors",
            RSyncRemoteModelTransfer.ValidateRelativeRemotePath("checkpoints/model.safetensors")
        );

        Assert.ThrowsException<InvalidOperationException>(() =>
            RSyncRemoteModelTransfer.ValidateRelativeRemotePath("../model.safetensors")
        );
        Assert.ThrowsException<InvalidOperationException>(() =>
            RSyncRemoteModelTransfer.ValidateRelativeRemotePath("checkpoints/.partial")
        );
        Assert.ThrowsException<InvalidOperationException>(() =>
            RSyncRemoteModelTransfer.ValidateRelativeRemotePath("checkpoints/nested/model.safetensors")
        );
    }

    [TestMethod]
    public async Task EnqueueCompletedDownloadAsync_OptInPublishesAndPersistsReadyJournal()
    {
        var (models, modelPath) = CreateModelFile(SharedFolderType.Lora, "ready.safetensors");
        var publisher = CreatePublisher(models, Path.GetDirectoryName(models)!, out var transfer);
        var download = CreateDownload(modelPath);

        await publisher.EnqueueCompletedDownloadAsync(download);
        var journal = await publisher.ReadJournalAsync();

        Assert.AreEqual(1, transfer.Calls.Count);
        Assert.AreEqual("loras/ready.safetensors", transfer.Calls[0].RelativeCategoryPath);
        Assert.AreEqual(RemoteModelPublishState.Ready, journal.Single().State);
    }

    [TestMethod]
    public async Task EnqueueCompletedDownloadAsync_RejectsPartialOrUnsupportedDownloadContext()
    {
        var (models, modelPath) = CreateModelFile(SharedFolderType.Lora, "partial.safetensors");
        var publisher = CreatePublisher(models, Path.GetDirectoryName(models)!, out var transfer);
        var download = CreateDownload(modelPath, withContext: false);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            publisher.EnqueueCompletedDownloadAsync(download)
        );

        Assert.AreEqual(0, transfer.Calls.Count);
        Assert.AreEqual(0, (await publisher.ReadJournalAsync()).Count);
    }

    [TestMethod]
    public async Task RetryIncompleteAsync_ReplaysFailedJournalEntryWithStableDigest()
    {
        var (models, modelPath) = CreateModelFile(SharedFolderType.StableDiffusion, "retry.safetensors");
        var publisher = CreatePublisher(models, Path.GetDirectoryName(models)!, out var transfer);
        transfer.FailNext = true;
        var download = CreateDownload(modelPath);

        await publisher.EnqueueCompletedDownloadAsync(download);
        Assert.AreEqual(RemoteModelPublishState.Failed, (await publisher.ReadJournalAsync()).Single().State);

        await publisher.RetryIncompleteAsync();

        Assert.AreEqual(2, transfer.Calls.Count);
        Assert.AreEqual(transfer.Calls[0].Sha256, transfer.Calls[1].Sha256);
        Assert.AreEqual(RemoteModelPublishState.Ready, (await publisher.ReadJournalAsync()).Single().State);
    }

    [TestMethod]
    public async Task EnqueueCompletedDownloadAsync_ConcurrentJournalUpdatesAreSerialized()
    {
        var library = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Library");
        var models = Path.Combine(library, "Models");
        var path1 = CreateModelFile(models, SharedFolderType.Lora, "one.safetensors");
        var path2 = CreateModelFile(models, SharedFolderType.Lora, "two.safetensors");
        var publisher = CreatePublisher(models, library, out _);

        await Task.WhenAll(
            publisher.EnqueueCompletedDownloadAsync(CreateDownload(path1)),
            publisher.EnqueueCompletedDownloadAsync(CreateDownload(path2))
        );

        var journal = await publisher.ReadJournalAsync();
        Assert.AreEqual(2, journal.Count);
        Assert.IsTrue(journal.All(e => e.State == RemoteModelPublishState.Ready));
    }

    [TestMethod]
    public async Task EnqueueCompletedDownloadAsync_OptOutDoesNothing()
    {
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.Settings.Returns(
            new Settings { RemoteModelPublishing = new RemoteModelPublishingSettings { IsEnabled = false } }
        );
        var transfer = new FakeTransfer();
        var publisher = new RemoteModelPublisher(
            NullLogger<RemoteModelPublisher>.Instance,
            settingsManager,
            transfer
        );

        await publisher.EnqueueCompletedDownloadAsync(
            new TrackedDownload
            {
                Id = Guid.NewGuid(),
                SourceUrl = new Uri("https://example.invalid/model.safetensors"),
                DownloadDirectory = new DirectoryPath(Path.GetTempPath()),
                FileName = "model.safetensors",
                TempFileName = "model.tmp",
            }
        );

        Assert.AreEqual(0, transfer.Calls.Count);
    }

    private static RemoteModelPublisher CreatePublisher(
        string models,
        string library,
        out FakeTransfer transfer
    )
    {
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.LibraryDir.Returns(new DirectoryPath(library));
        settingsManager.ModelsDirectory.Returns(models);
        settingsManager.Settings.Returns(
            new Settings
            {
                RemoteModelPublishing = new RemoteModelPublishingSettings
                {
                    IsEnabled = true,
                    Destination = "backend:/srv/model-library",
                },
            }
        );

        transfer = new FakeTransfer();
        return new RemoteModelPublisher(NullLogger<RemoteModelPublisher>.Instance, settingsManager, transfer);
    }

    private static (string ModelsRoot, string ModelPath) CreateModelFile(
        SharedFolderType type,
        string fileName
    )
    {
        var library = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Library");
        var models = Path.Combine(library, "Models");
        return (models, CreateModelFile(models, type, fileName));
    }

    private static string CreateModelFile(string modelsRoot, SharedFolderType type, string fileName)
    {
        var dir = Path.Combine(modelsRoot, type.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, fileName);
        return path;
    }

    private static TrackedDownload CreateDownload(string modelPath, bool withContext = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            SourceUrl = new Uri("https://example.invalid/" + Path.GetFileName(modelPath)),
            DownloadDirectory = new DirectoryPath(Path.GetDirectoryName(modelPath)!),
            FileName = Path.GetFileName(modelPath),
            TempFileName = Path.GetFileName(modelPath) + ".tmp",
            ContextAction = withContext ? new ModelPostDownloadContextAction() : null,
        };

    private sealed class FakeTransfer : IRemoteModelTransfer
    {
        public List<(string LocalPath, string Sha256, string RelativeCategoryPath)> Calls { get; } = [];
        public bool FailNext { get; set; }

        public Task PublishAsync(
            FileInfo localFile,
            string sha256,
            RemoteModelPublishingSettings settings,
            string relativeCategoryPath,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add((localFile.FullName, sha256, relativeCategoryPath));
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("simulated transfer failure");
            }
            return Task.CompletedTask;
        }
    }
}
