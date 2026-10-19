# Clean-up Plan: Bugs & Improvements

Findings from a deep review of the first-party code (`Teddy/`, `TeddyBench/`, `TonieAudio/`, `ID3/`). `Concentus` and `Concentus.Oggfile` are vendored third-party ports and were only consulted to confirm behavior. Ordered by impact; the top bugs were verified directly against the source.

---

## 1. High-impact bugs

### 1.1 CLI `.ogg` encoding is broken with default settings ✅ verified

`Teddy/Program.cs:175` defaults bitrate to 96; `:601` passes `bitRate * 1000` (96000) into `TonieAudio`, which hands it to the Opus **decoder as the sample rate** (`TonieAudio/TonieAudio.cs:547`: `new OpusWaveStream(..., bitRate, channels)`). Concentus only accepts 8/12/16/24/48 kHz, so every `.ogg` source fails with the generic "Failed processing \<file\>". The GUI only works by accident (it always passes 48000).
**Fix:** decode at a fixed 48000 Hz; pass bitrate and sample rate as separate parameters.

### 1.2 UI deadlock in the crash reporter

`TeddyMain.cs:2454` blocks the UI thread with `DiagnosticsSendInfo(...).Result`; `DiagnosticsSendInfo` (`:2028`) awaits `PostAsync` without `ConfigureAwait(false)`, and its continuation needs the UI thread — classic sync-over-async deadlock. Hits anyone who agrees to report a UI-thread exception.
**Fix:** make the handler async (or `ConfigureAwait(false)` throughout + `GetAwaiter().GetResult()` as a stopgap).

### 1.3 `teddy -m info -f json` emits invalid JSON ✅ verified

`TonieAudio/TonieTools.cs:102`: `bool first = false;` is never set, so every entry is prefixed with a comma → output starts `[` `,` and never parses. Also in `DumpInfo`:

- `:153-155`: `Array.IndexOf(info.AudioIds, AudioId)` can return −1 → `info.Hash[-1]` throws whenever a hash matches but the audio ID isn't listed.
- `:154`, `:172`: writes into the **shared** `TonieInfo` objects (resizes hash arrays), corrupting later lookups in the GUI (`TeddyMain.cs:2054`).
- `:228`: prepends an extra `0` chapter even when chapters already start at 0.

### 1.4 ProtoCoder never decodes booleans ✅ verified

`TonieAudio/ProtoCoder.cs:200`: `case "Bool":` — but `Type.Name` for `bool` is `"Boolean"`, so `FileHeader.Usable` (the sfx.bin flag, `TonieAudio.cs:57-59`) silently never deserializes. Also in `ProtoCoder.cs`: `lastField` (`:172`) is never updated so the field-order check is dead; the signed `Int*[]` cases (`:258-275`) are unreachable; fixed32/fixed64 wire types are not consumed, desyncing the parser.

### 1.5 The GUI "Delete..." button does nothing ✅ verified

`btnDelete_Click` is an empty method (`TeddyMain.cs:1367-1369`) yet wired up and enabled (`TeddyMain.Designer.cs:477`, `TeddyMain.cs:816`).
**Fix:** route it to the existing `DeleteSelected()`.

### 1.6 "Download tonies.json on startup" setting is doubly broken

- With the download disabled, `TeddyMain.cs:193` loads `customTonies.json` (a `Dictionary<string,string>`) **as the main tonie database** instead of the cached `tonies.json` → parse failure → crash-report prompt on every startup.
- The menu item never toggles its checkmark (`:2467-2471` flips `DownloadJson` off the *stale* `Checked` value and never updates `Checked`; no `CheckOnClick` in the Designer), so the setting can't be switched back from the UI.

### 1.7 Encoder drops silence and pads track ends

- `TonieAudio.cs:516`, `:577`: any 60 ms block that is all zeros is skipped entirely (`buffer.Where(v => v != 0).Count() == 0`), shortening deliberate pauses; the check also scans the whole buffer rather than just `bytesReturned`.
- `ConvertToFloat` (`:644`) sizes output from `pcmBuffer.Length`, appending up to 60 ms of zeros at the end of every track.

### 1.8 Pn5180Esp unlock can never succeed

