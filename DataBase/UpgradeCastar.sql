-- Run against the selected Castar database, or a new empty database.
-- This script preserves all existing application rows and may be run again.
SET XACT_ABORT ON;
SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
    AND OBJECT_ID(N'dbo.Purchases', N'U') IS NULL
    AND OBJECT_ID(N'dbo.Items', N'U') IS NULL
    AND OBJECT_ID(N'dbo.Incomes', N'U') IS NULL
    AND NOT EXISTS (SELECT 1 FROM sys.tables WHERE is_ms_shipped = 0)
BEGIN
    CREATE TABLE dbo.Users
    (
        Id uniqueidentifier ROWGUIDCOL NOT NULL CONSTRAINT PK_Users PRIMARY KEY CONSTRAINT DF_Users_Id DEFAULT NEWID(),
        TelegramId bigint NOT NULL,
        Name nvarchar(200) NOT NULL,
        Sex int NOT NULL,
        Color binary(4) NOT NULL,
        Image image NULL,
        CreatedOn datetime NOT NULL CONSTRAINT DF_Users_CreatedOn DEFAULT GETDATE(),
        ModifiedOn datetime NULL
    );

    CREATE TABLE dbo.Purchases
    (
        Id uniqueidentifier ROWGUIDCOL NOT NULL CONSTRAINT PK_Purchases PRIMARY KEY CONSTRAINT DF_Purchases_Id DEFAULT NEWID(),
        UserId uniqueidentifier NOT NULL,
        Name nvarchar(400) NOT NULL,
        TotalSum decimal(24, 2) NOT NULL CONSTRAINT DF_Purchases_TotalSum DEFAULT 0,
        MessageId bigint NULL,
        AddedOn datetime NOT NULL CONSTRAINT DF_Purchases_AddedOn DEFAULT GETDATE(),
        CreatedOn datetime NOT NULL CONSTRAINT DF_Purchases_CreatedOn DEFAULT GETDATE(),
        ModifiedOn datetime NULL,
        RetailPlace nvarchar(400) NULL,
        RetailPlaceAddress nvarchar(400) NULL,
        RawData binary(100) NULL,
        CONSTRAINT FK_Purchases_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
    );

    CREATE TABLE dbo.Items
    (
        Id uniqueidentifier ROWGUIDCOL NOT NULL CONSTRAINT PK_Items PRIMARY KEY CONSTRAINT DF_Items_Id DEFAULT NEWID(),
        PurchaseId uniqueidentifier NOT NULL,
        Name nvarchar(400) NOT NULL,
        Category int NULL,
        Quantity float NOT NULL,
        Price decimal(24, 2) NOT NULL,
        Sum decimal(24, 2) NOT NULL CONSTRAINT DF_Items_Sum DEFAULT 0,
        CreatedOn datetime NOT NULL CONSTRAINT DF_Items_CreatedOn DEFAULT GETDATE(),
        ModifiedOn datetime NULL,
        CONSTRAINT FK_Items_Purchases FOREIGN KEY (PurchaseId) REFERENCES dbo.Purchases (Id) ON DELETE CASCADE
    );

    CREATE TABLE dbo.Incomes
    (
        Id uniqueidentifier ROWGUIDCOL NOT NULL CONSTRAINT PK_Incomes PRIMARY KEY CONSTRAINT DF_Incomes_Id DEFAULT NEWID(),
        UserId uniqueidentifier NOT NULL,
        MessageId bigint NULL,
        Sum decimal(24, 2) NOT NULL,
        Source nvarchar(400) NOT NULL,
        AddedOn datetime NOT NULL CONSTRAINT DF_Incomes_AddedOn DEFAULT GETDATE(),
        CreatedOn datetime NOT NULL CONSTRAINT DF_Incomes_CreatedOn DEFAULT GETDATE(),
        ModifiedOn datetime NULL,
        CONSTRAINT FK_Incomes_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
    );
END;

IF COL_LENGTH(N'dbo.Users', N'Color') IS NULL
    OR COL_LENGTH(N'dbo.Purchases', N'RawData') IS NULL
    OR NOT EXISTS (
        SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Purchases')
          AND name = N'RawData' AND TYPE_NAME(system_type_id) = N'binary' AND max_length = 100
    )
    OR COL_LENGTH(N'dbo.Items', N'PurchaseId') IS NULL
    OR COL_LENGTH(N'dbo.Incomes', N'UserId') IS NULL
    THROW 51000, 'This database does not match the supported Castar SQL Server schema from July/August 2022.', 1;

IF COL_LENGTH(N'dbo.Purchases', N'ReceiptKey') IS NULL
    ALTER TABLE dbo.Purchases ADD ReceiptKey binary(32) NULL;
IF COL_LENGTH(N'dbo.Purchases', N'Currency') IS NULL
    ALTER TABLE dbo.Purchases ADD Currency char(3) NULL;
IF COL_LENGTH(N'dbo.Items', N'LineNumber') IS NULL
    ALTER TABLE dbo.Items ADD LineNumber int NULL;
IF COL_LENGTH(N'dbo.Incomes', N'LineNumber') IS NULL
    ALTER TABLE dbo.Incomes ADD LineNumber int NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Purchases') AND name = N'UX_Purchases_ReceiptKey')
    EXEC(N'CREATE UNIQUE INDEX UX_Purchases_ReceiptKey ON dbo.Purchases (ReceiptKey) WHERE ReceiptKey IS NOT NULL;');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Items') AND name = N'IX_Items_PurchaseId')
    CREATE INDEX IX_Items_PurchaseId ON dbo.Items (PurchaseId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Purchases') AND name = N'IX_Purchases_UserId_MessageId')
    CREATE INDEX IX_Purchases_UserId_MessageId ON dbo.Purchases (UserId, MessageId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Incomes') AND name = N'IX_Incomes_UserId_MessageId')
    CREATE INDEX IX_Incomes_UserId_MessageId ON dbo.Incomes (UserId, MessageId);

-- Castar writes line and purchase totals in the same transaction. The former
-- trigger double-counts inserts and overwrites fiscal totals including discounts.
IF OBJECT_ID(N'dbo.Items_UpdateTotalSumPurchase', N'TR') IS NOT NULL
    DISABLE TRIGGER dbo.Items_UpdateTotalSumPurchase ON dbo.Items;

EXEC(N'CREATE OR ALTER TRIGGER dbo.Users_updatedRecord ON dbo.Users AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(@@PROCID) > 1 RETURN;
    UPDATE target SET ModifiedOn = GETDATE() FROM dbo.Users target JOIN inserted i ON target.Id = i.Id;
END;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.Purchases_updatedRecord ON dbo.Purchases AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(@@PROCID) > 1 RETURN;
    UPDATE target SET ModifiedOn = GETDATE() FROM dbo.Purchases target JOIN inserted i ON target.Id = i.Id;
END;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.Items_updatedRecord ON dbo.Items AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(@@PROCID) > 1 RETURN;
    UPDATE target SET ModifiedOn = GETDATE() FROM dbo.Items target JOIN inserted i ON target.Id = i.Id;
END;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.Incomes_updatedRecord ON dbo.Incomes AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(@@PROCID) > 1 RETURN;
    UPDATE target SET ModifiedOn = GETDATE() FROM dbo.Incomes target JOIN inserted i ON target.Id = i.Id;
END;');

COMMIT TRANSACTION;
