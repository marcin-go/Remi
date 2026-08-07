using System.Globalization;
using Microsoft.Data.Sqlite;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

/// <summary>
/// Stores the Remi register in SQLite. Every registered record is held in a first-class table;
/// Remi deliberately does not carry a legacy JSON store or in-place schema upgrade path.
/// </summary>
public sealed class SqliteRemiStore : IRemiStore, IRemiDataResetter
{
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
            await EnsureChargeScheduleOptionalExtensionColumnAsync(connection, cancellationToken);
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
            var digitalMarketplaceServicesExist = await TableExistsAsync(connection, "digital_marketplace_services", cancellationToken);
            await CreateSchemaAsync(connection, cancellationToken);
            if (!digitalMarketplaceServicesExist)
            {
                await SeedDigitalMarketplaceServicesAsync(connection, cancellationToken);
            }
            await EnsureChargeScheduleOptionalExtensionColumnAsync(connection, cancellationToken);
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
                start_date TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS digital_marketplace_services (
                service_id TEXT PRIMARY KEY,
                name TEXT NOT NULL
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
        await using var command = CreateCommand(connection, "SELECT id, contract_id, contract_year, description, expected_invoice_date, value_ex_vat, is_optional_extension, created_at_utc FROM charge_schedule_items ORDER BY contract_id, contract_year, expected_invoice_date, id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ChargeScheduleItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ChargeScheduleItem(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                NullableDate(reader, 4),
                Number(reader.GetString(5)),
                reader.GetInt32(6) != 0,
                Timestamp(reader.GetString(7))));
        }

        return items;
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
        await using var command = CreateCommand(connection, "SELECT framework, start_date FROM framework_configurations ORDER BY framework;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var configurations = new List<FrameworkConfiguration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            configurations.Add(new FrameworkConfiguration(
                (FrameworkCode)reader.GetInt32(0),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        return configurations;
    }

    private static async Task<List<DigitalMarketplaceService>> LoadDigitalMarketplaceServicesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT service_id, name FROM digital_marketplace_services ORDER BY name COLLATE NOCASE, service_id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var services = new List<DigitalMarketplaceService>();
        while (await reader.ReadAsync(cancellationToken))
        {
            services.Add(new DigitalMarketplaceService(reader.GetString(0), reader.GetString(1)));
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
        await using var command = CreateCommand(connection, transaction, "INSERT INTO charge_schedule_items (id, contract_id, contract_year, description, expected_invoice_date, value_ex_vat, is_optional_extension, created_at_utc) VALUES ($id, $contractId, $contractYear, $description, $expectedInvoiceDate, $value, $isOptionalExtension, $createdAtUtc);");
        AddParameter(command, "$id", item.Id.ToString("D"));
        AddParameter(command, "$contractId", item.ContractId.ToString("D"));
        AddParameter(command, "$contractYear", item.ContractYear);
        AddParameter(command, "$description", item.Description);
        AddParameter(command, "$expectedInvoiceDate", Date(item.ExpectedInvoiceDate));
        AddParameter(command, "$value", Number(item.ValueExVat));
        AddParameter(command, "$isOptionalExtension", item.IsOptionalExtension ? 1 : 0);
        AddParameter(command, "$createdAtUtc", Timestamp(item.CreatedAtUtc));
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
        await using var command = CreateCommand(connection, transaction, "INSERT INTO framework_configurations (framework, start_date) VALUES ($framework, $startDate);");
        AddParameter(command, "$framework", (int)item.Framework);
        AddParameter(command, "$startDate", Date(item.StartDate));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDigitalMarketplaceServiceAsync(SqliteConnection connection, SqliteTransaction transaction, DigitalMarketplaceService item, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "INSERT INTO digital_marketplace_services (service_id, name) VALUES ($serviceId, $name);");
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

    /// <summary>
    /// Adds the optional-extension marker introduced for payment-position presentation. This is a
    /// narrow, backwards-compatible extension of Remi's current SQLite register rather than an
    /// upgrade path for legacy prototypes.
    /// </summary>
    private static async Task EnsureChargeScheduleOptionalExtensionColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var exists = false;
        {
            await using var command = CreateCommand(connection, "PRAGMA table_info(charge_schedule_items);");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "is_optional_extension", StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            await ExecuteAsync(connection, null, "ALTER TABLE charge_schedule_items ADD COLUMN is_optional_extension INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }
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
