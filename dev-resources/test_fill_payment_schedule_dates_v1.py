import datetime as dt
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

from fill_payment_schedule_dates_v1 import annual_date, plan, snapshot, apply_changes, verify_changes, backup_complete_data, connect, digest


class DateFillTests(unittest.TestCase):
    def fixture(self, start="2024-02-29", dates=(None, None, None), years=None):
        connection = sqlite3.connect(":memory:")
        connection.row_factory = sqlite3.Row
        connection.executescript("""
            CREATE TABLE remi_schema_migrations(version INTEGER PRIMARY KEY);
            CREATE TABLE contracts(id TEXT PRIMARY KEY,supplier_reference TEXT,start_date TEXT);
            CREATE TABLE charge_schedule_items(id TEXT PRIMARY KEY,contract_id TEXT,contract_year INTEGER,
                description TEXT,expected_invoice_date TEXT,value_ex_vat TEXT,is_optional_extension INTEGER);
            CREATE TABLE audit_events(id TEXT PRIMARY KEY,occurred_at_utc TEXT,action TEXT,entity_type TEXT,entity_id TEXT,summary TEXT,reason TEXT,actor TEXT);
            CREATE TABLE preserved_metadata(id TEXT PRIMARY KEY,value TEXT);
            INSERT INTO preserved_metadata VALUES ('key', 'retained evidence and reference metadata');
            INSERT INTO audit_events VALUES ('old', '2026-01-01', 'Prior event', 'Contract', 'c', 'Keep me', NULL, 'User');
        """)
        connection.executemany("INSERT INTO remi_schema_migrations VALUES (?)", [(year,) for year in range(1, 10)])
        connection.execute("INSERT INTO contracts VALUES ('c','TEST',?)", (start,))
        connection.executemany("INSERT INTO charge_schedule_items VALUES (?,'c',?,'Licence',?,'100',?)",
            [(str(index), (years or list(range(1, len(dates) + 1)))[index], date, int(index > 1)) for index, date in enumerate(dates)])
        connection.commit()
        self.addCleanup(connection.close)
        return connection

    def test_all_missing_dates_use_contract_year_not_row_number(self):
        result = plan(self.fixture(years=[1, 1, 4]))
        self.assertEqual(["2024-02-29", "2024-02-29", "2027-02-28"], [row["expected_date"] for row in result["changes"]])
        self.assertEqual(dt.date(2028, 2, 29), annual_date(dt.date(2024, 2, 29), 4))

    def test_latest_supplied_year_overrides_start_and_earlier_anchor(self):
        result = plan(self.fixture(dates=["2024-04-15", "2025-06-30", None, None]))
        self.assertEqual(["2026-06-30", "2027-06-30"], [row["expected_date"] for row in result["changes"]])
        self.assertTrue(all(row["basis"] == "last supplied expected date" for row in result["changes"]))

    def test_no_backwards_extrapolation_for_earlier_gaps(self):
        result = plan(self.fixture(dates=[None, "2025-03-01", None]))
        self.assertEqual(["2026-03-01"], [row["expected_date"] for row in result["changes"]])
        self.assertEqual(1, len(result["skipped"]))

    def test_missing_start_and_ambiguous_last_year_are_skipped(self):
        self.assertEqual([], plan(self.fixture(start=None))["changes"])
        result = plan(self.fixture(dates=["2025-03-01", "2025-04-01", None], years=[2, 2, 3]))
        self.assertEqual([], result["changes"])
        self.assertEqual(1, len(result["skipped"]))

    def test_version_nine_data_correction_is_additive_audited_and_idempotent(self):
        connection = self.fixture(dates=["2024-03-01", None, None])
        before = snapshot(connection)
        proposal = plan(connection)
        connection.execute("BEGIN IMMEDIATE")
        ids = apply_changes(connection, proposal, Path("verified-backup"), "Test")
        verify_changes(before, snapshot(connection), proposal, ids)
        self.assertEqual(3, len(ids))  # two positions and their contract history event
        self.assertEqual([], plan(connection)["changes"])
        self.assertEqual(before["preserved_metadata"], snapshot(connection)["preserved_metadata"])
        self.assertEqual(before["remi_schema_migrations"], snapshot(connection)["remi_schema_migrations"])
        self.assertEqual("2024-03-01", connection.execute("SELECT expected_invoice_date FROM charge_schedule_items WHERE id='0'").fetchone()[0])
        connection.rollback()
        self.assertEqual(before, snapshot(connection))

    def test_full_folder_backup_preserves_keys_evidence_and_other_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            data = root / "source" / "data"
            data.mkdir(parents=True)
            database = data / "remi-data.db"
            with closing(sqlite3.connect(database)) as destination:
                self.fixture().backup(destination)
            for name in ["protection-keys/key.xml", "evidence/document.bin", "logs/app.log", "reference-data/customers.json", "mail/message.eml", "migration-backups/older/remi-data.db"]:
                path = data / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"Preserve me")
            with closing(connect(database, write=True)) as connection:
                connection.execute("PRAGMA journal_mode=WAL")
                connection.execute("BEGIN IMMEDIATE")
                backup = backup_complete_data(database, root / "backups", snapshot(connection))
                manifest = json.loads((backup / "verified-manifest.json").read_text())
                for name, sha in manifest["files"].items():
                    self.assertEqual(sha, digest(backup / "data" / name))
                self.assertEqual((data / "protection-keys/key.xml").read_bytes(), (backup / "data/protection-keys/key.xml").read_bytes())
                self.assertEqual(b"Preserve me", (backup / "data/migration-backups/older/remi-data.db").read_bytes())
                connection.rollback()


if __name__ == "__main__":
    unittest.main()
