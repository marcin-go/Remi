"""User-authorised, versioned data correction; dry-run by default, no schema change.

All-undated schedules use contract start + (contract_year - 1) years. Otherwise
only years after the last explicitly dated year are filled from that year's date.
Earlier gaps and conflicting dates in the last dated year require review.
"""
import argparse
import calendar
import collections
from contextlib import closing
import datetime as dt
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import uuid

VERSION = "payment-schedule-dates-v1"
DEFAULT_DATABASE = Path(__file__).resolve().parents[1] / "publish/Remi/data/remi-data.db"
SIDECARS = {"remi-data.db-wal", "remi-data.db-shm", "remi-data.db-journal"}


def annual_date(anchor, increment):
    year = anchor.year + increment
    return anchor.replace(year=year, day=min(anchor.day, calendar.monthrange(year, anchor.month)[1]))


def connect(path, write=False):
    connection = sqlite3.connect(path.resolve().as_uri() + ("?mode=rw" if write else "?mode=ro"), uri=True, timeout=30)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA foreign_keys=ON")
    if not write:
        connection.execute("PRAGMA query_only=ON")
    return connection


def plan(connection):
    versions = [row[0] for row in connection.execute("SELECT version FROM remi_schema_migrations ORDER BY version")]
    if versions != list(range(1, 10)):
        raise ValueError("This data correction is tested against schema version 9 only.")
    grouped = collections.defaultdict(list)
    for row in connection.execute("""SELECT s.*, c.supplier_reference, c.start_date
        FROM charge_schedule_items s JOIN contracts c ON c.id=s.contract_id
        ORDER BY s.contract_id, s.contract_year, s.id"""):
        grouped[row["contract_id"]].append(dict(row))
    changes, skipped = [], []
    for contract_id, positions in grouped.items():
        missing = [row for row in positions if not row["expected_invoice_date"]]
        if not missing:
            continue
        reference = positions[0]["supplier_reference"]
        supplied = [row for row in positions if row["expected_invoice_date"]]
        try:
            if supplied:
                anchor_year = max(row["contract_year"] for row in supplied)
                dates = {row["expected_invoice_date"] for row in supplied if row["contract_year"] == anchor_year}
                if len(dates) != 1:
                    raise ValueError("Conflicting dates in the last supplied contract year")
                anchor = dt.date.fromisoformat(dates.pop())
                basis = "last supplied expected date"
            else:
                anchor_year = 1
                anchor = dt.date.fromisoformat(positions[0]["start_date"] or "")
                basis = "contract start date"
        except (ValueError, TypeError) as error:
            skipped.append({"contract_id": contract_id, "reference": reference, "positions": len(missing), "reason": str(error)})
            continue
        for row in missing:
            if row["contract_year"] < 1 or (supplied and row["contract_year"] <= anchor_year):
                skipped.append({"position_id": row["id"], "reference": reference, "reason": "Invalid year or gap at/before the last supplied date; not extrapolated backwards"})
                continue
            expected = annual_date(anchor, row["contract_year"] - anchor_year)
            changes.append({"position_id": row["id"], "contract_id": contract_id, "reference": reference,
                "year": row["contract_year"], "description": row["description"], "previous_date": row["expected_invoice_date"],
                "expected_date": expected.isoformat(), "anchor_date": anchor.isoformat(), "anchor_year": anchor_year,
                "basis": basis, "optional": bool(row["is_optional_extension"])})
    return {"operation": VERSION, "schema_version": 9, "changes": changes, "skipped": skipped}


