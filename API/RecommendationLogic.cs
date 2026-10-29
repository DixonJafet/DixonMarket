using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketDixon.Api;

internal sealed class UserProfileSnapshot
{
    public int RecentPurchaseCount { get; set; }
    public long? LastPurchaseSequence { get; set; }
    public Dictionary<string, decimal> ProductAffinities { get; set; } = NewDecimalMap();
    public Dictionary<string, decimal> CategoryAffinities { get; set; } = NewDecimalMap();
    public Dictionary<string, decimal> SubcategoryAffinities { get; set; } = NewDecimalMap();
    public Dictionary<string, decimal> BrandAffinities { get; set; } = NewDecimalMap();
    public Dictionary<string, int> FavoritePurchaseCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, decimal> NewDecimalMap() => new(StringComparer.OrdinalIgnoreCase);
}

internal static class RecommendationLogic
{
    public const int PurchaseWindowSize = 30;
    public const int DiscoveryOfferLimit = 8;
    public const int FavoriteOfferLimit = 6;
    public const int MinimumDiscountPercent = 5;
    public const int MaximumDiscountPercent = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static UserProfileSnapshot DeserializeProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new UserProfileSnapshot();
        }

        var profile = JsonSerializer.Deserialize<UserProfileSnapshot>(json, JsonOptions)
            ?? new UserProfileSnapshot();
        profile.ProductAffinities = RebuildMap(profile.ProductAffinities);
        profile.CategoryAffinities = RebuildMap(profile.CategoryAffinities);
        profile.SubcategoryAffinities = RebuildMap(profile.SubcategoryAffinities);
        profile.BrandAffinities = RebuildMap(profile.BrandAffinities);
        profile.FavoritePurchaseCounts = new Dictionary<string, int>(
            profile.FavoritePurchaseCounts,
            StringComparer.OrdinalIgnoreCase);
        return profile;
    }

    public static string SerializeProfile(UserProfileSnapshot profile) =>
        JsonSerializer.Serialize(profile, JsonOptions);

    public static void ApplyPurchase(
        UserProfileSnapshot profile,
        IReadOnlyList<PurchaseFeature>? removedPurchase,
        IReadOnlyList<PurchaseFeature> addedPurchase,
        long purchaseSequence)
    {
        if (removedPurchase is not null)
        {
            ApplyFeatures(profile, removedPurchase, -1);
        }

        ApplyFeatures(profile, addedPurchase, 1);
        profile.RecentPurchaseCount = Math.Min(PurchaseWindowSize, profile.RecentPurchaseCount + 1);
        profile.LastPurchaseSequence = purchaseSequence;
    }

    public static IReadOnlyList<ProductOfferSelection> SelectOffers(
        UserProfileSnapshot profile,
        IReadOnlyList<RecommendationCandidate> candidates)
    {
        var favorites = candidates
            .Where(candidate => profile.FavoritePurchaseCounts.TryGetValue(candidate.Product.Barcode, out var count) && count >= 2)
            .OrderByDescending(candidate => profile.FavoritePurchaseCounts[candidate.Product.Barcode])
            .ThenByDescending(candidate => Score(profile, candidate))
            .ThenBy(candidate => candidate.Product.Barcode, StringComparer.Ordinal)
            .ToArray();
        var favoriteBarcodes = favorites
            .Take(FavoriteOfferLimit)
            .Select(candidate => candidate.Product.Barcode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discovery = candidates
            .Where(candidate => !favoriteBarcodes.Contains(candidate.Product.Barcode))
            .Where(candidate => !profile.FavoritePurchaseCounts.ContainsKey(candidate.Product.Barcode))
            .OrderByDescending(candidate => Score(profile, candidate))
            .ThenBy(candidate => candidate.Product.Barcode, StringComparer.Ordinal)
            .ToArray();

        return SelectDiverse(discovery, DiscoveryOfferLimit, "discovery")
            .Concat(SelectDiverse(favorites, FavoriteOfferLimit, "favorite"))
            .ToArray();
    }

    public static int GetDiscountPercent(Guid userId, string barcode, string modelVersion, string kind)
    {
        var input = Encoding.UTF8.GetBytes($"{userId:D}:{barcode}:{modelVersion}:{kind}");
        var hash = SHA256.HashData(input);
        var value = (uint)(hash[0] | hash[1] << 8 | hash[2] << 16 | hash[3] << 24);
        return MinimumDiscountPercent + (int)(value % (MaximumDiscountPercent - MinimumDiscountPercent + 1));
    }

    public static decimal GetFinalPrice(decimal price, int discountPercent)
    {
        if (discountPercent is < MinimumDiscountPercent or > MaximumDiscountPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(discountPercent));
        }

        return decimal.Round(price * (100 - discountPercent) / 100, 2, MidpointRounding.AwayFromZero);
    }

    private static IEnumerable<ProductOfferSelection> SelectDiverse(
        IReadOnlyList<RecommendationCandidate> candidates,
        int limit,
        string kind)
    {
        var selected = new List<ProductOfferSelection>();
        var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var category = candidate.Product.Category ?? candidate.Product.Area ?? string.Empty;
            if (categoryCounts.GetValueOrDefault(category) >= 3)
            {
                continue;
            }

            selected.Add(new ProductOfferSelection(candidate.Product.Barcode, kind));
            categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;
            if (selected.Count == limit)
            {
                break;
            }
        }

        return selected;
    }

    private static decimal Score(UserProfileSnapshot profile, RecommendationCandidate candidate)
    {
        var product = candidate.Product;
        var categoryAffinity = GetAffinity(profile.CategoryAffinities, product.Category);
        var subcategoryAffinity = GetAffinity(profile.SubcategoryAffinities, product.Subcategory);
        var brandAffinity = GetAffinity(profile.BrandAffinities, product.Brand);
        return candidate.AssociationScore * 4m
            + (decimal)Math.Log(1 + (double)Math.Max(0, candidate.PopularityScore)) * 1.5m
            + (decimal)Math.Log(1 + (double)Math.Max(0, categoryAffinity)) * 0.8m
            + (decimal)Math.Log(1 + (double)Math.Max(0, subcategoryAffinity)) * 1.1m
            + (decimal)Math.Log(1 + (double)Math.Max(0, brandAffinity)) * 0.5m;
    }

    private static decimal GetAffinity(IReadOnlyDictionary<string, decimal> affinities, string? key) =>
        !string.IsNullOrWhiteSpace(key) && affinities.TryGetValue(key, out var score) ? score : 0m;

    private static void ApplyFeatures(
        UserProfileSnapshot profile,
        IReadOnlyList<PurchaseFeature> purchase,
        int direction)
    {
        foreach (var feature in purchase)
        {
            ApplyValue(profile.ProductAffinities, feature.Barcode, (decimal)feature.Quantity * direction);
            ApplyValue(profile.FavoritePurchaseCounts, feature.Barcode, direction);
            ApplyValue(profile.CategoryAffinities, feature.Category, (decimal)feature.Quantity * direction);
            ApplyValue(profile.SubcategoryAffinities, feature.Subcategory, (decimal)feature.Quantity * direction);
            ApplyValue(profile.BrandAffinities, feature.Brand, (decimal)feature.Quantity * direction);
        }
    }

    private static void ApplyValue(Dictionary<string, decimal> values, string? key, decimal delta)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var updated = values.GetValueOrDefault(key) + delta;
        if (updated <= 0)
        {
            values.Remove(key);
        }
        else
        {
            values[key] = updated;
        }
    }

    private static void ApplyValue(Dictionary<string, int> values, string? key, int delta)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var updated = values.GetValueOrDefault(key) + delta;
        if (updated <= 0)
        {
            values.Remove(key);
        }
        else
        {
            values[key] = updated;
        }
    }

    private static Dictionary<string, decimal> RebuildMap(Dictionary<string, decimal>? source) =>
        source is null
            ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, decimal>(source, StringComparer.OrdinalIgnoreCase);
}
