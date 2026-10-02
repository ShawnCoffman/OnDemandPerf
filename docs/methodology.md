# Methodology and limits

Every number is **application-observed throughput** under a fixed, deterministic workload. It is not a vendor peak figure, an
overall score, or a diagnosis. Compare results only when the benchmark version **and** profile fingerprint match
(`cpu.benchmarkVersion` + `cpu.profileHash`, and likewise for `memory` and `disk`). The fingerprint hashes the numeric
parameters (durations, pass counts, sizes, seeds); it cannot see logic changes, so a change to a dataset or algorithm still
needs a new version name. The runtime version, power state and hardware configuration are part of the comparison context.

Current profiles: `deflate-v3`, `memory-v3` (includes `dependent-access-v1`), `disk-qd1-v3`.

## Time budget

Full analysis targets about 50 seconds with a 55-second cooperative work deadline and a 60-second tray termination deadline.
Indicative split: environment baseline 2 s, CPU about 16 s, RAM about 17 s (single-thread bandwidth about 9 s, multithread about
3.5 s, latency about 5 s), disk about 16 s. Stage safety budgets are CPU 20 s, RAM 21 s, disk 22 s; the global 55-second deadline
still wins, so on a slow machine later stages can be cut short. The tool never pads a run with sleeps. Individual OS requests
can overrun cooperative limits.

## Statistics

Pass-to-pass variation is the coefficient of variation using the **sample** standard deviation (n-1) divided by the mean; with
only three passes the population formula understates spread by roughly 20%. Headline CPU scores are aggregates (total bytes /
total measured time); the median pass is also recorded because it is less sensitive to one disturbed pass. Quality notes use
provisional thresholds (more than 5% variation, more than 10% first-to-last decline). They suggest a retest; they are not
calibrated fleet thresholds, confidence intervals, or proof of thermal throttling. Nothing extends a run automatically.

## CPU (`deflate-v3`)

Single-thread compression (three 1.8 s passes) followed by multithread compression (three 3 s passes), at .NET's `Optimal`
Deflate level over a deterministic 1 MiB dataset (a 64 KiB xorshift/pattern block repeated). Each pass includes a separate 250 ms
warmup per worker. Dedicated workers rendezvous before timing; the measured interval includes wakeup and completed-operation
overrun but excludes thread creation, warmup and verification. After timing and CPU-time sampling, each worker's last compressed
block is decompressed and compared byte for byte (the final operation per worker and pass, not every operation, and not a
hardware stability test).

- Throughput uses decimal megabytes. Requested concurrency is distinguished from observed participation and per-worker bytes;
  participation does not prove simultaneous execution on separate physical cores.
- Per-pass process CPU seconds and average busy logical processors are diagnostics that include small coordination overheads.
- "Single-thread" means one worker, not a fixed affinity. On hybrid CPUs Windows may place it on a performance or an efficiency
  core, which can move the result noticeably between runs.
- If the stage exceeds its 20 s budget, completed stages are kept, the unfinished stage has no score, and the stage status is
  `TimedOut`. User cancellation or the global deadline still produces a Cancelled run without a partial aggregate.
- The result depends on the runtime's Deflate implementation, so the .NET version matters.

## RAM (`memory-v3`)

Two 32–256 MiB buffers, together at most one eighth of available RAM and at most 512 MiB; skipped if the minimum cannot fit.
Pages are touched before timing and the buffers are released before the disk stage.

- **Single worker**: copy, read and write, each with one untimed warmup and three 0.9 s passes. Copy and write use managed spans;
  read is a vector OR reduction over every byte whose result is checked. Copy/write contents are verified outside timing.
  A single thread cannot saturate the memory system, so this profile is largely insensitive to channel count.
