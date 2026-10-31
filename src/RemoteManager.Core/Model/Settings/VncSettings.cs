namespace RemoteManager.Core.Model.Settings;

public enum VncQuality { Auto, Low, Medium, High }

public sealed record VncSettings : ConnectionSettings
{
    public bool ViewOnly { get; init; }
    public bool ScaleToWindow { get; init; } = true;
    public VncQuality Quality { get; init; } = VncQuality.Auto;

    /// <summary>Open the external viewer instead of the embedded one.</summary>
    public bool OpenExternally { get; init; }

    public string? JumpHostId { get; init; }

    /// <summary>The overview may show a periodically refreshed picture of the open session (off for sensitive hosts).</summary>
    public bool LivePreview { get; init; } = true;
}
