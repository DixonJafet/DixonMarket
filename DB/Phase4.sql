IF SCHEMA_ID(N'MarketDixonDBSchema') IS NULL
    EXEC(N'CREATE SCHEMA [MarketDixonDBSchema]');
GO

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
GO

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
GO

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
GO

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
GO

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
GO

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
GO

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
GO
