# FreeFlow for Windows

Hold a key, talk, and your words appear wherever you were typing. Cleaned up,
punctuated, filler removed.

Free. No subscription, no account with us, no server in the middle. The only
thing that leaves your computer is the audio you dictate, sent to the
transcription provider you choose.

Setup takes about three minutes, most of which is getting a free API key.

---

## 1. Get a free Groq API key

FreeFlow needs a speech-to-text service. Groq's free tier is fast enough for
live dictation and costs nothing.

1. Go to **<https://console.groq.com/keys>**
2. Sign in (Google, GitHub, or email)
3. Click **Create API Key**, give it any name, click **Submit**
4. **Copy the key immediately.** Groq shows it once and never again. It starts
   with `gsk_`.

Paste it somewhere safe for a moment. You will need it in step 3.

---

## 2. Download and run

Download **`FreeFlow-win-x64.exe`** from the
[latest release](../../releases/latest).

Nothing to install. Put it wherever you keep programs, for example
`C:\Program Files\FreeFlow\` or just your Desktop, and double-click it.

### "Windows protected your PC"

You will almost certainly see this the first time:

> Microsoft Defender SmartScreen prevented an unrecognised app from starting.

That is expected. It appears for any program not signed with a paid
certificate, which this is not. Click **More info**, then **Run anyway**.

If you would rather not take that on faith, the entire source is in this
repository and you can build it yourself in two commands. See
[windows/README.md](README.md).

### If it will not start at all

Some Windows 11 machines have **Smart App Control** enabled, which blocks
unsigned programs outright rather than warning about them. Check with this in
PowerShell:

```powershell
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy").VerifiedAndReputablePolicyState
```

`0` means off and you are fine. `1` or `2` means it will block the `.exe`.

If it blocks it, download **`FreeFlow-win-x64-dotnet-required.zip`** from the
same release instead. That version runs through Microsoft's own signed .NET
host, which Smart App Control permits. It needs the .NET 8 Desktop Runtime
once:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.8
```

Then unzip it anywhere and run `install-shortcuts.ps1`, which puts FreeFlow in
your Start Menu.

**Do not turn Smart App Control off to work around this.** Disabling it is
one-way: turning it back on requires reinstalling Windows.

---

## 3. Paste your key

On first launch a setup window appears. Paste the `gsk_...` key from step 1.

It checks the key against Groq as you type. Once accepted, click
**Start dictating**.

The window closes and FreeFlow moves to the notification area, next to the
clock. Look for the microphone icon. You may need to click the little arrow to
show hidden icons.

Your key is encrypted to your Windows account and stored locally. It is never
written to any settings file that might get shared or backed up.

---

## 4. Dictate

Click into any text box: an email, a chat window, a document, a browser.

- **Hold Right Ctrl**, talk, let go. Your words appear at the cursor.
- **Right Ctrl + Shift** taps recording on hands-free. Tap again to stop.
- **Escape** cancels while recording.

A small capsule appears near the bottom of the screen while it listens,
showing your microphone level. If those bars stay flat while you are talking,
your microphone is not being heard: pick a different one under
**Settings → Audio** (right-click the tray icon).

### Changing the key you hold

Right Ctrl is the default because it exists on most desktop keyboards and is
rarely used for anything else. Laptops often lack it. Change it under
**Settings → Shortcuts**, where the choices are Right Ctrl, Right Alt, Caps
Lock, and F5.

The Mac version of FreeFlow uses the `Fn` key. That is not possible on
Windows: `Fn` is handled inside the keyboard's own firmware and never reaches
Windows at all, so no program can detect it.

---

## Things worth knowing

**It only listens while you hold the key.** The microphone is closed the rest
of the time. Recorded audio goes to a temporary file, is sent for
transcription, and is deleted immediately afterwards.

**Your clipboard is safe.** Pasting uses the clipboard, but whatever you had
copied is restored a moment later. Dictated text is also kept out of Windows
clipboard history and cloud clipboard sync by default.

**VPNs break it.** Groq blocks VPN exit addresses, so with a VPN connected you
will get an error mentioning network settings. That is not your key. Either
disconnect while dictating, or add an exception in your VPN app. If you use
the `.zip` version, the exception must name
`C:\Program Files\dotnet\dotnet.exe` rather than `FreeFlow.exe`, because the
app runs through the .NET host. **Restart FreeFlow after changing any VPN
setting**, or it keeps using stale connections.

**It works with other providers.** Groq is only the default. Under
**Settings → Provider** you can point it at OpenAI, or at a local model
through Ollama or LM Studio, in which case nothing leaves your machine at all.

---

## If something goes wrong

Errors appear as a notification balloon, which is easy to miss. Hovering the
tray icon shows the current status.

For anything stubborn, there is a diagnostic script in the `.zip` download, or
in this repository:

```powershell
.\fix-stuck.ps1
```

It reports whether the app is running, what the last dictation actually did,
whether your network can reach the provider, and whether a rate limit is in
effect. It fixes the most common cause automatically.

The app also keeps a local log at `%APPDATA%\FreeFlow\diagnostic.log`. It
records only stage names, timings, and error types, never your transcripts or
your key, so it is safe to share when asking for help.

---

## Credit

FreeFlow was created by [Zach Latta](https://github.com/zachlatta/freeflow)
for macOS. This is an independent Windows version, written in C# because
almost none of the original could be carried across. MIT licensed, same as the
original.
