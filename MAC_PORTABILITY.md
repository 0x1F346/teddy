# macOS Portability Plan

This document is a plan for getting the teddy tooling running on macOS. It is based on a full review of the solution's six projects, their package dependencies, and every platform-specific API call site.

## Headline assessment

| Project | Target | Portability |
|---|---|---|
| `Concentus`, `Concentus.Oggfile`, `ID3` | netstandard2.0 | ✅ Pure managed code — portable as-is |
| `TonieAudio` | netstandard2.0 | ⚠️ Portable except two NAudio classes in the encode path (see Phase 2) |
| `Teddy` (CLI) | net48 | ⚠️ Pure console app with **zero** WinForms usage, but pinned to .NET Framework. One retarget + the encode fix away from running natively on macOS |
| `TeddyBench` (GUI) | net48, win10-x64 | ❌ WinForms/System.Drawing across ~20 forms. Not portable without a UI rewrite (see Phase 5) |

**Recommended strategy: CLI-first.** Phases 1–4 are small-to-medium efforts that yield a fully working `teddy` CLI on macOS (dump, decode, encode, rename) plus macOS CI. Phase 5 (the GUI) is a separate, much larger effort and should be decided on independently.

---

## Phase 1 — Retarget the Teddy CLI (small)

The CLI (`Teddy/Program.cs`) contains no WinForms or System.Drawing code; its Windows pinning is purely build-configuration cruft.

- [ ] In `Teddy/Teddy.csproj`:
  - Change `<TargetFramework>net48</TargetFramework>` → `net8.0`.
  - Remove `<UseWindowsForms>true</UseWindowsForms>` and `<ImportWindowsDesktopTargets>true</ImportWindowsDesktopTargets>` — both are vestigial.
  - Remove the `System.Data.DataSetExtensions` and `Microsoft.CSharp` package references (built-in on modern .NET).
- [ ] Retarget is safe for the libraries: `TonieAudio`, `Concentus`, `Concentus.Oggfile`, and `ID3` are already `netstandard2.0` and compile under the .NET 8 SDK unchanged.
- [ ] Delete dead build files:
  - `ID3/packages.config` (leftover from the pre-SDK format; lists JetBrains.Annotations which nothing uses).
  - `Teddy/Properties/PublishProfiles/FolderProfile.pubxml` (references `netcoreapp3.1`) and `TeddyBench/Properties/PublishProfiles/FolderProfile.pubxml` (references `net6.0-windows`) — both stale.
- [ ] GitInfo (used for `ThisAssembly.Git.BaseTag` at `Teddy/Program.cs:173`) works on macOS as long as `git` is on PATH — no change needed.
- [ ] Cosmetic: help text says `Teddy.exe` (`Teddy/Program.cs:202-204`, `:835-838`); change to `teddy`.

**Result after Phase 1:** `dotnet build` and `dotnet run` work on macOS. The `info`, `decode`, and `rename` modes are pure managed code and work immediately. `encode` fails — that's Phase 2.

### Known obsolete-API warnings (non-blocking)

These compile with warnings on .NET 8 but still run; fix opportunistically (also tracked in CLEAN_UP.md):

- `HttpWebRequest` (`Teddy/Program.cs:59`) → `HttpClient`
- `SHA1CryptoServiceProvider` (`TonieAudio/TonieAudio.cs:627`, `:676`) → `SHA1.Create()`

---

## Phase 2 — Cross-platform encode (medium — the one real blocker)

Encoding goes through two NAudio classes that P/Invoke into Windows media stacks. These are the **only** native dependencies in the entire audio pipeline:

| Call site | Class | Native dependency |
|---|---|---|
| `TonieAudio/TonieAudio.cs:504`, `:543` | `Mp3FileReader` | `msacm32.dll` (ACM MP3 decoder) |
| `TonieAudio/TonieAudio.cs:505`, `:557` | `MediaFoundationResampler` | `mfplat.dll` / Media Foundation COM |

The resampler is used for **both** MP3 and OGG input, so on macOS every encode throws `DllNotFoundException` (swallowed at `TonieAudio.cs:607-611` as "Failed processing <file>").

Fix, all in `TonieAudio`:

