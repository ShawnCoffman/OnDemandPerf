# OnDemand Perf

Windows tray utility with on-demand CPU, RAM, and storage tests. Prototype version 0.5.0.

## Try it

1. Build with `pwsh -File scripts/Build.ps1` (requires the .NET 10 SDK).
2. Double-click `artifacts/OnDemandPerf/OnDemandPerf.exe`.
3. Find the application icon in the Windows system tray, possibly inside the hidden-icons menu.
4. Right-click it and choose **Run Perf Test**. A compact panel opens above the taskbar on that monitor.

The tray starts quietly. No inventory or benchmark runs at startup. **Run Perf Test** is the only run command in the tray and panel, and tests CPU, RAM and disk. The panel shows live operations, elapsed time, an activity log, and a compact result summary with **Open Results** for the full report. The activity indicator does not imply an estimated percentage. Duplicate starts are disabled while a test runs.

Click the tray icon or choose **Show Status** to reopen the panel. Closing it or pressing Escape hides it while work continues; **Exit** in the tray menu cancels active work and exits. **Cancel** requests a graceful stop; an unresponsive worker is terminated after a deadline. The panel distinguishes completed, incomplete, cancelled and failed tests. The Open Results button is disabled during a run and when no new report was saved. Historical reports remain accessible through the tray menu.

For a fresh process, launching `OnDemandPerf.exe /show` opens the panel immediately without starting a test. Launching without arguments stays quiet.

The panel uses a dark instrument-style theme with cyan headings and green CPU, purple RAM, and cyan disk cards. Segmented lights indicate activity, not utilization or percent complete. After completion, cards show CPU multithread MB/s, RAM copy GB/s, and disk sequential-read MB/s; the log and full report retain the other measurements. Incomplete stages show their status instead of a score. Drag the header to move the panel, or use its close button/Escape to hide it.

The app and tray share a custom meter icon with 16–256 px variants. Regenerate it with `pwsh -File scripts/Generate-Icon.ps1` before building when changing its design.

The CPU stage targets approximately 16 seconds, preceded by a two-second baseline sample. It measures single-thread compression (three 1.8-second passes) followed by multithread compression (three 3-second passes). Each pass includes a separate 250 ms minimum warmup per worker. Dedicated workers rendezvous before timing starts; the measured interval includes wakeup and completed-operation overrun, but excludes thread creation, warmup, and output verification. The CPU stage has an 20-second cooperative safety deadline. The computer can feel slower while the multithread test runs.

## Local development signing

Run `pwsh -File scripts/Sign-Local.ps1` to self-sign the existing artifacts, or
`pwsh -File scripts/Build.ps1 -SelfSign` to build and sign. This signs the app,
worker, and OnDemandPerf DLLs, including copies in subdirectories. Rebuilding
without `-SelfSign` replaces the signatures with unsigned output.

The script creates or reuses an RSA code-signing certificate in your current-user
Personal certificate store. Its private key is non-exportable; no private key is
saved in this repository. Signatures have no timestamp and expire with the
certificate. No trusted-root/publisher stores or Windows security settings are
changed, so Windows may report the signature as untrusted.

