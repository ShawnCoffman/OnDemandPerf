# CPU scores halved between v0.4.0 and v0.5.0

Found while reviewing the project on 2026-10-01. Status: **explained by the data, cause not yet confirmed on hardware.**

## Observation

Every saved run in `artifacts/` (machine: i9-14900K, 24 cores / 32 logical, Windows 11 25H2, .NET 10.0.7, "High performance"
power plan on AC, in every run):

| Date / time | App | Profile | Single-thread MB/s | Multithread MB/s |
|---|---|---|---|---|
| 09-24 19:54 – 20:08 (7 runs) | 0.3.0, 0.4.0 | deflate-v2 | 180 – 186 | 3,139 – 3,242 |
| 09-25 06:49 – 06:53 (4 runs) | 0.5.0 | deflate-v3 | 95.7 – 105.8 | 1,540 – 1,589 |

Within each version the runs agree to a few percent; the move between them is a step change of about -48% (single) and -50%
(multi). The README attributed v3 only to "longer passes" (1.5/2.0 s became 1.8/3.0 s), which cannot explain this.

## The evidence that matters: busy logical processors

Each pass records process CPU seconds divided by wall time:

| Run | Multithread workers | Average busy logical processors | Single-thread busy |
|---|---|---|---|
| v0.4.0 (full-validation) | 32 | **29.9 – 30.9** | 0.99 – 1.0 |
| v0.5.0 (full-smoke) | 32 | **15.2 – 15.8** | 0.85 – 0.98 |

With 32 requested workers, only half a machine's worth of CPU time was used in v0.5.0. A 14900K has 8 P-cores (16 threads) and
16 E-cores (16 threads). 15.5 busy logical processors is almost exactly "the 16 E-core threads were fully busy and the P-core
threads were not used". The throughput arithmetic agrees: 16 E-cores at about 96 MB/s each is about 1,540 MB/s (observed 1,589),
and the single-thread score of about 96 MB/s is what one E-core would give, versus about 186 MB/s on a P-core.

## Leading hypothesis

The v0.5.0 runs executed with the worker confined to efficiency cores, most likely Windows 11 efficiency mode / EcoQoS being
applied to the hidden worker (they were run by script with no window, apparently unattended early in the morning), or an
equivalent scheduler decision. The v0.4.0 runs, hours earlier, were not. Other possibilities that fit less well: a Windows or
microcode update overnight, a change in the Windows power-mode overlay (not captured; only the power *plan* is recorded), or a
change in the benchmark code that is not described in the README (there is no earlier history to diff; git starts at 0.5.0).

## What was done

- `ProcessQos.OptOutOfPowerThrottling()` runs at worker start and records `environment.processPowerThrottling`
  (`OptedOut`, `Failed: ...`). It changes only the worker process.
- `CpuBenchmark.UtilizationNote` adds a quality note when a stage's average busy logical processors fall below 75% of the
  requested workers. This pattern would have flagged the v0.5.0 runs immediately. It is unit tested against the numbers above.

## To confirm (needs the machine)

1. Build, run `/cpu` three times while the machine is idle and attended. If multithread is back near 3,000+ MB/s and
   `averageBusyLogicalProcessors` is near 30, the opt-out (or simply being foreground) fixed it.
2. Run it again as before (hidden, unattended, or with the screen off). If the note fires and the score drops, the cause is
   OS scheduling of the background process, and the tray/worker launch path needs to keep that from happening.
3. Look at the worker in Task Manager > Details, add the "Efficiency mode" column, and watch per-core load in the Performance
   tab during a run (P-cores idle while E-cores are pegged would settle it).
4. If it persists after the opt-out, consider setting thread QoS or a CPU-set preference explicitly, and recording the
   efficiency class of the cores each worker ran on.

Until this is settled, **do not compare v0.5.0 CPU scores with earlier ones, and treat v0.5.0 CPU numbers as possibly understating
the machine.** Disk and RAM results are not implicated.
