using AsyncAwaitBestPractices;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Progress;

namespace StabilityMatrix.Core.Services;

public sealed class RemoteModelPublishDownloadBridge : IDisposable
{
    private readonly ILogger<RemoteModelPublishDownloadBridge> logger;
    private readonly ITrackedDownloadService trackedDownloadService;
    private readonly IRemoteModelPublisher publisher;
    private readonly Dictionary<Guid, EventHandler<ProgressState>> handlers = [];

    public RemoteModelPublishDownloadBridge(
        ILogger<RemoteModelPublishDownloadBridge> logger,
        ITrackedDownloadService trackedDownloadService,
        IRemoteModelPublisher publisher
    )
    {
        this.logger = logger;
        this.trackedDownloadService = trackedDownloadService;
        this.publisher = publisher;
        trackedDownloadService.DownloadAdded += OnDownloadAdded;
    }

    private void OnDownloadAdded(object? sender, TrackedDownload download)
    {
        EventHandler<ProgressState> handler = (_, state) => OnProgressStateChanged(download, state);
        handlers[download.Id] = handler;
        download.ProgressStateChanged += handler;
    }

    private void OnProgressStateChanged(TrackedDownload download, ProgressState state)
    {
        if (state is ProgressState.Success or ProgressState.Cancelled)
        {
            if (handlers.Remove(download.Id, out var handler))
            {
                download.ProgressStateChanged -= handler;
            }
        }

        if (state != ProgressState.Success)
        {
            return;
        }

        publisher
            .EnqueueCompletedDownloadAsync(download)
            .SafeFireAndForget(ex =>
            {
                logger.LogWarning(
                    ex,
                    "Remote model publish enqueue failed for {FileName}",
                    download.FileName
                );
            });
    }

    public void Dispose()
    {
        trackedDownloadService.DownloadAdded -= OnDownloadAdded;
        foreach (var download in trackedDownloadService.Downloads)
        {
            if (handlers.TryGetValue(download.Id, out var handler))
            {
                download.ProgressStateChanged -= handler;
            }
        }
        handlers.Clear();
    }
}