- **Multithread** (new in v3): `min(logical processors, 8)` dedicated workers each stream their own 4 KiB-aligned slice of the
  same two buffers for read and for copy (three 400 ms passes each, after a warmup over the worker's slice and a rendezvous).
  Copy output is verified after timing. This is the figure that responds to single- versus dual-channel configurations. Passes are
  named `ReadMT` / `CopyMT` in the pass list; skipped with a note on single-processor machines.
- GB/s counts logical payload (decimal), not read-plus-write bus traffic, and is affected by CPU and cache behavior. This is not
  an exhaustive RAM integrity test.
- The RAM stage has a 21 s cooperative deadline. Completed bandwidth remains available if latency is interrupted; incomplete
  latency passes never produce an aggregate.

### Memory latency (`dependent-access-v1`)

Reuses one existing buffer after the bandwidth tests. A seeded Sattolo permutation forms one dependent index chain with 64-byte
node spacing; a full traversal verifies and warms the chain before three 1.25 s measured passes. The aggregate is total
measured time divided by total dependent accesses, in ns/access. It includes managed bounds checks, loop and cancellation
overhead, TLB and cache behavior, and scheduling; it is not a DRAM-only latency. Large caches or a reduced buffer change
comparisons.

## Disk (`disk-qd1-v3`)

Unique `CREATE_NEW` file with delete-on-close; it never opens a user file for writing and removes nothing that already existed.
At least 2 GiB of free space is required. Windows file-data caching is bypassed with `FILE_FLAG_NO_BUFFERING` and writes request
`WRITE_THROUGH`; native buffers are SafeHandle-owned, sector alignment is checked, and unsupported configurations (compressed,
encrypted or sparse files, network or UNC paths, non-fixed volumes) are reported unsupported rather than measured through a
cache. Hardware caches, controller or SAN caches, filters, deduplication and firmware can still influence results.

- **Sequential write**: 1 MiB requests, up to 1 GiB or 2 s; also creates the test file (at least 32 MiB must be created).
- **Sequential read**: 1 MiB requests over the created file for 6 s. Fast drives read the 1 GiB file many times in that window,
  so the report gives both the aggregate and `firstPassMBps` (the first complete read) plus `filePasses`. The card shows the
  first-pass figure because later passes can be served from device or controller cache.
- **Random write**: 4 KiB at deterministic offsets, queue depth 1, up to 16,384 operations (64 MiB) or 2 s; runs before random
  reads. Bytes are inverted from the initial pattern so writes change the file; every completed location is read back and
  verified outside timing.
- **Random read**: 4 KiB at deterministic offsets, queue depth 1, filling the remainder of the 16 s observation window (minimum
  1 s, capped at one million operations). Offsets use a **different seed from the writes**; in v2 they shared a seed, so the
  first 16,384 reads hit just-written blocks that could still be in a write cache.
- Total issued write payload is capped at 1,088 MiB per completed test, excluding filesystem metadata and write amplification.
  Actual bytes are recorded even on cancellation.
- Each measurement records operations, bytes, duration, MB/s, IOPS, mean and nearest-rank p95 latency, and what ended the sample.
  Fewer than eight operations is `InsufficientSample`. Under one second sets `shortBurst`. Nothing claims sustained or endurance
  behavior; the test file is a repeated 1 MiB pseudo-random block, so deduplicating or compressing storage can flatter results.
- Verification samples first/middle/last blocks after sequential phases, every random-write location, and the final random-read
  block, within the stage's 22 s budget. Disk failures keep earlier CPU and RAM results.

Not covered: queue depths above 1, sustained writes, device health, and low-space or very large-cache systems (these need fleet
validation).

## Environment and activity

Inventory: computer name, Windows product name, edition, release and build, architecture, CPU name, active physical cores,
system-wide and process-available logical processors, runtime version. Conditions before the test: OS-usable physical RAM,
utilization, AC/battery, battery charge when available, active power plan (not the Settings power-mode overlay), and a two-second
system CPU sample. During the test, roughly one sample per second records system CPU, available RAM and power source with the
actual interval. System CPU includes the benchmark itself and uses idle/kernel/user deltas (not frequency-normalized); multiple
processor groups are marked unsupported rather than reporting a partial group. Optional collector failures become `warnings`
and leave other collectors running; unavailable values are null, never invented zeros. Temperature and clock speed are **not**
sampled, so a throughput change cannot be attributed to thermal throttling from this data alone.

API references: [CPU topology](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getlogicalprocessorinformationex),
[CPU time counters](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes),
[physical memory](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex),
[power status](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status),
[unbuffered I/O and sector alignment](https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering).
