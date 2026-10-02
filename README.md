# OnDemand Perf

Windows tray utility with on-demand CPU, RAM and storage tests. Version 0.7.0 (prototype).

Documentation: [methodology](docs/methodology.md) (what each test measures and its limits) ·
[result schema](docs/schema.md) · [changelog](CHANGELOG.md) · [working notes](docs/notes/).

## Try it

1. Build with `pwsh -File scripts/Build.ps1` (requires the .NET 10 SDK).
2. Double-click `artifacts/OnDemandPerf/OnDemandPerf.exe`.
3. Find the icon in the Windows system tray (possibly in the hidden-icons menu), right-click it and choose **Run Perf Test**.
   A compact panel opens above the taskbar on that monitor.

The tray starts quietly: no inventory or benchmark runs at startup. **Run Perf Test** is the only run command and tests CPU,
RAM and disk in about 50 seconds. The panel shows live operations, elapsed time, an activity log and a compact summary, with
**Open Results** for the full report. The activity lights do not imply a percentage complete. Duplicate starts are disabled
while a test runs.

- Click the tray icon or choose **Show Status** to reopen the panel. Closing it (or Escape) hides it while work continues;
  a tray notification reports the outcome if the panel is hidden when the test finishes.
- **Cancel** requests a graceful stop; an unresponsive worker is terminated after a deadline. **Exit** cancels active work.
- The panel distinguishes completed, incomplete, cancelled and failed tests. Open Results is disabled during a run.
- `OnDemandPerf.exe /show` opens the panel immediately without starting a test; if a tray is already running, it raises that
  tray's panel. Launching without arguments stays quiet. The tray is single-instance per Windows session.
- The top of the panel identifies the machine: system make/model and BIOS, CPU (cores, threads, base clock), RAM (size, DDR
  generation, speed, module and slot count) and the drive under test (model, bus, NVMe version). It is read the first time the
  panel opens and is also stored in every report. An amber line appears when that identity suggests a cause (a single RAM module,
  RAM running below its rated speed, a spinning disk, USB storage).
- After a run, the cards show CPU multithread MB/s, RAM copy GB/s and disk sequential-read MB/s (first full pass of the test
  file); the log and report keep every other measurement.

## Results

Default location: `%LOCALAPPDATA%\OnDemandPerf`.

- `Latest.json` and `Latest.txt`
- `Results/`: up to 50 historical JSON/text pairs
- `OnDemandPerf.log`: rotates at 2 MiB, retaining one backup

Results include the computer name and CPU information and stay local. Review collection/output permissions before production
deployment. Field-level detail is in [docs/schema.md](docs/schema.md).

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

Exit codes: `0` success; `1` component failure, incomplete stage or cancellation; `2` fatal error or invalid arguments;
`3` another worker is running (including one owned by another account). Ctrl+C cancels a console run, and a controlling
process can send `cancel` plus a newline to redirected standard input. The worker stops new work at 55 seconds to reserve
cleanup/report time; the tray terminates the process at 60 seconds. A stalled operating-system I/O request can delay
cancellation or termination, so external management should also enforce a process deadline.

A machine-wide named mutex prevents overlapping worker executions; inability to access another identity's lock fails closed.
Cross-account/SYSTEM execution still requires deployment testing.

## Start at sign-in

After building, run `pwsh -File scripts/Set-Startup.ps1` to register this build for **your user account**; add `-Disable` to
remove it. The registration references the current artifacts folder, so moving it requires registering the new path. Neither
building nor the tests register startup. Fleet-wide registration belongs in a future managed installer.

## Development

```powershell
pwsh -File scripts/Build.ps1                 # add -SelfSign for a locally signed build
pwsh -File scripts/Test-Unit.ps1             # fast logic tests, no hardware, no packages
pwsh -File scripts/Test-Smoke.ps1            # real CPU workload, cancellation, overlap, tray startup
pwsh -File scripts/Test-FullAnalysis.ps1     # real CPU/RAM/disk run, write caps, cancellation, cleanup
```

The two hardware scripts delete their output under `artifacts` on success (pass `-Keep` to retain it) and stop the tray
instance they launch; close any running prototype before running them.

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

## Prototype limits

This is a framework-dependent build requiring the .NET 10 Desktop Runtime; keep the whole `artifacts/OnDemandPerf` folder
together. It is not a signed or self-contained production package.

Not yet implemented: serial numbers (deliberately omitted), PCIe link speed, CPU frequency/thermal sampling, comparison
against previous runs or a baseline, and a managed installer. Fixed inputs improve repeatability, but background activity, power
policy, thermals, runtime updates and hardware configuration still affect results; fleet comparisons need broader validation.
Interactive window and tray-menu behavior needs human acceptance testing.

Before broad deployment: complete inventory capture, validate multiple Windows versions and processor configurations, choose
runtime servicing and installation policy, sign releases, validate user/SYSTEM permissions, and pilot a staged rollout with
rollback.
