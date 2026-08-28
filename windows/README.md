# FreeFlow for Windows

A Windows port of [FreeFlow](https://github.com/zachlatta/freeflow), the free and open
source dictation app. Hold a key, talk, and your words are pasted wherever you were
typing.

The macOS sources remain in this repository under `Sources/` and stay the reference
implementation. This directory is a separate C# / .NET 8 application that reuses the
Mac version's design, prompts, and behavior.

## Why this is a rewrite and not a port

The macOS app is roughly 18,000 lines of Swift built directly with `swiftc`. About 78%
of it is bound to Apple-only frameworks: AppKit, SwiftUI, AVFoundation, CoreAudio, the
Accessibility API, ScreenCaptureKit, and Carbon. None of that exists on Windows, and
SwiftUI in particular has no Windows implementation, so the user interface had to be
rebuilt regardless of language.

What did carry over is the part that is hardest to get right, and it was ported
faithfully rather than reimagined:

| Component | Ported from |
|---|---|
| Hold / tap / latch shortcut semantics | `Sources/ShortcutCore/` |
| Transcript cleanup and Edit Mode prompts | `Sources/PostProcessingService.swift` |
| Whisper hallucination filter | `Sources/TranscriptTextCore.swift` |
| Instruction-execution guard | `Sources/TranscriptTextCore.swift` |
| Per-model request tuning and rate-limit cooldowns | `Sources/ModelConfiguration.swift`, `Sources/LLMCooldownManager.swift` |
| Context synthesis | `Sources/AppContextService.swift` |
| Live audio level normalization | `Sources/LiveAudioLevelNormalizer.swift` |
| Semantic version comparison | `Sources/UpdateManager.swift` |

Every one of these has a test suite ported one-for-one from the corresponding Swift
tests, so behavior is pinned rather than assumed.

## Differences you will actually notice

### The Fn key is gone

The Mac app's headline gesture is holding `Fn`. **That is not possible on Windows.** On
virtually all Windows keyboards, `Fn` is handled inside the keyboard firmware and never
produces a scan code the operating system can observe, so no application can detect it.

Right Ctrl is the default in its place. The available presets are Right Ctrl, Right Alt,
Caps Lock, and F5, and the toggle shortcut defaults to Right Ctrl + Shift so it still
extends the hold shortcut the way the Mac version does.

The five-modifier model is otherwise preserved, remapped to Ctrl, Alt, Shift, and Win.
Left and right sides are still distinguished, so "Right Alt" works as a standalone
trigger exactly as "Right Option" did.

### Selected text is readable in fewer applications

Edit Mode and context awareness read your selection through UI Automation, the closest
Windows equivalent to the macOS Accessibility API. It works in most native controls,
Office, and Chromium and Firefox with accessibility enabled. It does **not** work in
applications that draw their own text without exposing a UI Automation tree, which
includes some terminals, Electron apps with accessibility disabled, and most games.
This is a platform limitation, not a gap in the port. Missing context is handled as a
normal case and never blocks a dictation.

### Updates are not automatic

The Mac app downloads and installs a signed DMG itself. This build checks GitHub
releases and tells you, then opens the release page.

Silently replacing a running executable on Windows means writing to a protected
location, which requires elevation, and an unsigned self-updater that does so is
exactly the shape of an attack. Handing off to the browser keeps that decision visible.
A signed MSIX or Squirrel package could automate it properly later.

## Running it on your own machine

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
cd windows
dotnet build FreeFlow.sln --configuration Release
.\install-shortcuts.ps1
```

Then search the Start Menu for FreeFlow. It lives in the notification area; look for
the microphone icon. Add `-AtLogin` to the install script to start it at sign-in, and
`-Remove` to take the shortcuts away again.

The shortcut launches `run-freeflow.vbs`, which starts the app through the .NET host
rather than running `FreeFlow.exe` directly. That is deliberate, and the next section
explains why.

### Smart App Control and unsigned builds

If Smart App Control is enabled, Windows blocks unsigned executables from loading.
Your own local build is unsigned, so `FreeFlow.exe` will not start: it fails with
`0x800711C7`, or crashes with exit code `-532462766` and a bare `KERNELBASE.dll` fault
and no managed stack trace. `dotnet test` fails the same way.

Check whether it applies to you:

```powershell
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy").VerifiedAndReputablePolicyState
# 0 = off, 1 = enforced, 2 = evaluation
```

The block is confirmable in the `Microsoft-Windows-CodeIntegrity/Operational` event log,
event ID 3077, which names the exact assembly that was refused.

**The fix is to run the app through `dotnet.exe`**, which is signed by Microsoft, so the
managed assembly is allowed to load into it. That is all `run-freeflow.vbs` does, and it
costs nothing:

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" "src\FreeFlow.App\bin\Release\net8.0-windows10.0.19041.0\FreeFlow.dll"
```

This is why the build here is framework-dependent rather than a self-contained
single-file executable. A single-file publish produces exactly the unsigned executable
that gets blocked.

Note that Smart App Control state is per-machine, so a build that runs on one of your
computers may be refused on another.

Two things this is **not**:

- Not a way to distribute the app. Anyone else with Smart App Control on hits the same
  wall, and telling users to run a script is not a shipping story.
- Not a reason to disable Smart App Control. Turning it off is one-way; re-enabling it
  requires reinstalling Windows.

### Distributing it

Distribution needs an Authenticode signature from a CA in the Microsoft Trusted Root
Program. Since 2023, code-signing private keys must live on certified hardware, so a
downloadable `.pfx` is no longer an option; expect either a cloud HSM or a USB token.
[SignPath Foundation](https://signpath.org/) issues certificates free to open source
projects and integrates with CI.

Be aware that signing alone may not be enough at first. Smart App Control checks the
signature and the app's reputation as separate gates, so a newly signed, low-distribution
app can still be blocked until the Intelligent Security Graph has history for it.

## Building and testing

```powershell
cd windows
dotnet build FreeFlow.sln --configuration Release
dotnet test tests/FreeFlow.Core.Tests/FreeFlow.Core.Tests.csproj
```

Warnings are treated as errors, so the build also enforces that the port stays
warning-clean.

If Smart App Control blocks the test run locally, CI is the way to get a real result:
`.github/workflows/windows-check.yml` builds and tests on a `windows-latest` runner,
which has no Smart App Control.

## Project layout

```
windows/
  src/FreeFlow.Core/      Platform-independent logic and its tests' subject
    Shortcuts/            Binding model, matcher, session controller
    Transcription/        Upload and realtime transcription services
    PostProcessing/       Cleanup, Edit Mode, verbatim translation, prompts
    Context/              Context synthesis
    Models/               Per-model config, rate-limit cooldowns
    History/              Local pipeline history
    Updates/              Semantic versions, release checking
  src/FreeFlow.App/       The Windows application
    Platform/Input/       Low-level keyboard hook, synthetic input
    Platform/Audio/       WASAPI capture, feedback tones
    Platform/Text/        Clipboard save, paste, restore
    Platform/Context/     UI Automation reads, window capture
    Platform/Host/        Settings, DPAPI credentials, startup registration
    UI/                   Theme, tray icon, overlay, settings, setup
  tests/FreeFlow.Core.Tests/
```

`FreeFlow.Core` targets plain `net8.0` and has no Windows dependency, which is what
keeps the shortcut semantics and prompt behavior testable without a keyboard or a
microphone.

## Privacy

Unchanged from upstream: there is no FreeFlow server. The only data leaving your machine
goes to the transcription and LLM provider you configure.

Windows specifics:

- Your API key is encrypted with DPAPI, scoped to your Windows user account, in
  `%APPDATA%\FreeFlow\credentials.dat`. It is never written to the settings file.
- Recorded audio goes to a temporary file and is deleted as soon as it is transcribed.
- Dictated text is kept out of Windows clipboard history and cloud clipboard sync by
  default. You can opt in under Advanced.
- Pipeline history is off by default. When enabled it stores transcripts, prompts, and
  any context screenshots locally, and Settings can clear it.
- Context screenshots are off by default. Turning them on sends a picture of your
  focused window to your configured provider.

## License

MIT, same as upstream.
