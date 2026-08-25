SET XACT_ABORT ON;

BEGIN TRY
	BEGIN TRANSACTION;

	IF OBJECT_ID(N'[registry].[SubjectName]', N'U') IS NULL
	BEGIN
		CREATE TABLE [registry].[SubjectName]
		(
			[Id]						bigint IDENTITY(1,1)	NOT NULL CONSTRAINT [PK_SubjectName] PRIMARY KEY,
			[SubjectId]				uniqueidentifier		NOT NULL CONSTRAINT [FK_SubjectName_Subject] REFERENCES [registry].[Subject] ([Id]),
			[NameValue]				nvarchar(1000)			NOT NULL,
			[SourceCode]				varchar(30)				NOT NULL,
			[ValidFrom]				date					NULL,
			[ValidTo]				date					NULL,
			[FirstObservedAtUtc]		datetime2(3)			NOT NULL CONSTRAINT [DF_SubjectName_First] DEFAULT SYSUTCDATETIME(),
			[LastObservedAtUtc]		datetime2(3)			NOT NULL CONSTRAINT [DF_SubjectName_Last] DEFAULT SYSUTCDATETIME(),
			[NameHash] AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), [NameValue]))) PERSISTED,
			CONSTRAINT [CK_SubjectName_Validity] CHECK ([ValidTo] IS NULL OR [ValidFrom] IS NULL OR [ValidTo] >= [ValidFrom])
		);

		CREATE UNIQUE INDEX [UX_SubjectName_Attribution]
			ON [registry].[SubjectName] ([SubjectId], [SourceCode], [NameHash], [ValidFrom]);

		CREATE INDEX [IX_SubjectName_Current]
			ON [registry].[SubjectName] ([SubjectId], [ValidTo], [ValidFrom])
			INCLUDE ([NameValue], [SourceCode]);
	END;

	COMMIT TRANSACTION;
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0
		ROLLBACK TRANSACTION;

	THROW;
END CATCH;
GO

CREATE OR ALTER PROCEDURE [etl].[NormalizeRpoCoreBatch]
	@BatchSize int = 5000
