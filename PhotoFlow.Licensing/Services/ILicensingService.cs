namespace PhotoFlow.Licensing.Services;

public sealed record Entitlements(
    bool AiBackgroundRemovalAllowed,
    int MaxProductsPerDay,
    int MaxFramesPerProduct,
    bool WatermarkRequired
)
{
    public static Entitlements Trial3Days =>
        new(AiBackgroundRemovalAllowed: true,
            MaxProductsPerDay: int.MaxValue,
            MaxFramesPerProduct: int.MaxValue,
            WatermarkRequired: true);

    public static Entitlements MonthlyNoLimits =>
        new(AiBackgroundRemovalAllowed: true,
            MaxProductsPerDay: int.MaxValue,
            MaxFramesPerProduct: int.MaxValue,
            WatermarkRequired: false);

    public static Entitlements None =>
        new(AiBackgroundRemovalAllowed: false,
            MaxProductsPerDay: 0,
            MaxFramesPerProduct: 0,
            WatermarkRequired: true);
}


public interface ILicensingService
{
    bool IsValid();
    string GetStatusText();
    Entitlements GetEntitlements();
}
