# Result schema 2.0

JSON is camelCase. Memory is in bytes, utilization in percent, MB = 1,000,000 bytes, GB = 1,000,000,000 bytes. Unavailable
values are `null`. `Latest.json` and every file in `Results/` use the same schema; `/compact` removes indentation only.
The typed model is `OnDemandPerf.Core.AnalysisResult`; the tray reads results through it rather than by key lookups.

## Top level

`schemaVersion`, `applicationVersion` (from `Directory.Build.props`), `runId`, `timestamp`, `durationSeconds`, `status`
(`Success`, `Partial`, `Cancelled`, `Failed`), `device`, `environment`, `cpu`, `memory`, `disk`, `activity`, `warnings`, `errors`.
A stage that did not run is `null`. A successful run can carry warnings.

## Stage fields worth knowing

- Every stage has `benchmarkVersion`, `profileHash` and `status`. Pool results only when both version and hash match.
- `cpu.singleThread` / `cpu.multiThread`: `mBps` (aggregate, the headline), `medianMBps`, `variationPercent` (sample SD / mean),
  `firstToLastChangePercent`, `passes[]` (bytes, duration, participating workers, per-worker bytes, process CPU seconds,
  average busy logical processors, `outputVerified`). A stage is `null` if it did not finish (`cpu.status` is then `TimedOut`).
- `cpu.outputVerified` is true only if both stages completed.
- `memory`: `copyGBps`, `readGBps`, `writeGBps` (single worker), `multiThreadWorkers`, `multiThreadReadGBps`,
  `multiThreadCopyGBps`, `passes[]` (operations `Copy`, `Read`, `Write`, `ReadMT`, `CopyMT`), `variationPercent` per operation,
  `latency` (`nanosecondsPerAccess`, `cycleVerified`, passes).
- `disk.measurements[]`: `SequentialWrite`, `SequentialRead`, `RandomWrite`, `RandomRead`, each with operations, bytes, duration,
  `mBps`, `iops`, mean and p95 latency, `shortBurst`, `stopReason`. `SequentialRead` also has `filePasses` and `firstPassMBps`.

## Changes from 1.4 (breaking)

- Removed the aliases `cpu.compressionMBps`, `cpu.passes` and the top-level `cpu.requestedMillisecondsPerPass`; use
  `cpu.multiThread.mBps`, `cpu.multiThread.passes` and the per-stage `requestedMillisecondsPerPass`.
- Added `profileHash` to cpu, memory, latency and disk; `cpu.multiThread.medianMBps` and `cpu.singleThread.medianMBps`.
- `variationPercent` now uses the sample standard deviation (n-1); values are about 22% larger than 1.4 for three passes.
  The method is named in `variationMethod`.
- `memory-v3` adds the multithread fields above. `disk-qd1-v3` changes the random-read offset sequence and adds
  `filePasses` / `firstPassMBps` to the sequential read.
- `cpu.status` may be `TimedOut` with a null stage instead of the run failing outright.
- `applicationVersion` is read from the assembly rather than hard-coded.
