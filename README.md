# Retweak

A Windows service that keeps a configured set of registry values enforced, reacting to changes as they happen rather than polling.

It watches the keys it cares about with `RegNotifyChangeKeyValue`, repairs drift within a few hundred milliseconds, and runs a periodic sweep as a backstop. Per-user values are enforced across every loaded user hive, including users who log on after the service started.

## Build and install

```powershell
dotnet publish src\Retweak.Service -c Release -r win-x64
.\scripts\install.ps1 -SourcePath src\Retweak.Service\bin\Release\net10.0-windows\win-x64\publish
```

Run `dotnet test` for the unit tests. To debug interactively, run `Retweak.Service.exe` from a console as administrator: it detects it is not running under the SCM, skips the service lifetime, and logs to the console.

Uninstall with `.\scripts\uninstall.ps1 -RemoveFiles`. Add `-Report` first if you want to see what is currently being enforced. Note that uninstalling does not revert the values; it only stops enforcing them.

## Configuring the baseline

`appsettings.json` holds the entries. Each one is a hive scope, a key path relative to the hive root, a value name, a kind, and the desired value as a string:

```json
{
  "Scope": "PerUser",
  "Path": "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced",
  "Name": "HideFileExt",
  "Kind": "DWord",
  "Value": "0",
  "Watch": true
}
```

`Scope` is `Machine` (enforced once under HKLM) or `PerUser` (enforced under `HKU\{SID}` for every loaded user hive — do not include the SID in `Path`). `Kind` accepts `DWord`, `QWord`, `String`, `ExpandString`, `MultiString` and `Binary`. DWord and QWord values accept decimal or `0x` hex; `MultiString` uses the `Values` array instead of `Value`; `Binary` takes hex with optional separators. Setting `Watch` to false leaves an entry to the periodic audit only, which is what you want for keys that churn constantly and would otherwise generate a steady stream of notifications.

Bad entries fail validation at startup rather than being skipped silently, so a typo stops the service with a reason in the log instead of quietly leaving a value unenforced.

Overrides live at `HKLM\SOFTWARE\OptoCloud\Retweak\Config`. Scalar values there map onto option names directly (`AuditIntervalMinutes` as REG_DWORD, `EventSourceName` as REG_SZ, and a `LogLevel` REG_SZ mapping to the default log level). Because an array does not map onto flat registry values, a REG_SZ or REG_MULTI_SZ value named `Json` is parsed as a full JSON document and merged exactly like a second appsettings file — that is how you override `Entries`. Registry config is read once at startup; the service is cheap to restart.

## How enforcement works

A pass reads every configured value, compares it against the desired one, and writes only when they differ. That idempotence is what prevents watcher feedback loops, and it is more important than any timing trick: an enforced write produces a change notification, the resulting pass finds everything compliant, and the loop stops there without a second write and without a log line.

Only one pass runs at a time. A trigger arriving mid-pass sets a rerun flag rather than starting a second pass, so exactly one extra pass follows. That bounds the work under a notification storm while guaranteeing that whatever state the registry ends up in is observed by a pass that started after it.

`DebounceMilliseconds` (250 by default) does two things: it delays the pass slightly so a burst of related writes collapses into one, and it suppresses the "drift detected" warning for notifications arriving right after our own write. It deliberately does **not** suppress the pass itself — dropping notifications inside a time window would mean dropping a genuine change that happened to land there.

Drift is logged once per value when it goes out of compliance and once again when it comes back, tracked in a set of non-compliant value identities. A value that someone flips repeatedly produces a warning and a restored line per cycle, not one per pass.

Watchers use `subtree = false` and the `LAST_SET` filter, which is the cheap notification, and `REG_NOTIFY_THREAD_AGNOSTIC`, which is not optional. Without that flag the notification belongs to the thread that armed it and is cancelled when that thread exits — since re-arming happens on pooled threads, which come and go, omitting it gives you a watcher that works in testing and then stops firing in production. Waits go through `ThreadPool.RegisterWaitForSingleObject`, so fifty watched keys cost about a hundred handles and no threads.

## User hives

Loaded hives are the source of truth, not logon events. Session notifications are treated as a hint that reconciling now is worthwhile, and the actual work is a diff of `HKEY_USERS` against the watcher map. That handles the cases logon events do not: hives load asynchronously after the logon notification arrives, they stay loaded for a while after logoff, and `RunAs` or scheduled-task logons load hives without producing a console session change at all.

