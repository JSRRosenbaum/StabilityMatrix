namespace StabilityMatrix.Core.Models.Settings;

public class RemoteComfySettings
{
    /// <summary>
    /// Explicit opt-in for using a ComfyUI server that is not managed by a local Stability Matrix package.
    /// Local package backed inference remains the default when this is false.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// ComfyUI base URL for the managed backend.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";
}