`Pn5180Esp.cs:156`, `:274`: `response.Replace("\r", "")` — the return value is discarded (strings are immutable), so `"ok\r"` never equals `"ok"`. `UnlockTag` also ignores its `pass` argument (always sends `"u"`), and the `uid == null` branch at `:68` is dead (`GetUID` never returns null).

### 1.9 A reader exception permanently stops tag scanning

`Proxmark3.ReadMemory` / `EmulateTag` / `MeasureAntenna` (`Proxmark3.cs:1474-1532`) call `StopThread()` … `StartThread()` without `try/finally`; commit `6a95c2e` wrapped the *callers* in empty catches, so after one exception scanning silently dies. Also: `:1493` uses `throw ex;` (destroys stack trace); `MeasureAntenna` returns a zero-filled result on failure (`:1527-1531`), making the UI's "Measurement failed" branch unreachable.
**Fix:** `try/finally` around the stop/start, rethrow with `throw;`, return null on failed measurement.

### 1.10 File-management edge cases in the GUI

- Deleting or re-assigning a "flat" file (one sitting directly in `CurrentDirectory`, listed via `TeddyMain.cs:849-867`) deletes `info.Directory` — which is the CONTENT/root folder itself (`:1549-1558`, `:1606-1607`).
- `DeleteSelected` returns early on Cancel/error (`:1582`, `:1612`) without restarting the analyze thread.
- Failed encode/copy leaves the UI stuck: every failure path in `EncodeFile` (`:1297-1349`) and `CopyFile` (`:1208-1242`) is `MessageBox.Show(...); return;` from a background thread and only the success path restores the UI.
- Drag-and-drop before any directory is open: `CurrentDirectory == null` flows into `Path.Combine` (`OnDragDrop` `:583` → `:1308`) and throws.

---

## 2. Reliability & threading

- **FileSystemWatcher leak:** a new watcher per `OpenPath` call, never stored or disposed (`TeddyMain.cs:785-792`); old watchers keep firing refreshes. Also not recursive, so changes inside per-tag subfolders are missed.
- **AnalyzeMain** (`TeddyMain.cs:900-1038`): holds `lock(RegisteredItems)` while doing HTTP image downloads with no timeout (`:907`, `:956`); re-processes every "unknown"/corrupt item every 100 ms forever (failures never cached, `:1030`); mutates ListView/ImageList state from a background thread (`:916`, `:954`); `CachedAudios` (`:50`) is unbounded and unlocked.
- **`async void` UpdateCheck as a ThreadStart** (`TeddyMain.cs:278`, `:514`): the thread exits at the first `await`, so Join/Abort are no-ops and exceptions escape the SafeThread wrapper.
- **Non-volatile cross-thread flags:** `ScanCardThreadStop`, `AnalyzeThreadStop`, `PlayThreadStop`, `ExitScanThread`, `TrackNewPosition`. `AsyncTagActionThread` is null-checked then aborted from two threads (`:1947-1953`, `:2096-2103`, `:2160-2167`) — NRE race.
- **UI-thread blocking:** `MeasureAntenna` runs synchronously on the UI thread (~1 s join + up to ~10 timeouts, `:1965`, `:2245`); `NotifyDrive` sleeps 1 s on the UI thread (`:760`); the update download `CopyTo` is synchronous on the UI thread (`:534`); `StopAnalyzeThread` joins up to 2 s then Aborts on every FS event (`:886-898`).
- **Playback lockout:** if anything throws before playback starts, `PlayThread` is never reset and the Play button is dead until restart (`:2376-2379`).
- **Undisposed resources:**
  - `TonieAudio.cs`: `ReadFile` throws before `Close` (`:375-394`); `CalculateStatistics`/`ParsePositions` (`:115`, `:188`) and `DumpAudioFiles` (`:863`) lack `using`; `GenerateAudio` never disposes `Mp3FileReader`/`MediaFoundationResampler` (`:504-505`, `:557`) and leaks its temp file on exception (`:454`, `:633`); `OpusWaveStream` never disposes `BaseStream`; `new Mp3(...)` never disposed (`:261`, `:330`; also `TrackSortDialog.cs:47`).
  - `TeddyMain.cs`: `HttpWebResponse` never disposed (`:152`, `:1063`; also `Teddy/Program.cs:60`); `HttpClient` leaked in `UpdateCheck` (`:523`) and disposed *before* the content is read in `DiagnosticsSendInfo` (`:2030-2031`).