- [ ] Replace the NAudio package reference with **`NAudio.Core`** (managed-only; keeps `WaveStream`, `WaveFormat`, `ISampleProvider`). This also stops the WinMM/Wasapi/Asio/WinForms subpackages from being pulled into the netstandard2.0 library.
- [ ] MP3 decode: use `Mp3FileReaderBase` with a fully managed frame decompressor from **NLayer.NAudioSupport** (`Mp3FileReaderBase(file, wf => new Mp3FrameDecompressor(wf))`).
- [ ] Resampling: replace `MediaFoundationResampler` with NAudio.Core's managed `WdlResamplingSampleProvider`, plus a `MonoToStereoSampleProvider` upmix for mono sources (the Tonie format requires 48 kHz stereo).
  - Alternative: the repo already vendors a Speex resampler in `Concentus.Oggfile/OpusOggWriteStream.cs:75-104`, `:144-150`. It could be used by passing the real input sample rate to `OpusOggWriteStream`, but it only converts sample rate — mono→stereo upmixing is still needed, so the `WdlResamplingSampleProvider` route is simpler.
- [ ] While in there, fix the hard bug that bitrate is passed as the decoder *sample rate* for `.ogg` inputs (`Teddy/Program.cs:601` → `TonieAudio.cs:547`) — see CLEAN_UP.md bug #1. Decode should always be at 48 kHz.

**Result after Phase 2:** full CLI feature parity on macOS, and `TonieAudio` becomes genuinely cross-platform (it currently claims netstandard2.0 but is de-facto Windows-only).

---

## Phase 3 — Cross-platform correctness in shared/reader code (medium)

These matter for any future cross-platform GUI or for reusing the RFID code from the CLI, and several are bugs on modern .NET on *Windows* too.

### 3.1 `Thread.Abort` → cooperative cancellation

`Thread.Abort()` throws `PlatformNotSupportedException` on .NET 5+ on **every** OS. Call sites:

- `TeddyBench/SafeThread.cs:40` (the wrapper everything uses)
- `TeddyBench/RfidReaderBase.cs:106`
- `TeddyBench/Proxmark3.cs:1584`
- `TeddyBench/TeddyMain.cs:622, 629, 636, 642, 649, 895, 1952, 2101, 2165`

Replace with `CancellationToken` checks in the thread loops (the scan loops already poll `*ThreadStop` flags, so this is mostly mechanical).

### 3.2 Serial port enumeration

Ports are found via `SerialPort.GetPortNames()` and then **every** port is opened at 115200 to probe it (`Proxmark3.cs:1005-1022`, `Pn5180Esp.cs:194-211`). On macOS `GetPortNames()` returns both `/dev/tty.*` and `/dev/cu.*`, including Bluetooth ports — opening a `tty.*` dial-in device can block indefinitely.

- [ ] On Unix, filter candidates to `/dev/cu.usbmodem*` and `/dev/cu.usbserial*` (never `tty.*`, never Bluetooth).
- [ ] `System.IO.Ports` 6.0.0 ships a Unix implementation, so no package change is needed.
- [ ] The fragile `ex2.TargetSite.DeclaringType.Name == "SerialPort"` checks (`Proxmark3.cs:599`, `Pn5180Esp.cs:120`) should become exception-type checks.

### 3.3 Launching external programs

- `explorer.exe` hardcoded: update-zip reveal (`TeddyMain.cs:557-563`) and "Show in Explorer" (`TeddyMain.cs:1409`). Needs a platform helper: `open -R <file>` on macOS, `xdg-open` on Linux.
- `Process.Start(url)` (`AboutDialog.cs:17, 18, 24`) fails on .NET 5+ on all OSes unless `new ProcessStartInfo(url) { UseShellExecute = true }` is used.

### 3.4 File locations

Config/cache files are opened relative to the current working directory, which breaks when launched as a macOS `.app` (CWD is `/`) and is wrong on Windows too when launched from a shortcut:

- `teddyBench.cfg` (`TeddyMain.cs:263`, `:305`)
- `tonies.json` (`TeddyMain.cs:173`)
- `customTonies.json` (`TeddyMain.cs:193, 225, 249`)
- `cache/` image folder (`TeddyMain.cs:1042-1068`)
- `fullimage.elf` (`Proxmark3.cs:448`)
- Updater writes the zip next to the running exe (`TeddyMain.cs:544-545`) — fails under Program Files and would fail inside a signed `.app` bundle.

Move these under `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` (maps to `~/Library/Application Support` on macOS) with a one-time migration from the old location.

### 3.5 SD-card auto-detection

`TeddyMain.cs:678-688` looks for `DriveType.Removable` drives containing a `CONTENT` folder. On macOS, FAT/exFAT volumes under `/Volumes` typically report `Fixed`. Broaden the check: on macOS enumerate `/Volumes/*` (excluding the root volume) and look for the `CONTENT` folder regardless of reported drive type.

