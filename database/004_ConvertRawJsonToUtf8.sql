SET XACT_ABORT ON;

BEGIN TRY
	BEGIN TRANSACTION;

	IF EXISTS
	(
		SELECT 1
		FROM [sys].[columns] AS [column]
		INNER JOIN [sys].[types] AS [type]
			ON [type].[user_type_id] = [column].[user_type_id]
		WHERE [column].[object_id] = OBJECT_ID(N'raw.SourceRecord')
		  AND [column].[name] = N'JsonData'
		  AND
		  (
			[type].[name] <> N'varchar'
			OR COLLATIONPROPERTY([column].[collation_name], N'CodePage') <> 65001
		  )
	)
	BEGIN
		IF EXISTS
		(
			SELECT 1
			FROM [sys].[check_constraints]
			WHERE [parent_object_id] = OBJECT_ID(N'raw.SourceRecord')
			  AND [name] = N'CK_RawSourceRecord_Json'
		)
		BEGIN
			ALTER TABLE [raw].[SourceRecord]
			DROP CONSTRAINT [CK_RawSourceRecord_Json];
		END;

		EXEC
		(
			N'ALTER TABLE [raw].[SourceRecord]
			  ALTER COLUMN [JsonData]
			  varchar(max) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL;'
		);

		ALTER TABLE [raw].[SourceRecord] WITH CHECK
		ADD CONSTRAINT [CK_RawSourceRecord_Json]
			CHECK (ISJSON([JsonData]) = 1);

		ALTER TABLE [raw].[SourceRecord]
		CHECK CONSTRAINT [CK_RawSourceRecord_Json];
	END;

	IF EXISTS
	(
		SELECT 1
		FROM [raw].[SourceRecord]
		WHERE HASHBYTES('SHA2_256', CONVERT(varbinary(max), [JsonData])) <> [ContentHash]
	)
	BEGIN
		THROW 51004, 'UTF-8 conversion changed one or more raw JSON content hashes.', 1;
	END;

	COMMIT TRANSACTION;
END TRY
BEGIN CATCH
	IF @@TRANCOUNT > 0
		ROLLBACK TRANSACTION;

	THROW;
END CATCH;
