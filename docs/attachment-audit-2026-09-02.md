# Attachment intake audit — 2 September 2026

## Outcome and scope

All eight document-attachment workflows use the repaired shared uploader. Each has automated coverage for saving 20 distinct attachments and recovering from a failure after seven uploads. Each also passed a live, native file-picker check with 20 images in the published portable application: 20 editable document rows, 20 fully loaded previews, and no intake errors.

The shared JavaScript intake has separate 20-file tests for a single disk selection, two disk selections, drag-and-drop, three browser clipboard representations, repeated pastes, and mixed methods. The existing 15 MB **per-file** limit remains; there is no attachment-count limit of 20.

| Attachment workflow | Live disk intake | Save/reopen 20 | Partial failure/retry |
| --- | --- | --- | --- |
| Contract registration | 20/20 | Passed | Passed |
| Contract editing | 20/20 | Passed | Passed |
| Contract-change registration | 20/20 | Passed | Passed |
| Contract-change editing | 20/20 | Passed | Passed |
| Invoice registration | 20/20 | Passed | Passed |
| Invoice editing | 20/20 | Passed | Passed |
| Monthly submission recording | 20/20 | Passed | Passed |
| Monthly submission editing | 20/20 | Passed | Passed |

The template-workbook selector and maintenance backup-package selector are single-purpose imports, not document-attachment forms, and remain single-file operations. Nil/corrected submissions and invoice/credit-note entry use the corresponding shared forms above.

## Defects found and corrections

1. **Image bytes crossed the Blazor message channel.** The previous document-added callback included full base64 previews. Ordinary screenshots could exceed the default 32 KB incoming message limit, leaving a file in browser state without a visible row. A subsequent paste then reported a duplicate. Previews now use short browser-local blob URLs; file bytes travel only in multipart HTTP uploads. Live fixtures were about 88 KB each, above the old message limit.
2. **Preparation could depend on a callback to the server that was already waiting for preparation.** The browser queue now contains browser work only. Notifications do not gate intake or saving. Save reads an authoritative, revisioned metadata snapshot after preparation completes, even if notifications are delayed.
3. **Several forms relied on a stale pending-file count.** Every save now prepares the queue before recording changes and invokes the archive step after a successful record save. Late notifications cannot silently skip attachments.
4. **Partial saves could lose the retry context or duplicate a newly created record.** Successfully uploaded documents leave the queue individually; unfinished ones remain visible. New contracts, invoices, changes, and submission records retain their saved identifiers for retry. Failed uploads keep editors open. A repeated HTTP request with the same path/content is idempotent.
5. **Submission editing could replay a completed document deletion after a later upload failed.** Committed evidence edits are cleared and refreshed before retry. Regression coverage first reproduced this failure, then verified removal plus 20 new attachments with an interrupted upload and successful retry.
6. **Long-running uploads lacked useful feedback.** The panel now shows preparation/upload progress, explicit failures, and a per-file timeout. Editing/removal is disabled during upload, and relevant editor/tab/cancel actions are guarded while saving.
7. **Document titles containing dots could be truncated.** The upload handler preserves titles such as `Agreement v1.2`, avoids duplicate extensions, and validates title/path characters. Frontend validation leaves invalid titles editable before any upload.
8. **Preview resources and failure visibility needed explicit handling.** Blob URLs are released on remove, successful save, and disposal. A failed list-update notification exposes a browser-owned warning instead of silently hiding queued files.

## Automated verification

Commands run from the repository root:

```powershell
dotnet test .\Remi.sln -c Release --no-restore -m:1 --disable-build-servers
node --test .\src\Remi.Tests\clipboard-image-evidence.test.mjs
```

Results: **156/156 .NET tests passed; 19/19 JavaScript tests passed.**

`src/Remi.Tests/clipboard-image-evidence.test.mjs` exercises the production uploader with browser-event adapters and a controlled HTTP boundary. Its tests cover:

- Twenty files through each provisioning method, metadata below 32 KB, and byte-for-byte upload bodies.
- Immediate Save after twenty pastes while server notification promises remain unresolved.
- Failure on upload eight: seven successful files are not resent; all thirteen remaining files succeed on retry.
- Duplicate detection, removal/re-addition, zero-byte/oversize rejection without losing other valid files, and acceptance at the 15 MB boundary.
- Form ownership, unrelated text editing, callback failure visibility, title validation, upload timeout/retry, disposal, and preview cleanup.

`src/Remi.Tests/AttachmentWorkflowTests.cs` renders each actual form with bUnit. It deliberately starts with no parent notification, discovers 20 files during preparation, and captures the real target identifier. Each workflow runs both success and partial-failure cases. The test invokes the actual upload handler with real multipart file objects, a disposable SQLite register, and a real filesystem evidence archive. Every request is repeated to simulate a lost success response. It then reopens SQLite and all 20 archived files, compares their bytes, and checks record counts and contract/invoice document projections.

These are layered tests: the browser-event/HTTP boundary and the Blazor form/upload-handler/persistence boundary are tested separately. They are not represented as a single native-clipboard-to-production-save browser test.

## Live verification and limits

The browser skill was used to test the real published UI through the native file chooser, waiting for the twentieth editable title row and all image previews to load. Each of the eight forms passed. Contract editing additionally passed removal and re-selection of the same file.

Twenty valid PNG fixtures totalled approximately 1.76 MB. No Save action was performed against operational records. Pending fixtures were discarded by navigation. Native operating-system clipboard interaction and a live production save were **not** exercised; clipboard delivery, saving, persisted bytes, and retry behavior were verified by the automated tests above.

## Deployment and operational-data safety

- Rebuilt the self-contained `win-x64` portable package and deployed to `publish\Remi`.
- Started only `publish\Remi\Remi.exe`, using its existing `data` folder.
- No schema/storage-format change or migration was introduced.
- Before deployment, copied the complete published data folder and verified the file count and every SHA-256 hash: **191 files**.
- Latest recoverable backup: `backups\pre-attachment-audit-20260902-192700\data`.
- Earlier pre-audit backup: `backups\pre-attachment-audit-20260902-185935\data`.
- SQLite integrity checking passed for the backup. The operational register contains no audit-fixture documents.
- Published assembly, uploader script, and static asset manifest hashes matched the staged build.

Existing work on contract URL routing and reporting-month selection was preserved. No Git commit was made.
