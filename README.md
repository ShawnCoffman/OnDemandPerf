# OnDemand Perf

A small Windows tray app that checks how fast a PC's CPU, RAM and storage really are, on demand, in about 50 seconds. It
sits quietly in the notification area using no CPU until you ask it to run a test, then shows live progress, a compact
summary and a full text/JSON report. It also identifies the hardware (make and model, CPU, RAM modules and speed, test
drive) and points out common causes of slowness, such as a single RAM module, RAM below its rated speed, a spinning disk or
USB storage.

It is useful for a quick health check of a machine, comparing a PC before and after a change, or spotting a laptop that is
much slower than its siblings. It is early-stage software: see [Limitations](#limitations) and the [changelog](CHANGELOG.md).

Documentation: [methodology](docs/methodology.md) (what each test measures and its limits) ·
[result schema](docs/schema.md) · [changelog](CHANGELOG.md) · [working notes](docs/notes/).

## Getting started

There is no installer or prebuilt download yet; build it from source.

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell 7.
2. Build with `pwsh -File scripts/Build.ps1`.
3. Double-click `artifacts/OnDemandPerf/OnDemandPerf.exe`.
4. Find the icon in the Windows notification area (possibly in the hidden-icons menu), right-click it and choose
   **Run Perf Test**. A compact panel opens above the taskbar.

The tray starts quietly: no inventory or benchmark runs at startup. **Run Perf Test** is the only run command and tests CPU,
RAM and disk in about 50 seconds. The panel shows live operations, elapsed time, an activity log and a compact summary, with
**Open Results** for the full report. The activity lights do not imply a percentage complete. Duplicate starts are disabled
while a test runs.

- Click the tray icon or choose **Show Status** to reopen the panel. Closing it (or Escape) hides it while work continues;
  a tray notification reports the outcome (including failures and timeouts) if the panel is hidden when the test finishes.
- **Cancel** requests a graceful stop; an unresponsive worker is terminated after a deadline. **Exit** cancels active work.
  If the tray itself ends unexpectedly (crash, Task Manager, sign-out), Windows ends its worker too (job object).
- The panel distinguishes completed, incomplete, cancelled and failed tests. Open Results is disabled during a run.
- `OnDemandPerf.exe /show` opens the panel immediately without starting a test; if a tray is already running, it raises that
  tray's panel. Launching without arguments stays quiet. The tray is single-instance per Windows session.
- The top of the panel identifies the machine: system make/model and BIOS, CPU (cores, threads, base clock), RAM (size, DDR
  generation, speed, module and slot count) and the drive under test (model, bus, NVMe version). It is read the first time the
  panel opens and is also stored in every report. An amber line appears when that identity suggests a cause (a single RAM module,
  RAM running below its rated speed, a spinning disk, USB storage).
- After a run, the cards show the figures closest to how fast a PC feels: CPU single-thread MB/s, RAM multithread read GB/s
  and disk 4K random-read IOPS. The log and report keep every other measurement (CPU multithread, RAM copy/write and latency,
  disk sequential and write speeds).
- **Findings** appear after a run, in the panel's alert line (hover for all of them), at the top of the results text and at
  the top of the report. They name common causes of slowness found before or during the test: a busy PC (with the busiest
  processes), the CPU running below its base clock under load (heat or power limits), little free or committed memory, a
  nearly full Windows drive, no restart for over a week, a pending restart, power-plan CPU limits, and the hardware hints above.
- **Copy Summary** puts a short, paste-ready block on the clipboard for a support ticket: machine, whether the run is valid,
  power state, the three headline numbers, a few secondary numbers and every finding. The same block opens `Latest.txt`, above
  the full details.
- **Power saving makes a run not valid.** If the power plan is Power saver, the Windows power mode is "Best power efficiency",
  or Energy saver is on, the test still runs but the result is marked NOT VALID (red, in the panel and report) and should not be
  compared; switch to Balanced or High performance / Best performance and run again. A run on **battery** is valid but clearly
  labelled, because laptops usually run slower unplugged.

The disk test writes a temporary file of up to 1 GiB under `%LOCALAPPDATA%\OnDemandPerf\TestData` and deletes it afterwards,
including when a run is cancelled or killed. It always leaves at least 512 MiB free: on a nearly full drive it uses a smaller
file (reported as a finding, since those disk numbers are not comparable) and skips the disk test only below 576 MiB free.

## Results and privacy

Default location: `%LOCALAPPDATA%\OnDemandPerf`.

- `Latest.json` and `Latest.txt`
- `Results/`: up to 50 historical JSON/text pairs
- `OnDemandPerf.log`: rotates at 2 MiB, retaining one backup

Reports include the computer name, Windows version, the hardware identity above and the names of the five busiest processes
in the two seconds before the test. They are written only to this folder:
nothing is uploaded and the app makes no network connections. Serial numbers are deliberately not collected. Field-level
detail is in [docs/schema.md](docs/schema.md).

## Command line

```powershell
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /run
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /inventory
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /cpu
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /memory
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /disk /diskpath C:\Temp\OnDemandPerf\TestData
& .\artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe /run /output C:\Temp\OnDemandPerf /compact
```

`/output` changes report placement; `/diskpath` separately selects the test-file directory; `/compact` writes single-line JSON
for ingestion. Switches are case-insensitive. Lines starting with `##` on standard output are a machine-readable protocol for
the tray (`##stage <CPU|RAM|Disk>`, `##report <path>`); everything else is human-readable.

Exit codes: `0` success; `1` component failure, incomplete stage, cancellation, or a run that is not valid because power
saving was on (`validity` in the JSON says which); `2` fatal error or invalid arguments;
`3` another worker is running (including one owned by another account). Ctrl+C cancels a console run, and a controlling
process can send a `cancel` line to redirected standard input (closing standard input does not cancel). The worker stops
work at 55 seconds to reserve cleanup/report time: each stage ends early enough to keep its completed measurements, and a run
that reaches the deadline is `Partial`, not `Cancelled`. The tray terminates the process at 60 seconds. A stalled
operating-system I/O request can delay cancellation or termination, so scripts that run the worker should also enforce their
own process deadline.

A machine-wide named mutex prevents overlapping worker executions; if another account holds the lock, the worker refuses to
run rather than overlap with it.

## Start at sign-in

After building, run `pwsh -File scripts/Set-Startup.ps1` to start this build at sign-in for **your user account**; add
`-Disable` to remove it. The registration points at the current artifacts folder, so moving the folder means registering the
new path. Neither building nor the tests register startup.

## Development

```powershell
pwsh -File scripts/Build.ps1                 # add -SelfSign for a locally signed build
pwsh -File scripts/Test-Unit.ps1             # fast logic tests, no hardware, no packages
pwsh -File scripts/Test-Smoke.ps1            # real CPU workload, cancellation, overlap, tray startup
pwsh -File scripts/Test-FullAnalysis.ps1     # real CPU/RAM/disk run, write caps, cancellation, cleanup
```

The two hardware scripts delete their output under `artifacts` on success (pass `-Keep` to retain it) and stop the tray
instance they launch; close any running copy of the app before running them.

- `src/OnDemandPerf`: Windows Forms tray and panel
- `src/OnDemandPerf.Worker`: CLI entry point, process lifetime, cancellation
- `src/OnDemandPerf.Core`: inventory, benchmarks, result models, persistence, summaries
- `tests/OnDemandPerf.Tests`: dependency-free logic tests

No third-party NuGet dependencies, and the repository NuGet configuration has no feeds. Project code disallows unsafe blocks.
The app version lives only in `Directory.Build.props`. Regenerate the icon with `pwsh -File scripts/Generate-Icon.ps1`.

### Local signing

`pwsh -File scripts/Sign-Local.ps1` self-signs the existing artifacts (or use `Build.ps1 -SelfSign`). It creates or reuses a
non-exportable current-user code-signing certificate; signatures are untimestamped and Windows may report them as untrusted.
**Self-signing does not unblock Smart App Control**, which needs a certificate from a trusted provider
([requirements](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)).
If Smart App Control or SmartScreen blocks your own unsigned build, that is why.

## Limitations

- Developed and tested on Windows 11 (x64, Intel hybrid CPU, NVMe SSD). Windows 10, ARM64, AMD, laptops, virtual machines,
  RAID/Storage Spaces and USB drives have had little or no testing; reports of how it behaves there are welcome.
- It needs the .NET 10 Desktop Runtime and ships as a folder (keep `artifacts/OnDemandPerf` together): there is no installer,
  self-contained build or signed release yet.
- Numbers are a quick screen, not a lab benchmark. Fixed inputs make runs repeatable, but background activity, power plan,
  battery, thermals, the .NET version and hardware configuration all affect results. Compare runs from the same machine, or
  machines of the same model, and read [docs/methodology.md](docs/methodology.md) before drawing conclusions.
- Not yet implemented: PCIe link speed, CPU temperatures (the clock is sampled, so throttling is inferred, not measured), and
  comparison against previous runs or a per-model baseline. Finding thresholds are provisional.
- Running the worker as another account or as SYSTEM has not been tested.

## License

[MIT](LICENSE): free to use, modify and redistribute, including commercially. Provided as is, without warranty.
