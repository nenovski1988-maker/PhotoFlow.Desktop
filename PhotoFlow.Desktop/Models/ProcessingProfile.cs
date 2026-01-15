using PhotoFlow.Processing.Services;

namespace PhotoFlow.Desktop.Models;

public sealed class ProcessingProfile
{
    // Default: Manual (classic). You can change default to AiU2NetQuality if you want.
    public BackgroundMethod Method { get; set; } = BackgroundMethod.SimpleOverexposedToTransparent;

    // Manual cut controls
    public byte WhiteThreshold { get; set; } = 245;

    // Used for classic AND as soft edge for AI (blur)
    public byte Feather { get; set; } = 10;

    public ProcessingProfile Clone()
        => new ProcessingProfile
        {
            Method = this.Method,
            WhiteThreshold = this.WhiteThreshold,
            Feather = this.Feather
        };

    public override string ToString()
        => $"{Method} | threshold={WhiteThreshold} feather={Feather}";
}
