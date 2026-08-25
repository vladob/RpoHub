SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF SCHEMA_ID(N'etl') IS NULL EXEC(N'CREATE SCHEMA [etl]');
IF SCHEMA_ID(N'raw') IS NULL EXEC(N'CREATE SCHEMA [raw]');
IF SCHEMA_ID(N'registry') IS NULL EXEC(N'CREATE SCHEMA [registry]');
IF SCHEMA_ID(N'monitor') IS NULL EXEC(N'CREATE SCHEMA [monitor]');

IF OBJECT_ID(N'[etl].[ImportBatch]', N'U') IS NULL
CREATE TABLE [etl].[ImportBatch]
(
	[Id]						uniqueidentifier		NOT NULL CONSTRAINT [PK_ImportBatch] PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
	[SourceCode]				varchar(30)				NOT NULL,
	[BatchKind]					varchar(20)				NOT NULL,
	[SnapshotDate]				date						NULL,
	[Status]					varchar(20)				NOT NULL,
	[StartedAtUtc]				datetime2(3)			NOT NULL CONSTRAINT [DF_ImportBatch_Started] DEFAULT SYSUTCDATETIME(),
	[CompletedAtUtc]			datetime2(3)			NULL,
	[ErrorMessage]				nvarchar(4000)			NULL,
	CONSTRAINT [CK_ImportBatch_Kind] CHECK ([BatchKind] IN ('Initialization', 'Daily', 'Verification')),
	CONSTRAINT [CK_ImportBatch_Status] CHECK ([Status] IN ('Started', 'Completed', 'Failed', 'Cancelled'))
);

IF NOT EXISTS
(
	SELECT 1
	FROM [sys].[indexes]
	WHERE [object_id] = OBJECT_ID(N'[etl].[ImportBatch]')
	  AND [name] = N'UX_ImportBatch_Source_Snapshot'
)
CREATE UNIQUE INDEX [UX_ImportBatch_Source_Snapshot]
	ON [etl].[ImportBatch] ([SourceCode], [BatchKind], [SnapshotDate])
	WHERE [SnapshotDate] IS NOT NULL;

IF OBJECT_ID(N'[etl].[ImportFile]', N'U') IS NULL
CREATE TABLE [etl].[ImportFile]
(
	[Id]						uniqueidentifier		NOT NULL CONSTRAINT [PK_ImportFile] PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
	[ImportBatchId]				uniqueidentifier		NULL CONSTRAINT [FK_ImportFile_Batch] REFERENCES [etl].[ImportBatch] ([Id]),
	[SourceCode]				varchar(30)				NOT NULL,
	[RemoteKey]					nvarchar(1000)			NOT NULL,
	[RemoteUri]					nvarchar(2000)			NOT NULL,
	[BatchKind]					varchar(20)				NOT NULL,
	[Status]					varchar(20)				NOT NULL,
	[SizeBytes]					bigint					NULL,
	[ETag]						varchar(200)			NULL,
	[SourceModifiedAtUtc]		datetime2(3)			NULL,
	[DiscoveredAtUtc]			datetime2(3)			NOT NULL CONSTRAINT [DF_ImportFile_Discovered] DEFAULT SYSUTCDATETIME(),
	[ImportedAtUtc]				datetime2(3)			NULL,
	[RowCount]					bigint					NULL,
	[AttemptCount]				int						NOT NULL CONSTRAINT [DF_ImportFile_AttemptCount] DEFAULT 0,
	[LastAttemptAtUtc]			datetime2(3)			NULL,
	[ErrorMessage]				nvarchar(4000)			NULL,
	[RemoteKeyHash] AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), [RemoteKey]))) PERSISTED,
	CONSTRAINT [UQ_ImportFile_Source_KeyHash] UNIQUE ([SourceCode], [RemoteKeyHash]),
	CONSTRAINT [CK_ImportFile_Status] CHECK ([Status] IN ('Discovered', 'Downloading', 'Downloaded', 'Importing', 'Imported', 'Failed', 'Skipped'))
);

IF OBJECT_ID(N'[raw].[SourceRecord]', N'U') IS NULL
CREATE TABLE [raw].[SourceRecord]
(
	[Id]						bigint IDENTITY(1,1)	NOT NULL CONSTRAINT [PK_RawSourceRecord] PRIMARY KEY,
	[SourceCode]				varchar(30)				NOT NULL,
	[SourceEntityId]			varchar(100)			NOT NULL,
	[JsonData]					varchar(max) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
	[ContentHash]				binary(32)				NOT NULL,
	[SourceModifiedAtUtc]		datetime2(3)			NULL,
	[ImportFileId]				uniqueidentifier		NOT NULL CONSTRAINT [FK_RawSourceRecord_File] REFERENCES [etl].[ImportFile] ([Id]),
	[LoadedAtUtc]				datetime2(3)			NOT NULL CONSTRAINT [DF_RawSourceRecord_Loaded] DEFAULT SYSUTCDATETIME(),
	[NormalizedAtUtc]			datetime2(3)			NULL,
	CONSTRAINT [CK_RawSourceRecord_Json] CHECK (ISJSON([JsonData]) = 1),
	CONSTRAINT [UQ_RawSourceRecord_Version] UNIQUE ([SourceCode], [SourceEntityId], [ContentHash])
);
CREATE INDEX [IX_RawSourceRecord_Pending] ON [raw].[SourceRecord] ([SourceCode], [NormalizedAtUtc]) INCLUDE ([SourceEntityId], [SourceModifiedAtUtc]);