- **`OpusWaveStream` breaks the `WaveStream` contract:** `Length`/`Position` return TimeSpan ticks, not bytes (`OpusWaveStream.cs:22-24`).
- **Serial probing disturbs other devices:** both readers open/close every serial port every 250 ms (`Proxmark3.cs:1009-1022`, `Pn5180Esp.cs:198-211`); opening toggles DTR, which resets attached Arduino/ESP boards. (See MAC_PORTABILITY.md §3.2 for the macOS angle.)
- **Protocol robustness:** `Pm3UsbResponse` only catches `TimeoutException`; a garbage `dataLen` (up to 0x7FFF) overruns the 544-byte buffer with `ArgumentException` (`Proxmark3.cs:233`, `:317-321`). `Pm3UsbCommandNG` NREs with its default `payload = null` (`:174`). `versionstr_len - 1` unchecked (`:1176`). `HardwareType = versionString` (`:1187`) puts the raw multi-line, ANSI-coded version string into the status bar (commit `ca062fe` only filtered log lines). Thread names/log tags say "Pn5180Esp" inside Proxmark3 code (`RfidReaderBase.cs:82, 100, 105`). `Close()` uses `lock(this)` (`RfidReaderBase.cs:114`).
- **Ogg parsing has no bounds checks:** `GetOggPage` (`TonieAudio.cs:1035-1083`); `GetOggHeaders` dereferences a null page when `Segments.Length < 1` (`:984-991`); chapter offsets rely on `uint` wraparound (`:877`, `:882`).
- **Sorting bugs:** card list sorts by `CreationTime.ToString()` — culture-formatted, so under de-DE it sorts by day-of-month (`TeddyMain.cs:116-117`); the comparer returns −1 for two nulls, violating the comparer contract (`:125-132`); `TonieTools.cs:37` does `int.Parse(SortNumber_)` inside a comparer on the UI thread — a non-numeric value throws straight into bug 1.2.
- **`TonieAudio.FromFile(..., readAudio: false)` still scans the whole file:** `ReadFile` always calls `CalculateStatistics` (`TonieAudio.cs:396`), which seeks through every Ogg page — defeating the header-only mode the SD-card analyzer relies on; `DumpInfo` then scans a second time (`TonieTools.cs:87`).
- **Unbounded LogWindow:** the `StringBuilder` grows forever and the whole TextBox is rewritten every 250 ms; the window also force-shows itself whenever text arrives (`LogWindow.cs:55-66`).

---

## 3. Error handling & diagnostics

- **~25 empty catch blocks** swallow everything:
  - `TeddyMain.cs`: 237, 251, 570, 691, 719, 1030, 1056, 1070, 1075, 1935, 2039, 2084, 2144, 2259, 2276, 2294
  - `Settings.cs`: 27, 46 · `RfidReaderBase.cs`: 122, 130 · `Proxmark3.cs`: 1019, 1225-1236, 1568 · `Pn5180Esp.cs`: 208, 291-302 · `TonieAudio.cs`: 267 · `TrackSortDialog.cs`: 53
  At minimum log the exception; several (reader ops, JSON load) deserve user-visible messages.
- **Exceptions re-wrapped without the inner exception:** `TonieAudio.cs:532, 599, 605, 611` — the GUI then guesses "Most likely a MP3 was corrupted" (`TeddyMain.cs:1323`) with no root cause.
- **Message boxes that drop `ex.Message`:** `TeddyMain.cs:1225, 1235, 1333, 1342, 1543, 1562, 1611, 1725`.
- **Errors written to `Console` in a WinExe** (invisible): `TeddyMain.cs:1709, 1719-1720`; library code too: `TonieAudio.cs:130, 160, 168, 347-352`; `ProtoCoder.cs:225, 298, 408`.
- **Crash reports are thin:** `ReportException` sends only `e.Message` + `e.StackTrace` (`TeddyMain.cs:2436-2441`) — use `e.ToString()` to include inner exceptions. Typo "eception" at `:2428`.
- **Offline startup nags:** no network → the *crash-report* prompt appears on every launch (`TeddyMain.cs:160`); a locked file prompts up to 5 times (`:179`).
- **CLI:** `Main` is `void` — always exits 0 even on failure; "Written content to" prints even after a failed write (`Program.cs:613`); `SaveJson` error says "Failed to **load** JSON" (`:86`); the rename-mode argument check uses `&&` where `||` is intended (`:233`), so it proceeds when one of the two paths is missing.

