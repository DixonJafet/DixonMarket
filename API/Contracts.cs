namespace MarketDixon.Api;

internal sealed record SignupRequest(string? UserName, string? Password);
internal sealed record LoginRequest(string? UserName, string? Password);
internal sealed record AuthResponse(Guid UserId, string UserName, string AccessToken);
internal sealed record UserResponse(Guid UserId, string UserName);
internal sealed record UserRecord(Guid UserId, string UserName, string PasswordHash);
internal sealed record PaymentResponse(bool Success, string Message);
internal sealed record CheckoutRequest(Guid IdempotencyKey, IReadOnlyList<CheckoutItemRequest>? Items);
internal sealed record CheckoutItemRequest(string? Barcode, int Quantity, Guid? OfferId);
internal sealed record CheckoutResponse(
    bool Success,
    string Message,
    Guid PurchaseId,
    long PurchaseSequence,
    decimal TotalCRC);
internal sealed record RecommendationOffer(
    Guid OfferId,
    string Kind,
    Product Product,
    int DiscountPercent,
    decimal FinalPriceCRC,
    string ModelVersion);
internal sealed record RecommendationProfileResponse(
    int RecentPurchaseCount,
    long? LastPurchaseSequence,
    IReadOnlyDictionary<string, decimal> ProductAffinities,
    IReadOnlyDictionary<string, decimal> CategoryAffinities,
    IReadOnlyDictionary<string, decimal> SubcategoryAffinities,
    IReadOnlyDictionary<string, decimal> BrandAffinities,
    IReadOnlyDictionary<string, int> FavoritePurchaseCounts);
internal sealed record PurchaseFeature(
    string Barcode,
    string? Category,
    string? Subcategory,
    string? Brand,
    int Quantity);
internal sealed record RecommendationCandidate(Product Product, decimal AssociationScore, decimal PopularityScore);
internal sealed record ProductOfferSelection(string Barcode, string Kind);
internal sealed record ProductPage(IReadOnlyList<Product> Items, int Page, int PageSize, long TotalItems)
{
    public long TotalPages => (TotalItems + PageSize - 1) / PageSize;
}

internal sealed record Product(
    string Barcode,
    string ProductName,
    string? Brand,
    string? PackageQuantity,
    string? Area,
    string? Category,
    string? Subcategory,
    decimal? EnergyKcalPer100g,
    decimal? FatGramsPer100g,
    decimal? SaturatedFatGramsPer100g,
    decimal? CarbohydratesGramsPer100g,
    decimal? SugarsGramsPer100g,
    decimal? FiberGramsPer100g,
    decimal? ProteinGramsPer100g,
    decimal? SaltGramsPer100g,
    decimal PriceCRC,
    bool PriceIsEstimated,
    int StockQuantity,
    string? ImageUrl,
    string? ProductUrl,
    string DataSource,
    DateTime ImportedAtUtc);

internal sealed record CategoryGroup(string Area, IReadOnlyList<CategoryItem> Categories);
internal sealed record CategoryItem(string Name, IReadOnlyList<string> Subcategories);
