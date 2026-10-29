IF SCHEMA_ID(N'MarketDixonDBSchema') IS NULL
    EXEC(N'CREATE SCHEMA [MarketDixonDBSchema]');
GO

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
GO

IF OBJECT_ID(N'[MarketDixonDBSchema].[RevokedTokens]', N'U') IS NULL
BEGIN
    CREATE TABLE [MarketDixonDBSchema].[RevokedTokens]
    (
        [TokenHash] char(64) NOT NULL
            CONSTRAINT [PK_MarketDixonDBSchema_RevokedTokens] PRIMARY KEY,
        [ExpiresAtUtc] datetime2(0) NOT NULL
    );
END;
GO