IF OBJECT_ID(N'[registry].[Subject]', N'U') IS NULL
CREATE TABLE [registry].[Subject]
(
	[Id]						uniqueidentifier		NOT NULL CONSTRAINT [PK_Subject] PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
	[DisplayName]				nvarchar(1000)			NULL,
	[CreatedAtUtc]				datetime2(3)			NOT NULL CONSTRAINT [DF_Subject_Created] DEFAULT SYSUTCDATETIME(),
	[UpdatedAtUtc]				datetime2(3)			NOT NULL CONSTRAINT [DF_Subject_Updated] DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'[registry].[IdentifierType]', N'U') IS NULL
BEGIN
	CREATE TABLE [registry].[IdentifierType]
	(
		[Code]					varchar(30)				NOT NULL CONSTRAINT [PK_IdentifierType] PRIMARY KEY,
		[Name]					nvarchar(100)			NOT NULL,
		[IsSensitive]			bit						NOT NULL CONSTRAINT [DF_IdentifierType_Sensitive] DEFAULT 0
	);
	INSERT INTO [registry].[IdentifierType] ([Code], [Name], [IsSensitive])
	VALUES
		('ICO', N'IČO', 0),
		('TAX_ID', N'DIČ', 0),
		('VAT_ID', N'IČ DPH', 0),
		('BIRTH_NUMBER', N'Rodné číslo', 1),
		('SOURCE_ENTITY_ID', N'ID v zdrojovom registri', 0),
		('OTHER', N'Iný identifikátor', 0);
END;

IF OBJECT_ID(N'[registry].[SubjectIdentifier]', N'U') IS NULL
CREATE TABLE [registry].[SubjectIdentifier]
(
	[Id]						bigint IDENTITY(1,1)	NOT NULL CONSTRAINT [PK_SubjectIdentifier] PRIMARY KEY,
	[SubjectId]					uniqueidentifier		NOT NULL CONSTRAINT [FK_SubjectIdentifier_Subject] REFERENCES [registry].[Subject] ([Id]),
	[IdentifierTypeCode]		varchar(30)				NOT NULL CONSTRAINT [FK_SubjectIdentifier_Type] REFERENCES [registry].[IdentifierType] ([Code]),
	[IdentifierValue]			nvarchar(100)			NOT NULL,
	[SourceCode]				varchar(30)				NOT NULL,
	[ValidFrom]					date					NULL,
	[ValidTo]					date					NULL,
	[IsVerified]				bit						NOT NULL CONSTRAINT [DF_SubjectIdentifier_Verified] DEFAULT 0,
	[FirstObservedAtUtc]		datetime2(3)			NOT NULL CONSTRAINT [DF_SubjectIdentifier_First] DEFAULT SYSUTCDATETIME(),
	[LastObservedAtUtc]			datetime2(3)			NOT NULL CONSTRAINT [DF_SubjectIdentifier_Last] DEFAULT SYSUTCDATETIME(),
	CONSTRAINT [UQ_SubjectIdentifier_Attribution] UNIQUE ([SubjectId], [IdentifierTypeCode], [IdentifierValue], [SourceCode], [ValidFrom], [ValidTo])
);
CREATE INDEX [IX_SubjectIdentifier_Lookup] ON [registry].[SubjectIdentifier] ([IdentifierTypeCode], [IdentifierValue]) INCLUDE ([SubjectId], [SourceCode], [IsVerified]);

IF OBJECT_ID(N'[registry].[SourceRecord]', N'U') IS NULL
CREATE TABLE [registry].[SourceRecord]
(
	[SourceCode]				varchar(30)				NOT NULL,
	[SourceEntityId]			varchar(100)			NOT NULL,
	[SubjectId]					uniqueidentifier		NULL CONSTRAINT [FK_SourceRecord_Subject] REFERENCES [registry].[Subject] ([Id]),
	[CurrentRawRecordId]		bigint					NOT NULL CONSTRAINT [FK_SourceRecord_Raw] REFERENCES [raw].[SourceRecord] ([Id]),
	[IsCurrent]					bit						NOT NULL CONSTRAINT [DF_SourceRecord_Current] DEFAULT 1,
	CONSTRAINT [PK_SourceRecord] PRIMARY KEY ([SourceCode], [SourceEntityId])
);

