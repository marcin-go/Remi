using System.Globalization;
using Microsoft.Data.Sqlite;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

/// <summary>
/// Stores the Remi register in SQLite. Every registered record is held in a first-class table;
/// operational schema changes are applied through additive, numbered migrations.
/// </summary>
public sealed class SqliteRemiStore : IRemiStore, IRemiDataResetter
{
    internal const int CurrentSchemaVersion = 8;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly string databasePath;
    private bool initialized;

    public SqliteRemiStore(string? databasePath = null)
    {
        this.databasePath = Path.GetFullPath(databasePath ?? RemiDataPaths.DefaultDatabaseFile);
    }

    public async Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return reader(await LoadDatabaseAsync(connection, cancellationToken));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var database = await LoadDatabaseAsync(connection, cancellationToken);
            var result = update(database);
            await SaveDatabaseAsync(connection, transaction, database, cancellationToken);
            transaction.Commit();
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Discards every register table and creates the current schema. It is only used by the
    /// explicitly confirmed source-data repopulation workflow.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(databasePath)
                ?? throw new InvalidOperationException("The SQLite database path has no parent directory.");
            Directory.CreateDirectory(directory);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await DropRemiTablesAsync(connection, cancellationToken);
            await CreateSchemaAsync(connection, cancellationToken);
            await SeedDigitalMarketplaceServicesAsync(connection, cancellationToken);
            await ApplySchemaMigrationsAsync(connection, existingDatabase: false, cancellationToken);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(databasePath)
                ?? throw new InvalidOperationException("The SQLite database path has no parent directory.");
            Directory.CreateDirectory(directory);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            var existingDatabase = await TableExistsAsync(connection, "contracts", cancellationToken);
            var digitalMarketplaceServicesExist = await TableExistsAsync(connection, "digital_marketplace_services", cancellationToken);
            await CreateSchemaAsync(connection, cancellationToken);
            if (!digitalMarketplaceServicesExist)
            {
                await SeedDigitalMarketplaceServicesAsync(connection, cancellationToken);
            }
            await ApplySchemaMigrationsAsync(connection, existingDatabase, cancellationToken);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(connection, "workspace_state", cancellationToken) ||
            await TableExistsAsync(connection, "schema_metadata", cancellationToken))
        {
            throw new InvalidOperationException(
                "This SQLite file belongs to an earlier Remi prototype. Use Maintenance to rebuild the local register from source data; Remi does not upgrade legacy data files in place.");
        }

        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecuteAsync(connection, null, """
            CREATE TABLE IF NOT EXISTS contracts (
                id TEXT PRIMARY KEY,
                framework INTEGER NOT NULL,
                supplier_reference TEXT NOT NULL,
                customer_name TEXT NOT NULL,
                customer_urn TEXT NULL,
                start_date TEXT NULL,
                end_date TEXT NULL,
                lot_number TEXT NULL,
                service_group TEXT NULL,
                service_group_level_2 TEXT NULL,
                service_description TEXT NULL,
                order_channel TEXT NULL,
                digital_marketplace_service_id TEXT NULL,
                total_contract_value_ex_vat TEXT NOT NULL,
                report_month TEXT NOT NULL,
                source_workbook TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_contracts_framework_reference
                ON contracts (framework, supplier_reference);

            CREATE TABLE IF NOT EXISTS invoices (
                id TEXT PRIMARY KEY,
                framework INTEGER NOT NULL,
                supplier_reference TEXT NOT NULL,
                customer_name TEXT NOT NULL,
                customer_urn TEXT NULL,
                invoice_date TEXT NULL,
                invoice_number TEXT NOT NULL,
                lot_number TEXT NULL,
                service_group TEXT NULL,
                service_group_level_2 TEXT NULL,
                service_description TEXT NULL,
                order_channel TEXT NULL,
                digital_marketplace_service_id TEXT NULL,
                unit_of_measure TEXT NULL,
                quantity TEXT NULL,
                price_per_unit_ex_vat TEXT NULL,
                total_cost_ex_vat TEXT NOT NULL,
                original_vendor TEXT NULL,
                subcontractor_name TEXT NULL,
                report_month TEXT NOT NULL,
                source_workbook TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_invoices_framework_reference
                ON invoices (framework, supplier_reference);

            CREATE TABLE IF NOT EXISTS contract_changes (
                id TEXT PRIMARY KEY,
                contract_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                agreement_date TEXT NOT NULL,
                effective_start_date TEXT NULL,
                effective_end_date TEXT NULL,
                incremental_value_ex_vat TEXT NOT NULL,
                was_provided_for_in_original_call_off INTEGER NOT NULL,
                has_written_agreement INTEGER NOT NULL,
                reference TEXT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_contract_changes_contract_agreement
                ON contract_changes (contract_id, agreement_date);

            CREATE TABLE IF NOT EXISTS invoice_contract_change_links (
                invoice_id TEXT PRIMARY KEY,
                contract_change_id TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_invoice_contract_change_links_change
                ON invoice_contract_change_links (contract_change_id);

            CREATE TABLE IF NOT EXISTS invoice_plan_items (
                id TEXT PRIMARY KEY,
                contract_id TEXT NOT NULL,
                label TEXT NOT NULL,
                expected_invoice_date TEXT NULL,
                expected_value_ex_vat TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS charge_schedule_items (
                id TEXT PRIMARY KEY,
                contract_id TEXT NOT NULL,
                contract_service_part_id TEXT NULL,
                contract_year INTEGER NOT NULL,
                description TEXT NOT NULL,
                expected_invoice_date TEXT NULL,
                value_ex_vat TEXT NOT NULL,
                is_optional_extension INTEGER NOT NULL DEFAULT 0,
                created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_charge_schedule_contract
                ON charge_schedule_items (contract_id, contract_year);

            CREATE TABLE IF NOT EXISTS monthly_returns (
                id TEXT PRIMARY KEY,
                framework INTEGER NOT NULL,
                report_month TEXT NOT NULL,
                status INTEGER NOT NULL,
                submitted_at_utc TEXT NULL,
                submission_reference TEXT NULL,
                original_workbook_name TEXT NULL,
                updated_at_utc TEXT NOT NULL,
                UNIQUE (framework, report_month)
            );

            CREATE TABLE IF NOT EXISTS evidence (
                id TEXT PRIMARY KEY,
                kind INTEGER NOT NULL,
                framework INTEGER NULL,
                report_month TEXT NULL,
                file_name TEXT NOT NULL,
                original_relative_path TEXT NOT NULL,
                stored_relative_path TEXT NOT NULL,
                content_type TEXT NOT NULL,
                file_size_bytes INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                contract_reference TEXT NULL,
                archived_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_evidence_framework_month
                ON evidence (framework, report_month);

            CREATE TABLE IF NOT EXISTS mi_templates (
                id TEXT PRIMARY KEY,
                framework INTEGER NOT NULL,
                evidence_id TEXT NOT NULL,
                workbook_name TEXT NOT NULL,
                is_active INTEGER NOT NULL,
                registered_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_mi_templates_framework_active
                ON mi_templates (framework, is_active);

            CREATE TABLE IF NOT EXISTS framework_configurations (
                framework INTEGER PRIMARY KEY,
                start_date TEXT NOT NULL,
                end_date TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS digital_marketplace_services (
                framework INTEGER NOT NULL,
                service_id TEXT NOT NULL,
                name TEXT NOT NULL,
                PRIMARY KEY (framework, service_id)
            );

            CREATE TABLE IF NOT EXISTS audit_events (
                id TEXT PRIMARY KEY,
                occurred_at_utc TEXT NOT NULL,
                action TEXT NOT NULL,
                entity_type TEXT NOT NULL,
                entity_id TEXT NULL,
                summary TEXT NOT NULL,
                reason TEXT NULL,
                actor TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_audit_events_occurred
                ON audit_events (occurred_at_utc DESC);
            """, cancellationToken);
    }

    private static async Task DropRemiTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, """
            PRAGMA foreign_keys = OFF;
            DROP TABLE IF EXISTS mail_events;
            DROP TABLE IF EXISTS mail_contents;
            DROP TABLE IF EXISTS mail_recipients;
            DROP TABLE IF EXISTS mail_messages;
            DROP TABLE IF EXISTS mail_template_recipients;
            DROP TABLE IF EXISTS mail_templates;
            DROP TABLE IF EXISTS mail_scheduler_state;
            DROP TABLE IF EXISTS contract_reporting_occurrences;
            DROP TABLE IF EXISTS contract_service_parts;
            DROP TABLE IF EXISTS audit_events;
            DROP TABLE IF EXISTS framework_configurations;
            DROP TABLE IF EXISTS digital_marketplace_services;
            DROP TABLE IF EXISTS mi_templates;
            DROP TABLE IF EXISTS evidence;
            DROP TABLE IF EXISTS monthly_returns;
            DROP TABLE IF EXISTS charge_schedule_items;
            DROP TABLE IF EXISTS invoice_plan_items;
            DROP TABLE IF EXISTS invoice_contract_change_links;
            DROP TABLE IF EXISTS contract_changes;
            DROP TABLE IF EXISTS invoices;
            DROP TABLE IF EXISTS contracts;
            DROP TABLE IF EXISTS workspace_state;
            DROP TABLE IF EXISTS schema_metadata;
            DROP TABLE IF EXISTS remi_schema_migrations;
            """, cancellationToken);
    }

    private async Task<RemiDatabase> LoadDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        new()
        {
            Contracts = await LoadContractsAsync(connection, cancellationToken),
            Invoices = await LoadInvoicesAsync(connection, cancellationToken),
            ContractChanges = await LoadContractChangesAsync(connection, cancellationToken),
            InvoiceContractChangeLinks = await LoadInvoiceContractChangeLinksAsync(connection, cancellationToken),
            InvoicePlanItems = await LoadInvoicePlanItemsAsync(connection, cancellationToken),
            ChargeScheduleItems = await LoadChargeScheduleItemsAsync(connection, cancellationToken),
            ContractServiceParts = await LoadContractServicePartsAsync(connection, cancellationToken),
            ContractReportingOccurrences = await LoadContractReportingOccurrencesAsync(connection, cancellationToken),
            MonthlyReturns = await LoadMonthlyReturnsAsync(connection, cancellationToken),
            Evidence = await LoadEvidenceAsync(connection, cancellationToken),
            MiTemplates = await LoadTemplatesAsync(connection, cancellationToken),
            FrameworkConfigurations = await LoadFrameworkConfigurationsAsync(connection, cancellationToken),
            DigitalMarketplaceServices = await LoadDigitalMarketplaceServicesAsync(connection, cancellationToken),
            AuditEvents = await LoadAuditEventsAsync(connection, cancellationToken),
        };

    private static async Task<List<ContractRecord>> LoadContractsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, framework, supplier_reference, customer_name, customer_urn, start_date, end_date, lot_number, service_group, service_group_level_2, service_description, order_channel, digital_marketplace_service_id, total_contract_value_ex_vat, report_month, source_workbook, created_at_utc FROM contracts ORDER BY created_at_utc, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var contracts = new List<ContractRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            contracts.Add(new ContractRecord(
                Guid.Parse(reader.GetString(0)),
                (FrameworkCode)reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                NullableString(reader, 4),
                NullableDate(reader, 5),
                NullableDate(reader, 6),
                NullableString(reader, 7),
                NullableString(reader, 8),
                NullableString(reader, 9),
                NullableString(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                Number(reader.GetString(13)),
                reader.GetString(14),
                reader.GetString(15),
                Timestamp(reader.GetString(16))));
        }

        return contracts;
    }

    private static async Task<List<InvoiceRecord>> LoadInvoicesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, framework, supplier_reference, customer_name, customer_urn, invoice_date, invoice_number, lot_number, service_group, service_group_level_2, service_description, order_channel, digital_marketplace_service_id, unit_of_measure, quantity, price_per_unit_ex_vat, total_cost_ex_vat, original_vendor, subcontractor_name, report_month, source_workbook, created_at_utc FROM invoices ORDER BY created_at_utc, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var invoices = new List<InvoiceRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            invoices.Add(new InvoiceRecord(
                Guid.Parse(reader.GetString(0)),
                (FrameworkCode)reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                NullableString(reader, 4),
                NullableDate(reader, 5),
                reader.GetString(6),
                NullableString(reader, 7),
                NullableString(reader, 8),
                NullableString(reader, 9),
                NullableString(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                NullableString(reader, 13),
                NullableNumber(reader, 14),
                NullableNumber(reader, 15),
                Number(reader.GetString(16)),
                NullableString(reader, 17),
                NullableString(reader, 18),
                reader.GetString(19),
                reader.GetString(20),
                Timestamp(reader.GetString(21))));
        }

        return invoices;
    }

    /// <summary>
    /// Makes a transactionally consistent SQLite backup while excluding transient WAL state.
    /// </summary>
    public async Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
                ?? throw new InvalidOperationException("The SQLite backup path has no parent directory.");
            Directory.CreateDirectory(destinationDirectory);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            await using var source = await OpenConnectionAsync(cancellationToken);
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Prevents register operations while a verified datastore is being swapped in.
    /// </summary>
    public async Task ReplaceDataAsync(Func<CancellationToken, Task> replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            SqliteConnection.ClearAllPools();
            initialized = false;
            await replacement(cancellationToken);
            await EnsureInitializedAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<List<ContractChangeRecord>> LoadContractChangesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, contract_id, kind, agreement_date, effective_start_date, effective_end_date, incremental_value_ex_vat, was_provided_for_in_original_call_off, has_written_agreement, reference, created_at_utc FROM contract_changes ORDER BY agreement_date, created_at_utc, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var changes = new List<ContractChangeRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(new ContractChangeRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                (ContractChangeKind)reader.GetInt32(2),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                NullableDate(reader, 4),
                NullableDate(reader, 5),
                Number(reader.GetString(6)),
                reader.GetInt32(7) != 0,
                reader.GetInt32(8) != 0,
                NullableString(reader, 9),
                Timestamp(reader.GetString(10))));
        }

        return changes;
    }

    private static async Task<List<InvoiceContractChangeLink>> LoadInvoiceContractChangeLinksAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT invoice_id, contract_change_id FROM invoice_contract_change_links ORDER BY invoice_id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var links = new List<InvoiceContractChangeLink>();
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new InvoiceContractChangeLink(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
        }

        return links;
    }

    private static async Task<List<InvoicePlanItem>> LoadInvoicePlanItemsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, contract_id, label, expected_invoice_date, expected_value_ex_vat FROM invoice_plan_items ORDER BY contract_id, expected_invoice_date, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<InvoicePlanItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new InvoicePlanItem(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                NullableDate(reader, 3),
                Number(reader.GetString(4))));
        }

        return items;
    }

    private static async Task<List<ChargeScheduleItem>> LoadChargeScheduleItemsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, contract_id, contract_service_part_id, contract_year, description, expected_invoice_date, value_ex_vat, is_optional_extension, created_at_utc FROM charge_schedule_items ORDER BY contract_id, contract_year, expected_invoice_date, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ChargeScheduleItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ChargeScheduleItem(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetString(4),
                NullableDate(reader, 5),
                Number(reader.GetString(6)),
                reader.GetInt32(7) != 0,
                Timestamp(reader.GetString(8))));
        }

        return items;
    }

    private static async Task<List<ContractServicePart>> LoadContractServicePartsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, contract_id, name, go_live_date, sort_order, created_at_utc FROM contract_service_parts ORDER BY contract_id, sort_order, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var parts = new List<ContractServicePart>();
        while (await reader.ReadAsync(cancellationToken))
        {
            parts.Add(new ContractServicePart(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                NullableDate(reader, 3),
                reader.GetInt32(4),
                Timestamp(reader.GetString(5))));
        }

        return parts;
    }

    private static async Task<List<ContractReportingOccurrence>> LoadContractReportingOccurrencesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, contract_id, monthly_return_id, reporting_month, reported_at_utc FROM contract_reporting_occurrences ORDER BY reported_at_utc, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var occurrences = new List<ContractReportingOccurrence>();
        while (await reader.ReadAsync(cancellationToken))
        {
            occurrences.Add(new ContractReportingOccurrence(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                Timestamp(reader.GetString(4))));
        }

        return occurrences;
    }

    private static async Task<List<MonthlyReturn>> LoadMonthlyReturnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, framework, report_month, status, submitted_at_utc, submission_reference, original_workbook_name, updated_at_utc FROM monthly_returns ORDER BY framework, report_month;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var returns = new List<MonthlyReturn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            returns.Add(new MonthlyReturn(
                Guid.Parse(reader.GetString(0)),
                (FrameworkCode)reader.GetInt32(1),
                reader.GetString(2),
                (ReturnStatus)reader.GetInt32(3),
                NullableTimestamp(reader, 4),
                NullableString(reader, 5),
                NullableString(reader, 6),
                Timestamp(reader.GetString(7))));
        }

        return returns;
    }

    private static async Task<List<EvidenceRecord>> LoadEvidenceAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, kind, framework, report_month, file_name, original_relative_path, stored_relative_path, content_type, file_size_bytes, sha256, contract_reference, archived_at_utc FROM evidence ORDER BY archived_at_utc, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var evidence = new List<EvidenceRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            evidence.Add(new EvidenceRecord(
                Guid.Parse(reader.GetString(0)),
                (EvidenceKind)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : (FrameworkCode)reader.GetInt32(2),
                NullableString(reader, 3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetString(9),
                NullableString(reader, 10),
                Timestamp(reader.GetString(11))));
        }

        return evidence;
    }

    private static async Task<List<MiTemplateConfiguration>> LoadTemplatesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, framework, evidence_id, workbook_name, is_active, registered_at_utc FROM mi_templates ORDER BY framework, registered_at_utc, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var templates = new List<MiTemplateConfiguration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            templates.Add(new MiTemplateConfiguration(
                Guid.Parse(reader.GetString(0)),
                (FrameworkCode)reader.GetInt32(1),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4) != 0,
                Timestamp(reader.GetString(5))));
        }

        return templates;
    }

    private static async Task<List<FrameworkConfiguration>> LoadFrameworkConfigurationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT framework, start_date, end_date FROM framework_configurations ORDER BY framework;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var configurations = new List<FrameworkConfiguration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            configurations.Add(new FrameworkConfiguration(
                (FrameworkCode)reader.GetInt32(0),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        return configurations;
    }

    private static async Task<List<DigitalMarketplaceService>> LoadDigitalMarketplaceServicesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT framework, service_id, name FROM digital_marketplace_services ORDER BY framework, name COLLATE NOCASE, service_id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var services = new List<DigitalMarketplaceService>();
        while (await reader.ReadAsync(cancellationToken))
        {
            services.Add(new DigitalMarketplaceService(
                reader.GetString(1),
                reader.GetString(2),
                (FrameworkCode)reader.GetInt32(0)));
        }

        return services;
    }

    private static async Task<List<AuditEvent>> LoadAuditEventsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, occurred_at_utc, action, entity_type, entity_id, summary, reason, actor FROM audit_events ORDER BY occurred_at_utc DESC, id DESC;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<AuditEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new AuditEvent(
                Guid.Parse(reader.GetString(0)),
                Timestamp(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.GetString(5),
                NullableString(reader, 6),
                reader.GetString(7)));
        }

        return events;
    }

    private async Task SaveDatabaseAsync(SqliteConnection connection, SqliteTransaction transaction, RemiDatabase database, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM contract_reporting_occurrences;
            DELETE FROM contract_service_parts;
            DELETE FROM audit_events;
            DELETE FROM framework_configurations;
            DELETE FROM digital_marketplace_services;
            DELETE FROM mi_templates;
            DELETE FROM evidence;
            DELETE FROM monthly_returns;
            DELETE FROM charge_schedule_items;
            DELETE FROM invoice_plan_items;
            DELETE FROM invoice_contract_change_links;
            DELETE FROM contract_changes;
            DELETE FROM invoices;
            DELETE FROM contracts;
            """, cancellationToken);

        foreach (var contract in database.Contracts)
        {
            await InsertContractAsync(connection, transaction, contract, cancellationToken);
        }

        foreach (var invoice in database.Invoices)
        {
            await InsertInvoiceAsync(connection, transaction, invoice, cancellationToken);
        }

        foreach (var change in database.ContractChanges)
        {
            await InsertContractChangeAsync(connection, transaction, change, cancellationToken);
        }

        foreach (var link in database.InvoiceContractChangeLinks)
        {
            await InsertInvoiceContractChangeLinkAsync(connection, transaction, link, cancellationToken);
        }

        foreach (var item in database.InvoicePlanItems)
        {
            await InsertInvoicePlanItemAsync(connection, transaction, item, cancellationToken);
        }

        foreach (var item in database.ChargeScheduleItems)
        {
            await InsertChargeScheduleItemAsync(connection, transaction, item, cancellationToken);
        }

        foreach (var part in database.ContractServiceParts)
        {
            await InsertContractServicePartAsync(connection, transaction, part, cancellationToken);
        }

        foreach (var occurrence in database.ContractReportingOccurrences)
        {
            await InsertContractReportingOccurrenceAsync(connection, transaction, occurrence, cancellationToken);
        }

        foreach (var monthlyReturn in database.MonthlyReturns)
        {
            await InsertMonthlyReturnAsync(connection, transaction, monthlyReturn, cancellationToken);
        }

        foreach (var item in database.Evidence)
        {
            await InsertEvidenceAsync(connection, transaction, item, cancellationToken);
        }

        foreach (var template in database.MiTemplates)
        {
            await InsertTemplateAsync(connection, transaction, template, cancellationToken);
        }

        foreach (var configuration in database.FrameworkConfigurations)
        {
            await InsertFrameworkConfigurationAsync(connection, transaction, configuration, cancellationToken);
        }

        foreach (var service in database.DigitalMarketplaceServices)
        {
            await InsertDigitalMarketplaceServiceAsync(connection, transaction, service, cancellationToken);
        }

        foreach (var auditEvent in database.AuditEvents)
        {
            await InsertAuditEventAsync(connection, transaction, auditEvent, cancellationToken);
        }
    }

    private static async Task InsertContractAsync(SqliteConnection connection, SqliteTransaction transaction, ContractRecord item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO contracts (id, framework, supplier_reference, customer_name, customer_urn, start_date, end_date, lot_number, service_group, service_group_level_2, service_description, order_channel, digital_marketplace_service_id, total_contract_value_ex_vat, report_month, source_workbook, created_at_utc) VALUES ($id, $framework, $supplierReference, $customerName, $customerUrn, $startDate, $endDate, $lotNumber, $serviceGroup, $serviceGroupLevel2, $serviceDescription, $orderChannel, $digitalMarketplaceServiceId, $totalContractValue, $reportMonth, $sourceWorkbook, $createdAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$framework", (int)item.Framework);
        AddParameter(command, "$supplierReference", item.SupplierReference);
        AddParameter(command, "$customerName", item.CustomerName);
        AddParameter(command, "$customerUrn", item.CustomerUrn);
        AddParameter(command, "$startDate", Date(item.StartDate));
        AddParameter(command, "$endDate", Date(item.EndDate));
        AddParameter(command, "$lotNumber", item.LotNumber);
        AddParameter(command, "$serviceGroup", item.ServiceGroup);
        AddParameter(command, "$serviceGroupLevel2", item.ServiceGroupLevel2);
        AddParameter(command, "$serviceDescription", item.ServiceDescription);
        AddParameter(command, "$orderChannel", item.OrderChannel);
        AddParameter(command, "$digitalMarketplaceServiceId", item.DigitalMarketplaceServiceId);
        AddParameter(command, "$totalContractValue", Number(item.TotalContractValueExVat));
        AddParameter(command, "$reportMonth", item.ReportMonth);
        AddParameter(command, "$sourceWorkbook", item.SourceWorkbook);
        AddParameter(command, "$createdAtUtc", Timestamp(item.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInvoiceAsync(SqliteConnection connection, SqliteTransaction transaction, InvoiceRecord item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO invoices (id, framework, supplier_reference, customer_name, customer_urn, invoice_date, invoice_number, lot_number, service_group, service_group_level_2, service_description, order_channel, digital_marketplace_service_id, unit_of_measure, quantity, price_per_unit_ex_vat, total_cost_ex_vat, original_vendor, subcontractor_name, report_month, source_workbook, created_at_utc) VALUES ($id, $framework, $supplierReference, $customerName, $customerUrn, $invoiceDate, $invoiceNumber, $lotNumber, $serviceGroup, $serviceGroupLevel2, $serviceDescription, $orderChannel, $digitalMarketplaceServiceId, $unitOfMeasure, $quantity, $pricePerUnit, $totalCost, $originalVendor, $subcontractorName, $reportMonth, $sourceWorkbook, $createdAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$framework", (int)item.Framework);
        AddParameter(command, "$supplierReference", item.SupplierReference);
        AddParameter(command, "$customerName", item.CustomerName);
        AddParameter(command, "$customerUrn", item.CustomerUrn);
        AddParameter(command, "$invoiceDate", Date(item.InvoiceDate));
        AddParameter(command, "$invoiceNumber", item.InvoiceNumber);
        AddParameter(command, "$lotNumber", item.LotNumber);
        AddParameter(command, "$serviceGroup", item.ServiceGroup);
        AddParameter(command, "$serviceGroupLevel2", item.ServiceGroupLevel2);
        AddParameter(command, "$serviceDescription", item.ServiceDescription);
        AddParameter(command, "$orderChannel", item.OrderChannel);
        AddParameter(command, "$digitalMarketplaceServiceId", item.DigitalMarketplaceServiceId);
        AddParameter(command, "$unitOfMeasure", item.UnitOfMeasure);
        AddParameter(command, "$quantity", item.Quantity is null ? null : Number(item.Quantity.Value));
        AddParameter(command, "$pricePerUnit", item.PricePerUnitExVat is null ? null : Number(item.PricePerUnitExVat.Value));
        AddParameter(command, "$totalCost", Number(item.TotalCostExVat));
        AddParameter(command, "$originalVendor", item.OriginalVendor);
        AddParameter(command, "$subcontractorName", item.SubcontractorName);
        AddParameter(command, "$reportMonth", item.ReportMonth);
        AddParameter(command, "$sourceWorkbook", item.SourceWorkbook);
        AddParameter(command, "$createdAtUtc", Timestamp(item.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertContractChangeAsync(SqliteConnection connection, SqliteTransaction transaction, ContractChangeRecord item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO contract_changes (id, contract_id, kind, agreement_date, effective_start_date, effective_end_date, incremental_value_ex_vat, was_provided_for_in_original_call_off, has_written_agreement, reference, created_at_utc) VALUES ($id, $contractId, $kind, $agreementDate, $effectiveStartDate, $effectiveEndDate, $incrementalValue, $providedFor, $writtenAgreement, $reference, $createdAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$contractId", item.ContractId.ToString("D"));
        AddParameter(command, "$kind", (int)item.Kind);
        AddParameter(command, "$agreementDate", Date(item.AgreementDate));
        AddParameter(command, "$effectiveStartDate", Date(item.EffectiveStartDate));
        AddParameter(command, "$effectiveEndDate", Date(item.EffectiveEndDate));
        AddParameter(command, "$incrementalValue", Number(item.IncrementalValueExVat));
        AddParameter(command, "$providedFor", item.WasProvidedForInOriginalCallOff ? 1 : 0);
        AddParameter(command, "$writtenAgreement", item.IsConfirmed ? 1 : 0);
        AddParameter(command, "$reference", item.Reference);
        AddParameter(command, "$createdAtUtc", Timestamp(item.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInvoiceContractChangeLinkAsync(SqliteConnection connection, SqliteTransaction transaction, InvoiceContractChangeLink item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO invoice_contract_change_links (invoice_id, contract_change_id) VALUES ($invoiceId, $contractChangeId);");
        AddParameter(command, "$invoiceId", item.InvoiceId.ToString("D"));
        AddParameter(command, "$contractChangeId", item.ContractChangeId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInvoicePlanItemAsync(SqliteConnection connection, SqliteTransaction transaction, InvoicePlanItem item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO invoice_plan_items (id, contract_id, label, expected_invoice_date, expected_value_ex_vat) VALUES ($id, $contractId, $label, $expectedInvoiceDate, $expectedValue);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$contractId", item.ContractId.ToString("D"));
        AddParameter(command, "$label", item.Label);
        AddParameter(command, "$expectedInvoiceDate", Date(item.ExpectedInvoiceDate));
        AddParameter(command, "$expectedValue", Number(item.ExpectedValueExVat));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertChargeScheduleItemAsync(SqliteConnection connection, SqliteTransaction transaction, ChargeScheduleItem item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO charge_schedule_items (id, contract_id, contract_service_part_id, contract_year, description, expected_invoice_date, value_ex_vat, is_optional_extension, created_at_utc) VALUES ($id, $contractId, $contractServicePartId, $contractYear, $description, $expectedInvoiceDate, $value, $isOptionalExtension, $createdAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$contractId", item.ContractId.ToString("D"));
        AddParameter(command, "$contractServicePartId", item.ContractServicePartId?.ToString("D"));
        AddParameter(command, "$contractYear", item.ContractYear);
        AddParameter(command, "$description", item.Description);
        AddParameter(command, "$expectedInvoiceDate", Date(item.ExpectedInvoiceDate));
        AddParameter(command, "$value", Number(item.ValueExVat));
        AddParameter(command, "$isOptionalExtension", item.IsOptionalExtension ? 1 : 0);
        AddParameter(command, "$createdAtUtc", Timestamp(item.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertContractServicePartAsync(SqliteConnection connection, SqliteTransaction transaction, ContractServicePart item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO contract_service_parts (id, contract_id, name, go_live_date, sort_order, created_at_utc) VALUES ($id, $contractId, $name, $goLiveDate, $sortOrder, $createdAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$contractId", item.ContractId.ToString("D"));
        AddParameter(command, "$name", item.Name);
        AddParameter(command, "$goLiveDate", Date(item.GoLiveDate));
        AddParameter(command, "$sortOrder", item.SortOrder);
        AddParameter(command, "$createdAtUtc", Timestamp(item.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertContractReportingOccurrenceAsync(SqliteConnection connection, SqliteTransaction transaction, ContractReportingOccurrence item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO contract_reporting_occurrences (id, contract_id, monthly_return_id, reporting_month, reported_at_utc) VALUES ($id, $contractId, $monthlyReturnId, $reportingMonth, $reportedAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$contractId", item.ContractId.ToString("D"));
        AddParameter(command, "$monthlyReturnId", item.MonthlyReturnId.ToString("D"));
        AddParameter(command, "$reportingMonth", item.ReportingMonth);
        AddParameter(command, "$reportedAtUtc", Timestamp(item.ReportedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMonthlyReturnAsync(SqliteConnection connection, SqliteTransaction transaction, MonthlyReturn item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO monthly_returns (id, framework, report_month, status, submitted_at_utc, submission_reference, original_workbook_name, updated_at_utc) VALUES ($id, $framework, $reportMonth, $status, $submittedAtUtc, $submissionReference, $originalWorkbookName, $updatedAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$framework", (int)item.Framework);
        AddParameter(command, "$reportMonth", item.ReportMonth);
        AddParameter(command, "$status", (int)item.Status);
        AddParameter(command, "$submittedAtUtc", item.SubmittedAtUtc is null ? null : Timestamp(item.SubmittedAtUtc.Value));
        AddParameter(command, "$submissionReference", item.SubmissionReference);
        AddParameter(command, "$originalWorkbookName", item.OriginalWorkbookName);
        AddParameter(command, "$updatedAtUtc", Timestamp(item.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction, EvidenceRecord item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO evidence (id, kind, framework, report_month, file_name, original_relative_path, stored_relative_path, content_type, file_size_bytes, sha256, contract_reference, archived_at_utc) VALUES ($id, $kind, $framework, $reportMonth, $fileName, $originalRelativePath, $storedRelativePath, $contentType, $fileSizeBytes, $sha256, $contractReference, $archivedAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$kind", (int)item.Kind);
        AddParameter(command, "$framework", item.Framework is null ? null : (int)item.Framework.Value);
        AddParameter(command, "$reportMonth", item.ReportMonth);
        AddParameter(command, "$fileName", item.FileName);
        AddParameter(command, "$originalRelativePath", item.OriginalRelativePath);
        AddParameter(command, "$storedRelativePath", item.StoredRelativePath);
        AddParameter(command, "$contentType", item.ContentType);
        AddParameter(command, "$fileSizeBytes", item.FileSizeBytes);
        AddParameter(command, "$sha256", item.Sha256);
        AddParameter(command, "$contractReference", item.ContractReference);
        AddParameter(command, "$archivedAtUtc", Timestamp(item.ArchivedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertTemplateAsync(SqliteConnection connection, SqliteTransaction transaction, MiTemplateConfiguration item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO mi_templates (id, framework, evidence_id, workbook_name, is_active, registered_at_utc) VALUES ($id, $framework, $evidenceId, $workbookName, $isActive, $registeredAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$framework", (int)item.Framework);
        AddParameter(command, "$evidenceId", item.EvidenceId.ToString("D"));
        AddParameter(command, "$workbookName", item.WorkbookName);
        AddParameter(command, "$isActive", item.IsActive ? 1 : 0);
        AddParameter(command, "$registeredAtUtc", Timestamp(item.RegisteredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFrameworkConfigurationAsync(SqliteConnection connection, SqliteTransaction transaction, FrameworkConfiguration item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO framework_configurations (framework, start_date, end_date) VALUES ($framework, $startDate, $endDate);");
        AddParameter(command, "$framework", (int)item.Framework);
        AddParameter(command, "$startDate", Date(item.StartDate));
        AddParameter(command, "$endDate", Date(item.EndDate));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDigitalMarketplaceServiceAsync(SqliteConnection connection, SqliteTransaction transaction, DigitalMarketplaceService item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO digital_marketplace_services (framework, service_id, name) VALUES ($framework, $serviceId, $name);");
        AddParameter(command, "$framework", (int)item.Framework);
        AddParameter(command, "$serviceId", item.ServiceId);
        AddParameter(command, "$name", item.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedDigitalMarketplaceServicesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        foreach (var service in MarketplaceCatalogues.ForFramework(FrameworkCode.GCloud14))
        {
            await InsertDigitalMarketplaceServiceAsync(
                connection,
                transaction,
                new DigitalMarketplaceService(service.MarketplaceServiceId, service.ProductName),
                cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task InsertAuditEventAsync(SqliteConnection connection, SqliteTransaction transaction, AuditEvent item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO audit_events (id, occurred_at_utc, action, entity_type, entity_id, summary, reason, actor) VALUES ($id, $occurredAtUtc, $action, $entityType, $entityId, $summary, $reason, $actor);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$occurredAtUtc", Timestamp(item.OccurredAtUtc));
        AddParameter(command, "$action", item.Action);
        AddParameter(command, "$entityType", item.EntityType);
        AddParameter(command, "$entityId", item.EntityId?.ToString("D"));
        AddParameter(command, "$summary", item.Summary);
        AddParameter(command, "$reason", item.Reason);
        AddParameter(command, "$actor", item.Actor);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        return command;
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string commandText)
    {
        var command = CreateCommand(connection, commandText);
        command.Transaction = transaction;
        return command;
    }

    private async Task ApplySchemaMigrationsAsync(
        SqliteConnection connection,
        bool existingDatabase,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, """
            CREATE TABLE IF NOT EXISTS remi_schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            );
            """, cancellationToken);

        var applied = new HashSet<int>();
        await using (var command = CreateCommand(connection, "SELECT version FROM remi_schema_migrations ORDER BY version;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        if (!applied.Contains(1))
        {
            await RecordMigrationAsync(connection, null, 1, "Baseline register schema", cancellationToken);
            applied.Add(1);
        }

        if (!applied.Contains(2))
        {
            if (existingDatabase)
            {
                await CreateAutomaticMigrationBackupAsync(connection, 2, cancellationToken);
            }

            using var transaction = connection.BeginTransaction();
            if (!await ColumnExistsAsync(connection, transaction, "charge_schedule_items", "is_optional_extension", cancellationToken))
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE charge_schedule_items ADD COLUMN is_optional_extension INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            }
            if (!await ColumnExistsAsync(connection, transaction, "charge_schedule_items", "contract_service_part_id", cancellationToken))
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE charge_schedule_items ADD COLUMN contract_service_part_id TEXT NULL;", cancellationToken);
            }

            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS contract_service_parts (
                    id TEXT PRIMARY KEY,
                    contract_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    go_live_date TEXT NULL,
                    sort_order INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_contract_service_parts_contract
                    ON contract_service_parts (contract_id, sort_order);

                CREATE TABLE IF NOT EXISTS contract_reporting_occurrences (
                    id TEXT PRIMARY KEY,
                    contract_id TEXT NOT NULL UNIQUE,
                    monthly_return_id TEXT NOT NULL,
                    reporting_month TEXT NOT NULL,
                    reported_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_contract_reporting_occurrences_return
                    ON contract_reporting_occurrences (monthly_return_id);

                CREATE TABLE IF NOT EXISTS mail_templates (
                    event_type TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    trigger_mode INTEGER NOT NULL,
                    subject_template TEXT NOT NULL,
                    greeting TEXT NOT NULL,
                    introduction TEXT NOT NULL,
                    request_text TEXT NOT NULL,
                    closing TEXT NOT NULL,
                    signature TEXT NOT NULL,
                    schedule_day INTEGER NULL,
                    schedule_time_local TEXT NULL,
                    time_zone_id TEXT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS mail_template_recipients (
                    id TEXT PRIMARY KEY,
                    event_type TEXT NOT NULL,
                    recipient_type INTEGER NOT NULL,
                    display_name TEXT NULL,
                    email_address TEXT NOT NULL,
                    sort_order INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_mail_template_recipients_template
                    ON mail_template_recipients (event_type, recipient_type, sort_order);

                CREATE TABLE IF NOT EXISTS mail_messages (
                    id TEXT PRIMARY KEY,
                    event_type TEXT NOT NULL,
                    delivery_key TEXT NOT NULL UNIQUE,
                    processing_state INTEGER NOT NULL,
                    delivery_mode INTEGER NOT NULL,
                    scheduled_for_utc TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    captured_at_utc TEXT NULL,
                    subject TEXT NOT NULL,
                    related_entity_type TEXT NULL,
                    related_entity_id TEXT NULL,
                    source_period TEXT NULL,
                    failure_summary TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_mail_messages_created
                    ON mail_messages (created_at_utc DESC);

                CREATE INDEX IF NOT EXISTS ix_mail_messages_event_period
                    ON mail_messages (event_type, source_period);

                CREATE TABLE IF NOT EXISTS mail_recipients (
                    id TEXT PRIMARY KEY,
                    mail_message_id TEXT NOT NULL,
                    recipient_type INTEGER NOT NULL,
                    display_name TEXT NULL,
                    email_address TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_mail_recipients_message
                    ON mail_recipients (mail_message_id, recipient_type);

                CREATE TABLE IF NOT EXISTS mail_contents (
                    id TEXT PRIMARY KEY,
                    mail_message_id TEXT NOT NULL,
                    attempt_number INTEGER NOT NULL,
                    storage_key TEXT NOT NULL UNIQUE,
                    size_bytes INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    UNIQUE (mail_message_id, attempt_number)
                );

                CREATE TABLE IF NOT EXISTS mail_events (
                    id TEXT PRIMARY KEY,
                    mail_message_id TEXT NOT NULL,
                    event_type INTEGER NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    summary TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_mail_events_message
                    ON mail_events (mail_message_id, occurred_at_utc);

                CREATE TABLE IF NOT EXISTS mail_scheduler_state (
                    event_type TEXT PRIMARY KEY,
                    last_evaluated_period TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                INSERT INTO contract_service_parts (id, contract_id, name, go_live_date, sort_order, created_at_utc)
                SELECT lower(hex(randomblob(16))), contracts.id,
                       COALESCE(NULLIF(trim(contracts.service_description), ''), 'Whole contract'),
                       NULL, 0, contracts.created_at_utc
                FROM contracts
                WHERE NOT EXISTS (
                    SELECT 1 FROM contract_service_parts parts WHERE parts.contract_id = contracts.id
                );

                INSERT INTO contract_reporting_occurrences (id, contract_id, monthly_return_id, reporting_month, reported_at_utc)
                SELECT lower(hex(randomblob(16))), contracts.id, monthly_returns.id, contracts.report_month,
                       COALESCE(monthly_returns.submitted_at_utc, monthly_returns.updated_at_utc)
                FROM contracts
                INNER JOIN monthly_returns
                    ON monthly_returns.framework = contracts.framework
                   AND monthly_returns.report_month = contracts.report_month
                   AND monthly_returns.status = 1
                WHERE NOT EXISTS (
                    SELECT 1 FROM contract_reporting_occurrences occurrences
                    WHERE occurrences.contract_id = contracts.id
                );
                """, cancellationToken);

            await RecordMigrationAsync(
                connection,
                transaction,
                2,
                "Operational contract parts and Capture mail foundation",
                cancellationToken);
            transaction.Commit();
            applied.Add(2);
        }

        if (!applied.Contains(3))
        {
            if (existingDatabase)
            {
                await CreateAutomaticMigrationBackupAsync(connection, 3, cancellationToken);
            }

            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, """
                UPDATE mail_templates
                SET subject_template = 'Framework MI submission accepted - {{reporting_month}}',
                    greeting = 'Hi,',
                    introduction = 'I hope this message finds you well.' || char(10) || char(10) ||
                        'I''m pleased to inform you that the G-Cloud and VAS monitoring information has been accepted by GCA.',
                    request_text = 'This month we reported the following values.',
                    closing = 'This concludes our reporting obligations for this month.',
                    signature = 'Take care' || char(10) || 'Marcin',
                    updated_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE event_type = 'post-submission-report'
                  AND introduction = 'The framework MI return has been submitted.'
                  AND request_text = 'Submission evidence will be included when this event is enabled.';
                """, cancellationToken);
            await RecordMigrationAsync(
                connection,
                transaction,
                3,
                "Post-submission evidence mail renderer defaults",
                cancellationToken);
            transaction.Commit();
            applied.Add(3);
        }

        if (!applied.Contains(4))
        {
            if (existingDatabase)
            {
                await CreateAutomaticMigrationBackupAsync(connection, 4, cancellationToken);
            }

            using var transaction = connection.BeginTransaction();
            if (!await ColumnExistsAsync(connection, transaction, "mail_templates", "body_template", cancellationToken))
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE mail_templates ADD COLUMN body_template TEXT NOT NULL DEFAULT '';", cancellationToken);
            }
            await ExecuteAsync(connection, transaction, """
                UPDATE mail_templates
                SET body_template = greeting || char(10) || char(10) ||
                    introduction || char(10) || char(10) ||
                    request_text ||
                    CASE event_type
                        WHEN 'monthly-active-contracts' THEN char(10) || char(10) || '{{active_contracts}}'
                        WHEN 'customer-go-live' THEN char(10) || char(10) || '{{operational_parts}}'
                        WHEN 'post-submission-report' THEN char(10) || char(10) || '{{submission_evidence}}'
                        WHEN 'expiring-contracts' THEN char(10) || char(10) || '{{expiring_contracts}}'
                        ELSE ''
                    END || char(10) || char(10) ||
                    closing || char(10) || char(10) || signature
                WHERE trim(body_template) = '';
                """, cancellationToken);
            await RecordMigrationAsync(
                connection,
                transaction,
                4,
                "Single-body mail templates with explicit content placements",
                cancellationToken);
            transaction.Commit();
            applied.Add(4);
        }

        if (!applied.Contains(5))
        {
            if (existingDatabase)
            {
                await CreateAutomaticMigrationBackupAsync(connection, 5, cancellationToken);
            }

            using var transaction = connection.BeginTransaction();
            if (!await ColumnExistsAsync(connection, transaction, "digital_marketplace_services", "framework", cancellationToken))
            {
                await ExecuteAsync(connection, transaction, $"""
                    ALTER TABLE digital_marketplace_services RENAME TO digital_marketplace_services_legacy;

                    CREATE TABLE digital_marketplace_services (
                        framework INTEGER NOT NULL,
                        service_id TEXT NOT NULL,
                        name TEXT NOT NULL,
                        PRIMARY KEY (framework, service_id)
                    );

                    INSERT INTO digital_marketplace_services (framework, service_id, name)
                    SELECT {(int)FrameworkCode.GCloud14}, service_id, name
                    FROM digital_marketplace_services_legacy;

                    DROP TABLE digital_marketplace_services_legacy;
                    """, cancellationToken);
            }
            await RecordMigrationAsync(
                connection,
                transaction,
                5,
                "Framework-specific Digital Marketplace services",
                cancellationToken);
            transaction.Commit();
            applied.Add(5);
        }

        if (!applied.Contains(6))
        {
            if (existingDatabase)
            {
                await CreateAutomaticMigrationBackupAsync(connection, 6, cancellationToken);
            }

            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, """
                UPDATE mail_templates
                SET trigger_mode = 1,
                    schedule_day = NULL,
                    schedule_time_local = NULL,
                    time_zone_id = NULL
                WHERE trigger_mode <> 1
                   OR schedule_day IS NOT NULL
                   OR schedule_time_local IS NOT NULL
                   OR time_zone_id IS NOT NULL;

                DELETE FROM mail_scheduler_state;
                """, cancellationToken);
            await RecordMigrationAsync(
                connection,
                transaction,
                6,
                "Manual-only mail event triggers",
                cancellationToken);
            transaction.Commit();
            applied.Add(6);
        }

        if (!applied.Contains(7))
        {
            if (existingDatabase)
            {
                await CreateAutomaticMigrationBackupAsync(connection, 7, cancellationToken);
            }

            using var transaction = connection.BeginTransaction();
            if (!await ColumnExistsAsync(connection, transaction, "framework_configurations", "end_date", cancellationToken))
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE framework_configurations ADD COLUMN end_date TEXT NULL;", cancellationToken);
            }
            await RecordMigrationAsync(
                connection,
                transaction,
                7,
                "Framework operational end dates",
                cancellationToken);
            transaction.Commit();
            applied.Add(7);
        }

        if (!applied.Contains(8))
        {
            if (existingDatabase)
            {
                await CreateAutomaticMigrationBackupAsync(connection, 8, cancellationToken);
            }

            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, $"""
                UPDATE evidence
                SET contract_reference = (
                        SELECT contracts.supplier_reference
                        FROM contracts
                        WHERE contracts.framework = evidence.framework
                          AND lower(substr(evidence.file_name, 1, length(contracts.supplier_reference) + 1)) =
                              lower(contracts.supplier_reference || '_')
                        ORDER BY length(contracts.supplier_reference) DESC
                        LIMIT 1
                    ),
                    kind = {(int)EvidenceKind.ContractDocument}
                WHERE evidence.kind = {(int)EvidenceKind.SupportingDocument}
                  AND evidence.contract_reference IS NULL
                  AND lower(evidence.original_relative_path) NOT LIKE 'clipboard/%'
                  AND EXISTS (
                        SELECT 1
                        FROM contracts
                        WHERE contracts.framework = evidence.framework
                          AND lower(substr(evidence.file_name, 1, length(contracts.supplier_reference) + 1)) =
                              lower(contracts.supplier_reference || '_')
                    );
                """, cancellationToken);
            await RecordMigrationAsync(
                connection,
                transaction,
                8,
                "Associate suffixed migrated contract documents",
                cancellationToken);
            transaction.Commit();
            applied.Add(8);
        }
    }

    private async Task CreateAutomaticMigrationBackupAsync(
        SqliteConnection source,
        int targetVersion,
        CancellationToken cancellationToken)
    {
        var dataDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The Remi data path has no parent directory.");
        var backupDirectory = Path.Combine(dataDirectory, "migration-backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(
            backupDirectory,
            $"remi-data-before-schema-v{targetVersion}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int version,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "INSERT INTO remi_schema_migrations (version, name, applied_at_utc) VALUES ($version, $name, $appliedAtUtc);");
        command.Transaction = transaction;
        AddParameter(command, "$version", version);
        AddParameter(command, "$name", name);
        AddParameter(command, "$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");");
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;");
        AddParameter(command, "$name", name);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string commandText, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, commandText);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateOnly? NullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateOnly.ParseExact(reader.GetString(ordinal), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static decimal? NullableNumber(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Number(reader.GetString(ordinal));

    private static DateTimeOffset? NullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Timestamp(reader.GetString(ordinal));

    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Timestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    private static decimal Number(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
