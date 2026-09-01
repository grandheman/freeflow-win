Hold a key, talk, and your words appear wherever you were typing. Cleaned up,
punctuated, filler removed. Free, with no subscription and no server in the
middle.

A Windows version of [FreeFlow](https://github.com/zachlatta/freeflow), which
is macOS only.

## Getting started

**Full instructions: [INSTALL.md](../blob/main/windows/INSTALL.md)** — about
three minutes, most of it spent getting a free API key.

The short version:

1. Get a free key at <https://console.groq.com/keys> (it starts with `gsk_`,
   and Groq only shows it once)
2. Download **`FreeFlow-win-x64.exe`** below and run it
3. Paste the key into the setup window
4. Hold **Right Ctrl**, talk, let go

## Which download

**`FreeFlow-win-x64.exe`** — start here. Self-contained, nothing to install.

**`FreeFlow-win-x64-dotnet-required.zip`** — only if the `.exe` refuses to
start. Some Windows 11 machines run Smart App Control, which blocks unsigned
programs outright. This version runs through Microsoft's own signed .NET host,
which is permitted, and needs the .NET 8 Desktop Runtime installed once.

## Expect a SmartScreen warning

The first launch will show *"Windows protected your PC"*. That appears for any
program not signed with a paid certificate, which this is not. Click
**More info**, then **Run anyway**.

The full source is in this repository if you would rather build it yourself.

## Notes

- The microphone is only open while you hold the key. Recorded audio is
  deleted as soon as it has been transcribed.
- Your API key is encrypted to your Windows account and stored locally.
- Your clipboard is restored after each paste, and dictated text is kept out
  of Windows clipboard history by default.
- **VPNs break it.** Groq blocks VPN exit addresses. Disconnect while
  dictating, or add an exception, and restart FreeFlow afterwards.
- Works with OpenAI or a local model (Ollama, LM Studio) instead of Groq, via
  **Settings → Provider**.

## Differences from the macOS original

- **The `Fn` key cannot be used.** Windows keyboards handle it in firmware, so
  no program can see it. Right Ctrl is the default; Right Alt, Caps Lock, and
  F5 are also available.
- **Edit Mode reads selected text in fewer applications.** Windows exposes
  this through UI Automation, which most native apps, Office, and browsers
  support, but some terminals and Electron apps do not.
- **Updates are not automatic.** It tells you when a new version exists and
  opens the release page.

MIT licensed, same as the original.
