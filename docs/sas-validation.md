# Local SAS validation attempt — 2026-11-07

Result: **blocked during SAS startup; generated-program correctness has not been validated in SAS.**

The installed executable was found at `C:/Program Files/SASHome/SASFoundation/9.4/sas.exe`, file version `9413.00.25544.34773`. The presence of an executable does not establish that it can initialize successfully.

The final smoke harness explicitly selects the installed `nls/en/sasv9.cfg`. Its input program, intentionally empty autoexec, requested log/listing, WORK directory, SASUSER directory, and stdout/stderr captures are all under this `Codex/work/sas-validation` directory. It uses `-noterminal -nodms -nosplash -rsasuser` and launches hidden. No personal autoexec, user SAS session, participant dataset, or remote SAS service was opened.

| Attempt | Owned process ID | Startup result |
|---|---:|---|
| Explicit scratch SYSIN/autoexec/log/print/WORK/SASUSER; default installed-config discovery | 346576 | Exit 115; no requested SAS log/listing, WORK files, or completion marker |
| Same, with stdout/stderr redirected to task files | 358196 | Exit 115; both captured streams empty; no requested SAS log/listing or WORK files |
| Explicit installed English configuration plus an experimental console-log flag | 237936 | Exit 116; both streams empty; no requested log or WORK files. The experimental flag was removed from the retained harness. |
| Explicit installed English configuration with ordinary batch flags, no experimental console-log flag | 362296 | Exit 115; both streams empty; no requested SAS log/listing, WORK files, or completion marker |

The installed root `sasv9.cfg` points to the installed English configuration. Both configuration files were inspected. A narrowly process-ID-filtered Application Event Log query was denied as an unauthorized operation; no escalation or scan of user AppData was performed.

No diagnostic message established a cause. In particular, these results do **not** establish an expired license, a missing product, a code-generation defect, or successful SAS execution. No generated fixture programs or fixture PROC COMPARE checks were run after smoke initialization failed.

To retry after the local SAS startup issue is resolved, run `./run-smoke.ps1` from PowerShell. The harness returns SAS's exit status. Success requires `DH_SMOKE_COMPARE_SYSINFO=0` and `DH_SMOKE_COMPLETE` in `smoke.log`; an exit status alone is insufficient. Keep any future fixtures and outputs inside this directory, and use only the project's fictional test fixtures.