AS
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	IF @BatchSize < 1 OR @BatchSize > 20000
		THROW 51005, 'Batch size must be between 1 and 20000.', 1;

	DECLARE @NormalizedRecords int = 0;
	DECLARE @InsertedSubjects int = 0;
	DECLARE @InsertedIdentifiers int = 0;
	DECLARE @InsertedNames int = 0;

	BEGIN TRY
		BEGIN TRANSACTION;

		DECLARE @LockResult int;
		EXEC @LockResult = [sys].[sp_getapplock]
			@Resource = N'RpoHub:RPO:CoreNormalization',
			@LockMode = N'Exclusive',
			@LockOwner = N'Transaction',
			@LockTimeout = 0;

		IF @LockResult < 0
		BEGIN
			ROLLBACK TRANSACTION;
			SELECT
				CAST(0 AS int) AS [NormalizedRecords],
				CAST(0 AS int) AS [InsertedSubjects],
				CAST(0 AS int) AS [InsertedIdentifiers],
				CAST(0 AS int) AS [InsertedNames],
				CAST(1 AS bit) AS [LockUnavailable];
			RETURN;
		END;

		IF NOT EXISTS
		(
			SELECT 1
			FROM [etl].[ImportBatch]
			WHERE [SourceCode] = 'RPO'
			  AND [BatchKind] = 'Initialization'
			  AND [Status] = 'Completed'
		)
		BEGIN
			COMMIT TRANSACTION;
			SELECT
				CAST(0 AS int) AS [NormalizedRecords],
				CAST(0 AS int) AS [InsertedSubjects],
				CAST(0 AS int) AS [InsertedIdentifiers],
				CAST(0 AS int) AS [InsertedNames],
				CAST(0 AS bit) AS [LockUnavailable];
			RETURN;
		END;

		CREATE TABLE [#Pending]
		(
			[RawRecordId]		bigint			NOT NULL CONSTRAINT [PK_Pending] PRIMARY KEY,
			[SourceEntityId]	varchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
			[JsonData]			varchar(max) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
			[DisplayName]		nvarchar(1000)	NULL
		);

		INSERT INTO [#Pending]
			([RawRecordId], [SourceEntityId], [JsonData], [DisplayName])
		SELECT TOP (@BatchSize)
			[raw].[Id],
			[raw].[SourceEntityId],
			[raw].[JsonData],
			[displayName].[NameValue]
		FROM [raw].[SourceRecord] AS [raw] WITH (UPDLOCK, READPAST, ROWLOCK)
		OUTER APPLY
		(
			SELECT TOP (1)
				CONVERT(nvarchar(1000), JSON_VALUE([name].[value], '$.value')) COLLATE DATABASE_DEFAULT AS [NameValue]
			FROM OPENJSON([raw].[JsonData], '$.fullNames') AS [name]
			WHERE NULLIF(JSON_VALUE([name].[value], '$.value') COLLATE DATABASE_DEFAULT, '') IS NOT NULL
			ORDER BY
				CASE WHEN JSON_VALUE([name].[value], '$.validTo') IS NULL THEN 0 ELSE 1 END,
				TRY_CONVERT(date, JSON_VALUE([name].[value], '$.validFrom')) DESC,
				[name].[key] DESC
		) AS [displayName]
		WHERE [raw].[SourceCode] = 'RPO'
		  AND [raw].[NormalizedAtUtc] IS NULL
		ORDER BY [raw].[Id];

		IF NOT EXISTS (SELECT 1 FROM [#Pending])
		BEGIN
			COMMIT TRANSACTION;
			SELECT
				CAST(0 AS int) AS [NormalizedRecords],
				CAST(0 AS int) AS [InsertedSubjects],
				CAST(0 AS int) AS [InsertedIdentifiers],
				CAST(0 AS int) AS [InsertedNames],
				CAST(0 AS bit) AS [LockUnavailable];
			RETURN;
		END;

		CREATE TABLE [#SubjectMap]
		(
			[RawRecordId]		bigint			NOT NULL CONSTRAINT [PK_SubjectMap] PRIMARY KEY,
			[SourceEntityId]	varchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
			[SubjectId]			uniqueidentifier NOT NULL
		);

		INSERT INTO [#SubjectMap]
			([RawRecordId], [SourceEntityId], [SubjectId])
		SELECT
			[pending].[RawRecordId],
			[pending].[SourceEntityId],
			[source].[SubjectId]
		FROM [#Pending] AS [pending]
		INNER JOIN [registry].[SourceRecord] AS [source]
			ON [source].[SourceCode] = 'RPO'
		   AND [source].[SourceEntityId] = [pending].[SourceEntityId]
		WHERE [source].[SubjectId] IS NOT NULL;

		MERGE [registry].[Subject] AS [target]
		USING
		(
			SELECT
				[pending].[RawRecordId],
				[pending].[SourceEntityId],
				[pending].[DisplayName]
			FROM [#Pending] AS [pending]
			WHERE NOT EXISTS
			(
				SELECT 1
				FROM [#SubjectMap] AS [mapped]
				WHERE [mapped].[RawRecordId] = [pending].[RawRecordId]
			)
		) AS [source]
		ON 1 = 0
		WHEN NOT MATCHED THEN
			INSERT ([DisplayName])
			VALUES ([source].[DisplayName])
		OUTPUT
			[source].[RawRecordId],
			[source].[SourceEntityId],
			[inserted].[Id]
		INTO [#SubjectMap]
			([RawRecordId], [SourceEntityId], [SubjectId]);

		SET @InsertedSubjects = @@ROWCOUNT;

		MERGE [registry].[SourceRecord] WITH (HOLDLOCK) AS [target]
		USING [#SubjectMap] AS [source]
		   ON [target].[SourceCode] = 'RPO'
		  AND [target].[SourceEntityId] = [source].[SourceEntityId]
		WHEN MATCHED THEN
			UPDATE SET
				[SubjectId] = COALESCE([target].[SubjectId], [source].[SubjectId]),
				[CurrentRawRecordId] = [source].[RawRecordId],
				[IsCurrent] = 1
		WHEN NOT MATCHED THEN
			INSERT ([SourceCode], [SourceEntityId], [SubjectId], [CurrentRawRecordId], [IsCurrent])
			VALUES ('RPO', [source].[SourceEntityId], [source].[SubjectId], [source].[RawRecordId], 1);

		UPDATE [subject]
		SET
			[DisplayName] = [pending].[DisplayName],
			[UpdatedAtUtc] = SYSUTCDATETIME()
		FROM [registry].[Subject] AS [subject]
		INNER JOIN [#SubjectMap] AS [mapped]
			ON [mapped].[SubjectId] = [subject].[Id]
		INNER JOIN [#Pending] AS [pending]
			ON [pending].[RawRecordId] = [mapped].[RawRecordId]
		WHERE [pending].[DisplayName] IS NOT NULL;

		INSERT INTO [registry].[SubjectIdentifier]
			([SubjectId], [IdentifierTypeCode], [IdentifierValue], [SourceCode], [ValidFrom], [ValidTo], [IsVerified])
		SELECT DISTINCT
			[mapped].[SubjectId],
			'SOURCE_ENTITY_ID',
			CONVERT(nvarchar(100), [mapped].[SourceEntityId]),
			'RPO',
			NULL,
			NULL,
			1
		FROM [#SubjectMap] AS [mapped]
		WHERE NOT EXISTS
		(
			SELECT 1
			FROM [registry].[SubjectIdentifier] AS [existing]
			WHERE [existing].[SubjectId] = [mapped].[SubjectId]
			  AND [existing].[IdentifierTypeCode] = 'SOURCE_ENTITY_ID'
			  AND [existing].[IdentifierValue] = CONVERT(nvarchar(100), [mapped].[SourceEntityId])
			  AND [existing].[SourceCode] = 'RPO'
			  AND [existing].[ValidFrom] IS NULL
		);

		SET @InsertedIdentifiers = @@ROWCOUNT;

		INSERT INTO [registry].[SubjectIdentifier]
			([SubjectId], [IdentifierTypeCode], [IdentifierValue], [SourceCode], [ValidFrom], [ValidTo], [IsVerified])
		SELECT DISTINCT
			[mapped].[SubjectId],
			'ICO',
			[identifier].[IdentifierValue],
			'RPO',
			[identifier].[ValidFrom],
			[identifier].[ValidTo],
			1
		FROM [#Pending] AS [pending]
		INNER JOIN [#SubjectMap] AS [mapped]
			ON [mapped].[RawRecordId] = [pending].[RawRecordId]
		CROSS APPLY OPENJSON([pending].[JsonData], '$.identifiers') AS [jsonIdentifier]
		CROSS APPLY
		(
			SELECT
				CONVERT(nvarchar(100), JSON_VALUE([jsonIdentifier].[value], '$.value')) COLLATE DATABASE_DEFAULT AS [IdentifierValue],
				TRY_CONVERT(date, JSON_VALUE([jsonIdentifier].[value], '$.validFrom')) AS [ValidFrom],
				TRY_CONVERT(date, JSON_VALUE([jsonIdentifier].[value], '$.validTo')) AS [ValidTo]
		) AS [identifier]
		WHERE NULLIF([identifier].[IdentifierValue], '') IS NOT NULL
		  AND NOT EXISTS
		(
			SELECT 1
			FROM [registry].[SubjectIdentifier] AS [existing]
			WHERE [existing].[SubjectId] = [mapped].[SubjectId]
			  AND [existing].[IdentifierTypeCode] = 'ICO'
			  AND [existing].[IdentifierValue] = [identifier].[IdentifierValue]
			  AND [existing].[SourceCode] = 'RPO'
			  AND
			  (
				[existing].[ValidFrom] = [identifier].[ValidFrom]
				OR ([existing].[ValidFrom] IS NULL AND [identifier].[ValidFrom] IS NULL)
			  )
		);

		SET @InsertedIdentifiers += @@ROWCOUNT;

		INSERT INTO [registry].[SubjectName]
			([SubjectId], [NameValue], [SourceCode], [ValidFrom], [ValidTo])
		SELECT DISTINCT
			[mapped].[SubjectId],
			[name].[NameValue],
			'RPO',
			[name].[ValidFrom],
			[name].[ValidTo]
		FROM [#Pending] AS [pending]
		INNER JOIN [#SubjectMap] AS [mapped]
			ON [mapped].[RawRecordId] = [pending].[RawRecordId]
		CROSS APPLY OPENJSON([pending].[JsonData], '$.fullNames') AS [jsonName]
		CROSS APPLY
		(
			SELECT
				CONVERT(nvarchar(1000), JSON_VALUE([jsonName].[value], '$.value')) COLLATE DATABASE_DEFAULT AS [NameValue],
				TRY_CONVERT(date, JSON_VALUE([jsonName].[value], '$.validFrom')) AS [ValidFrom],
				TRY_CONVERT(date, JSON_VALUE([jsonName].[value], '$.validTo')) AS [ValidTo]
		) AS [name]
		WHERE NULLIF([name].[NameValue], '') IS NOT NULL
		  AND NOT EXISTS
		(
			SELECT 1
			FROM [registry].[SubjectName] AS [existing]
			WHERE [existing].[SubjectId] = [mapped].[SubjectId]
			  AND [existing].[SourceCode] = 'RPO'
			  AND [existing].[NameHash] = CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), [name].[NameValue])))
			  AND
			  (
				[existing].[ValidFrom] = [name].[ValidFrom]
				OR ([existing].[ValidFrom] IS NULL AND [name].[ValidFrom] IS NULL)
			  )
		);

		SET @InsertedNames = @@ROWCOUNT;

		UPDATE [raw]
		SET [NormalizedAtUtc] = SYSUTCDATETIME()
		FROM [raw].[SourceRecord] AS [raw]
		INNER JOIN [#Pending] AS [pending]
			ON [pending].[RawRecordId] = [raw].[Id];

		SET @NormalizedRecords = @@ROWCOUNT;

		COMMIT TRANSACTION;

		SELECT
			@NormalizedRecords AS [NormalizedRecords],
			@InsertedSubjects AS [InsertedSubjects],
			@InsertedIdentifiers AS [InsertedIdentifiers],
			@InsertedNames AS [InsertedNames],
			CAST(0 AS bit) AS [LockUnavailable];
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION;

		THROW;
	END CATCH;
END;
GO
