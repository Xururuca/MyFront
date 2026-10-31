<p align="center">
  <img src="docs/logo.png" width="96" height="96" alt="MyFront">
</p>

<h1 align="center">MyFront</h1>

<p align="center">
  <b>The fast, lightweight MySQL &amp; MariaDB client for Windows.</b><br>
  One file. Ready in a quarter of a second. Edit data like a spreadsheet, move it to and from Excel in seconds —<br>
  and not a single byte sent anywhere but your own database server.
</p>

<p align="center"><i>Inspired by the good old MySQL-Front — rebuilt from scratch for today's MariaDB and MySQL.</i></p>

<p align="center">
  <a href="https://github.com/Xururuca/MyFront/releases/latest/download/MyFront-Standalone.exe"><b>⬇ Download MyFront</b> — standalone, 147 MB</a>
  &nbsp;&nbsp;|&nbsp;&nbsp;
  <a href="https://github.com/Xururuca/MyFront/releases/latest/download/MyFront.exe">Small download — 16 MB, needs .NET 10</a>
  &nbsp;&nbsp;|&nbsp;&nbsp;
  <a href="https://github.com/Xururuca/MyFront/releases">All releases</a>
</p>

<p align="center">
  Windows 10 / 11 (64-bit) &nbsp;·&nbsp; MariaDB 10.x / 11.x &nbsp;·&nbsp; MySQL 8 &nbsp;·&nbsp; light &amp; dark theme
</p>

---

## Why MyFront

- **Instant.** Ready to work about 0.25 s after you double-click. Results stream into the grid while they arrive, and
  200,000 rows scroll without a stutter.
- **Edit like a spreadsheet.** Type straight into a table or into your query's results, paste a block from Excel,
  press Delete for NULL and Ctrl+Z to take it back. Every change is a parameterized `UPDATE … WHERE key … LIMIT 1`.
- **Excel in, Excel out.** 200,000 rows to a formatted .xlsx in under 2 seconds. Import Excel and CSV with automatic
  column matching, type detection, Upsert, and a single transaction that rolls back on any error.
- **Your workspace, remembered.** One tab per connection, each with its own tree and SQL Editor 1–5 — saved as you
  type. Quick query buttons on Ctrl+1 … 9. Hide the tables you never open.
- **Secure by default.** Passwords encrypted with Windows DPAPI, TLS with full certificate checks, SSH tunnels with
  pinned host keys, and read-only connections that the server itself enforces.
- **Private by design.** No telemetry, no update checks, no sign-up. MyFront talks to your database servers and
  nothing else.
- **Nothing to install.** A single portable .exe: copy it anywhere and run it. No installer, no admin rights.

## Download