---

## 4. Code hygiene

- **God class:** `TeddyMain.cs` is 2,481 lines mixing UI, JSON, HTTP, update checks, image caching, threading, RFID, and playback. Worst methods: `Main` in `Teddy/Program.cs` (~480 lines, `:159-640`), `TonieAudio.GenerateAudio` (`:432-634`), `TonieTools.DumpInfo` (`:82-257`), `Proxmark3.TryOpen` (`:1070-1240`) and `ScanThreadFunc` (`:455-618`), `TeddyMain.AnalyzeMain` (`:900-1038`), `SaveSelected` (`:1619-1730`), `LoadJson` (`:140-241`). Extracting the non-UI logic is also the prerequisite for any cross-platform GUI (MAC_PORTABILITY.md §5).
- **Duplication:**
  - Three async tag-action methods share the same scaffold (`TeddyMain.cs:1885-1958`, `:2058-2107`, `:2109-2171`); the "Report sent" dialog is pasted 4× (`:1870`, `:1918`, `:1992`, `:2455`).
  - `SaveSelected` largely re-implements the CLI decode (`Program.cs:484-523`).
  - `RemoveInvalidChars` ×2 (`Program.cs:143`, `TeddyMain.cs:1807`); UID-reverse logic repeated (`TonieTools.cs:88-91`); `StructureToByteArray`/`ByteArrayToStructure` ×2 (`Proxmark3.cs:420-439`, `TonieAudio.cs:930-949`).
  - `Detect`/`TryOpen`/`ScanThreadFunc` copy-pasted between `Proxmark3` and `Pn5180Esp`; `flashFirmware_Click` ≡ `flashBootloader_Click` (`TeddyMain.cs:2264-2298`).
- **Dead code:** `BuildCueSheet`/`ParsePositions` (`Program.cs:102`; call commented out at `TonieAudio.cs:855`); `Verbosity` (`Program.cs:48`); `ReadAck`, `GetHFVoltage`, `crc16k` (`Proxmark3.cs:1414, 1534, 199`); unreachable `return false` (`Proxmark3.cs:899`); `AskPassForm` never used; `saveAll` always true (`TeddyMain.cs:1621, 1650`); empty `btnOk_Click`/`Cancel_Click` (`AskUIDForm.cs:93-101`); commented-out blocks (`Program.cs:553-558`, `Proxmark3.cs:162-163, 283-284, 492, 591`, `Pn5180Esp.cs:85, 268`, `TeddyMain.cs:1378`).
- **Unused packages/imports:** `Octokit` (only a stray `using` at `ISO15693.cs:1`) and `Semver` (referenced, never used — ironically needed for bug 5.1); `using ScottPlot.Drawing.Colormaps` (`Proxmark3.cs:2`); a long tail of `System.*` 4.3 shim packages net48 doesn't need (`TeddyBench.csproj:51-74`).
- **Obsolete APIs:** `HttpWebRequest` (`TeddyMain.cs:151, 1062`; `Program.cs:59`) → `HttpClient`; `SHA1CryptoServiceProvider` (`TonieAudio.cs:627, 676`) → `SHA1.Create()`; `Thread.Abort` throughout (see MAC_PORTABILITY.md §3.1).
- **Magic numbers** worth naming as constants: the `0x50000000` custom-ID epoch (`TeddyMain.cs:983, 1317`; `AskHexForm.cs:25`; `Program.cs:560`; `TonieTools.cs:96`); unlock passwords `0x0F0F0F0F`/`0x7FFD6E5B` duplicated in both readers; the `0xDEADBEEF` probe; `0x1000`/`0xFFC` header sizes; `0x77359400` max size; `lstTonies.Groups[0..3]` indices.
- **Latent bug:** `ISO15693.CheckChecksum` computes the CRC position incorrectly for `start > 0` (`ISO15693.cs:114`).
- **Misleading metadata:** hardcoded Opus comment claims `opusenc 0.1.9` / `--bitrate 96 --vbr` regardless of actual settings (`TonieAudio.cs:460-463`).
- **`.gitignore`** doesn't cover runtime-generated files (`teddyBench.cfg`, `tonies.json`, `customTonies.json`, `cache/`) — or move them out of CWD per MAC_PORTABILITY.md §3.4.
- **No tests anywhere.** The pure-managed core (`TonieAudio`, `ProtoCoder`, `TonieTools`, `ISO15693` checksums) is very testable; bugs 1.1, 1.3, 1.4, and 1.7 would all have been caught by round-trip tests.

