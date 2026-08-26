SET XACT_ABORT ON;

BEGIN TRY
	BEGIN TRANSACTION;

	IF COL_LENGTH(N'raw.SourceRecord', N'LegalLifecycleNormalizedAtUtc') IS NULL
	BEGIN
		ALTER TABLE [raw].[SourceRecord]
		ADD [LegalLifecycleNormalizedAtUtc] datetime2(3) NULL;
	END;

	IF NOT EXISTS
	(
		SELECT 1
		FROM [sys].[indexes]
		WHERE [object_id] = OBJECT_ID(N'[raw].[SourceRecord]')
		  AND [name] = N'IX_RawSourceRecord_LegalLifecyclePending'
	)
	BEGIN
		EXEC
		(
			N'CREATE INDEX [IX_RawSourceRecord_LegalLifecyclePending]
			  ON [raw].[SourceRecord] ([SourceCode], [Id])
			  INCLUDE ([SourceEntityId], [NormalizedAtUtc], [LegalLifecycleNormalizedAtUtc])
			  WHERE [LegalLifecycleNormalizedAtUtc] IS NULL;'
		);
	END;

	IF OBJECT_ID(N'[registry].[SubjectLifecycle]', N'U') IS NULL
	BEGIN
		CREATE TABLE [registry].[SubjectLifecycle]
		(
			[SubjectId]					uniqueidentifier	NOT NULL CONSTRAINT [FK_SubjectLifecycle_Subject] REFERENCES [registry].[Subject] ([Id]),
			[SourceCode]					varchar(30)			NOT NULL,
			[EstablishedOn]				date				NULL,
			[TerminatedOn]				date				NULL,
			[ContainsSourceInconsistency]	bit				NULL,
			[FirstObservedAtUtc]			datetime2(3)		NOT NULL CONSTRAINT [DF_SubjectLifecycle_First] DEFAULT SYSUTCDATETIME(),
			[LastObservedAtUtc]			datetime2(3)		NOT NULL CONSTRAINT [DF_SubjectLifecycle_Last] DEFAULT SYSUTCDATETIME(),
			CONSTRAINT [PK_SubjectLifecycle] PRIMARY KEY ([SubjectId], [SourceCode])
		);

		CREATE INDEX [IX_SubjectLifecycle_Status]
			ON [registry].[SubjectLifecycle] ([SourceCode], [TerminatedOn], [EstablishedOn])
			INCLUDE ([SubjectId], [ContainsSourceInconsistency]);
	END;

	IF OBJECT_ID(N'[registry].[SubjectLegalForm]', N'U') IS NULL
	BEGIN
		CREATE TABLE [registry].[SubjectLegalForm]
		(
			[Id]					bigint IDENTITY(1,1)	NOT NULL CONSTRAINT [PK_SubjectLegalForm] PRIMARY KEY,
			[SubjectId]			uniqueidentifier		NOT NULL CONSTRAINT [FK_SubjectLegalForm_Subject] REFERENCES [registry].[Subject] ([Id]),
			[LegalFormCode]		varchar(50)				NULL,
			[LegalFormName]		nvarchar(500)			NULL,
			[CodelistCode]		varchar(100)			NULL,
			[SourceCode]			varchar(30)				NOT NULL,
			[ValidFrom]			date					NULL,
			[ValidTo]			date					NULL,
			[AttributionHash]	binary(32)				NOT NULL,
			[FirstObservedAtUtc]	datetime2(3)			NOT NULL CONSTRAINT [DF_SubjectLegalForm_First] DEFAULT SYSUTCDATETIME(),
			[LastObservedAtUtc]	datetime2(3)			NOT NULL CONSTRAINT [DF_SubjectLegalForm_Last] DEFAULT SYSUTCDATETIME()
		);

		CREATE UNIQUE INDEX [UX_SubjectLegalForm_Attribution]
			ON [registry].[SubjectLegalForm]
				([SubjectId], [SourceCode], [AttributionHash], [ValidFrom], [ValidTo]);

		CREATE INDEX [IX_SubjectLegalForm_Current]
			ON [registry].[SubjectLegalForm]
				([SourceCode], [LegalFormCode], [ValidTo], [ValidFrom])
			INCLUDE ([SubjectId], [LegalFormName], [CodelistCode]);
	END;

	COMMIT TRANSACTION;
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0
		ROLLBACK TRANSACTION;

	THROW;
END CATCH;
GO

