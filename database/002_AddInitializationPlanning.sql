SET XACT_ABORT ON;

BEGIN TRY
	BEGIN TRANSACTION;

	IF COL_LENGTH(N'etl.ImportBatch', N'SnapshotDate') IS NULL
	BEGIN
		EXEC
		(
			N'ALTER TABLE [etl].[ImportBatch]
			  ADD [SnapshotDate] date NULL;'
		);
	END;

	IF NOT EXISTS
	(
		SELECT 1
		FROM [sys].[indexes]
		WHERE [object_id] = OBJECT_ID(N'etl.ImportBatch')
		  AND [name] = N'UX_ImportBatch_Source_Snapshot'
	)
	BEGIN
		EXEC
		(
			N'CREATE UNIQUE INDEX [UX_ImportBatch_Source_Snapshot]
			  ON [etl].[ImportBatch]
			  (
				  [SourceCode],
				  [BatchKind],
				  [SnapshotDate]
			  )
			  WHERE [SnapshotDate] IS NOT NULL;'
		);
	END;

	COMMIT TRANSACTION;
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0
		ROLLBACK TRANSACTION;

	THROW;
END CATCH;