---

## Phase 4 — CI (small)

`.github/workflows/build.yml` currently runs only on `windows-latest`, builds with MSBuild + `nuget restore` (even though it installs .NET 6 and never uses it), and uploads only `TeddyBench.exe`.

- [ ] Add a `macos-latest` job after Phase 1: `dotnet build Teddy/Teddy.csproj` + `dotnet test` (once tests exist).
- [ ] Add `dotnet publish Teddy -r osx-arm64 --self-contained -p:PublishSingleFile=true` (and `osx-x64`, `linux-x64` for free) and upload the CLI artifacts — today the CLI is never shipped at all.
- [ ] Keep the Windows MSBuild job for TeddyBench until Phase 5 is decided.
- [ ] Housekeeping: bump `microsoft/setup-msbuild@v1` → `@v2`; drop the unused .NET 6 install from the Windows job or switch that job to `dotnet build` as well.

---

## Phase 5 — TeddyBench GUI strategy (large, optional)

TeddyBench cannot be ported incrementally. The inventory of hard Windows dependencies:

1. **WinForms + System.Drawing** through all ~20 forms (`*.Designer.cs`, `Program.cs` `[STAThread]`/`Application.Run`, `FolderBrowserDialog`/`MessageBox`/`ImageList` usage throughout `TeddyMain.cs`, image composition at `TeddyMain.cs:1053-1129`). System.Drawing.Common is Windows-only since .NET 7. Mono's WinForms on macOS requires X11 and is not viable.
2. **Audio playback** via `WaveOutEvent` (winmm) at `TeddyMain.cs:2333`.
3. **Encode path** — fixed by Phase 2.
4. **`Thread.Abort`**, **`explorer.exe`**, **CWD file paths**, **drive detection** — fixed by Phase 3.
5. **Packaging**: `win10-x64` RuntimeIdentifier + `PublishSingleFile` in the csproj, Costura.Fody embedding, `ScottPlot.WinForms` for the antenna plot.
6. Designer files hardcode Windows fonts ("Microsoft Sans Serif", "Courier New", e.g. `AboutDialog.Designer.cs:53`).

**Options:**

- **A. Keep TeddyBench Windows-only; invest in the CLI (recommended).** Phases 1–4 give macOS users the full dump/encode/decode workflow. The RFID/serial layer (`Proxmark3.cs`, `Pn5180Esp.cs`, `ISO15693.cs`) is mostly portable after Phase 3 and could be exposed through new CLI verbs (`teddy read-tag`, `teddy write-tag`) at moderate cost.
- **B. Avalonia rewrite.** Cross-platform (Windows/macOS/Linux), closest conceptual fit to WinForms. Requires rewriting every form, replacing `ScottPlot.WinForms` with `ScottPlot.Avalonia` (supported), replacing playback with a cross-platform output (e.g. OpenTK/`libsoundio`-based, or shell out to `afplay`), and replacing System.Drawing image composition with `SkiaSharp`. Realistic scope: a multi-week rewrite; prerequisite: Phases 1–3 so the non-UI logic is already clean. If attempted, first extract the non-UI logic out of `TeddyMain.cs` (2,481 lines mixing UI, HTTP, threading, RFID, and file management — see CLEAN_UP.md) into a shared core so both GUIs can live side by side during the transition.

**Decision point:** choose A now, revisit B only if there is real demand for a macOS GUI.

---

## Suggested order of work

1. Phase 1 (retarget CLI) — ~1 hour, immediately testable on this machine.
2. Phase 2 (encode) — the key unlock; test by round-tripping an MP3 → Tonie file → decode and comparing against a Windows-produced file.
3. Phase 4 (CI) — lock in the gains.
4. Phase 3 — as groundwork for CLI tag-reading verbs and/or Phase 5.
5. Phase 5 — separate decision.

## Verification

- `dotnet build Teddy.sln` fails on macOS today only for TeddyBench; all other projects build (keep it that way via CI).
- `teddy -m info`, `-m decode`, `-m rename` against a sample Tonie dump on macOS.
- `teddy -m encode` an MP3 and an OGG on macOS; decode the result and verify audio integrity; byte-compare header/chapters against a Windows-encoded reference.
- Proxmark3 attached via USB: verify port filtering finds `/dev/cu.usbmodem*` and does not touch Bluetooth ports.
