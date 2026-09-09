-- Saga step 3 prerequisite: enable SQL Server Change Data Capture on the monolith Identity + OpenIddict tables.
-- Run once against the monolith database (requires sysadmin / db_owner; SQL Server Agent must be running).
USE [QuickApp];
GO

IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = DB_NAME() AND is_cdc_enabled = 1)
    EXEC sys.sp_cdc_enable_db;
GO

DECLARE @tables TABLE (name sysname);
INSERT INTO @tables VALUES
    ('AspNetRoles'), ('AspNetUsers'), ('AspNetRoleClaims'), ('AspNetUserClaims'),
    ('AspNetUserLogins'), ('AspNetUserRoles'), ('AspNetUserTokens'),
    ('OpenIddictApplications'), ('OpenIddictScopes'), ('OpenIddictAuthorizations'), ('OpenIddictTokens');

DECLARE @name sysname;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @tables;
OPEN c;
FETCH NEXT FROM c INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM cdc.change_tables ct JOIN sys.tables t ON ct.source_object_id = t.object_id WHERE t.name = @name)
    BEGIN
        EXEC sys.sp_cdc_enable_table
            @source_schema = N'dbo',
            @source_name   = @name,
            @role_name     = NULL,
            @supports_net_changes = 1;
        PRINT 'CDC enabled on dbo.' + @name;
    END
    FETCH NEXT FROM c INTO @name;
END
CLOSE c; DEALLOCATE c;
GO
