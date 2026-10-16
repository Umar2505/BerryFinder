// Product plans and what each unlocks. Prices and limits are preliminary pilot values to be tested with
// customers; every number lives here so changing one is a one-line edit.
public sealed record PlanLimits(
    string Id,
    int RecommendationLimit,
    int SavedLimit,
    bool SeasonalEstimates,
    bool AdvancedFilters,
    bool TripPlanner,
    bool Export,
    double MaxSearchRadiusKm);

public sealed record PlanOffer(string OfferId, string PlanId, string NameEn, string NameFi, long AmountMinor, string Currency, string Interval);

public static class Plans
{
    public static readonly PlanLimits Free = new("free", RecommendationLimit: 5, SavedLimit: 10, SeasonalEstimates: false, AdvancedFilters: false,
        TripPlanner: false, Export: false, MaxSearchRadiusKm: 30);

    public static readonly PlanLimits Premium = new("premium", RecommendationLimit: 40, SavedLimit: 200, SeasonalEstimates: true, AdvancedFilters: true,
        TripPlanner: true, Export: false, MaxSearchRadiusKm: 80);

    public static readonly PlanLimits Professional = new("professional", RecommendationLimit: 200, SavedLimit: 2000, SeasonalEstimates: true, AdvancedFilters: true,
        TripPlanner: true, Export: true, MaxSearchRadiusKm: 100);

    public static readonly PlanOffer[] Offers =
    [
        new("premium-monthly", "premium", "Premium, monthly", "Premium, kuukausi", 499, "EUR", "month"),
        new("premium-season", "premium", "Premium season pass (renews yearly)", "Premium-kausikortti (uusiutuu vuosittain)", 1999, "EUR", "year"),
        new("professional-monthly", "professional", "Professional, monthly", "Ammattilainen, kuukausi", 2900, "EUR", "month"),
    ];

    // During the closed pilot an admin can open every feature to testers without touching billing
    public static bool PilotOpenAccess { get; set; }

    public static PlanLimits For(string planId) => planId switch
    {
        "professional" => Professional,
        "premium" => Premium,
        _ => Free,
    };
}
