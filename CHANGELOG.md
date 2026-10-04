# Changelog

## Unreleased — review fixes

Result schema and benchmark profiles are unchanged; scores stay comparable with 0.7.0.

### Added
- MIT license. README rewritten for outside readers (what it is, getting started, privacy, limitations).

### Fixed
- A run that reaches the 55-second work deadline is no longer reported as "Test cancelled". User cancellation and the deadline
  are tracked separately; each stage also stops one second before the deadline (the disk random-read window shortens to fit),
  so a late stage ends `TimedOut` with its measurements and the run is `Partial`. Only user cancellation gives `Cancelled`.
- The tray notification now covers failures, timeouts and worker crashes, not just completed runs, and lists only the stages
  that produced a score (no more "CPU — MB/s").
- The worker is in a job object owned by the tray, so it is ended if the tray crashes, is killed or the user signs out.
  The worker's standard-input reader now accepts `cancel` on any line.
- A reader error during cleanup could escape an async event handler and crash the tray.
- The RAM "XMP/EXPO may be off" hint no longer appears for laptop memory (SODIMM, CAMM, soldered); CAMM modules are
  recognised; the below-rated check cannot overflow on bad firmware values.
- The panel shrinks to fit short screens, is positioned before it appears, and `/show` places it on the primary monitor
  instead of wherever the cursor is.
- The build no longer leaves a stray `OnDemandPerf.Worker.exe` (without its dll) next to the tray, and `Build.ps1` starts
  from empty output folders and refuses to run while the built tray is running.
- The text report formats every number with the invariant culture.
- Ctrl+C after a console run finished no longer throws; report files are flushed to disk before they replace the old ones.

## 0.7.0 (2026-10-01) — hardware identity

Result schema 2.1 (additive, see `docs/schema.md`). Benchmark profiles are unchanged, so scores stay comparable with 0.6.0.

### Added
- System make, model and BIOS; RAM size, DDR generation, rated and running speed, module and slot count, vendor and part
  number (SMBIOS); CPU base clock; test-drive model, firmware, bus type, SSD/HDD and NVMe specification version.
- A "this machine" block at the top of the panel, read when the panel is first shown, plus a HARDWARE section in the text
  report and an amber hint line (single RAM module, RAM below rated speed, spinning disk, USB storage).
- Unit tests for the SMBIOS and storage parsers (hand-built byte buffers) and for the summary wording.

### Changed
- `SystemInventory.Collect` takes the disk-test directory so the right drive is identified.
- Panel is taller (480x590) to fit the new block. `Test-Smoke.ps1` / `Test-FullAnalysis.ps1` expect schema 2.1.

## 0.6.0 (2026-10-01) — review fixes; written without a compiler, see `docs/notes/open-items.md`

Result schema 2.0 (breaking, see `docs/schema.md`). Profiles: `deflate-v3` (unchanged), `memory-v3`, `disk-qd1-v3`.

### Added
- Multithread RAM read/copy bandwidth (up to 8 workers) alongside the single-worker numbers.
- Disk sequential read reports `firstPassMBps` and `filePasses`; the card shows the first full pass.
- `profileHash` on every stage: a fingerprint of the numeric parameters that define a profile.
- Median pass rate for CPU stages; `variationMethod` fields.
- CPU quality note when workers received far less CPU time than requested; worker opts out of Windows efficiency mode and
  records `environment.processPowerThrottling`.
- Tray notification when a test finishes with the panel hidden; `/show` raises an already-running tray.
- `/compact` worker switch (single-line JSON); case-insensitive switches.
- Worker-to-tray protocol lines `##stage` and `##report`.
- Git repository, dependency-free unit tests (`scripts/Test-Unit.ps1`), `docs/` and `docs/notes/`.

### Changed
- Variation uses the sample standard deviation (n-1).
- Disk random reads use a different offset sequence from random writes.
- CPU stage timeout keeps completed stages and reports `TimedOut` instead of failing the stage.
- Tray reads the typed result via `AnalysisResult` and the exact report path the worker names; summaries moved to Core.
- Worker exits 3 (not 2) when another account holds the lock. UNC paths are reported unsupported for the disk test.
- RAM stage deadline 18 s to 21 s; full run about 50 s.
- Version is read from `Directory.Build.props` instead of being hard-coded.
- README split into README, `docs/methodology.md` and `docs/schema.md`; test scripts clean up their output (`-Keep` retains it).

### Removed
- `cpu.compressionMBps`, `cpu.passes`, top-level `cpu.requestedMillisecondsPerPass` (aliases).
- `BuildInfo.txt` (stale; recoverable from the baseline commit).

## 0.5.0
Project as received: CPU, RAM, memory latency and disk profiles, tray panel, worker, smoke tests.
