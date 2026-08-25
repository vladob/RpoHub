SET XACT_ABORT ON;

BEGIN TRY
	BEGIN TRANSACTION;

	IF COL_LENGTH(N'etl.ImportFile', N'AttemptCount') IS NULL
	BEGIN
		ALTER TABLE [etl].[ImportFile]
		ADD [AttemptCount] int NOT NULL
			CONSTRAINT [DF_ImportFile_AttemptCount] DEFAULT 0;
	END;

	IF COL_LENGTH(N'etl.ImportFile', N'LastAttemptAtUtc') IS NULL
	BEGIN
		ALTER TABLE [etl].[ImportFile]
		ADD [LastAttemptAtUtc] datetime2(3) NULL;
	END;

	IF COL_LENGTH(N'etl.ImportFile', N'ErrorMessage') IS NULL
	BEGIN
		ALTER TABLE [etl].[ImportFile]
		ADD [ErrorMessage] nvarchar(4000) NULL;
	END;

	COMMIT TRANSACTION;
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0
		ROLLBACK TRANSACTION;

	THROW;
END CATCH;
