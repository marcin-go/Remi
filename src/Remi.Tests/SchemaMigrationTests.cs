using Microsoft.Data.Sqlite;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task Single_body_upgrade_preserves_custom_copy_event_state_and_recipients()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        Directory.CreateDirectory(root);
        try
        {
            var schemaStore = new SqliteRemiStore(databasePath);
            var mailStore = new SqliteRemiMailStore(databasePath, schemaStore);
            var template = Assert.Single(await mailStore.GetTemplatesAsync(), item => item.EventType == MailEventTypes.PostSubmissionReport);
            await mailStore.SaveTemplateAsync(new MailTemplateUpdate(
                template.EventType,
                true,
                "Custom post-submission subject",
                template.BodyTemplate,
                [new MailRecipient(Guid.NewGuid(), MailRecipientType.To, "Director", "director@example.test", 0)]),
                DateTimeOffset.UtcNow);
            await using (var connection = await OpenAsync(databasePath))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE mail_templates
                    SET greeting = 'Dear team,', introduction = 'Custom introduction.',
                        request_text = 'Custom explanation.', closing = 'Regards,',
                        signature = 'Marcin', body_template = '', trigger_mode = 0,
                        schedule_day = 5, schedule_time_local = '08:30', time_zone_id = 'Europe/London'
                    WHERE event_type = 'post-submission-report';
                    DELETE FROM remi_schema_migrations WHERE version = 4;
                    DELETE FROM remi_schema_migrations WHERE version = 6;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var upgradedSchema = new SqliteRemiStore(databasePath);
            var upgradedMailStore = new SqliteRemiMailStore(databasePath, upgradedSchema);
            var upgraded = Assert.Single(await upgradedMailStore.GetTemplatesAsync(), item => item.EventType == MailEventTypes.PostSubmissionReport);

            Assert.True(upgraded.Enabled);
            Assert.Equal("Custom post-submission subject", upgraded.SubjectTemplate);
            Assert.Equal("Dear team,\n\nCustom introduction.\n\nCustom explanation.\n\n{{submission_evidence}}\n\nRegards,\n\nMarcin", upgraded.BodyTemplate);
            Assert.Equal(MailTriggerMode.Manual, upgraded.TriggerMode);
            Assert.Null(upgraded.ScheduleDay);
            Assert.Null(upgraded.ScheduleTimeLocal);
            Assert.Null(upgraded.TimeZoneId);
            var recipient = Assert.Single(upgraded.Recipients);
            Assert.Equal("director@example.test", recipient.EmailAddress);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "migration-backups"), "remi-data-before-schema-v4-*.db"));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "migration-backups"), "remi-data-before-schema-v6-*.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evidence_association_upgrade_repairs_suffixed_migrated_contract_documents()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        Directory.CreateDirectory(root);

        try
        {
            var initialStore = new SqliteRemiStore(databasePath);
            await initialStore.ReadAsync(database => database.Contracts.Count);

            await using (var connection = await OpenAsync(databasePath))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO contracts VALUES (
                        '11111111-1111-1111-1111-111111111111', 2, 'MVA_202410_PMA', 'Mole Valley District Council',
                        NULL, '2024-10-01', '2027-09-30', NULL, NULL, NULL, NULL, NULL, NULL,
                        '120000.00', '2024-10', 'RM6259 - 202410.xlsx', '2024-10-01T09:00:00.0000000+00:00');
                    INSERT INTO evidence VALUES (
                        '22222222-2222-2222-2222-222222222222', 4, 2, '2024-10',
                        'MVA_202410_PMA_contract_dates.png',
                        'RM6259 - Vertical Application Solutions\\202410\\MVA_202410_PMA_contract_dates.png',
                        '48b8e7b11542-MVA_202410_PMA_contract_dates.png', 'image/png', 134576,
                        '48b8e7b115420000000000000000000000000000000000000000000000000000', NULL,
                        '2026-08-06T08:47:59.0000000+00:00');
                    DELETE FROM remi_schema_migrations WHERE version = 8;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var upgradedStore = new SqliteRemiStore(databasePath);
            var repaired = await upgradedStore.ReadAsync(database =>
                Assert.Single(database.Evidence, item => item.Id == Guid.Parse("22222222-2222-2222-2222-222222222222")));

            Assert.Equal(EvidenceKind.ContractDocument, repaired.Kind);
            Assert.Equal("MVA_202410_PMA", repaired.ContractReference);
            Assert.Equal("48b8e7b11542-MVA_202410_PMA_contract_dates.png", repaired.StoredRelativePath);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "migration-backups"), "remi-data-before-schema-v8-*.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Go_live_backfill_upgrades_v8_and_preserves_explicit_operational_dates()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        Directory.CreateDirectory(root);

        try
        {
            var missingPartId = Guid.NewGuid();
            var explicitPartId = Guid.NewGuid();
            var missingContractId = Guid.NewGuid();
            var explicitContractId = Guid.NewGuid();
            var initialStore = new SqliteRemiStore(databasePath);
            await initialStore.UpdateAsync(database =>
            {
                database.Contracts.AddRange(
                [
                    MigrationContract(missingContractId, "MISSING-GO-LIVE", new DateOnly(2025, 4, 1)),
                    MigrationContract(explicitContractId, "EXPLICIT-GO-LIVE", new DateOnly(2025, 5, 1)),
                ]);
                database.ContractServiceParts.AddRange(
                [
                    new ContractServicePart(missingPartId, missingContractId, "Whole contract", null, 0, DateTimeOffset.UtcNow),
                    new ContractServicePart(explicitPartId, explicitContractId, "Implementation", new DateOnly(2025, 6, 15), 0, DateTimeOffset.UtcNow),
                ]);
                return 0;
            });

            await using (var connection = await OpenAsync(databasePath))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM remi_schema_migrations WHERE version = 9;";
                await command.ExecuteNonQueryAsync();
            }

            var upgradedStore = new SqliteRemiStore(databasePath);
            var parts = await upgradedStore.ReadAsync(database => database.ContractServiceParts.ToDictionary(item => item.Id));

            Assert.Equal(new DateOnly(2025, 4, 1), parts[missingPartId].GoLiveDate);
            Assert.Equal(new DateOnly(2025, 6, 15), parts[explicitPartId].GoLiveDate);

            var automaticBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v9-*.db"));
            await using var backup = await OpenAsync(automaticBackup, readOnly: true);
            await using (var integrity = backup.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", await integrity.ExecuteScalarAsync());
            }
            await using (var preMigrationValue = backup.CreateCommand())
            {
                preMigrationValue.CommandText = "SELECT go_live_date FROM contract_service_parts WHERE id = $id;";
                preMigrationValue.Parameters.AddWithValue("$id", missingPartId.ToString("D"));
                Assert.Equal(DBNull.Value, await preMigrationValue.ExecuteScalarAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Current_operational_schema_is_upgraded_additively_without_losing_register_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        Directory.CreateDirectory(root);

        try
        {
            await CreatePreMigrationDatabaseAsync(databasePath);

            var store = new SqliteRemiStore(databasePath);
            var snapshot = await store.ReadAsync(database => new
            {
                Contracts = database.Contracts.ToList(),
                Invoices = database.Invoices.ToList(),
                Returns = database.MonthlyReturns.ToList(),
                Evidence = database.Evidence.ToList(),
                Parts = database.ContractServiceParts.ToList(),
                ReportingOccurrences = database.ContractReportingOccurrences.ToList(),
                MarketplaceServices = database.DigitalMarketplaceServices.ToList(),
            });

            Assert.Single(snapshot.Contracts);
            Assert.Equal("LIVE-CONTRACT", snapshot.Contracts[0].SupplierReference);
            Assert.Single(snapshot.Invoices);
            Assert.Equal("INV-001", snapshot.Invoices[0].InvoiceNumber);
            Assert.Single(snapshot.Returns);
            Assert.Single(snapshot.Evidence);
            Assert.Single(snapshot.Parts);
            Assert.Equal("Planning Management", snapshot.Parts[0].Name);
            Assert.Equal(new DateOnly(2024, 10, 1), snapshot.Parts[0].GoLiveDate);
            Assert.Single(snapshot.ReportingOccurrences);
            Assert.Equal(snapshot.Contracts[0].Id, snapshot.ReportingOccurrences[0].ContractId);
            Assert.Equal(snapshot.Returns[0].Id, snapshot.ReportingOccurrences[0].MonthlyReturnId);
            var marketplaceService = Assert.Single(snapshot.MarketplaceServices);
            Assert.Equal(FrameworkCode.GCloud14, marketplaceService.Framework);
            Assert.Equal("legacy-service", marketplaceService.ServiceId);

            await using var verification = await OpenAsync(databasePath);
            var versions = new List<int>();
            await using (var command = verification.CreateCommand())
            {
                command.CommandText = "SELECT version FROM remi_schema_migrations ORDER BY version;";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    versions.Add(reader.GetInt32(0));
                }
            }
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], versions);
            Assert.True(await ColumnExistsAsync(verification, "charge_schedule_items", "contract_service_part_id"));
            Assert.True(await ColumnExistsAsync(verification, "mail_templates", "body_template"));
            Assert.True(await ColumnExistsAsync(verification, "digital_marketplace_services", "framework"));
            Assert.True(await ColumnExistsAsync(verification, "framework_configurations", "end_date"));

            var automaticBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v2-*.db"));
            await using var backup = await OpenAsync(automaticBackup, readOnly: true);
            await using var integrity = backup.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await integrity.ExecuteScalarAsync());

            var postSubmissionBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v3-*.db"));
            await using var versionThreeBackup = await OpenAsync(postSubmissionBackup, readOnly: true);
            await using var versionThreeIntegrity = versionThreeBackup.CreateCommand();
            versionThreeIntegrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await versionThreeIntegrity.ExecuteScalarAsync());

            var singleBodyBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v4-*.db"));
            await using var versionFourBackup = await OpenAsync(singleBodyBackup, readOnly: true);
            await using var versionFourIntegrity = versionFourBackup.CreateCommand();
            versionFourIntegrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await versionFourIntegrity.ExecuteScalarAsync());

            var marketplaceBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v5-*.db"));
            await using var versionFiveBackup = await OpenAsync(marketplaceBackup, readOnly: true);
            await using var versionFiveIntegrity = versionFiveBackup.CreateCommand();
            versionFiveIntegrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await versionFiveIntegrity.ExecuteScalarAsync());

            var manualTriggerBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v6-*.db"));
            await using var versionSixBackup = await OpenAsync(manualTriggerBackup, readOnly: true);
            await using var versionSixIntegrity = versionSixBackup.CreateCommand();
            versionSixIntegrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await versionSixIntegrity.ExecuteScalarAsync());

            var frameworkDatesBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v7-*.db"));
            await using var versionSevenBackup = await OpenAsync(frameworkDatesBackup, readOnly: true);
            await using var versionSevenIntegrity = versionSevenBackup.CreateCommand();
            versionSevenIntegrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await versionSevenIntegrity.ExecuteScalarAsync());

            var evidenceAssociationBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v8-*.db"));
            await using var versionEightBackup = await OpenAsync(evidenceAssociationBackup, readOnly: true);
            await using var versionEightIntegrity = versionEightBackup.CreateCommand();
            versionEightIntegrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await versionEightIntegrity.ExecuteScalarAsync());

            var goLiveBackup = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "migration-backups"),
                "remi-data-before-schema-v9-*.db"));
            await using var versionNineBackup = await OpenAsync(goLiveBackup, readOnly: true);
            await using var versionNineIntegrity = versionNineBackup.CreateCommand();
            versionNineIntegrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await versionNineIntegrity.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CreatePreMigrationDatabaseAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE contracts (
                id TEXT PRIMARY KEY, framework INTEGER NOT NULL, supplier_reference TEXT NOT NULL,
                customer_name TEXT NOT NULL, customer_urn TEXT NULL, start_date TEXT NULL,
                end_date TEXT NULL, lot_number TEXT NULL, service_group TEXT NULL,
                service_group_level_2 TEXT NULL, service_description TEXT NULL,
                order_channel TEXT NULL, digital_marketplace_service_id TEXT NULL,
                total_contract_value_ex_vat TEXT NOT NULL, report_month TEXT NOT NULL,
                source_workbook TEXT NOT NULL, created_at_utc TEXT NOT NULL);
            CREATE TABLE invoices (
                id TEXT PRIMARY KEY, framework INTEGER NOT NULL, supplier_reference TEXT NOT NULL,
                customer_name TEXT NOT NULL, customer_urn TEXT NULL, invoice_date TEXT NULL,
                invoice_number TEXT NOT NULL, lot_number TEXT NULL, service_group TEXT NULL,
                service_group_level_2 TEXT NULL, service_description TEXT NULL,
                order_channel TEXT NULL, digital_marketplace_service_id TEXT NULL,
                unit_of_measure TEXT NULL, quantity TEXT NULL, price_per_unit_ex_vat TEXT NULL,
                total_cost_ex_vat TEXT NOT NULL, original_vendor TEXT NULL,
                subcontractor_name TEXT NULL, report_month TEXT NOT NULL,
                source_workbook TEXT NOT NULL, created_at_utc TEXT NOT NULL);
            CREATE TABLE monthly_returns (
                id TEXT PRIMARY KEY, framework INTEGER NOT NULL, report_month TEXT NOT NULL,
                status INTEGER NOT NULL, submitted_at_utc TEXT NULL, submission_reference TEXT NULL,
                original_workbook_name TEXT NULL, updated_at_utc TEXT NOT NULL,
                UNIQUE (framework, report_month));
            CREATE TABLE evidence (
                id TEXT PRIMARY KEY, kind INTEGER NOT NULL, framework INTEGER NULL,
                report_month TEXT NULL, file_name TEXT NOT NULL, original_relative_path TEXT NOT NULL,
                stored_relative_path TEXT NOT NULL, content_type TEXT NOT NULL,
                file_size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL,
                contract_reference TEXT NULL, archived_at_utc TEXT NOT NULL);
            CREATE TABLE charge_schedule_items (
                id TEXT PRIMARY KEY, contract_id TEXT NOT NULL, contract_year INTEGER NOT NULL,
                description TEXT NOT NULL, expected_invoice_date TEXT NULL, value_ex_vat TEXT NOT NULL,
                is_optional_extension INTEGER NOT NULL DEFAULT 0, created_at_utc TEXT NOT NULL);
            CREATE TABLE digital_marketplace_services (
                service_id TEXT PRIMARY KEY, name TEXT NOT NULL);

            INSERT INTO contracts VALUES (
                '11111111-1111-1111-1111-111111111111', 2, 'LIVE-CONTRACT', 'Mole Valley District Council',
                '10000000', '2024-10-01', '2027-09-30', '1', NULL, NULL,
                'Planning Management', 'Framework Catalogue', NULL, '120000.00', '2024-10',
                'operational-source.xlsx', '2024-10-01T09:00:00.0000000+00:00');
            INSERT INTO invoices VALUES (
                '22222222-2222-2222-2222-222222222222', 2, 'LIVE-CONTRACT', 'Mole Valley District Council',
                '10000000', '2026-07-15', 'INV-001', '1', NULL, NULL, 'Planning Management',
                'Framework Catalogue', NULL, 'Per annum', '1', '40000.00', '40000.00',
                'StatMap Ltd', 'Not applicable', '2026-07', 'manual', '2026-07-15T09:00:00.0000000+00:00');
            INSERT INTO monthly_returns VALUES (
                '33333333-3333-3333-3333-333333333333', 2, '2024-10', 1,
                '2024-11-07T10:00:00.0000000+00:00', 'GCA-1', 'submitted.xlsx',
                '2024-11-07T10:00:00.0000000+00:00');
            INSERT INTO evidence VALUES (
                '44444444-4444-4444-4444-444444444444', 1, 2, '2024-10', 'submitted.xlsx',
                'submitted.xlsx', 'aa/submitted.xlsx',
                'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 1024,
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', NULL,
                '2024-11-07T10:00:00.0000000+00:00');
            INSERT INTO charge_schedule_items VALUES (
                '55555555-5555-5555-5555-555555555555',
                '11111111-1111-1111-1111-111111111111', 1, 'Annual charge', '2024-10-01',
                '40000.00', 0, '2024-10-01T09:00:00.0000000+00:00');
            INSERT INTO digital_marketplace_services VALUES ('legacy-service', 'Legacy product');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static ContractRecord MigrationContract(Guid id, string reference, DateOnly startDate) =>
        new(
            id,
            FrameworkCode.GCloud14,
            reference,
            "Migration customer",
            "URN-MIGRATION",
            startDate,
            startDate.AddYears(1),
            "2",
            "Information and Communication Technology (ICT)",
            null,
            null,
            null,
            "migration-service",
            1000,
            startDate.ToString("yyyy-MM"),
            "migration-test",
            DateTimeOffset.UtcNow);

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
