using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MarketDixon.Api;

internal sealed class RecommendationDatabase(string connectionString)
{
    private const string ProductColumns = """
        p.[Barcode], p.[ProductName], p.[Brand], p.[PackageQuantity], p.[Area],
        p.[Category], p.[Subcategory], p.[EnergyKcalPer100g], p.[FatGramsPer100g],
        p.[SaturatedFatGramsPer100g], p.[CarbohydratesGramsPer100g],
        p.[SugarsGramsPer100g], p.[FiberGramsPer100g], p.[ProteinGramsPer100g],
        p.[SaltGramsPer100g], p.[PriceCRC], p.[PriceIsEstimated], p.[StockQuantity],
        p.[ImageUrl], p.[ProductUrl], p.[DataSource], p.[ImportedAtUtc]
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'[MarketDixonDBSchema].[Purchases]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[Purchases]
                (
                    [PurchaseSequence] bigint IDENTITY(1,1) NOT NULL
                        CONSTRAINT [PK_MarketDixonDBSchema_Purchases] PRIMARY KEY,
                    [PurchaseId] uniqueidentifier NOT NULL,
                    [UserId] uniqueidentifier NOT NULL,
                    [IdempotencyKey] uniqueidentifier NOT NULL,
                    [RequestHash] binary(32) NOT NULL,
                    [TotalCRC] decimal(18,2) NOT NULL,
                    [CreatedAtUtc] datetime2(0) NOT NULL
                        CONSTRAINT [DF_MarketDixonDBSchema_Purchases_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT [FK_MarketDixonDBSchema_Purchases_Users]
                        FOREIGN KEY ([UserId]) REFERENCES [MarketDixonDBSchema].[Users]([UserId]),
                    CONSTRAINT [UQ_MarketDixonDBSchema_Purchases_PurchaseId] UNIQUE ([PurchaseId]),
                    CONSTRAINT [UQ_MarketDixonDBSchema_Purchases_UserId_IdempotencyKey]
                        UNIQUE ([UserId], [IdempotencyKey])
                );

                CREATE INDEX [IX_MarketDixonDBSchema_Purchases_UserId_Sequence]
                    ON [MarketDixonDBSchema].[Purchases]([UserId], [PurchaseSequence] DESC);
            END;

            IF OBJECT_ID(N'[MarketDixonDBSchema].[PurchaseItems]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[PurchaseItems]
                (
                    [PurchaseSequence] bigint NOT NULL,
                    [Barcode] nvarchar(40) NOT NULL,
                    [ProductName] nvarchar(500) NOT NULL,
                    [Category] nvarchar(500) NULL,
                    [Subcategory] nvarchar(500) NULL,
                    [Brand] nvarchar(500) NULL,
                    [Quantity] int NOT NULL,
                    [UnitPriceCRC] decimal(18,2) NOT NULL,
                    [OfferId] uniqueidentifier NULL,
                    [DiscountPercent] int NULL,
                    CONSTRAINT [PK_MarketDixonDBSchema_PurchaseItems]
                        PRIMARY KEY ([PurchaseSequence], [Barcode]),
                    CONSTRAINT [FK_MarketDixonDBSchema_PurchaseItems_Purchases]
                        FOREIGN KEY ([PurchaseSequence]) REFERENCES [MarketDixonDBSchema].[Purchases]([PurchaseSequence])
                        ON DELETE CASCADE
                );

                CREATE INDEX [IX_MarketDixonDBSchema_PurchaseItems_Barcode]
                    ON [MarketDixonDBSchema].[PurchaseItems]([Barcode], [PurchaseSequence]);
            END;

            IF OBJECT_ID(N'[MarketDixonDBSchema].[UserRecommendationProfiles]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[UserRecommendationProfiles]
                (
                    [UserId] uniqueidentifier NOT NULL
                        CONSTRAINT [PK_MarketDixonDBSchema_UserRecommendationProfiles] PRIMARY KEY,
                    [ProfileJson] nvarchar(max) NOT NULL,
                    [RecentPurchaseCount] tinyint NOT NULL,
                    [LastPurchaseSequence] bigint NULL,
                    [UpdatedAtUtc] datetime2(0) NOT NULL
                        CONSTRAINT [DF_MarketDixonDBSchema_UserRecommendationProfiles_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT [FK_MarketDixonDBSchema_UserRecommendationProfiles_Users]
                        FOREIGN KEY ([UserId]) REFERENCES [MarketDixonDBSchema].[Users]([UserId])
                );
            END;

            IF OBJECT_ID(N'[MarketDixonDBSchema].[RecommendationOffers]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[RecommendationOffers]
                (
                    [OfferId] uniqueidentifier NOT NULL
                        CONSTRAINT [PK_MarketDixonDBSchema_RecommendationOffers] PRIMARY KEY,
                    [UserId] uniqueidentifier NOT NULL,
                    [Barcode] nvarchar(40) NOT NULL,
                    [Kind] nvarchar(20) NOT NULL,
                    [DiscountPercent] int NOT NULL,
                    [FinalPriceCRC] decimal(18,2) NOT NULL,
                    [ModelVersion] nvarchar(64) NOT NULL,
                    [IsActive] bit NOT NULL
                        CONSTRAINT [DF_MarketDixonDBSchema_RecommendationOffers_IsActive] DEFAULT 1,
                    [CreatedAtUtc] datetime2(0) NOT NULL
                        CONSTRAINT [DF_MarketDixonDBSchema_RecommendationOffers_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT [FK_MarketDixonDBSchema_RecommendationOffers_Users]
                        FOREIGN KEY ([UserId]) REFERENCES [MarketDixonDBSchema].[Users]([UserId]),
                    CONSTRAINT [CK_MarketDixonDBSchema_RecommendationOffers_DiscountPercent]
                        CHECK ([DiscountPercent] BETWEEN 5 AND 20),
                    CONSTRAINT [CK_MarketDixonDBSchema_RecommendationOffers_Kind]
                        CHECK ([Kind] IN (N'discovery', N'favorite'))
                );

                CREATE INDEX [IX_MarketDixonDBSchema_RecommendationOffers_UserId_Active]
                    ON [MarketDixonDBSchema].[RecommendationOffers]([UserId], [IsActive], [Kind]);
            END;

            IF OBJECT_ID(N'[MarketDixonDBSchema].[RecommendationModelStatus]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[RecommendationModelStatus]
                (
                    [ModelKey] tinyint NOT NULL
                        CONSTRAINT [PK_MarketDixonDBSchema_RecommendationModelStatus] PRIMARY KEY
                        CONSTRAINT [CK_MarketDixonDBSchema_RecommendationModelStatus_ModelKey] CHECK ([ModelKey] = 1),
                    [ModelVersion] nvarchar(64) NOT NULL,
                    [TrainedAtUtc] datetime2(0) NOT NULL,
                    [PurchaseCount] bigint NOT NULL,
                    [ProductCount] int NOT NULL
                );
            END;

            IF OBJECT_ID(N'[MarketDixonDBSchema].[RecommendationModelAssociations]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[RecommendationModelAssociations]
                (
                    [ModelVersion] nvarchar(64) NOT NULL,
                    [SourceBarcode] nvarchar(40) NOT NULL,
                    [RecommendedBarcode] nvarchar(40) NOT NULL,
                    [Score] float NOT NULL,
                    [CoPurchaseCount] bigint NOT NULL,
                    CONSTRAINT [PK_MarketDixonDBSchema_RecommendationModelAssociations]
                        PRIMARY KEY ([ModelVersion], [SourceBarcode], [RecommendedBarcode])
                );

                CREATE INDEX [IX_MarketDixonDBSchema_RecommendationModelAssociations_Recommended]
                    ON [MarketDixonDBSchema].[RecommendationModelAssociations]
                        ([ModelVersion], [RecommendedBarcode]) INCLUDE ([Score]);
            END;

            IF OBJECT_ID(N'[MarketDixonDBSchema].[RecommendationModelPopularity]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[RecommendationModelPopularity]
                (
                    [ModelVersion] nvarchar(64) NOT NULL,
                    [Barcode] nvarchar(40) NOT NULL,
                    [PurchaseCount] bigint NOT NULL,
                    [Score] float NOT NULL,
                    CONSTRAINT [PK_MarketDixonDBSchema_RecommendationModelPopularity]
                        PRIMARY KEY ([ModelVersion], [Barcode])
                );
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CheckoutResponse> CheckoutAsync(
        Guid userId,
        CheckoutRequest request,
        CancellationToken cancellationToken)
    {
        if (request.IdempotencyKey == Guid.Empty ||
            request.Items is not { Count: > 0 and <= 50 } ||
            request.Items.Any(item => string.IsNullOrWhiteSpace(item.Barcode) || item.Barcode.Length > 40 ||
                                      item.Quantity is < 1 or > 99) ||
            request.Items.Select(item => item.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Items.Count)
        {
            throw new InvalidCheckoutException("A unique idempotency key and 1-50 distinct cart items with quantities from 1 to 99 are required.");
        }

        var requestHash = HashRequest(request.Items);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var previousCheckout = await FindIdempotentCheckoutAsync(
            connection,
            transaction,
            userId,
            request.IdempotencyKey,
            cancellationToken);
        if (previousCheckout is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(previousCheckout.Value.RequestHash, requestHash))
            {
                throw new IdempotencyConflictException("This idempotency key has already been used for a different cart.");
            }

            await transaction.CommitAsync(cancellationToken);
            return previousCheckout.Value.Response;
        }

        var items = new List<PreparedPurchaseItem>(request.Items.Count);
        decimal total = 0;
        foreach (var item in request.Items.OrderBy(item => item.Barcode, StringComparer.Ordinal))
        {
            var preparedItem = await PreparePurchaseItemAsync(
                connection,
                transaction,
                userId,
                item,
                cancellationToken);
            items.Add(preparedItem);
            total += preparedItem.UnitPriceCRC * item.Quantity;
        }

        var evictedPurchase = await FindEvictedPurchaseAsync(connection, transaction, userId, cancellationToken);
        var removedFeatures = evictedPurchase is null
            ? null
            : await GetPurchaseFeaturesAsync(connection, transaction, evictedPurchase.Value, cancellationToken);

        var purchaseId = Guid.NewGuid();
        long purchaseSequence;
        await using (var command = new SqlCommand("""
            INSERT INTO [MarketDixonDBSchema].[Purchases]
                ([PurchaseId], [UserId], [IdempotencyKey], [RequestHash], [TotalCRC])
            OUTPUT INSERTED.[PurchaseSequence]
            VALUES
                (@PurchaseId, @UserId, @IdempotencyKey, @RequestHash, @TotalCRC);
            """, connection, transaction))
        {
            command.Parameters.Add("@PurchaseId", SqlDbType.UniqueIdentifier).Value = purchaseId;
            command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
            command.Parameters.Add("@IdempotencyKey", SqlDbType.UniqueIdentifier).Value = request.IdempotencyKey;
            command.Parameters.Add("@RequestHash", SqlDbType.Binary, 32).Value = requestHash;
            command.Parameters.Add("@TotalCRC", SqlDbType.Decimal).Value = total;
            command.Parameters["@TotalCRC"].Precision = 18;
            command.Parameters["@TotalCRC"].Scale = 2;
            purchaseSequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        foreach (var item in items)
        {
            await using var command = new SqlCommand("""
                INSERT INTO [MarketDixonDBSchema].[PurchaseItems]
                    ([PurchaseSequence], [Barcode], [ProductName], [Category], [Subcategory], [Brand],
                     [Quantity], [UnitPriceCRC], [OfferId], [DiscountPercent])
                VALUES
                    (@PurchaseSequence, @Barcode, @ProductName, @Category, @Subcategory, @Brand,
                     @Quantity, @UnitPriceCRC, @OfferId, @DiscountPercent);
                """, connection, transaction);
            command.Parameters.Add("@PurchaseSequence", SqlDbType.BigInt).Value = purchaseSequence;
            command.Parameters.Add("@Barcode", SqlDbType.NVarChar, 40).Value = item.Barcode;
            command.Parameters.Add("@ProductName", SqlDbType.NVarChar, 500).Value = item.ProductName;
            AddNullableString(command, "@Category", 500, item.Category);
            AddNullableString(command, "@Subcategory", 500, item.Subcategory);
            AddNullableString(command, "@Brand", 500, item.Brand);
            command.Parameters.Add("@Quantity", SqlDbType.Int).Value = item.Quantity;
            AddDecimal(command, "@UnitPriceCRC", item.UnitPriceCRC);
            command.Parameters.Add("@OfferId", SqlDbType.UniqueIdentifier).Value = item.OfferId is null
                ? DBNull.Value
                : item.OfferId.Value;
            command.Parameters.Add("@DiscountPercent", SqlDbType.Int).Value = item.DiscountPercent is null
                ? DBNull.Value
                : item.DiscountPercent.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var profile = await LoadProfileAsync(connection, transaction, userId, cancellationToken);
        var addedFeatures = items.Select(item => new PurchaseFeature(
            item.Barcode,
            item.Category,
            item.Subcategory,
            item.Brand,
            item.Quantity)).ToArray();
        RecommendationLogic.ApplyPurchase(profile, removedFeatures, addedFeatures, purchaseSequence);
        await SaveProfileAsync(connection, transaction, userId, profile, cancellationToken);

        await using (var command = new SqlCommand("""
            UPDATE [MarketDixonDBSchema].[RecommendationOffers]
            SET [IsActive] = 0
            WHERE [UserId] = @UserId AND [IsActive] = 1;
            """, connection, transaction))
        {
            command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CheckoutResponse(true, "Mock payment approved.", purchaseId, purchaseSequence, total);
    }

    public async Task<IReadOnlyList<RecommendationOffer>> GetOffersAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var modelVersion = await GetModelVersionAsync(connection, transaction, cancellationToken);
        var activeOffers = await ReadOffersAsync(connection, transaction, userId, modelVersion, cancellationToken);
        if (activeOffers.Count > 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return activeOffers;
        }

        await using (var command = new SqlCommand("""
            UPDATE [MarketDixonDBSchema].[RecommendationOffers]
            SET [IsActive] = 0
            WHERE [UserId] = @UserId AND [IsActive] = 1;
            """, connection, transaction))
        {
            command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var profile = await LoadProfileAsync(connection, transaction, userId, cancellationToken);
        var candidates = await GetCandidatesAsync(
            connection,
            transaction,
            RecommendationLogic.SerializeProfile(profile),
            modelVersion,
            cancellationToken);
        var selections = RecommendationLogic.SelectOffers(profile, candidates);
        var result = new List<RecommendationOffer>(selections.Count);
        foreach (var selection in selections)
        {
            var candidate = candidates.First(candidate =>
                StringComparer.OrdinalIgnoreCase.Equals(candidate.Product.Barcode, selection.Barcode));
            var discount = RecommendationLogic.GetDiscountPercent(
                userId,
                selection.Barcode,
                modelVersion,
                selection.Kind);
            var finalPrice = RecommendationLogic.GetFinalPrice(candidate.Product.PriceCRC, discount);
            var offerId = Guid.NewGuid();
            await using var command = new SqlCommand("""
                INSERT INTO [MarketDixonDBSchema].[RecommendationOffers]
                    ([OfferId], [UserId], [Barcode], [Kind], [DiscountPercent], [FinalPriceCRC], [ModelVersion])
                VALUES
                    (@OfferId, @UserId, @Barcode, @Kind, @DiscountPercent, @FinalPriceCRC, @ModelVersion);
                """, connection, transaction);
            command.Parameters.Add("@OfferId", SqlDbType.UniqueIdentifier).Value = offerId;
            command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
            command.Parameters.Add("@Barcode", SqlDbType.NVarChar, 40).Value = selection.Barcode;
            command.Parameters.Add("@Kind", SqlDbType.NVarChar, 20).Value = selection.Kind;
            command.Parameters.Add("@DiscountPercent", SqlDbType.Int).Value = discount;
            AddDecimal(command, "@FinalPriceCRC", finalPrice);
            command.Parameters.Add("@ModelVersion", SqlDbType.NVarChar, 64).Value = modelVersion;
            await command.ExecuteNonQueryAsync(cancellationToken);
            result.Add(new RecommendationOffer(
                offerId,
                selection.Kind,
                candidate.Product,
                discount,
                finalPrice,
                modelVersion));
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<RecommendationProfileResponse> GetProfileAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var profile = await LoadProfileAsync(connection, null, userId, cancellationToken);
        return new RecommendationProfileResponse(
            profile.RecentPurchaseCount,
            profile.LastPurchaseSequence,
            profile.ProductAffinities,
            profile.CategoryAffinities,
            profile.SubcategoryAffinities,
            profile.BrandAffinities,
            profile.FavoritePurchaseCounts);
    }

    private static byte[] HashRequest(IReadOnlyList<CheckoutItemRequest> items)
    {
        var canonical = JsonSerializer.Serialize(items
            .OrderBy(item => item.Barcode, StringComparer.Ordinal)
            .Select(item => new { barcode = item.Barcode!.Trim(), item.Quantity, item.OfferId }));
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private async Task<(CheckoutResponse Response, byte[] RequestHash)?> FindIdempotentCheckoutAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid userId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT [PurchaseId], [PurchaseSequence], [RequestHash], [TotalCRC]
            FROM [MarketDixonDBSchema].[Purchases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [UserId] = @UserId AND [IdempotencyKey] = @IdempotencyKey;
            """, connection, transaction);
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@IdempotencyKey", SqlDbType.UniqueIdentifier).Value = idempotencyKey;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var response = new CheckoutResponse(
            true,
            "Mock payment approved.",
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetDecimal(3));
        return (response, (byte[])reader[2]);
    }

    private static async Task<long?> FindEvictedPurchaseAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT [PurchaseSequence]
            FROM [MarketDixonDBSchema].[Purchases] WITH (UPDLOCK, HOLDLOCK)
            WHERE [UserId] = @UserId
            ORDER BY [PurchaseSequence] DESC
            OFFSET 29 ROWS FETCH NEXT 1 ROWS ONLY;
            """, connection, transaction);
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task<IReadOnlyList<PurchaseFeature>> GetPurchaseFeaturesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long purchaseSequence,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT [Barcode], [Category], [Subcategory], [Brand], [Quantity]
            FROM [MarketDixonDBSchema].[PurchaseItems]
            WHERE [PurchaseSequence] = @PurchaseSequence;
            """, connection, transaction);
        command.Parameters.Add("@PurchaseSequence", SqlDbType.BigInt).Value = purchaseSequence;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PurchaseFeature>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PurchaseFeature(
                reader.GetString(0),
                ReadNullableString(reader, 1),
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                reader.GetInt32(4)));
        }

        return items;
    }

    private async Task<PreparedPurchaseItem> PreparePurchaseItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid userId,
        CheckoutItemRequest item,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT p.[Barcode], p.[ProductName], p.[Category], p.[Subcategory], p.[Brand], p.[PriceCRC],
                   o.[OfferId], o.[DiscountPercent], o.[FinalPriceCRC]
            FROM [MarketDixonDBSchema].[Products] AS p
            LEFT JOIN [MarketDixonDBSchema].[RecommendationOffers] AS o
                ON o.[OfferId] = @OfferId
               AND o.[UserId] = @UserId
               AND o.[Barcode] = p.[Barcode]
               AND o.[IsActive] = 1
            WHERE p.[Barcode] = @Barcode;
            """, connection, transaction);
        command.Parameters.Add("@OfferId", SqlDbType.UniqueIdentifier).Value = item.OfferId is null
            ? DBNull.Value
            : item.OfferId.Value;
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@Barcode", SqlDbType.NVarChar, 40).Value = item.Barcode!.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidCheckoutException($"Product '{item.Barcode}' is not in the catalog.");
        }

        if (item.OfferId is not null && reader.IsDBNull(6))
        {
            throw new InvalidCheckoutException($"The offer for product '{item.Barcode}' is no longer active.");
        }

        var hasOffer = item.OfferId is not null;
        return new PreparedPurchaseItem(
            reader.GetString(0),
            reader.GetString(1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            hasOffer ? reader.GetDecimal(8) : reader.GetDecimal(5),
            item.Quantity,
            hasOffer ? item.OfferId : null,
            hasOffer ? reader.GetInt32(7) : null);
    }

    private async Task<IReadOnlyList<RecommendationOffer>> ReadOffersAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid userId,
        string modelVersion,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT o.[OfferId], o.[Kind], o.[DiscountPercent], o.[FinalPriceCRC], o.[ModelVersion],
                   {ProductColumns}
            FROM [MarketDixonDBSchema].[RecommendationOffers] AS o
            INNER JOIN [MarketDixonDBSchema].[Products] AS p ON p.[Barcode] = o.[Barcode]
            WHERE o.[UserId] = @UserId AND o.[IsActive] = 1 AND o.[ModelVersion] = @ModelVersion
              AND p.[StockQuantity] > 0
            ORDER BY CASE o.[Kind] WHEN N'discovery' THEN 0 ELSE 1 END, o.[CreatedAtUtc], p.[ProductName];
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@ModelVersion", SqlDbType.NVarChar, 64).Value = modelVersion;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var offers = new List<RecommendationOffer>();
        while (await reader.ReadAsync(cancellationToken))
        {
            offers.Add(new RecommendationOffer(
                reader.GetGuid(0),
                reader.GetString(1),
                ProductDatabase.ReadProduct(reader, 5),
                reader.GetInt32(2),
                reader.GetDecimal(3),
                reader.GetString(4)));
        }

        return offers;
    }

    private async Task<IReadOnlyList<RecommendationCandidate>> GetCandidatesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string profileJson,
        string modelVersion,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RecommendationCandidate>();
        var sql = $"""
            WITH RecentProducts AS
            (
                SELECT [key] COLLATE DATABASE_DEFAULT AS [Barcode]
                FROM OPENJSON(@ProfileJson, '$.productAffinities')
            ),
            FavoriteProducts AS
            (
                SELECT [key] COLLATE DATABASE_DEFAULT AS [Barcode]
                FROM OPENJSON(@ProfileJson, '$.favoritePurchaseCounts')
                WHERE TRY_CONVERT(int, [value]) >= 2
            ),
            AssociationScores AS
            (
                SELECT association.[RecommendedBarcode], SUM(association.[Score]) AS [Score]
                FROM [MarketDixonDBSchema].[RecommendationModelAssociations] AS association
                INNER JOIN RecentProducts AS recent ON recent.[Barcode] = association.[SourceBarcode]
                WHERE association.[ModelVersion] = @ModelVersion
                GROUP BY association.[RecommendedBarcode]
            )
            SELECT TOP (500) {ProductColumns},
                   COALESCE(association.[Score], 0), COALESCE(popularity.[Score], 0)
            FROM [MarketDixonDBSchema].[Products] AS p
            LEFT JOIN AssociationScores AS association ON association.[RecommendedBarcode] = p.[Barcode]
            LEFT JOIN [MarketDixonDBSchema].[RecommendationModelPopularity] AS popularity
                ON popularity.[Barcode] = p.[Barcode] AND popularity.[ModelVersion] = @ModelVersion
            WHERE p.[StockQuantity] > 0
              AND (association.[RecommendedBarcode] IS NOT NULL
                   OR popularity.[Barcode] IS NOT NULL
                   OR p.[Barcode] IN (SELECT [Barcode] FROM FavoriteProducts))
            ORDER BY association.[Score] DESC, popularity.[Score] DESC, p.[ProductName], p.[Barcode];
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ProfileJson", SqlDbType.NVarChar, -1).Value = profileJson;
        command.Parameters.Add("@ModelVersion", SqlDbType.NVarChar, 64).Value = modelVersion;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new RecommendationCandidate(
                    ProductDatabase.ReadProduct(reader),
                    Convert.ToDecimal(reader.GetDouble(22)),
                    Convert.ToDecimal(reader.GetDouble(23))));
            }
        }

        var knownBarcodes = candidates
            .Select(candidate => candidate.Product.Barcode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallbackSql = $"""
            WITH RecentProducts AS
            (
                SELECT [key] COLLATE DATABASE_DEFAULT AS [Barcode]
                FROM OPENJSON(@ProfileJson, '$.productAffinities')
            ),
            ProductPopularity AS
            (
                SELECT purchases.[Barcode], COUNT_BIG(*) AS [PurchaseCount]
                FROM
                (
                    SELECT DISTINCT items.[PurchaseSequence], items.[Barcode]
                    FROM [MarketDixonDBSchema].[PurchaseItems] AS items
                ) AS purchases
                GROUP BY purchases.[Barcode]
            )
            SELECT TOP (100) {ProductColumns},
                   CAST(0 AS float), CAST(COALESCE(popularity.[PurchaseCount], 0) AS float)
            FROM [MarketDixonDBSchema].[Products] AS p
            LEFT JOIN ProductPopularity AS popularity ON popularity.[Barcode] = p.[Barcode]
            WHERE p.[StockQuantity] > 0
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM RecentProducts AS recent
                  WHERE recent.[Barcode] = p.[Barcode]
              )
            ORDER BY popularity.[PurchaseCount] DESC, p.[ProductName], p.[Barcode];
            """;
        await using var fallback = new SqlCommand(fallbackSql, connection, transaction);
        fallback.Parameters.Add("@ProfileJson", SqlDbType.NVarChar, -1).Value = profileJson;
        await using var fallbackReader = await fallback.ExecuteReaderAsync(cancellationToken);
        while (await fallbackReader.ReadAsync(cancellationToken))
        {
            var candidate = new RecommendationCandidate(
                ProductDatabase.ReadProduct(fallbackReader),
                0,
                Convert.ToDecimal(fallbackReader.GetDouble(23)));
            if (knownBarcodes.Add(candidate.Product.Barcode))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static async Task<string> GetModelVersionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT [ModelVersion]
            FROM [MarketDixonDBSchema].[RecommendationModelStatus]
            WHERE [ModelKey] = 1;
            """, connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? "bootstrap-v1" : (string)value;
    }

    private static async Task<UserProfileSnapshot> LoadProfileAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT [ProfileJson]
            FROM [MarketDixonDBSchema].[UserRecommendationProfiles]
            WHERE [UserId] = @UserId;
            """, connection, transaction);
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return RecommendationLogic.DeserializeProfile(value as string);
    }

    private static async Task SaveProfileAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid userId,
        UserProfileSnapshot profile,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            UPDATE [MarketDixonDBSchema].[UserRecommendationProfiles]
            SET [ProfileJson] = @ProfileJson,
                [RecentPurchaseCount] = @RecentPurchaseCount,
                [LastPurchaseSequence] = @LastPurchaseSequence,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [UserId] = @UserId;

            IF @@ROWCOUNT = 0
                INSERT INTO [MarketDixonDBSchema].[UserRecommendationProfiles]
                    ([UserId], [ProfileJson], [RecentPurchaseCount], [LastPurchaseSequence])
                VALUES
                    (@UserId, @ProfileJson, @RecentPurchaseCount, @LastPurchaseSequence);
            """, connection, transaction);
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@ProfileJson", SqlDbType.NVarChar, -1).Value =
            RecommendationLogic.SerializeProfile(profile);
        command.Parameters.Add("@RecentPurchaseCount", SqlDbType.TinyInt).Value = profile.RecentPurchaseCount;
        command.Parameters.Add("@LastPurchaseSequence", SqlDbType.BigInt).Value =
            profile.LastPurchaseSequence is null ? DBNull.Value : profile.LastPurchaseSequence.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullableString(SqlCommand command, string name, int size, string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = (object?)value ?? DBNull.Value;

    private static void AddDecimal(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 2;
        parameter.Value = value;
    }

    private static string? ReadNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record PreparedPurchaseItem(
        string Barcode,
        string ProductName,
        string? Category,
        string? Subcategory,
        string? Brand,
        decimal UnitPriceCRC,
        int Quantity,
        Guid? OfferId,
        int? DiscountPercent);
}

internal sealed class InvalidCheckoutException(string message) : Exception(message);
internal sealed class IdempotencyConflictException(string message) : Exception(message);
