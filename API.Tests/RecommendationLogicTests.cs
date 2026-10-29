using MarketDixon.Api;
using Xunit;

namespace MarketDixon.Api.Tests;

public sealed class RecommendationLogicTests
{
    [Fact]
    public void ApplyPurchaseKeepsOnlyTheLatestThirtyPurchases()
    {
        var profile = new UserProfileSnapshot();
        var history = new Queue<PurchaseFeature[]>();

        for (var sequence = 1; sequence <= 31; sequence++)
        {
            var purchase = new[]
            {
                new PurchaseFeature($"item-{sequence}", "Pantry", "Grains", "Brand A", 2)
            };
            var removed = history.Count == RecommendationLogic.PurchaseWindowSize
                ? history.Dequeue()
                : null;
            RecommendationLogic.ApplyPurchase(profile, removed, purchase, sequence);
            history.Enqueue(purchase);
        }

        Assert.Equal(30, profile.RecentPurchaseCount);
        Assert.Equal(31, profile.LastPurchaseSequence);
        Assert.False(profile.ProductAffinities.ContainsKey("item-1"));
        Assert.Equal(2, profile.ProductAffinities["item-31"]);
        Assert.Equal(60, profile.CategoryAffinities["Pantry"]);
        Assert.Equal(1, profile.FavoritePurchaseCounts["item-31"]);
    }

    [Fact]
    public void ApplyPurchaseRemovesExpiredProductAndItsAffinity()
    {
        var profile = new UserProfileSnapshot();
        var oldPurchase = new[] { new PurchaseFeature("expired-item", "Produce", "Fruit", "Brand B", 1) };
        RecommendationLogic.ApplyPurchase(profile, null, oldPurchase, 1);
        for (var sequence = 2; sequence <= 30; sequence++)
        {
            RecommendationLogic.ApplyPurchase(
                profile,
                null,
                [new PurchaseFeature($"item-{sequence}", "Pantry", "Grains", "Brand A", 1)],
                sequence);
        }

        RecommendationLogic.ApplyPurchase(
            profile,
            oldPurchase,
            [new PurchaseFeature("new-item", "Dairy", "Milk", "Brand C", 1)],
            31);

        Assert.False(profile.FavoritePurchaseCounts.ContainsKey("expired-item"));
        Assert.False(profile.ProductAffinities.ContainsKey("expired-item"));
        Assert.Equal(1, profile.FavoritePurchaseCounts["new-item"]);
    }

    [Fact]
    public void ApplyPurchaseKeepsAllPurchasesWhenHistoryIsUnderThirty()
    {
        var profile = new UserProfileSnapshot();
        for (var sequence = 1; sequence <= 15; sequence++)
        {
            RecommendationLogic.ApplyPurchase(
                profile,
                null,
                [new PurchaseFeature($"item-{sequence}", "Pantry", "Grains", "Brand A", 1)],
                sequence);
        }

        Assert.Equal(15, profile.RecentPurchaseCount);
        Assert.Equal(15, profile.ProductAffinities.Count);
        Assert.Equal(15, profile.FavoritePurchaseCounts.Count);
        Assert.Equal(15, profile.LastPurchaseSequence);
    }

    [Fact]
    public void DifferentProfilesSelectDifferentDiscoveryProducts()
    {
        var firstProfile = new UserProfileSnapshot();
        var secondProfile = new UserProfileSnapshot();
        RecommendationLogic.ApplyPurchase(
            firstProfile,
            null,
            [new PurchaseFeature("fruit-history", "Produce", "Fruit", "Fresh", 1)],
            1);
        RecommendationLogic.ApplyPurchase(
            secondProfile,
            null,
            [new PurchaseFeature("dairy-history", "Dairy", "Milk", "Daily", 1)],
            2);
        var candidates = Enumerable.Range(1, 10)
            .Select(index => new RecommendationCandidate(
                Product(
                    index == 1 ? "fruit-offer" : index == 2 ? "dairy-offer" : $"other-{index}",
                    index == 1 ? "Produce" : index == 2 ? "Dairy" : $"Category {index}",
                    $"Subcategory {index}"),
                0,
                0))
            .ToArray();

        var firstOffers = RecommendationLogic.SelectOffers(firstProfile, candidates);
        var secondOffers = RecommendationLogic.SelectOffers(secondProfile, candidates);

        Assert.Equal("fruit-offer", firstOffers[0].Barcode);
        Assert.Equal("dairy-offer", secondOffers[0].Barcode);
        Assert.NotEqual(
            string.Join(",", firstOffers.Select(offer => offer.Barcode)),
            string.Join(",", secondOffers.Select(offer => offer.Barcode)));
        Assert.All(firstOffers.Concat(secondOffers), offer => Assert.Equal("discovery", offer.Kind));
    }

    [Fact]
    public void NewProfileCanReceiveDiscoveryOffersWithoutPurchaseHistory()
    {
        var offers = RecommendationLogic.SelectOffers(
            new UserProfileSnapshot(),
            Enumerable.Range(1, 10)
                .Select(index => new RecommendationCandidate(
                    Product($"popular-{index}", $"Category {index}", $"Subcategory {index}"),
                    0,
                    1))
                .ToArray());

        Assert.Equal(RecommendationLogic.DiscoveryOfferLimit, offers.Count);
        Assert.All(offers, offer => Assert.Equal("discovery", offer.Kind));
    }

    [Fact]
    public void RepeatedProductsAreSeparatedIntoFavoriteOffers()
    {
        var profile = new UserProfileSnapshot();
        var repeatedPurchase = new[] { new PurchaseFeature("favorite", "Dairy", "Milk", "Daily", 1) };
        RecommendationLogic.ApplyPurchase(profile, null, repeatedPurchase, 1);
        RecommendationLogic.ApplyPurchase(profile, null, repeatedPurchase, 2);

        var offers = RecommendationLogic.SelectOffers(
            profile,
            [
                new RecommendationCandidate(Product("favorite", "Dairy", "Milk"), 0, 1),
                new RecommendationCandidate(Product("new", "Produce", "Fruit"), 0, 1)
            ]);

        Assert.Contains(offers, offer => offer.Barcode == "favorite" && offer.Kind == "favorite");
        Assert.DoesNotContain(offers, offer => offer.Barcode == "favorite" && offer.Kind == "discovery");
        Assert.Contains(offers, offer => offer.Barcode == "new" && offer.Kind == "discovery");
    }

    [Fact]
    public void DiscountsStayWithinRangeAndPriceIsDeterministic()
    {
        var userId = Guid.NewGuid();
        var firstPercent = RecommendationLogic.GetDiscountPercent(userId, "barcode", "model-v1", "discovery");

        for (var index = 0; index < 10_000; index++)
        {
            var discount = RecommendationLogic.GetDiscountPercent(Guid.NewGuid(), $"item-{index}", "model-v1", "favorite");
            Assert.InRange(discount, 5, 20);
        }

        Assert.Equal(firstPercent, RecommendationLogic.GetDiscountPercent(userId, "barcode", "model-v1", "discovery"));
        Assert.Equal(100m * (100 - firstPercent) / 100m, RecommendationLogic.GetFinalPrice(100, firstPercent));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecommendationLogic.GetFinalPrice(100, 21));
    }

    private static Product Product(string barcode, string category, string subcategory) => new(
        barcode,
        barcode,
        "Brand",
        null,
        "Grocery",
        category,
        subcategory,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        100,
        false,
        10,
        null,
        null,
        "test",
        DateTime.UtcNow);
}
