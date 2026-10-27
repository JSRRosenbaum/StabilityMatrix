namespace StabilityMatrix.Core.Models.Settings;

public class RemoteModelPublishingSettings
{
    /// <summary>Explicit opt-in for publishing completed local model downloads to a remote Comfy model library.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>SSH destination in rsync form, for example "backend:/srv/model-library".</summary>
    public string? Destination { get; set; }

    /// <summary>Hidden staging directory name under <see cref="Destination"/>.</summary>
    public string StagingDirectoryName { get; set; } = ".staging";

    /// <summary>Maximum persisted journal entries kept for resume/status.</summary>
    public int MaxJournalEntries { get; set; } = 256;
}
