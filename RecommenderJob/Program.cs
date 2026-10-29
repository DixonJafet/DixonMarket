using System.Data;
using Microsoft.Data.SqlClient;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__MarketDixonDb")
    ?? Environment.GetEnvironmentVariable("MARKETDIXON_DB_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Set ConnectionStrings__MarketDixonDb before running the recommendation model job.");
}

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

var purchaseCount = Convert.ToInt64(await new SqlCommand("""
    SELECT COUNT_BIG(*)
    FROM [MarketDixonDBSchema].[Purchases];
    """, connection).ExecuteScalarAsync());
if (purchaseCount == 0)
{
    throw new InvalidOperationException("The recommendation model cannot be trained until at least one checkout is recorded.");
}

var popularity = new List<ModelPopularity>();
await using (var command = new SqlCommand("""
    SELECT [Barcode], COUNT_BIG(*) AS [PurchaseCount]
    FROM
    (
        SELECT DISTINCT purchases.[PurchaseSequence], items.[Barcode]
        FROM [MarketDixonDBSchema].[Purchases] AS purchases
        INNER JOIN [MarketDixonDBSchema].[PurchaseItems] AS items
            ON items.[PurchaseSequence] = purchases.[PurchaseSequence]
    ) AS purchases
    GROUP BY [Barcode]
    ORDER BY COUNT_BIG(*) DESC, [Barcode];
    """, connection))
await using (var reader = await command.ExecuteReaderAsync())
{
    while (await reader.ReadAsync())
    {
        popularity.Add(new ModelPopularity(reader.GetString(0), reader.GetInt64(1)));
    }
}

var associations = new List<ModelAssociation>();
await using (var command = new SqlCommand("""
    WITH BasketProducts AS
    (
        SELECT [PurchaseSequence], [Barcode]
        FROM [MarketDixonDBSchema].[PurchaseItems]
        GROUP BY [PurchaseSequence], [Barcode]
    ),
    ProductPurchaseCounts AS
    (
        SELECT [Barcode], COUNT_BIG(*) AS [PurchaseCount]
        FROM BasketProducts
        GROUP BY [Barcode]
    ),
    PairCounts AS
    (
        SELECT source.[Barcode] AS [SourceBarcode],
               target.[Barcode] AS [RecommendedBarcode],
               COUNT_BIG(*) AS [CoPurchaseCount]
        FROM BasketProducts AS source
        INNER JOIN BasketProducts AS target
            ON target.[PurchaseSequence] = source.[PurchaseSequence]
           AND target.[Barcode] <> source.[Barcode]
        GROUP BY source.[Barcode], target.[Barcode]
    ),
    ScoredPairs AS
    (
        SELECT pairs.[SourceBarcode], pairs.[RecommendedBarcode], pairs.[CoPurchaseCount],
               CAST(pairs.[CoPurchaseCount] AS float) /
                   NULLIF(SQRT(CAST(sourceCount.[PurchaseCount] AS float) *
                               CAST(targetCount.[PurchaseCount] AS float)), 0) AS [Score],
               ROW_NUMBER() OVER
               (
                   PARTITION BY pairs.[SourceBarcode]
                   ORDER BY pairs.[CoPurchaseCount] DESC, pairs.[RecommendedBarcode]
               ) AS [Rank]
        FROM PairCounts AS pairs
        INNER JOIN ProductPurchaseCounts AS sourceCount
            ON sourceCount.[Barcode] = pairs.[SourceBarcode]
        INNER JOIN ProductPurchaseCounts AS targetCount
            ON targetCount.[Barcode] = pairs.[RecommendedBarcode]
    )
    SELECT [SourceBarcode], [RecommendedBarcode], [CoPurchaseCount], [Score]
    FROM ScoredPairs
    WHERE [Rank] <= 100
    ORDER BY [SourceBarcode], [Rank];
    """, connection))
await using (var reader = await command.ExecuteReaderAsync())
{
    while (await reader.ReadAsync())
    {
        associations.Add(new ModelAssociation(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetDouble(3)));
    }
}

var modelVersion = Guid.NewGuid().ToString("N");
await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
await using (var command = new SqlCommand("""
    DELETE FROM [MarketDixonDBSchema].[RecommendationModelAssociations];
    DELETE FROM [MarketDixonDBSchema].[RecommendationModelPopularity];

    DELETE FROM [MarketDixonDBSchema].[RecommendationModelStatus]
    WHERE [ModelKey] = 1;
    """, connection, transaction))
{
    await command.ExecuteNonQueryAsync();
}

foreach (var row in popularity)
{
    await using var command = new SqlCommand("""
        INSERT INTO [MarketDixonDBSchema].[RecommendationModelPopularity]
            ([ModelVersion], [Barcode], [PurchaseCount], [Score])
        VALUES (@ModelVersion, @Barcode, @PurchaseCount, @Score);
        """, connection, transaction);
    command.Parameters.Add("@ModelVersion", SqlDbType.NVarChar, 64).Value = modelVersion;
    command.Parameters.Add("@Barcode", SqlDbType.NVarChar, 40).Value = row.Barcode;
    command.Parameters.Add("@PurchaseCount", SqlDbType.BigInt).Value = row.PurchaseCount;
    command.Parameters.Add("@Score", SqlDbType.Float).Value = row.PurchaseCount;
    await command.ExecuteNonQueryAsync();
}

foreach (var row in associations)
{
    await using var command = new SqlCommand("""
        INSERT INTO [MarketDixonDBSchema].[RecommendationModelAssociations]
            ([ModelVersion], [SourceBarcode], [RecommendedBarcode], [Score], [CoPurchaseCount])
        VALUES (@ModelVersion, @SourceBarcode, @RecommendedBarcode, @Score, @CoPurchaseCount);
        """, connection, transaction);
    command.Parameters.Add("@ModelVersion", SqlDbType.NVarChar, 64).Value = modelVersion;
    command.Parameters.Add("@SourceBarcode", SqlDbType.NVarChar, 40).Value = row.SourceBarcode;
    command.Parameters.Add("@RecommendedBarcode", SqlDbType.NVarChar, 40).Value = row.RecommendedBarcode;
    command.Parameters.Add("@Score", SqlDbType.Float).Value = row.Score;
    command.Parameters.Add("@CoPurchaseCount", SqlDbType.BigInt).Value = row.CoPurchaseCount;
    await command.ExecuteNonQueryAsync();
}

await using (var command = new SqlCommand("""
    INSERT INTO [MarketDixonDBSchema].[RecommendationModelStatus]
        ([ModelKey], [ModelVersion], [TrainedAtUtc], [PurchaseCount], [ProductCount])
    VALUES (1, @ModelVersion, SYSUTCDATETIME(), @PurchaseCount, @ProductCount);
    """, connection, transaction))
{
    command.Parameters.Add("@ModelVersion", SqlDbType.NVarChar, 64).Value = modelVersion;
    command.Parameters.Add("@PurchaseCount", SqlDbType.BigInt).Value = purchaseCount;
    command.Parameters.Add("@ProductCount", SqlDbType.Int).Value = popularity.Count;
    await command.ExecuteNonQueryAsync();
}

await transaction.CommitAsync();
Console.WriteLine(
    $"Trained model {modelVersion}: {purchaseCount} purchases, {popularity.Count} products, {associations.Count} directed product relationships.");

internal sealed record ModelPopularity(string Barcode, long PurchaseCount);
internal sealed record ModelAssociation(string SourceBarcode, string RecommendedBarcode, long CoPurchaseCount, double Score);
