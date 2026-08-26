SET XACT_ABORT ON;

BEGIN TRY
	BEGIN TRANSACTION;

	DECLARE @LockResult int;
	DECLARE @InsertedIdentifierObservations int = 0;
	DECLARE @InsertedNameObservations int = 0;

	EXEC @LockResult = [sys].[sp_getapplock]
		@Resource = N'RpoHub:RPO:CoreNormalization',
		@LockMode = N'Exclusive',
		@LockOwner = N'Transaction',
		@LockTimeout = 60000;

	IF @LockResult < 0
		THROW 51006, 'Could not acquire the RPO core-normalization lock for data-quality backfill.', 1;

	CREATE TABLE [#BackfillRaw]
	(
		[RawRecordId]		bigint			NOT NULL CONSTRAINT [PK_BackfillRaw] PRIMARY KEY,
		[SourceEntityId]	varchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
		[SubjectId]			uniqueidentifier NOT NULL
	);

	INSERT INTO [#BackfillRaw]
		([RawRecordId], [SourceEntityId], [SubjectId])
	SELECT TOP (75020)
		[raw].[Id],
		[raw].[SourceEntityId],
		[source].[SubjectId]
	FROM [raw].[SourceRecord] AS [raw]
	INNER JOIN [registry].[SourceRecord] AS [source]
		ON [source].[SourceCode] = [raw].[SourceCode]
	   AND [source].[SourceEntityId] = [raw].[SourceEntityId]
	   AND [source].[CurrentRawRecordId] = [raw].[Id]
	WHERE [raw].[SourceCode] = 'RPO'
	  AND [raw].[NormalizedAtUtc] IS NOT NULL
	  AND [source].[SubjectId] IS NOT NULL
	ORDER BY [raw].[Id];

	;WITH [IdentifierIntervals] AS
	(
		SELECT
			[backfill].[SourceEntityId],
			[backfill].[SubjectId],
			CONVERT(nvarchar(100), JSON_VALUE([jsonIdentifier].[value], '$.value')) COLLATE DATABASE_DEFAULT AS [IdentifierValue],
			TRY_CONVERT(date, JSON_VALUE([jsonIdentifier].[value], '$.validFrom')) AS [ValidFrom],
			TRY_CONVERT(date, JSON_VALUE([jsonIdentifier].[value], '$.validTo')) AS [ValidTo]
		FROM [#BackfillRaw] AS [backfill]
		INNER JOIN [raw].[SourceRecord] AS [raw]
			ON [raw].[Id] = [backfill].[RawRecordId]
		CROSS APPLY OPENJSON([raw].[JsonData], '$.identifiers') AS [jsonIdentifier]
	),
	[IdentifierConflicts] AS
	(
		SELECT
			[SourceEntityId],
			[SubjectId],
			[IdentifierValue],
			[ValidFrom]
		FROM [IdentifierIntervals]
		WHERE NULLIF([IdentifierValue], '') IS NOT NULL
		GROUP BY
			[SourceEntityId],
			[SubjectId],
			[IdentifierValue],
			[ValidFrom]
		HAVING COUNT
		(
			DISTINCT COALESCE(CONVERT(varchar(10), [ValidTo], 23), '<NULL>')
		) > 1
	),
	[IdentifierObservations] AS
	(
		SELECT
			[SourceEntityId],
			[SubjectId],
			CONVERT
			(
				nvarchar(4000),
				CONCAT
				(
					N'IČO ',
					[IdentifierValue],
					N' has multiple ValidTo values for ValidFrom ',
					COALESCE(CONVERT(nvarchar(10), [ValidFrom], 23), N'<NULL>'),
					N'. All distinct source intervals were preserved.'
				)
			) AS [Details]
		FROM [IdentifierConflicts]
	)
	INSERT INTO [registry].[DataQualityObservation]
		([SourceCode], [SourceEntityId], [SubjectId], [RuleCode], [Severity], [Details])
	SELECT
		'RPO',
		[observation].[SourceEntityId],
		[observation].[SubjectId],
		'RPO_IDENTIFIER_CONFLICTING_VALID_TO',
		'Warning',
		[observation].[Details]
	FROM [IdentifierObservations] AS [observation]
	WHERE NOT EXISTS
	(
		SELECT 1
		FROM [registry].[DataQualityObservation] AS [existing]
		WHERE [existing].[SourceCode] = 'RPO'
		  AND [existing].[SourceEntityId] = [observation].[SourceEntityId]
		  AND [existing].[SubjectId] = [observation].[SubjectId]
		  AND [existing].[RuleCode] = 'RPO_IDENTIFIER_CONFLICTING_VALID_TO'
		  AND [existing].[Details] = [observation].[Details]
		  AND [existing].[ResolvedAtUtc] IS NULL
	);

	SET @InsertedIdentifierObservations = @@ROWCOUNT;

	;WITH [NameIntervals] AS
	(
		SELECT
			[backfill].[SourceEntityId],
			[backfill].[SubjectId],
			CONVERT(nvarchar(1000), JSON_VALUE([jsonName].[value], '$.value')) COLLATE DATABASE_DEFAULT AS [NameValue],
			TRY_CONVERT(date, JSON_VALUE([jsonName].[value], '$.validFrom')) AS [ValidFrom],
			TRY_CONVERT(date, JSON_VALUE([jsonName].[value], '$.validTo')) AS [ValidTo]
		FROM [#BackfillRaw] AS [backfill]
		INNER JOIN [raw].[SourceRecord] AS [raw]
			ON [raw].[Id] = [backfill].[RawRecordId]
		CROSS APPLY OPENJSON([raw].[JsonData], '$.fullNames') AS [jsonName]
	),
	[NameConflicts] AS
	(
		SELECT
			[SourceEntityId],
			[SubjectId],
			[NameValue],
			[ValidFrom]
		FROM [NameIntervals]
		WHERE NULLIF([NameValue], '') IS NOT NULL
		GROUP BY
			[SourceEntityId],
			[SubjectId],
			[NameValue],
			[ValidFrom]
		HAVING COUNT
		(
			DISTINCT COALESCE(CONVERT(varchar(10), [ValidTo], 23), '<NULL>')
		) > 1
	),
	[NameObservations] AS
	(
		SELECT
			[SourceEntityId],
			[SubjectId],
			CONVERT
			(
				nvarchar(4000),
				CONCAT
				(
					N'Name "',
					[NameValue],
					N'" has multiple ValidTo values for ValidFrom ',
					COALESCE(CONVERT(nvarchar(10), [ValidFrom], 23), N'<NULL>'),
					N'. All distinct source intervals were preserved.'
				)
			) AS [Details]
		FROM [NameConflicts]
	)
	INSERT INTO [registry].[DataQualityObservation]
		([SourceCode], [SourceEntityId], [SubjectId], [RuleCode], [Severity], [Details])
	SELECT
		'RPO',
		[observation].[SourceEntityId],
		[observation].[SubjectId],
		'RPO_NAME_CONFLICTING_VALID_TO',
		'Warning',
		[observation].[Details]
	FROM [NameObservations] AS [observation]
	WHERE NOT EXISTS
	(
		SELECT 1
		FROM [registry].[DataQualityObservation] AS [existing]
		WHERE [existing].[SourceCode] = 'RPO'
		  AND [existing].[SourceEntityId] = [observation].[SourceEntityId]
		  AND [existing].[SubjectId] = [observation].[SubjectId]
		  AND [existing].[RuleCode] = 'RPO_NAME_CONFLICTING_VALID_TO'
		  AND [existing].[Details] = [observation].[Details]
		  AND [existing].[ResolvedAtUtc] IS NULL
	);

	SET @InsertedNameObservations = @@ROWCOUNT;

	COMMIT TRANSACTION;

	SELECT
		@InsertedIdentifierObservations AS [InsertedIdentifierObservations],
		@InsertedNameObservations AS [InsertedNameObservations];
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0
		ROLLBACK TRANSACTION;

	THROW;
END CATCH;
