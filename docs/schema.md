# Result schema 2.2

JSON is camelCase. Memory is in bytes, utilization in percent, MB = 1,000,000 bytes, GB = 1,000,000,000 bytes. Unavailable
values are `null`. `Latest.json` and every file in `Results/` use the same schema; `/compact` removes indentation only.
The typed model is `OnDemandPerf.Core.AnalysisResult`; the tray reads results through it rather than by key lookups.

## Top level

`schemaVersion`, `applicationVersion` (from `Directory.Build.props`), `runId`, `timestamp`, `durationSeconds`, `status`
(`Success`, `Partial`, `Cancelled`, `Failed`), `validity` (`Valid`, `Invalid`), `findings`, `device`, `environment`, `cpu`, `memory`, `disk`, `activity`, `warnings`, `errors`.
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
- `environment.processPowerThrottling`: whether the worker opted out of Windows efficiency mode (`OptedOut`, `Failed: ...`).
- `disk.measurements[]`: `SequentialWrite`, `SequentialRead`, `RandomWrite`, `RandomRead`, each with operations, bytes, duration,
  `mBps`, `iops`, mean and p95 latency, `shortBurst`, `stopReason`. `SequentialRead` also has `filePasses` and `firstPassMBps`.

## Findings, power and conditions (2.2, additive)

- `validity`: `Invalid` when the PC was in a power-saving plan, power mode or Energy saver. `status` is unchanged by it.
- `findings[]`: `severity` (`Invalid` or `Warning`), `area` (`Power`, `CPU`, `RAM`, `Disk`, `System`), `message`. Invalid
  first. Rules and thresholds are in [methodology](methodology.md#validity-and-findings).
- `environment` gained, all nullable: `powerPlanType` (`Balanced`, `HighPerformance`, `UltimatePerformance`, `PowerSaver`,
  `Custom`), `powerMode` (`Balanced`, `BestPerformance`, `BestPowerEfficiency`, `BatterySaver`, `Other {guid}`),
  `energySaverOn`, `maxProcessorStatePercent`, `processorBoostMode` (0 = disabled), `uptimeDays`, `pendingRestart` (list of
  reasons, empty when none), `commitBytes`, `commitLimitBytes`, `systemDrive`, `systemDriveFreeBytes`, `systemDriveTotalBytes`,
  and `topProcesses[]` (`name`, `processId`, `cpuPercent` of the whole machine).
- `activity.samples[]` gained `processorPerformancePercent` and `performanceLimitPercent`.
- `cpu.singleThread`, its passes, `memory` and `memory.latency` gained `corePlacement` and `onFastestCoresPercent`.
- `disk` gained `fileLimitBytes` and `reducedForFreeSpace`.
- Profiles changed: `deflate-v4`, `memory-v4` (fixed 256 MiB buffers), `dependent-access-v2`. `disk-qd1-v3` is unchanged for full-size
  runs.
- `disk.notes` no longer repeats the method text or short-burst warnings on every run (see `cachePolicy`, `dataset`,
  `shortBurst` and the methodology).

## Hardware identity (2.1, additive)

`device` gained, all nullable: `cpuBaseMhz` (registry nominal clock, not a live or turbo frequency), `hardware`
(`manufacturer`, `model`, `family`, `biosVendor`, `biosVersion`, `biosDate`), `installedMemory` (`type` such as `DDR5`,
`totalBytes`, `moduleCount`, `slotCount`, `ratedMts`, `configuredMts`, `modules[]` with `slot`, `sizeBytes`, `type`,
`formFactor`, `ratedMts`, `configuredMts`, `manufacturer`, `partNumber`) and `testDrive` (`volume`, `model`, `firmware`,
`busType`, `nvmeVersion`, `isSolidState`, `sizeBytes` of the volume). Placeholder firmware strings ("To Be Filled By O.E.M.")
are null. Serial numbers are not collected. 2.0 readers keep working; the fields are simply absent in older files.

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
- Added `environment.processPowerThrottling`.