On logon the service resolves the session to a SID with `WTSQueryUserToken` and retries for up to `LogonHiveWaitSeconds` until that hive is actually openable. If the SID cannot be resolved, it falls back to the diff. Either way the reconcile loop catches it within a minute.

Note that a brand new user's `Advanced` key may not exist when the hive first appears. The first pass creates it, and the following reconcile installs the watcher; this is why the startup path reconciles twice.

## Operations

The service logs to a rolling file at `%ProgramData%\Retweak\logs\retweak.log` and, when the Event Log source exists, to the Application log under the source name `Retweak`. The file log is always present because it is the only thing that can report a failure caused by the Event Log source being missing. Adding the Event Log provider with a nonexistent source throws on first write, so the source is probed at startup and the provider is only added if it checks out.

SCM recovery restarts the service three times at 60-second intervals with the counter resetting after a day, and `failureflag 1` makes a non-zero exit count as a failure rather than only a crash. A background loop that throws is caught, logged, and restarted with backoff inside the process, so SCM restart is the backstop rather than the first line of defence.

Expected steady state is near-zero CPU and no log output above Debug. The hourly audit logs one summary line. If you see repeated "Enforced" lines for the same value, something else on the machine is fighting you — Group Policy refresh and Windows Update are the usual candidates, and enforcement will win the exchange but noisily.

## Two things in the spec I built differently

**`CanStop = false` is off by default.** It is available as `ServiceControl:RefuseStop`, but it is not on unless you ask. Refusing STOP does not stop anything determined — anyone who can stop a service can also terminate the process, disable the service and reboot, or edit the config — so what it actually buys is obstruction of the administrator, who is you. It makes upgrades awkward (the installer has to kill the process instead of stopping it), makes debugging worse, and makes a misconfigured baseline harder to back out of. `OnShutdown` still disposes watchers promptly either way. If you turn it on, keep `uninstall.ps1` somewhere you can find it.

**Safe Mode registration is opt-in and I would leave it off.** Safe Mode is where you go to fix a machine that is broken. A service that enforces registry state there is one more variable in the way of that, and neither of the example values needs to be enforced on a machine you are already repairing. The installer supports `-SafeMode` and warns when you use it.

The offline-profile mode (`RegLoadKey` over `ProfileList`) is stubbed as a config flag and not implemented. Loading another user's hive from a service is genuinely risky: if the profile is in use, or if you fail to unload cleanly, you corrupt or lock a user profile, and the failure mode is a user who cannot log in. Loaded-hive enforcement plus the logon path covers every user who actually signs in, which is the case that matters. If you do want it, do it with `RegLoadAppKey` where possible and unload in a `finally` with retries.

## Project layout

```
src/Retweak.Service/
  Program.cs                          host, config sources, logging, DI
  Configuration/RetweakOptions.cs     options, baseline entries, value parsing
  Configuration/RegistryConfigurationSource.cs
  Core/BaselineManager.cs             apply/audit, serialisation, drift bookkeeping
  Core/RegistryHelpers.cs             EqualsNorm, view selection
  Core/UserHives.cs                   HKU enumeration
  Infra/NativeMethods.cs              P/Invoke
  Infra/RegistryWatcher.cs            notification watcher
  Infra/WatcherManager.cs             watcher lifecycle and hive reconciliation
  Infra/SessionEvents.cs              session change plumbing, custom lifetime
  Infra/ChangeTrigger.cs              coalescing signal
  Infra/FileLogger.cs                 fallback rolling file log
  Workers/BaselineWorker.cs           the three loops
tests/Retweak.Service.Tests/          EqualsNorm and configuration parsing
scripts/install.ps1, uninstall.ps1
```

## Verifying it works

With the service running, change an enforced value by hand and watch it come back:

```powershell
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' HideFileExt 1
Start-Sleep -Milliseconds 800
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' HideFileExt
Get-Content "$env:ProgramData\Retweak\logs\retweak.log" -Tail 5
```

You should see the value back at 0, one `Drift` warning and one `Enforced` line. Kill the process with `Stop-Process -Name Retweak.Service -Force` and confirm SCM brings it back within a minute. For handle counts under load, `Get-Process Retweak.Service | Select-Object Handles` should sit at roughly two per watched key plus the process baseline.
