# Changelog

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
