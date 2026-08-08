# Repository instructions

1. When committing to Git, always provide a conscious description in addition to the title.
2. Always use PortableGit: `C:\Software\PortableGit\bin\git.exe`.
3. Remi began holding operational reporting data on 8 August 2026. Preserve the existing published register, evidence, reference data and application keys. Do not rebuild or discard them as a development convenience.
4. Every persistent-storage change must use a safe, additive, versioned migration with upgrade coverage against the preceding schema. Before an upgraded published build first opens operational data, create and verify a recoverable backup of the complete published `data` folder.
5. After the initial source-data migration, Remi prepares and exports monthly reporting spreadsheets from its own register. Do not add or retain a workflow that imports completed monthly return workbooks back into Remi.
6. When rebuilding or starting Remi, always use the published portable instance in `publish\Remi` and its existing `data` folder. Do not launch the development build-output instance, which has a separate register. Rebuild the portable instance before starting it.