IF OBJECT_ID(N'[registry].[DataQualityObservation]', N'U') IS NULL
CREATE TABLE [registry].[DataQualityObservation]
(
	[Id]						bigint IDENTITY(1,1)	NOT NULL CONSTRAINT [PK_DataQualityObservation] PRIMARY KEY,
	[SourceCode]				varchar(30)				NOT NULL,
	[SourceEntityId]			varchar(100)			NOT NULL,
	[SubjectId]					uniqueidentifier		NULL CONSTRAINT [FK_DataQualityObservation_Subject] REFERENCES [registry].[Subject] ([Id]),
	[RuleCode]					varchar(100)			NOT NULL,
	[Severity]					varchar(20)				NOT NULL,
	[Details]					nvarchar(4000)			NOT NULL,
	[ObservedAtUtc]				datetime2(3)			NOT NULL CONSTRAINT [DF_DataQualityObservation_Observed] DEFAULT SYSUTCDATETIME(),
	[ResolvedAtUtc]				datetime2(3)			NULL,
	CONSTRAINT [CK_DataQualityObservation_Severity] CHECK ([Severity] IN ('Information', 'Warning', 'Error'))
);

IF OBJECT_ID(N'[monitor].[Watchlist]', N'U') IS NULL
CREATE TABLE [monitor].[Watchlist]
(
	[Id]						uniqueidentifier		NOT NULL CONSTRAINT [PK_Watchlist] PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
	[Name]						nvarchar(200)			NOT NULL,
	[IsEnabled]					bit						NOT NULL CONSTRAINT [DF_Watchlist_Enabled] DEFAULT 1,
	[CreatedAtUtc]				datetime2(3)			NOT NULL CONSTRAINT [DF_Watchlist_Created] DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'[monitor].[WatchlistSubject]', N'U') IS NULL
CREATE TABLE [monitor].[WatchlistSubject]
(
	[WatchlistId]				uniqueidentifier		NOT NULL CONSTRAINT [FK_WatchlistSubject_Watchlist] REFERENCES [monitor].[Watchlist] ([Id]),
	[SubjectId]					uniqueidentifier		NOT NULL CONSTRAINT [FK_WatchlistSubject_Subject] REFERENCES [registry].[Subject] ([Id]),
	[RelationshipType]			varchar(30)				NULL,
	CONSTRAINT [PK_WatchlistSubject] PRIMARY KEY ([WatchlistId], [SubjectId])
);

IF OBJECT_ID(N'[monitor].[ObservedChange]', N'U') IS NULL
CREATE TABLE [monitor].[ObservedChange]
(
	[Id]						bigint IDENTITY(1,1)	NOT NULL CONSTRAINT [PK_ObservedChange] PRIMARY KEY,
	[SubjectId]					uniqueidentifier		NULL CONSTRAINT [FK_ObservedChange_Subject] REFERENCES [registry].[Subject] ([Id]),
	[SourceCode]				varchar(30)				NOT NULL,
	[SourceEntityId]			varchar(100)			NOT NULL,
	[FieldPath]					nvarchar(500)			NOT NULL,
	[OldValue]					nvarchar(max)			NULL,
	[NewValue]					nvarchar(max)			NULL,
	[ObservedAtUtc]				datetime2(3)			NOT NULL CONSTRAINT [DF_ObservedChange_Observed] DEFAULT SYSUTCDATETIME(),
	[AcknowledgedAtUtc]			datetime2(3)			NULL
);

IF OBJECT_ID(N'[etl].[NotificationOutbox]', N'U') IS NULL
CREATE TABLE [etl].[NotificationOutbox]
(
	[Id]						bigint IDENTITY(1,1)	NOT NULL CONSTRAINT [PK_NotificationOutbox] PRIMARY KEY,
	[EventType]					varchar(100)			NOT NULL,
	[PayloadJson]				nvarchar(max)			NOT NULL,
	[CreatedAtUtc]				datetime2(3)			NOT NULL CONSTRAINT [DF_NotificationOutbox_Created] DEFAULT SYSUTCDATETIME(),
	[ProcessedAtUtc]			datetime2(3)			NULL,
	[AttemptCount]				int						NOT NULL CONSTRAINT [DF_NotificationOutbox_Attempts] DEFAULT 0,
	[LastError]					nvarchar(4000)			NULL,
	CONSTRAINT [CK_NotificationOutbox_Json] CHECK (ISJSON([PayloadJson]) = 1)
);
CREATE INDEX [IX_NotificationOutbox_Pending] ON [etl].[NotificationOutbox] ([ProcessedAtUtc], [CreatedAtUtc]) INCLUDE ([EventType], [AttemptCount]);

COMMIT TRANSACTION;
