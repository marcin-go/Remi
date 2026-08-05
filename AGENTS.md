# Repository instructions

1. When committing to Git, always provide a conscious description in addition to the title.
2. Always use PortableGit: `C:\Software\PortableGit\bin\git.exe`.
3. Do not preserve backwards compatibility until the user explicitly declares Remi production-ready. Prefer clean, direct changes over compatibility shims or deprecation paths.
4. Data is disposable until the user declares Remi production-ready. Before that declaration, prefer clean, direct model changes and do not preserve backwards compatibility or write migrations for superseded development models. Once Remi is production-ready, preserve data and use safe, verified migrations whenever storage is affected.
5. After the initial source-data migration, Remi prepares and exports monthly reporting spreadsheets from its own register. Do not add or retain a workflow that imports completed monthly return workbooks back into Remi.
