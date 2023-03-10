USE [master]
GO
/****** Object:  Database [castardb]    Script Date: 24.07.2022 23:12:01 ******/
CREATE DATABASE [castardb]
 CONTAINMENT = NONE
 ON  PRIMARY 
( NAME = N'castardb', FILENAME = N'C:\GitHub\Castar\DataBase\castardb.mdf' , SIZE = 8192KB , MAXSIZE = UNLIMITED, FILEGROWTH = 65536KB )
 LOG ON 
( NAME = N'castardb_log', FILENAME = N'C:\GitHub\Castar\DataBase\castardb_log.ldf' , SIZE = 8192KB , MAXSIZE = 2048GB , FILEGROWTH = 65536KB )
 WITH CATALOG_COLLATION = DATABASE_DEFAULT
GO
ALTER DATABASE [castardb] SET COMPATIBILITY_LEVEL = 150
GO
IF (1 = FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'))
begin
EXEC [castardb].[dbo].[sp_fulltext_database] @action = 'enable'
end
GO
ALTER DATABASE [castardb] SET ANSI_NULL_DEFAULT OFF 
GO
ALTER DATABASE [castardb] SET ANSI_NULLS OFF 
GO
ALTER DATABASE [castardb] SET ANSI_PADDING OFF 
GO
ALTER DATABASE [castardb] SET ANSI_WARNINGS OFF 
GO
ALTER DATABASE [castardb] SET ARITHABORT OFF 
GO
ALTER DATABASE [castardb] SET AUTO_CLOSE ON 
GO
ALTER DATABASE [castardb] SET AUTO_SHRINK OFF 
GO
ALTER DATABASE [castardb] SET AUTO_UPDATE_STATISTICS ON 
GO
ALTER DATABASE [castardb] SET CURSOR_CLOSE_ON_COMMIT OFF 
GO
ALTER DATABASE [castardb] SET CURSOR_DEFAULT  GLOBAL 
GO
ALTER DATABASE [castardb] SET CONCAT_NULL_YIELDS_NULL OFF 
GO
ALTER DATABASE [castardb] SET NUMERIC_ROUNDABORT OFF 
GO
ALTER DATABASE [castardb] SET QUOTED_IDENTIFIER OFF 
GO
ALTER DATABASE [castardb] SET RECURSIVE_TRIGGERS OFF 
GO
ALTER DATABASE [castardb] SET  DISABLE_BROKER 
GO
ALTER DATABASE [castardb] SET AUTO_UPDATE_STATISTICS_ASYNC OFF 
GO
ALTER DATABASE [castardb] SET DATE_CORRELATION_OPTIMIZATION OFF 
GO
ALTER DATABASE [castardb] SET TRUSTWORTHY OFF 
GO
ALTER DATABASE [castardb] SET ALLOW_SNAPSHOT_ISOLATION OFF 
GO
ALTER DATABASE [castardb] SET PARAMETERIZATION SIMPLE 
GO
ALTER DATABASE [castardb] SET READ_COMMITTED_SNAPSHOT OFF 
GO
ALTER DATABASE [castardb] SET HONOR_BROKER_PRIORITY OFF 
GO
ALTER DATABASE [castardb] SET RECOVERY SIMPLE 
GO
ALTER DATABASE [castardb] SET  MULTI_USER 
GO
ALTER DATABASE [castardb] SET PAGE_VERIFY CHECKSUM  
GO
ALTER DATABASE [castardb] SET DB_CHAINING OFF 
GO
ALTER DATABASE [castardb] SET FILESTREAM( NON_TRANSACTED_ACCESS = OFF ) 
GO
ALTER DATABASE [castardb] SET TARGET_RECOVERY_TIME = 60 SECONDS 
GO
ALTER DATABASE [castardb] SET DELAYED_DURABILITY = DISABLED 
GO
ALTER DATABASE [castardb] SET ACCELERATED_DATABASE_RECOVERY = OFF  
GO
ALTER DATABASE [castardb] SET QUERY_STORE = OFF
GO
USE [castardb]
GO
/****** Object:  Table [dbo].[Incomes]    Script Date: 24.07.2022 23:12:01 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Incomes](
	[Id] [uniqueidentifier] ROWGUIDCOL  NOT NULL,
	[UserId] [uniqueidentifier] NOT NULL,
	[MessageId] [bigint] NULL,
	[Sum] [decimal](24, 2) NOT NULL,
	[Source] [nvarchar](400) NOT NULL,
	[AddedOn] [datetime] NOT NULL,
	[CreatedOn] [datetime] NOT NULL,
	[ModifiedOn] [datetime] NULL,
 CONSTRAINT [PK_Incomes] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Items]    Script Date: 24.07.2022 23:12:01 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Items](
	[Id] [uniqueidentifier] ROWGUIDCOL  NOT NULL,
	[PurchaseId] [uniqueidentifier] NOT NULL,
	[Name] [nvarchar](400) NOT NULL,
	[Category] [int] NULL,
	[Quantity] [float] NOT NULL,
	[Price] [decimal](24, 2) NOT NULL,
	[Sum] [decimal](24, 2) NOT NULL,
	[CreatedOn] [datetime] NOT NULL,
	[ModifiedOn] [datetime] NULL,
 CONSTRAINT [PK_Items] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Purchases]    Script Date: 24.07.2022 23:12:01 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Purchases](
	[Id] [uniqueidentifier] ROWGUIDCOL  NOT NULL,
	[UserId] [uniqueidentifier] NOT NULL,
	[Name] [nvarchar](400) NOT NULL,
	[TotalSum] [decimal](24, 2) NOT NULL,
	[MessageId] [bigint] NULL,
	[AddedOn] [datetime] NOT NULL,
	[CreatedOn] [datetime] NOT NULL,
	[ModifiedOn] [datetime] NULL,
	[RetailPlace] [nvarchar](400) NULL,
	[RetailPlaceAddress] [nvarchar](400) NULL,
	[RawData] [nvarchar](150) NULL,
 CONSTRAINT [PK_Purchases] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[Users]    Script Date: 24.07.2022 23:12:01 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Users](
	[Id] [uniqueidentifier] ROWGUIDCOL  NOT NULL,
	[TelegramId] [bigint] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[Sex] [int] NOT NULL,
	[Color] [binary](4) NOT NULL,
	[Image] [image] NULL,
	[CreatedOn] [datetime] NOT NULL,
	[ModifiedOn] [datetime] NULL,
 CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
ALTER TABLE [dbo].[Incomes] ADD  CONSTRAINT [DF_Incomes_Id]  DEFAULT (newid()) FOR [Id]
GO
ALTER TABLE [dbo].[Incomes] ADD  CONSTRAINT [DF_Incomes_AddedOn]  DEFAULT (getdate()) FOR [AddedOn]
GO
ALTER TABLE [dbo].[Incomes] ADD  CONSTRAINT [DF_Incomes_CreatedOn]  DEFAULT (getdate()) FOR [CreatedOn]
GO
ALTER TABLE [dbo].[Items] ADD  CONSTRAINT [DF_Items_Id]  DEFAULT (newid()) FOR [Id]
GO
ALTER TABLE [dbo].[Items] ADD  CONSTRAINT [DF_Items_Sum]  DEFAULT ((0)) FOR [Sum]
GO
ALTER TABLE [dbo].[Items] ADD  CONSTRAINT [DF_Items_CreatedOn]  DEFAULT (getdate()) FOR [CreatedOn]
GO
ALTER TABLE [dbo].[Purchases] ADD  CONSTRAINT [DF_Purchases_Id]  DEFAULT (newid()) FOR [Id]
GO
ALTER TABLE [dbo].[Purchases] ADD  CONSTRAINT [DF_Purchases_TotalSum]  DEFAULT ((0)) FOR [TotalSum]
GO
ALTER TABLE [dbo].[Purchases] ADD  CONSTRAINT [DF_Purchases_AddedOn]  DEFAULT (getdate()) FOR [AddedOn]
GO
ALTER TABLE [dbo].[Purchases] ADD  CONSTRAINT [DF_Purchases_CreatedOn]  DEFAULT (getdate()) FOR [CreatedOn]
GO
ALTER TABLE [dbo].[Users] ADD  CONSTRAINT [DF_Users_Id]  DEFAULT (newid()) FOR [Id]
GO
ALTER TABLE [dbo].[Users] ADD  CONSTRAINT [DF_Users_CreatedOn]  DEFAULT (getdate()) FOR [CreatedOn]
GO
ALTER TABLE [dbo].[Incomes]  WITH CHECK ADD  CONSTRAINT [FK_Incomes_Users] FOREIGN KEY([UserId])
REFERENCES [dbo].[Users] ([Id])
GO
ALTER TABLE [dbo].[Incomes] CHECK CONSTRAINT [FK_Incomes_Users]
GO
ALTER TABLE [dbo].[Items]  WITH CHECK ADD  CONSTRAINT [FK_Items_Purchases] FOREIGN KEY([PurchaseId])
REFERENCES [dbo].[Purchases] ([Id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[Items] CHECK CONSTRAINT [FK_Items_Purchases]
GO
ALTER TABLE [dbo].[Purchases]  WITH CHECK ADD  CONSTRAINT [FK_Purchases_Users] FOREIGN KEY([UserId])
REFERENCES [dbo].[Users] ([Id])
GO
ALTER TABLE [dbo].[Purchases] CHECK CONSTRAINT [FK_Purchases_Users]
GO
USE [master]
GO
ALTER DATABASE [castardb] SET  READ_WRITE 
GO