---

## 5. Update check & network

(All endpoints verified live as of 2026-10-19: `api.revvox.de/tonies.json` 302→`raw.githubusercontent.com/toniebox-reverse-engineering/tonies-json/release/tonies.json`; `api.revvox.de/diag` 200; GitHub releases API live, latest v1.7.0.)

- **`UpdateCheck` (`TeddyMain.cs:514-573`):** compares versions with plain string inequality — dev builds/forks get nagged and downgrades are offered (the referenced-but-unused Semver package exists for exactly this); assumes `assets[0]` is the zip; saves the zip into the exe folder (fails under Program Files → lands in deadlock bug 1.2); no hash verification; upstream repo hardcoded so forks never see their own releases.
- **tonies.json handling:** the 6.2 MB file is downloaded fresh on *every* GUI start and every CLI run — including encodes that never use it. Add ETag/If-Modified-Since caching, a timeout, a friendly offline message instead of the crash-report prompt, and in the CLI load it only for `info`/`rename`/`decode`.

---

## 6. Feature improvements

1. **Re-enable SLIX-L emulation safely.** Disabled in commit `03e1d6e` (`TeddyMain.Designer.cs:181`) because the firmware implementation isn't SLIX-L-compatible. Bring it back behind a firmware capability probe, and validate the 40-byte dump length the error text promises — `TeddyMain.cs:2130` currently accepts any length.
2. **Console mode is permanently disabled** — `Enabled = false` in the Designer and never re-enabled, so `EnterConsole`/`ExitConsole` are unreachable. Either wire it to reader-connected state or remove it.
3. **Implement the Delete button** (bug 1.5) via the existing `DeleteSelected`.
4. **GUI bitrate/VBR selection.** The GUI is hardcoded to 48 kbps CBR while the CLI defaults to 96 kbps; expose bitrate + VBR in settings.
5. **Restore cue-sheet export** (`BuildCueSheet`), and make single-file `.ogg` export keep its tags — `TonieAudio.cs:850-856` computes `AddTags` and drops the result.
6. **Natural filename sort** for untagged inputs (`TrackSortDialog.cs:35`, `TonieAudio.cs:324`) so `track10` doesn't sort before `track2`.
7. **Selectable serial port** instead of probing every port every 250 ms (also fixes the DTR-reset side effect, §2).
8. **CLI exit codes** on failure, so scripting the CLI is possible.
9. **Proper error surfacing:** a log/error panel with full exception detail instead of lossy message boxes; send `ex.ToString()` in diagnostics reports.
10. **ID3 v2.4 robustness:** the v2.4 handler always decodes frame sizes as syncsafe (`ID3/v2/Id3v24Handler.cs:137`) although files with non-syncsafe sizes exist in the wild (noted in commit `51ddccc`) — add a fallback heuristic. Misc ID3 TODOs/stubs: `TagComparer.cs:46`, `Id3Tag.cs:85`, `ListTextFrame.cs:26`, `Mp3.cs:297`, lyrics-encoding `NotImplementedException` (`Id3v23Handler.Frames.cs:191`, `Id3v24Handler.Frames.cs:189`).

---

## Suggested attack order

1. **Quick wins, big payoff:** 1.1 (ogg encode), 1.3 (JSON output), 1.4 (Bool), 1.5 (Delete button), 1.8 (Pn5180 unlock), CLI exit codes — all small, mechanical fixes.
2. **Stability:** 1.2 (deadlock), 1.9 (scan death), §2 resource leaks, replace empty catches with logging.
3. **Tests:** round-trip encode/decode tests for `TonieAudio` + `ProtoCoder` unit tests, locking in step 1's fixes.
4. **Refactors:** extract non-UI logic from `TeddyMain.cs`, dedupe reader code — ideally alongside the macOS work (see MAC_PORTABILITY.md).
5. **Features** (§6) as desired.
