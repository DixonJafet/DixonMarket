using Microsoft.Data.SqlClient;

namespace MarketDixon.Api;

internal static class DatabaseConfiguration
{
    public static string GetConnectionString(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration.GetConnectionString("MarketDixonDb")
            ?? Environment.GetEnvironmentVariable("MARKETDIXON_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (environment.IsDevelopment())
        {
            var variablesPath = FindVariablesFile();
            if (variablesPath is not null)
            {
                var entry = File.ReadLines(variablesPath)
                    .FirstOrDefault(line => line.TrimStart().StartsWith("SecondConnectionString", StringComparison.OrdinalIgnoreCase) &&
                                            line.Contains(':'));
                if (entry is not null)
                {
                    var separator = entry.IndexOf(':');
                    var connectionString = entry[(separator + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(connectionString))
                    {
                        return connectionString;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            "Configure ConnectionStrings:MarketDixonDb or MARKETDIXON_DB_CONNECTION before starting the API.");
    }

    private static string? FindVariablesFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "variables.txt");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

internal sealed class ProductDatabase(string connectionString)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF SCHEMA_ID(N'MarketDixonDBSchema') IS NULL
                EXEC(N'CREATE SCHEMA [MarketDixonDBSchema]');

            IF OBJECT_ID(N'[MarketDixonDBSchema].[Users]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[Users]
                (
                    [UserId] uniqueidentifier NOT NULL
                        CONSTRAINT [PK_MarketDixonDBSchema_Users] PRIMARY KEY,
                    [UserName] nvarchar(100) NOT NULL,
                    [NormalizedUserName] nvarchar(100) NOT NULL,
                    [PasswordHash] nvarchar(256) NOT NULL,
                    [CreatedAtUtc] datetime2(0) NOT NULL
                        CONSTRAINT [DF_MarketDixonDBSchema_Users_CreatedAtUtc] DEFAULT SYSUTCDATETIME()
                );

                CREATE UNIQUE INDEX [UX_MarketDixonDBSchema_Users_NormalizedUserName]
                    ON [MarketDixonDBSchema].[Users]([NormalizedUserName]);
            END;

            IF OBJECT_ID(N'[MarketDixonDBSchema].[RevokedTokens]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MarketDixonDBSchema].[RevokedTokens]
                (
                    [TokenHash] char(64) NOT NULL
                        CONSTRAINT [PK_MarketDixonDBSchema_RevokedTokens] PRIMARY KEY,
                    [ExpiresAtUtc] datetime2(0) NOT NULL
                );
            END;

            DELETE FROM [MarketDixonDBSchema].[RevokedTokens]
            WHERE [ExpiresAtUtc] <= SYSUTCDATETIME();
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsTokenRevokedAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM [MarketDixonDBSchema].[RevokedTokens]
                WHERE [TokenHash] = @TokenHash
                  AND [ExpiresAtUtc] > SYSUTCDATETIME()
            ) THEN 1 ELSE 0 END;
            """;
        command.Parameters.Add("@TokenHash", System.Data.SqlDbType.Char, 64).Value = tokenHash;
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
    }

    public async Task RevokeTokenAsync(
        string tokenHash,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM [MarketDixonDBSchema].[RevokedTokens] WITH (UPDLOCK, HOLDLOCK)
                WHERE [TokenHash] = @TokenHash
            )
            BEGIN
                INSERT INTO [MarketDixonDBSchema].[RevokedTokens] ([TokenHash], [ExpiresAtUtc])
                VALUES (@TokenHash, @ExpiresAtUtc);
            END;
            """;
        command.Parameters.Add("@TokenHash", System.Data.SqlDbType.Char, 64).Value = tokenHash;
        command.Parameters.Add("@ExpiresAtUtc", System.Data.SqlDbType.DateTime2).Value = expiresAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateUserAsync(
        Guid userId,
        string userName,
        string normalizedUserName,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [MarketDixonDBSchema].[Users]
                ([UserId], [UserName], [NormalizedUserName], [PasswordHash])
            VALUES
                (@UserId, @UserName, @NormalizedUserName, @PasswordHash);
            """;
        command.Parameters.Add("@UserId", System.Data.SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@UserName", System.Data.SqlDbType.NVarChar, 100).Value = userName;
        command.Parameters.Add("@NormalizedUserName", System.Data.SqlDbType.NVarChar, 100).Value = normalizedUserName;
        command.Parameters.Add("@PasswordHash", System.Data.SqlDbType.NVarChar, 256).Value = passwordHash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<UserRecord?> FindUserAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [UserId], [UserName], [PasswordHash]
            FROM [MarketDixonDBSchema].[Users]
            WHERE [NormalizedUserName] = @NormalizedUserName;
            """;
        command.Parameters.Add("@NormalizedUserName", System.Data.SqlDbType.NVarChar, 100).Value = normalizedUserName;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new UserRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<UserRecord?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [UserId], [UserName], [PasswordHash]
            FROM [MarketDixonDBSchema].[Users]
            WHERE [UserId] = @UserId;
            """;
        command.Parameters.Add("@UserId", System.Data.SqlDbType.UniqueIdentifier).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new UserRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<ProductPage> GetProductsAsync(
        int page,
        int pageSize,
        string? search,
        string? area,
        string? category,
        string? subcategory,
        CancellationToken cancellationToken)
    {
        var filters = new List<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var countCommand = connection.CreateCommand();
        AddProductFilters(countCommand, filters, search, area, category, subcategory);
        var where = filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters) + " ";
        countCommand.CommandText = "SELECT COUNT_BIG(*) FROM [MarketDixonDBSchema].[Products]" + where + ";";
        var total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using var command = connection.CreateCommand();
        AddProductFilters(command, filters = [], search, area, category, subcategory);
        command.CommandText = """
            SELECT [Barcode], [ProductName], [Brand], [PackageQuantity], [Area],
                   [Category], [Subcategory], [EnergyKcalPer100g], [FatGramsPer100g],
                   [SaturatedFatGramsPer100g], [CarbohydratesGramsPer100g],
                   [SugarsGramsPer100g], [FiberGramsPer100g], [ProteinGramsPer100g],
                   [SaltGramsPer100g], [PriceCRC], [PriceIsEstimated], [StockQuantity],
                   [ImageUrl], [ProductUrl], [DataSource], [ImportedAtUtc]
            FROM [MarketDixonDBSchema].[Products]
            """ + where + """
            ORDER BY [ProductName], [Barcode]
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.Parameters.Add("@Offset", System.Data.SqlDbType.BigInt).Value = (long)(page - 1) * pageSize;
        command.Parameters.Add("@PageSize", System.Data.SqlDbType.Int).Value = pageSize;
        var products = new List<Product>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(ReadProduct(reader));
        }

        return new ProductPage(products, page, pageSize, total);
    }

    public async Task<Product?> GetProductAsync(string barcode, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [Barcode], [ProductName], [Brand], [PackageQuantity], [Area],
                   [Category], [Subcategory], [EnergyKcalPer100g], [FatGramsPer100g],
                   [SaturatedFatGramsPer100g], [CarbohydratesGramsPer100g],
                   [SugarsGramsPer100g], [FiberGramsPer100g], [ProteinGramsPer100g],
                   [SaltGramsPer100g], [PriceCRC], [PriceIsEstimated], [StockQuantity],
                   [ImageUrl], [ProductUrl], [DataSource], [ImportedAtUtc]
            FROM [MarketDixonDBSchema].[Products]
            WHERE [Barcode] = @Barcode;
            """;
        command.Parameters.Add("@Barcode", System.Data.SqlDbType.NVarChar, 40).Value = barcode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProduct(reader) : null;
    }

    public async Task<IReadOnlyList<CategoryGroup>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [Area], [Category], [Subcategory]
            FROM [MarketDixonDBSchema].[Products]
            WHERE [Area] IS NOT NULL AND [Category] IS NOT NULL
            GROUP BY [Area], [Category], [Subcategory]
            ORDER BY [Area], [Category], [Subcategory];
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var areas = new Dictionary<string, Dictionary<string, SortedSet<string>>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var area = reader.GetString(0);
            var category = reader.GetString(1);
            if (!areas.TryGetValue(area, out var categories))
            {
                categories = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
                areas.Add(area, categories);
            }

            if (!categories.TryGetValue(category, out var subcategories))
            {
                subcategories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                categories.Add(category, subcategories);
            }

            if (!reader.IsDBNull(2))
            {
                subcategories.Add(reader.GetString(2));
            }
        }

        return areas.Select(area => new CategoryGroup(
            area.Key,
            area.Value.Select(category => new CategoryItem(
                category.Key,
                category.Value.ToArray())).ToArray())).ToArray();
    }

    private static void AddProductFilters(
        SqlCommand command,
        List<string> filters,
        string? search,
        string? area,
        string? category,
        string? subcategory)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add("([ProductName] LIKE @Search ESCAPE N'\\' OR [Brand] LIKE @Search ESCAPE N'\\')");
            var term = EscapeLike(search.Trim());
            command.Parameters.Add("@Search", System.Data.SqlDbType.NVarChar, 1000).Value = $"%{term}%";
        }

        AddExactFilter(command, filters, area, "@Area", "[Area]");
        AddExactFilter(command, filters, category, "@Category", "[Category]");
        AddExactFilter(command, filters, subcategory, "@Subcategory", "[Subcategory]");
    }

    private static void AddExactFilter(SqlCommand command, List<string> filters, string? value, string parameter, string column)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        filters.Add($"{column} = {parameter}");
        command.Parameters.Add(parameter, System.Data.SqlDbType.NVarChar, 1000).Value = value.Trim();
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);

    internal static Product ReadProduct(SqlDataReader reader, int ordinalOffset = 0) => new(
        reader.GetString(ordinalOffset),
        reader.GetString(ordinalOffset + 1),
        ReadNullableString(reader, ordinalOffset + 2),
        ReadNullableString(reader, ordinalOffset + 3),
        ReadNullableString(reader, ordinalOffset + 4),
        ReadNullableString(reader, ordinalOffset + 5),
        ReadNullableString(reader, ordinalOffset + 6),
        ReadNullableDecimal(reader, ordinalOffset + 7),
        ReadNullableDecimal(reader, ordinalOffset + 8),
        ReadNullableDecimal(reader, ordinalOffset + 9),
        ReadNullableDecimal(reader, ordinalOffset + 10),
        ReadNullableDecimal(reader, ordinalOffset + 11),
        ReadNullableDecimal(reader, ordinalOffset + 12),
        ReadNullableDecimal(reader, ordinalOffset + 13),
        ReadNullableDecimal(reader, ordinalOffset + 14),
        reader.GetDecimal(ordinalOffset + 15),
        reader.GetBoolean(ordinalOffset + 16),
        reader.GetInt32(ordinalOffset + 17),
        ReadNullableString(reader, ordinalOffset + 18),
        ReadNullableString(reader, ordinalOffset + 19),
        reader.GetString(ordinalOffset + 20),
        reader.GetDateTime(ordinalOffset + 21));

    private static string? ReadNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? ReadNullableDecimal(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
}