|  | **Standalone** (recommended) | **Small** |
|---|---|---|
| File | [`MyFront-Standalone.exe`](https://github.com/Xururuca/MyFront/releases/latest/download/MyFront-Standalone.exe) | [`MyFront.exe`](https://github.com/Xururuca/MyFront/releases/latest/download/MyFront.exe) |
| Size | ~147 MB | ~16 MB |
| Needs | Nothing — .NET is built in | The free [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) from Microsoft |
| Pick it when | You just want it to run, on any PC | .NET 10 is already installed, or you want the small file |

Both are the same app with the same features, each a single .exe. Settings live in `%APPDATA%\MyFront` — copy that
folder to take everything to another PC, or move one connection with **Export… / Import…**.

> **First start:** the exe isn't code-signed, so Windows SmartScreen may say *"Windows protected your PC"*.
> Click **More info → Run anyway**.

## Features

### Connections

- Saved connections with **DPAPI-encrypted passwords**; TLS **Preferred / Required / Verify CA / Verify Full** with a CA file.
- **SSH tunnel** per connection: password or private key (OpenSSH, PEM, PuTTY `.ppk`), host keys trusted on first use
  and pinned, automatic reconnect after sleep or a network drop.
- **Read-only connections**, refused in the app and enforced by the server (`SET SESSION TRANSACTION READ ONLY`).
- Limit a connection to **one database** — the tree then shows only that one.
- **One tab per connection** with its own tree, Object Browser, Data Browser and SQL editors: work on two servers never mixes.
- **Export / Import a connection** to a `.json` file — settings, SQL editor texts, quick query buttons and hidden
  tables. Never a password.

### Explore

- **Navigator**: databases → tables, views, procedures, functions → keys and fields, plus **Processes** (kill,
  auto-refresh) and **Variables**. Loaded lazily, never blocking the window.
- **Hide / Unhide** tables you never use: they move into a closed **Hidden** folder at the bottom of their database.
- **Object Browser**: sizes, row counts, engines and dates at a glance; fields and indexes of a table.
- **Change structure safely**: Edit field, Add key, Edit key and Rename — each shows the exact statement before it runs.
- **Table Properties** (Alt+Enter): fields, keys, foreign keys, triggers, partitions, dependencies, source code.
- **Drag** a table or field name into the editor; Copy name, Copy CREATE statement, Delete with a clear warning.

### Edit data

- **Data Browser**: paging, server-side sort, WHERE filter, quick search, add and delete rows.
- **Editable query results** whenever each row can be found again — by its key, or by all its values for tables without one.
- **Delete** sets the selected cells to NULL, **Ctrl+Z** puts values back, **Ctrl+V** pastes a block from Excel,
  **Esc** reverts a row. Deleting whole rows always asks first.
- A row the server refuses keeps its mark and shows the server's message; nothing is lost.
- Shaded alternate rows in both themes for easy reading.

### SQL editor

- Syntax highlighting; run everything, the selection or the current statement (F9, Ctrl+Enter); `DELIMITER`; **Stop**
  sends `KILL QUERY`.
- One result tab per result set, a Messages tab with timing and row counts, errors highlighted in the code.
- **Explain** in plain words: which index is used, full table scans, filesorts, temporary tables.
- **Format** (Ctrl+Shift+F) makes any query readable; only spacing and keyword case change.
- **SQL Editor 1–5** for every connection, saved as you type — no "save changes?" prompts. Ctrl+T for more.
- **SQL history** per connection (passwords masked), **Run SQL file** for big scripts, `{{parameters}}` that ask for values.

### Excel, CSV and SQL files

- **Export** a table, a whole database (one sheet per table), a result, or a query streamed straight to disk.
  Real Excel dates and numbers, bold frozen header with filters; long numbers and old dates kept safe as text.
  CSV with your delimiter, decimal mark and encoding.
- **Import** `.xlsx`, `.xls` and `.csv` into a new or existing table — or just drop the file on the window.
  Columns are matched automatically (case, spaces and Turkish letters don't matter), types are suggested, and you
  choose **Insert, Truncate + Insert, Upsert or Insert Ignore**. Preview first; one transaction; bad rows saved to
  Excel. Your mapping is remembered for next time.
- **SQL dump** of a table (mysqldump-style, with keys, triggers and data) and **Import SQL** that brings it back exactly.

### Quick Query bar

- Your own buttons next to the toolbar: a name, colour or icon, the SQL, and a hotkey **Ctrl+1 … 9**.
- Each button belongs to its connection, so it never runs on the wrong server.
- Results in a tab — or **straight to Excel** in one click. Parameters like `{{date}}` ask for values and are bound safely.
- Statements that change data (UPDATE, DELETE, DROP, TRUNCATE, ALTER) ask before they run.

## Performance

Measured against a remote MariaDB 10.11 server:

| Task | Time |
|---|---|
| Start-up to a ready window | ~0.25 s |
| 200,000 rows × 9 columns → .xlsx (16.5 MB) | ~1.8 s |
| 200,000 rows → CSV (14 MB) | ~1.5 s |
| 200,000 rows through an SSH tunnel | ~1.5 s |
| Excel sheet of 48,066 rows × 15 columns → table | ~3.9 s |
| 206,937 rows × 13 columns → new table | ~10 s |

Exports stream their rows straight from the server, so memory stays flat even for millions of rows.

## Keyboard shortcuts

| Key | Action |
|---|---|
| F9 | Run the selection, or all statements when nothing is selected |
| Ctrl+Enter | Run the statement at the cursor |
| Ctrl+Shift+F | Format SQL |
| F5 | Refresh |
| Ctrl+T / Ctrl+W | New / close an extra SQL editor tab |
| Ctrl+O | Open connection |
| Ctrl+H | SQL history |
| Ctrl+S | Save SQL file |
| Ctrl+E | Export to Excel |
| Ctrl+I | Import Excel / CSV |
| Ctrl+1 … Ctrl+9 | Run a quick query button |
| Alt+Enter | Table properties (tree) |
| Ctrl+C / Ctrl+V | Copy / paste cells, tab-separated like Excel (Ctrl+Shift+C adds the column names) |
| Delete | Set the selected cells to NULL; rows selected by their headers: delete after confirming |
| Ctrl+0 | Set NULL |
| Ctrl+Z | Undo the last change in a grid |
| Esc | Revert the row |

## Security and privacy

- Database and SSH passwords are encrypted with Windows DPAPI for your Windows user. Never stored in plain text,
  never logged, never exported.
- SSH host keys are pinned per connection; a changed key is refused with a warning. Tunnels listen on 127.0.0.1 only.
- Values always travel as query parameters; names are quoted.
- Read-only connections are checked twice: by MyFront and by the server.
- No telemetry, no update checks, no network access except to your database servers.

## Your files

| Path | Content |
|---|---|
| `%APPDATA%\MyFront\accounts.json` | Connections (passwords only DPAPI-encrypted) and hidden tables |
| `%APPDATA%\MyFront\editors\` | SQL Editor tabs per connection |
| `%APPDATA%\MyFront\history\` | SQL history per connection |
| `%APPDATA%\MyFront\quickqueries.json` | Quick Query buttons |
| `%APPDATA%\MyFront\import-mappings.json` | Remembered import mappings |
| `%APPDATA%\MyFront\settings.json` | Theme, word wrap, export folder, CSV options |
| `%LOCALAPPDATA%\MyFront\native\` | The editor's native DLLs, unpacked from the exe and checksum-verified |

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bat
build.bat          :: publish\fd\MyFront.exe   small, needs the .NET 10 Desktop Runtime
build.bat sc       :: publish\sc\MyFront.exe   standalone, .NET built in
build.bat all      :: both
```

For a release, upload `publish\sc\MyFront.exe` as **MyFront-Standalone.exe** and `publish\fd\MyFront.exe` as
**MyFront.exe**, so the download links above always point at the latest version.

<details>
<summary><b>Self-tests</b></summary>

The self-tests run against a real server. Copy `testdb.local.example.json` to `testdb.local.json` (git-ignored) and
fill in a test server, then:

```bat
build.bat test                                                  :: everything
dotnet run --project tests\MyFront.SelfTest -c Release -- core   :: logic + server (TLS, cancel, read-only, 200k rows …)
dotnet run --project tests\MyFront.SelfTest -c Release -- ui     :: drives the real window, screenshots to tests\samples\out
```

Other modes: `ddl`, `ssh`, `export`, `import`, `quick`, `uicur` (the work in progress). The tests create and drop
scratch tables named `mf_*` and expect the fixture table `prices` with its 200,000 rows. UI tests never take the
keyboard focus, open on another monitor, put the clipboard back, and delete the files they made afterwards
(`MYFRONT_TEST_KEEP_FILES=1` keeps them). SSH tests never try a wrong password — repeated failed logins can get
your PC banned (fail2ban).

</details>

## License

MyFront is free and open source under the [MIT License](LICENSE).

## Third-party software

| Package | License | Used for |
|---|---|---|
| [MySqlConnector](https://mysqlconnector.net/) 2.6.2 | MIT | Database driver |
| [Scintilla5.NET](https://github.com/desjarlais/Scintilla.NET) 6.1.2 (Scintilla, Lexilla) | MIT / Scintilla | SQL editor |
| [MiniExcel](https://github.com/mini-software/MiniExcel) 1.46.0 | Apache-2.0 | .xlsx export and import |
| [ExcelDataReader](https://github.com/ExcelDataReader/ExcelDataReader) 3.9.0 | MIT | Legacy .xls import |
| [SSH.NET](https://github.com/sshnet/SSH.NET) 2026.0.0 (BouncyCastle) | MIT | SSH tunnels |

Full license texts: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), also shipped next to `MyFront.exe`.

---

<sub>MySQL is a trademark of Oracle; MariaDB is a trademark of MariaDB. MyFront is an independent project, not
affiliated with either, nor with MySQL-Front or its authors.</sub>
