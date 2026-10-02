# Open items

## Needs the real machine (nothing below has been run)

- [ ] `pwsh -File scripts/Build.ps1` and confirm it compiles clean (warnings are errors). The 0.6.0 changes were written and
      desk-checked, not compiled: no .NET SDK was available where they were made.
- [ ] `pwsh -File scripts/Test-Unit.ps1`
- [ ] `pwsh -File scripts/Test-Smoke.ps1` then `Test-FullAnalysis.ps1`
- [ ] Confirm the full run still finishes inside the 55 s work deadline. Estimate is about 50 s (the 0.5.0 run took 47.5 s,
      and the multithread RAM pass adds roughly 3.5 s); slack is thin on slower machines.
- [ ] Settle the CPU score discontinuity ([note](2026-10-01-cpu-score-halved.md)).
- [ ] Run the tray by hand: balloon on completion with the panel hidden, `/show` raising a running tray, cards, Open Results.
- [ ] Check the multithread RAM figure responds as expected (compare against single-thread; try a single-channel config if one
      is available).

## Product ideas, in rough priority order

1. Compare with previous runs on this machine (history is already kept) and an optional per-model baseline with Pass/Warn.
2. "Likely causes" section from data already captured (battery, power plan, baseline CPU, low RAM, high variance, low busy-CPU).
3. Hardware identity: make, model, serial, BIOS, RAM speed and DIMM count, storage model and bus type.
4. Frequency / thermal sampling (the `% Processor Performance` counter is the candidate; `CallNtPowerInformation` current MHz is
   unreliable on modern CPUs).
5. Move static method text (`method`, `cachePolicy`, `dataset`, notes) out of every JSON record behind the profile id.
6. More, shorter CPU passes, and a median-based headline.
7. Queue-depth > 1 random read; non-repeating disk test data for deduplicating storage.
8. `/quick` (~15 s) profile; Job Object so a crashed tray cannot orphan the worker.