**Self-signing does not unblock Smart App Control.** It requires a certificate
from a trusted provider; see [Microsoft's signing requirements](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control).

## Results

Default location: `%LOCALAPPDATA%\OnDemandPerf`.

- `Latest.json` and `Latest.txt`
- `Results/`: up to 50 historical JSON/text pairs
- `OnDemandPerf.log`: rotates at 2 MiB, retaining one backup

JSON records the dataset identifier, compression settings, worker count, runtime version, actual bytes processed, individual pass timings, and aggregate throughput. MB/s uses decimal megabytes. Aggregate throughput is total bytes divided by total measured time, excluding warmup. This is application compression throughput, not an overall CPU score.

Results include the computer name and CPU information and remain local. Review collection/output permissions before production deployment.

## Command line

```powershell
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /inventory
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /cpu
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /cpu /output C:\Temp\OnDemandPerf
```

Exit codes: `0` success; `1` component failure or cancellation; `2` fatal error or invalid arguments; `3` another worker is running. Ctrl+C cancels a console run. A controlling process can send `cancel` followed by a newline to redirected standard input. The worker stops new work at 55 seconds to reserve cleanup/report time; the tray requests process termination at 60 seconds. A stalled operating-system I/O request can delay cancellation or termination. External management should also enforce a process deadline.

The tray is single-instance per Windows session. A machine-wide named mutex prevents overlapping worker executions; inability to access another identity's lock fails closed. Cross-account/SYSTEM execution still requires deployment testing.

## Start at sign-in

After building, run `pwsh -File scripts/Set-Startup.ps1` to register this build for **your user account**. Run the same command with `-Disable` to remove registration. This references the current artifacts folder, so moving it requires registering the new path. Windows startup settings can disable the entry.

Neither building nor the smoke tests register startup automatically. Fleet-wide startup registration belongs in a future managed installer.

## Development and validation

```powershell
pwsh -File scripts/Build.ps1
pwsh -File scripts/Test-Smoke.ps1
pwsh -File scripts/Test-FullAnalysis.ps1
```

The smoke script runs a real CPU workload and checks inventory-only operation, pass accounting, overlap prevention, cancellation, invalid arguments, output failure, and quiet tray startup. It stores test results under `artifacts` and stops the tray instance it launches. Close any existing prototype instance before running it.

- `src/OnDemandPerf`: Windows Forms tray and test window
- `src/OnDemandPerf.Worker`: CLI entry point, process lifetime, cancellation
- `src/OnDemandPerf.Core`: inventory, compression benchmark, result models and persistence

No third-party NuGet dependencies. Project code disallows unsafe blocks. The solution uses a repository NuGet configuration with no feeds; installed SDK targeting packs are required.

## Prototype limits

This is a framework-dependent build requiring the .NET 10 Desktop Runtime on the target machine; keep the entire artifacts/OnDemandPerf folder together. It is not a signed or self-contained production package.

Inventory includes computer name, friendly Windows product name, edition, release, build, architecture, CPU name, active physical core count, system-wide logical processor count, process-available logical processor count, and runtime version. Conditions include OS-usable physical RAM (total/available/used), RAM utilization, AC/battery state, battery charge when available, active power plan name/GUID, and a two-second CPU activity sample before warmup. CPU, RAM, and disk benchmarks are implemented. Full BIOS/physical-storage inventory, DIMM details, and CPU socket/hybrid-core breakdown remain future work.

Fixed input improves repeatability, but background activity, power policy, thermals, runtime updates, and machine configuration still affect results. A two-second baseline and roughly one-second activity samples record system CPU load, available memory, and AC/battery state. System CPU includes the benchmark; temperature and clock speed are not sampled, so throughput changes do not diagnose thermal throttling. Fleet comparisons still need broader validation. The app includes a custom meter icon in multiple resolutions. Interactive window/tray-menu behavior needs human UI acceptance testing; automated smoke checks cover process startup and the worker.

Before broad deployment: complete inventory/environment capture, validate multiple Windows versions and processor configurations, choose runtime servicing and installation policy, sign releases, validate user/SYSTEM permissions, and pilot staged rollout with rollback.

## Inventory and environment semantics (schema 1.1)

Existing JSON fields remain available. `device.logicalProcessors` continues to mean processors available to the worker; `device.systemLogicalProcessors` counts active logical processors across the system. Physical cores are active cores reported by Windows, not CPU-name parsing.

`environment` stores memory in bytes and utilization in percent. Text reports display binary GiB. Physical memory is OS-usable RAM and can be lower than the installed DIMM total. CPU sampling uses idle/kernel/user counter deltas, includes all processes, and is not frequency-normalized like some Task Manager metrics. The sample duration is recorded. Multiple processor groups are explicitly marked unsupported for this CPU activity measurement rather than reporting a partial group as the entire machine; the compression benchmark can still run.

Optional collector failures add `warnings` to JSON, the text report, and the log while leaving other collectors and benchmarks running. Unavailable values are null, not fabricated zeros. A successful run can have collection warnings. Cancellation/fatal failures remain distinct from missing optional fields. A desktop with no battery is normal.

Power plan is the active Windows power scheme; it is not the separate Windows Settings power-mode overlay. No power setting is changed.

API references: [CPU topology](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getlogicalprocessorinformationex), [CPU time counters](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes), [physical memory](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex), [power status](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status).
## CPU benchmark v3 and the full-analysis budget

Full analysis targets approximately 45–55 seconds with a 60-second overall deadline and a 55-second cooperative work deadline. The baseline takes two seconds, CPU approximately 16 seconds, RAM bandwidth about eight seconds, memory latency about five seconds, and disk approximately 16 seconds. Disk write caps remain firm; remaining disk time goes to random reads. Unsupported stages, cancellation, and read sample caps can shorten a run; the tool does not pad it with sleeps. Individual OS requests can overrun cooperative time limits.

`deflate-v3` uses longer passes; compare it only with matching v3 runs. The earlier `deflate-v2` changed timing from `deflate-v1`: synchronized dedicated workers and a reusable in-memory output buffer change the measurement method. The deterministic dataset and compression level remain fixed. The runtime version remains part of the comparison context.

JSON schema 1.2 contains `cpu.singleThread` and `cpu.multiThread`. Each has three passes, weighted aggregate MB/s, population coefficient of variation (standard deviation / arithmetic mean of pass throughput), and signed first-to-last throughput change. Existing `cpu.compressionMBps` and `cpu.passes` remain aliases for multithread results. Requested concurrency is distinguished from observed worker participation and per-worker processed bytes; participation does not prove simultaneous execution on separate physical cores.

Each worker retains its last compressed block. After all timing and CPU-time sampling finish, each block is decompressed and compared byte-for-byte to the input. This checks the final operation per worker/pass, not every timed operation and not general CPU hardware stability. Verification failures prevent a successful score.

Per-pass process CPU seconds and average busy logical processors (`process CPU seconds / wall seconds`) are diagnostic measurements. They include small process coordination overheads and do not measure system-wide background load or CPU frequency. Single-thread means one compression worker, not a fixed processor affinity; Windows chooses where it runs.

Quality notes use provisional thresholds of >5% pass variation and a >10% first-to-last decline. These suggest a retest; they are not calibrated fleet health thresholds, statistical confidence intervals, or proof of thermal throttling. No automatic repeat extends the run. Cancellation or an exceeded stage budget produces an unsuccessful run rather than a misleading partial aggregate.
## RAM and disk profiles (schema 1.4)

```powershell
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /run
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /memory
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /disk
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /disk /diskpath C:\Temp\OnDemandPerf\TestData
```

`/output` changes report placement; `/diskpath` separately selects the test-file directory. The default is `%LOCALAPPDATA%\OnDemandPerf\TestData`, checked to reside on the Windows drive. Only local fixed volumes are supported. A redirected default directory on another drive is reported unsupported instead of silently testing it. Compare results only with matching benchmark version, RAM buffer sizes, storage workload/cache policy, file size, runtime, and hardware configuration.

RAM (`memory-v2`) uses two 32–256 MiB buffers. Together they use at most one eighth of available RAM at allocation time and at most 512 MiB; tests skip if the minimum buffer size cannot fit this policy. Allocation failure can reduce the buffer size. Pages are touched before timing. One worker runs copy/read/write with one untimed warmup and three 0.9-second measured passes each. Copy and write use managed spans; read uses a vector OR reduction over every byte, with its result checked to prevent dead-code elimination. Copy/write contents are verified outside the timed passes. This is not an exhaustive RAM integrity test. The profile is application throughput, affected by CPU/cache behavior; GB/s counts logical payload, not doubled read-plus-write bus traffic. RAM bandwidth and latency share an 18-second cooperative stage deadline. Completed bandwidth remains available if latency is interrupted; incomplete latency passes never produce an aggregate latency score. Copy/read/write pass variation is reported separately. The worker releases the large buffers before disk testing.

Disk (`disk-qd1-v2`) uses a unique CREATE_NEW file and Windows delete-on-close. It never opens a user file for writing. At least 2 GiB of free volume space is required. System file-data caching is bypassed using FILE_FLAG_NO_BUFFERING; writes request WRITE_THROUGH. Native buffers are owned by SafeHandles, sector alignment is checked, and unsupported configurations do not fall back to cached measurements. Compressed/encrypted/sparse test files are unsupported. Hardware caches, filters, deduplication, and device firmware can still influence measurements; these are application I/O measurements, not vendor peak or sustained media bandwidth.

- Sequential write: 1 MiB requests, up to 1 GiB or two seconds. This also creates the test file. At least 32 MiB must be created to proceed.
- Sequential read: 1 MiB requests over the created file for six seconds; a one-million-operation safety cap bounds sample storage.
- Random read: deterministic 4 KiB offsets, queue depth 1, after random-write verification. Samples fill the remaining 16-second disk observation window (minimum one second), capped at one million operations. Previously modified offsets are tracked so read verification uses the correct original or inverted pattern.
- Random write: deterministic 4 KiB offsets, queue depth 1, up to 16,384 operations (64 MiB) or two seconds; runs before random reads. Bytes are inverted from the initial pattern so writes change the file.
- Total issued write payload is capped at 1,088 MiB per completed disk test, excluding filesystem metadata and device write amplification. Actual completed bytes are recorded even on cancellation.

Each disk result records operations, bytes, duration, MB/s, IOPS, mean and nearest-rank p95 request latency, and whether the byte cap or time budget ended the sample. Fewer than eight operations are marked insufficient. Samples under one second have `shortBurst: true` and a visible short-burst note. No benchmark claims sustained thermal/storage endurance behavior.

Verification samples sequential first/middle/last blocks and the final random-read block. Every completed random-write location is read back outside timing and compared. Verification is included in the disk stage's 22-second budget. Disk failures retain previously completed measurements and do not erase CPU/RAM results. Deleting on handle close also covers worker termination; normal cleanup is checked and recorded. No pre-existing files are removed during cleanup.

The full integration script exercises real CPU/RAM/disk workloads, verifies accounting and write limits, checks RAM/disk cancellation, kills a disk worker to check delete-on-close, and checks an existing sentinel file remains unchanged. Test data and reports stay under the repository's `artifacts` directory. Low-space, storage alignment, very large-cache systems, and slow/unsupported devices still need fleet validation.

Windows reference: [unbuffered I/O and sector alignment](https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering).


## Memory latency and live context

`dependent-access-v1` reuses one existing 32–256 MiB RAM buffer after bandwidth testing. A seeded Sattolo permutation forms one dependent index chain with nodes spaced 64 bytes apart. A full traversal verifies the chain and warms it before three 1.25-second measured passes. The aggregate is total measured time divided by total dependent accesses, reported in ns/access with pass variation and working-set size. It includes managed bounds checks, loop/cancellation overhead, cache behavior, and scheduling; it is application-observed latency, not a DRAM-only measurement. A large cache or a reduced buffer can affect comparisons. No new large buffer is allocated for this stage.

Schema 1.4 adds `memory.latency`, `memory.variationPercent`, disk `shortBurst`, and an `activity` timeline. Each activity sample records elapsed time, its actual interval, the stage at the end of that interval, total system CPU utilization when supported, available physical memory, and AC/battery state. Missing readings remain null. The text report includes time-weighted mean/peak sampled CPU and lowest sampled available RAM. System CPU includes this benchmark and is not an estimate of other applications' CPU usage. No continuous temperature, frequency, or wattage monitoring is claimed.

Profile versions are now `deflate-v3`, `memory-v2`, and `disk-qd1-v2`; do not pool scores with older profiles. Disk write measurements remain bounded burst observations, not sustained SSD or endurance results. Reads revisit the test file and device caches can still affect measurements despite bypassing Windows file-data caching.
