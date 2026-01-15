using System;

namespace PhotoFlow.Licensing.Services;

public sealed class DevLicensingService : ILicensingService
{
    public bool IsValid() => true;

    public string GetStatusText() => "DEV license (no restrictions)";

    public Entitlements GetEntitlements()
        => new Entitlements(
            AiBackgroundRemovalAllowed: true,
            MaxProductsPerDay: int.MaxValue,
            MaxFramesPerProduct: int.MaxValue,
            WatermarkRequired: false
        );
}
