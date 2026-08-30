# Setting up FreeFlow on the laptop

Syncthing already carried the code over. This is everything else, in order.
Should take about five minutes.

Nothing here needs administrator rights.

---

## 1. Install the .NET 8 runtime

Only needed once per machine.

```powershell
winget install Microsoft.DotNet.DesktopRuntime.8
```

If you want to build or change code on the laptop, install the SDK instead
(it includes the runtime):

```powershell
winget install Microsoft.DotNet.SDK.8
```

Close and reopen PowerShell afterwards so `dotnet` is on the PATH.

---

## 2. Check Smart App Control

This decides how you launch the app, so check it before anything else.

```powershell
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy").VerifiedAndReputablePolicyState
```

| Result | What it means |
|---|---|
| `0` | Off. You can run `FreeFlow.exe` directly. |
| `1` | Enforced. Unsigned builds are blocked; use the launcher below. |
| `2` | Evaluation mode. Treat it as enforced. |

The office PC returns `1`. If the laptop also returns `1`, everything below
still works, because the launcher runs the app through the Microsoft-signed
.NET host instead of the unsigned executable.

**Do not turn Smart App Control off to work around this.** Disabling it is
one-way: re-enabling requires reinstalling Windows.

---

## 3. Build

```powershell
cd C:\develop\freeflow-win\windows
dotnet build FreeFlow.sln --configuration Release
```

Build fresh even if Syncthing copied a `bin` folder from the office PC. A
build from another machine is not guaranteed to match this one's runtime.

Expect `0 Warning(s), 0 Error(s)`. Warnings are errors in this project, so
anything else means something is genuinely wrong.

---

## 4. Install the shortcuts

```powershell
.\install-shortcuts.ps1
```

Puts FreeFlow in the Start Menu. To also start it when you sign in:

```powershell
.\install-shortcuts.ps1 -AtLogin
```

To remove them again:

```powershell
.\install-shortcuts.ps1 -Remove
```

> Use this script rather than the **Start FreeFlow when I sign in** checkbox in
> Settings. The in-app setting registers a path Smart App Control blocks; the
> script routes through the signed .NET host.

---

## 5. Start it and add your API key

Hit Start, type **FreeFlow**, press Enter.

No window opens. It lives in the notification area — look for the microphone
icon, and expand the hidden-icons arrow if you do not see it.

On first launch the setup window appears asking for a Groq API key.

**Your key from the office PC will not be here.** It is encrypted with DPAPI
scoped to that machine's user profile, so `credentials.dat` is deliberately
unreadable on any other computer even though Syncthing copies the file. That
is the correct behavior, not a bug.

Get your key from <https://console.groq.com/keys> and paste it in. The window
validates it live and enables **Start dictating** once Groq accepts it.

---

## 6. Use it

- **Hold Right Ctrl**, talk, let go. Text appears where your cursor is.
- **Right Ctrl + Shift** taps to latch recording on hands-free. Tap again to stop.
- **Escape** cancels a dictation in progress.

A small capsule appears near the bottom of the screen while recording, showing
live microphone level. If those bars stay flat while you are talking, the
microphone is not being heard: pick a different device under
**Settings → Audio**.

The Fn key cannot be used on Windows. It is handled in keyboard firmware and
never reaches the operating system, so no application can bind it. Right Ctrl
is the default in its place, and you can change it under
**Settings → Shortcuts**.

---

## If you use a VPN

**Groq blocks VPN exit IPs.** With Surfshark connected you will get:

```
{"error":{"message":"Access denied. Please check your network settings."}}
```

That is a network block, not a bad key. Two ways around it:

1. **Surfshark Bypasser**, adding this app. Note it must be
   `C:\Program Files\dotnet\dotnet.exe`, **not** `FreeFlow.exe` — the app runs
   through the .NET host, so a rule targeting `FreeFlow.exe` matches nothing.
   This is what works on the office PC.
2. **Disconnect the VPN** while dictating.

If you change the VPN state while FreeFlow is running, **restart FreeFlow**.
It holds pooled network connections, so it will keep failing on stale ones
until it is restarted.

---

## Troubleshooting

**Records but never pastes, and restarting does not help?** Run:

```powershell
cd C:\developreeflow-win\windows
.ix-stuck.ps1
```

That state surviving a restart means something was persisted, and the usual
cause is a rate-limit cooldown written to `settings.json`. FreeFlow keeps a
daily-looking cooldown across restarts on purpose, and while it is active the
cleanup stage will not even attempt a request. Recording still works, because
transcription is a different endpoint that does not consult it. A burst of
rapid dictations, for instance from fumbling the shortcut key several times, is
enough to trigger it. The script reports what it finds and clears it.

**Check the diagnostic log first.** It records what each stage of the pipeline
did, and the missing line names the broken stage:

```powershell
Get-Content "$env:APPDATA\FreeFlow\diagnostic.log"
```

A healthy dictation looks like this:

```
record.start      mode=Hold
record.stop       bytes=110446
transcribe.begin
transcribe.done   chars=30
context.ready     chars=210
cleanup.done      chars=30
paste.begin       chars=30
paste.done
```

The log holds stage names, timings, byte counts, and error types only. It
never contains your transcripts, prompts, context, or API key, so it is safe
to share.

**Errors appear as tray balloon notifications**, which are easy to miss.
Hovering the microphone icon shows the current status message, which is more
reliable.

**The app will not start.** Check for a Code Integrity block:

```powershell
Get-WinEvent -LogName "Microsoft-Windows-CodeIntegrity/Operational" -MaxEvents 5 |
  Where-Object { $_.Id -eq 3077 } | Select-Object TimeCreated, Message
```

Event 3077 names the exact blocked file. If it names a FreeFlow assembly,
Smart App Control is the cause: use the Start Menu shortcut rather than
launching `FreeFlow.exe` directly.

**Nothing happens when you hold Right Ctrl.** Check FreeFlow is actually
running:

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object { $_.CommandLine -like "*FreeFlow*" } |
  Select-Object ProcessId, CommandLine
```

**Force-quit a stuck instance:**

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object { $_.CommandLine -like "*FreeFlow*" } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

---

## What lives where

| | |
|---|---|
| Settings | `%APPDATA%\FreeFlow\settings.json` |
| API key (encrypted, per-machine) | `%APPDATA%\FreeFlow\credentials.dat` |
| Diagnostic log | `%APPDATA%\FreeFlow\diagnostic.log` |
| Pipeline history (off by default) | `%APPDATA%\FreeFlow\pipeline-history.json` |
| Recordings (deleted after transcription) | `%TEMP%\FreeFlow\` |

Settings do **not** sync between machines. `%APPDATA%` is outside the
Syncthing folder, so shortcuts, models, and vocabulary are configured
per-machine.

See `README.md` in this folder for how the port works and what differs from
the macOS original.