CREATE OR ALTER PROCEDURE [etl].[NormalizeRpoLegalLifecycleBatch]
	@BatchSize int = 5000
AS
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	IF @BatchSize < 1 OR @BatchSize > 20000
		THROW 51007, 'Batch size must be between 1 and 20000.', 1;

	DECLARE @NormalizedRecords int = 0;
	DECLARE @UpsertedLifecycleRecords int = 0;
	DECLARE @InsertedLegalForms int = 0;
	DECLARE @QualityObservations int = 0;

	BEGIN TRY
		BEGIN TRANSACTION;

		DECLARE @LockResult int;
		EXEC @LockResult = [sys].[sp_getapplock]
			@Resource = N'RpoHub:RPO:LegalLifecycleNormalization',
			@LockMode = N'Exclusive',
			@LockOwner = N'Transaction',
			@LockTimeout = 0;

		IF @LockResult < 0
		BEGIN
			ROLLBACK TRANSACTION;
			SELECT
				CAST(0 AS int) AS [NormalizedRecords],
				CAST(0 AS int) AS [UpsertedLifecycleRecords],
				CAST(0 AS int) AS [InsertedLegalForms],
				CAST(0 AS int) AS [QualityObservations],
				CAST(1 AS bit) AS [LockUnavailable];
			RETURN;
		END;

		CREATE TABLE [#Pending]
		(
			[RawRecordId]		bigint			NOT NULL CONSTRAINT [PK_LegalLifecyclePending] PRIMARY KEY,
			[SourceEntityId]	varchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
			[SubjectId]			uniqueidentifier NOT NULL,
			[JsonData]			varchar(max) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
			[EstablishedRaw]	varchar(30) COLLATE DATABASE_DEFAULT NULL,
			[TerminatedRaw]	varchar(30) COLLATE DATABASE_DEFAULT NULL,
			[EstablishedOn]		date			NULL,
			[TerminatedOn]		date			NULL,
			[ContainsInconsistency]	bit		NULL
		);

		INSERT INTO [#Pending]
			([RawRecordId], [SourceEntityId], [SubjectId], [JsonData], [EstablishedRaw], [TerminatedRaw], [EstablishedOn], [TerminatedOn], [ContainsInconsistency])
		SELECT TOP (@BatchSize)
			[raw].[Id],
			[raw].[SourceEntityId],
			[source].[SubjectId],
			[raw].[JsonData],
			JSON_VALUE([raw].[JsonData], '$.establishment') COLLATE DATABASE_DEFAULT,
			JSON_VALUE([raw].[JsonData], '$.termination') COLLATE DATABASE_DEFAULT,
			TRY_CONVERT(date, JSON_VALUE([raw].[JsonData], '$.establishment')),
			TRY_CONVERT(date, JSON_VALUE([raw].[JsonData], '$.termination')),
			CASE JSON_VALUE([raw].[JsonData], '$.containsInconsistency')
				WHEN 'true' THEN CAST(1 AS bit)
				WHEN 'false' THEN CAST(0 AS bit)
				ELSE NULL
			END
		FROM [raw].[SourceRecord] AS [raw] WITH (UPDLOCK, READPAST, ROWLOCK)
		INNER JOIN [registry].[SourceRecord] AS [source]
			ON [source].[SourceCode] = [raw].[SourceCode]
		   AND [source].[SourceEntityId] = [raw].[SourceEntityId]
		   AND [source].[CurrentRawRecordId] = [raw].[Id]
		WHERE [raw].[SourceCode] = 'RPO'
		  AND [raw].[NormalizedAtUtc] IS NOT NULL
		  AND [raw].[LegalLifecycleNormalizedAtUtc] IS NULL
		  AND [source].[SubjectId] IS NOT NULL
		ORDER BY [raw].[Id];

		IF NOT EXISTS (SELECT 1 FROM [#Pending])
		BEGIN
			COMMIT TRANSACTION;
			SELECT
				CAST(0 AS int) AS [NormalizedRecords],
				CAST(0 AS int) AS [UpsertedLifecycleRecords],
				CAST(0 AS int) AS [InsertedLegalForms],
				CAST(0 AS int) AS [QualityObservations],
				CAST(0 AS bit) AS [LockUnavailable];
			RETURN;
		END;

		MERGE [registry].[SubjectLifecycle] WITH (HOLDLOCK) AS [target]
		USING [#Pending] AS [source]
		   ON [target].[SubjectId] = [source].[SubjectId]
		  AND [target].[SourceCode] = 'RPO'
		WHEN MATCHED THEN
			UPDATE SET
				[EstablishedOn] = [source].[EstablishedOn],
				[TerminatedOn] = [source].[TerminatedOn],
				[ContainsSourceInconsistency] = [source].[ContainsInconsistency],
				[LastObservedAtUtc] = SYSUTCDATETIME()
		WHEN NOT MATCHED THEN
			INSERT
				([SubjectId], [SourceCode], [EstablishedOn], [TerminatedOn], [ContainsSourceInconsistency])
			VALUES
				([source].[SubjectId], 'RPO', [source].[EstablishedOn], [source].[TerminatedOn], [source].[ContainsInconsistency]);

		SET @UpsertedLifecycleRecords = @@ROWCOUNT;

		INSERT INTO [registry].[DataQualityObservation]
			([SourceCode], [SourceEntityId], [SubjectId], [RuleCode], [Severity], [Details])
		SELECT
			'RPO',
			[pending].[SourceEntityId],
			[pending].[SubjectId],
			'RPO_LIFECYCLE_REVERSED_DATES',
			'Warning',
			CONVERT
			(
				nvarchar(4000),
				CONCAT
				(
					N'Establishment ',
					CONVERT(nvarchar(10), [pending].[EstablishedOn], 23),
					N' is after termination ',
					CONVERT(nvarchar(10), [pending].[TerminatedOn], 23),
					N'. Source values were preserved.'
				)
			)
		FROM [#Pending] AS [pending]
		WHERE [pending].[EstablishedOn] IS NOT NULL
		  AND [pending].[TerminatedOn] IS NOT NULL
		  AND [pending].[TerminatedOn] < [pending].[EstablishedOn];

		SET @QualityObservations += @@ROWCOUNT;

		INSERT INTO [registry].[DataQualityObservation]
			([SourceCode], [SourceEntityId], [SubjectId], [RuleCode], [Severity], [Details])
		SELECT
			'RPO',
			[pending].[SourceEntityId],
			[pending].[SubjectId],
			'RPO_LIFECYCLE_INVALID_DATE',
			'Warning',
			CONVERT
			(
				nvarchar(4000),
				CONCAT
				(
					N'Invalid lifecycle date value: establishment=',
					COALESCE(CONVERT(nvarchar(30), [pending].[EstablishedRaw]), N'<NULL>'),
					N', termination=',
					COALESCE(CONVERT(nvarchar(30), [pending].[TerminatedRaw]), N'<NULL>'),
					N'. Raw values remain available in raw.SourceRecord.'
				)
			)
		FROM [#Pending] AS [pending]
		WHERE
			([pending].[EstablishedRaw] IS NOT NULL AND [pending].[EstablishedOn] IS NULL)
			OR
			([pending].[TerminatedRaw] IS NOT NULL AND [pending].[TerminatedOn] IS NULL);

		SET @QualityObservations += @@ROWCOUNT;

		INSERT INTO [registry].[DataQualityObservation]
			([SourceCode], [SourceEntityId], [SubjectId], [RuleCode], [Severity], [Details])
		SELECT DISTINCT
			'RPO',
			[pending].[SourceEntityId],
			[pending].[SubjectId],
			'RPO_LEGAL_FORM_REVERSED_VALIDITY',
			'Warning',
			CONVERT
			(
				nvarchar(4000),
				CONCAT
				(
					N'Legal form ',
					COALESCE([legalForm].[LegalFormCode], N'<NULL>'),
					N' has ValidFrom ',
					CONVERT(nvarchar(10), [legalForm].[ValidFrom], 23),
					N' after ValidTo ',
					CONVERT(nvarchar(10), [legalForm].[ValidTo], 23),
					N'. Source values were preserved.'
				)
			)
		FROM [#Pending] AS [pending]
		CROSS APPLY OPENJSON([pending].[JsonData], '$.legalForms') AS [jsonLegalForm]
		CROSS APPLY
		(
			SELECT
				CONVERT(nvarchar(50), JSON_VALUE([jsonLegalForm].[value], '$.value.code')) COLLATE DATABASE_DEFAULT AS [LegalFormCode],
				TRY_CONVERT(date, JSON_VALUE([jsonLegalForm].[value], '$.validFrom')) AS [ValidFrom],
				TRY_CONVERT(date, JSON_VALUE([jsonLegalForm].[value], '$.validTo')) AS [ValidTo]
		) AS [legalForm]
		WHERE [legalForm].[ValidFrom] IS NOT NULL
		  AND [legalForm].[ValidTo] IS NOT NULL
		  AND [legalForm].[ValidTo] < [legalForm].[ValidFrom];

		SET @QualityObservations += @@ROWCOUNT;

		INSERT INTO [registry].[SubjectLegalForm]
			([SubjectId], [LegalFormCode], [LegalFormName], [CodelistCode], [SourceCode], [ValidFrom], [ValidTo], [AttributionHash])
		SELECT DISTINCT
			[pending].[SubjectId],
			[legalForm].[LegalFormCode],
			[legalForm].[LegalFormName],
			[legalForm].[CodelistCode],
			'RPO',
			[legalForm].[ValidFrom],
			[legalForm].[ValidTo],
			[legalForm].[AttributionHash]
		FROM [#Pending] AS [pending]
		CROSS APPLY OPENJSON([pending].[JsonData], '$.legalForms') AS [jsonLegalForm]
		CROSS APPLY
		(
			SELECT
				CONVERT(varchar(50), JSON_VALUE([jsonLegalForm].[value], '$.value.code')) COLLATE DATABASE_DEFAULT AS [LegalFormCode],
				CONVERT(nvarchar(500), JSON_VALUE([jsonLegalForm].[value], '$.value.value')) COLLATE DATABASE_DEFAULT AS [LegalFormName],
				CONVERT(varchar(100), JSON_VALUE([jsonLegalForm].[value], '$.value.codelistCode')) COLLATE DATABASE_DEFAULT AS [CodelistCode],
				TRY_CONVERT(date, JSON_VALUE([jsonLegalForm].[value], '$.validFrom')) AS [ValidFrom],
				TRY_CONVERT(date, JSON_VALUE([jsonLegalForm].[value], '$.validTo')) AS [ValidTo],
				CONVERT
				(
					binary(32),
					HASHBYTES
					(
						'SHA2_256',
						CONVERT
						(
							varbinary(max),
							CONCAT
							(
								COALESCE(CONVERT(nvarchar(50), JSON_VALUE([jsonLegalForm].[value], '$.value.code')), N'<NULL>'),
								NCHAR(31),
								COALESCE(CONVERT(nvarchar(500), JSON_VALUE([jsonLegalForm].[value], '$.value.value')), N'<NULL>'),
								NCHAR(31),
								COALESCE(CONVERT(nvarchar(100), JSON_VALUE([jsonLegalForm].[value], '$.value.codelistCode')), N'<NULL>')
							)
						)
					)
				) AS [AttributionHash]
		) AS [legalForm]
		WHERE
		  (
			[legalForm].[LegalFormCode] IS NOT NULL
			OR [legalForm].[LegalFormName] IS NOT NULL
			OR [legalForm].[CodelistCode] IS NOT NULL
		  )
		  AND NOT EXISTS
		(
			SELECT 1
			FROM [registry].[SubjectLegalForm] AS [existing]
			WHERE [existing].[SubjectId] = [pending].[SubjectId]
			  AND [existing].[SourceCode] = 'RPO'
			  AND [existing].[AttributionHash] = [legalForm].[AttributionHash]
			  AND
			  (
				[existing].[ValidFrom] = [legalForm].[ValidFrom]
				OR ([existing].[ValidFrom] IS NULL AND [legalForm].[ValidFrom] IS NULL)
			  )
			  AND
			  (
				[existing].[ValidTo] = [legalForm].[ValidTo]
				OR ([existing].[ValidTo] IS NULL AND [legalForm].[ValidTo] IS NULL)
			  )
		);

		SET @InsertedLegalForms = @@ROWCOUNT;

		UPDATE [raw]
		SET [LegalLifecycleNormalizedAtUtc] = SYSUTCDATETIME()
		FROM [raw].[SourceRecord] AS [raw]
		INNER JOIN [#Pending] AS [pending]
			ON [pending].[RawRecordId] = [raw].[Id];

		SET @NormalizedRecords = @@ROWCOUNT;

		COMMIT TRANSACTION;

		SELECT
			@NormalizedRecords AS [NormalizedRecords],
			@UpsertedLifecycleRecords AS [UpsertedLifecycleRecords],
			@InsertedLegalForms AS [InsertedLegalForms],
			@QualityObservations AS [QualityObservations],
			CAST(0 AS bit) AS [LockUnavailable];
	END TRY
	BEGIN CATCH
		IF @@TRANCOUNT > 0
			ROLLBACK TRANSACTION;

		THROW;
	END CATCH;
END;
GO