def snapshot(connection):
    tables = [row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
    return {name: sorted([dict(row) for row in connection.execute('SELECT * FROM "' + name.replace('"', '""') + '"')], key=repr) for name in tables}


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def files(folder):
    result = {}
    for path in sorted(folder.rglob("*")):
        if path.is_symlink():
            raise ValueError("Data backup refuses symbolic links: " + str(path))
        if path.is_file() and not (path.parent == folder and (path.name == "remi-data.db" or path.name in SIDECARS)):
            result[path.relative_to(folder).as_posix()] = digest(path)
    return result


def backup_complete_data(database, backup_root, before):
    """Caller holds SQLite's write reservation: other application updates cannot interleave.

    SQLite backup folds any WAL into the recoverable database. Transient sidecars
    contain live locks and must not be copied over that consistent snapshot.
    """
    folder = backup_root / (VERSION + "-" + dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8])
    folder.mkdir(parents=True)
    data = folder / "data"
    source_hashes = files(database.parent)
    shutil.copytree(database.parent, data, ignore=lambda directory, names: [name for name in names if Path(directory) == database.parent and (name == database.name or name in SIDECARS)])
    with closing(connect(database)) as source, closing(sqlite3.connect(data / database.name)) as destination:
        source.backup(destination)
    with closing(connect(data / database.name)) as verification:
        if verification.execute("PRAGMA integrity_check").fetchone()[0] != "ok" or snapshot(verification) != before:
            raise ValueError("The database backup failed verification; no changes applied.")
    if files(data) != source_hashes or files(database.parent) != source_hashes:
        raise ValueError("A data file changed during backup; no changes applied. Retry when the application is idle.")
    manifest = {"operation": VERSION, "database": str(database), "files": {**source_hashes, database.name: digest(data / database.name)}, "database_integrity": "ok"}
    (folder / "verified-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    (folder / "RESTORE.txt").write_text("Stop Remi before recovery. The data subfolder is the complete recoverable data folder, including evidence, reference data, mail, logs and protection keys. Preserve the current folder before replacing it. SQLite's native backup includes committed WAL contents in the verified database snapshot. Do not overlay live or old SQLite sidecars on the restored database.\n", encoding="utf-8")
    return folder


def apply_changes(connection, proposal, backup_path, actor):
    timestamp = dt.datetime.now(dt.timezone.utc).isoformat()
    new_audits = []
    by_contract = collections.defaultdict(list)
    for item in proposal["changes"]:
        cursor = connection.execute("UPDATE charge_schedule_items SET expected_invoice_date=? WHERE id=? AND expected_invoice_date IS ?",
            (item["expected_date"], item["position_id"], item["previous_date"]))
        if cursor.rowcount != 1:
            raise ValueError("The payment position changed since preview.")
        reason = json.dumps({"operation": VERSION, "user_rule": item["basis"], "anchor_date": item["anchor_date"], "anchor_year": item["anchor_year"], "previous_date": item["previous_date"], "backup": str(backup_path)})
        new_audits.append((str(uuid.uuid4()), timestamp, "ChargeScheduleUpdated", "ChargeSchedule", item["position_id"],
            f"Filled expected date for {item['reference']} year {item['year']} ({item['description']}): not recorded to {item['expected_date']}.", reason, actor))
        by_contract[item["contract_id"]].append(item)
    for contract_id, items in by_contract.items():
        new_audits.append((str(uuid.uuid4()), timestamp, "ContractPaymentScheduleDatesFilled", "Contract", contract_id,
            f"Filled {len(items)} missing expected payment dates for {items[0]['reference']} using annual increments. Existing dates, values and optional flags retained.",
            json.dumps({"operation": VERSION, "changes": [{"position_id": item["position_id"], "expected_date": item["expected_date"]} for item in items], "backup": str(backup_path)}), actor))
    connection.executemany("INSERT INTO audit_events (id,occurred_at_utc,action,entity_type,entity_id,summary,reason,actor) VALUES (?,?,?,?,?,?,?,?)", new_audits)
    return {item[0] for item in new_audits}


def verify_changes(before, after, proposal, audit_ids):
    expected_dates = {item["position_id"]: item["expected_date"] for item in proposal["changes"]}
    expected = {name: [dict(row) for row in rows] for name, rows in before.items()}
    for row in expected["charge_schedule_items"]:
        if row["id"] in expected_dates:
            row["expected_invoice_date"] = expected_dates[row["id"]]
    after_old = {name: rows for name, rows in after.items()}
    after_old["audit_events"] = [row for row in after["audit_events"] if row["id"] not in audit_ids]
    if {row["id"] for row in after["audit_events"]} - {row["id"] for row in before["audit_events"]} != audit_ids:
        raise ValueError("Unexpected audit changes")
    if {name: sorted(rows, key=repr) for name, rows in expected.items()} != after_old:
        raise ValueError("Unexpected changes outside planned dates and appended audit events")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path, default=DEFAULT_DATABASE)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--expected-plan", type=Path)
    parser.add_argument("--backup-root", type=Path)
    args = parser.parse_args()
    database = args.database.resolve()
    if database != DEFAULT_DATABASE.resolve():
        raise ValueError("This operation targets only the existing published portable database.")
    args.report.parent.mkdir(parents=True, exist_ok=True)
    if args.backup_root and args.backup_root.resolve().is_relative_to(database.parent):
        raise ValueError("The backup destination must be outside the published data folder.")
    with closing(connect(database, write=args.apply)) as connection, connection:
        connection.execute("BEGIN IMMEDIATE" if args.apply else "BEGIN")
        proposal = plan(connection)
        if args.apply and proposal["changes"]:
            if not args.expected_plan or not args.backup_root:
                raise ValueError("Apply requires a saved preview and complete-folder backup destination.")
            if proposal != json.loads(args.expected_plan.read_text(encoding="utf-8")):
                raise ValueError("The proposal differs from the reviewed preview; run a fresh preview first.")
            before = snapshot(connection)
            existing_fk_errors = [tuple(row) for row in connection.execute("PRAGMA foreign_key_check")]
            backup = backup_complete_data(database, args.backup_root.resolve(), before)
            print("Verified complete-folder backup:", backup, flush=True)
            audit_ids = apply_changes(connection, proposal, backup, "Codex (user-authorised bulk update)")
            verify_changes(before, snapshot(connection), proposal, audit_ids)
            if connection.execute("PRAGMA integrity_check").fetchone()[0] != "ok" or [tuple(row) for row in connection.execute("PRAGMA foreign_key_check")] != existing_fk_errors:
                raise ValueError("Integrity verification failed; rolling back.")
            connection.commit()
            with closing(connect(database)) as verification:
                verify_changes(before, snapshot(verification), proposal, audit_ids)
            proposal = {**proposal, "applied": True, "backup": str(backup), "audit_events_added": len(audit_ids)}
        else:
            connection.rollback()
    args.report.write_text(json.dumps(proposal, indent=2), encoding="utf-8")
    print(json.dumps({"positions": len(proposal["changes"]), "contracts": len({item["contract_id"] for item in proposal["changes"]}), "skipped": len(proposal["skipped"]), "applied": proposal.get("applied", False), "report": str(args.report)}))


if __name__ == "__main__":
    main()
