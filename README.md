# Overlingo

**EN** | [CS](README.cs.md)

**Live subtitle translator for games on Windows.** Overlingo reads the subtitles on your screen with OCR, translates them, and draws the translation as a transparent, click-through overlay on top of the game, covering the original text if you want. It also works with a capture card, so you can play on a console and still get translated subtitles.

> Unofficial community tool, MIT licensed. Not affiliated with Rockstar Games or any other publisher. Beta: see [Status](#status).

## Features

- **Sources:** a window (pick from a list), a program (by process name, reconnects after the game restarts), the whole screen, or a **capture card** (auto-detected).
- **Two modes:** *Conversation only* (a subtitle area you mark) and *Everything on screen* (menus, letters, signs).
- **Overlay:** shows the translation above the original, or on a black box **directly over the original** so it is hidden. Click-through, always on top. A graphical editor sets where subtitles are read and where the translation appears.
- **Black bars:** detects the real game image area, so the setup works at any aspect ratio.
- **35 languages** for subtitles and translation. Per-game **glossary** (names like "Vice City" stay untranslated, or force a fixed translation).
- **Translators** (you choose; there is no silent switching, errors are shown):
  - Google Translate without a key (unofficial endpoint, can stop working)
  - Google Cloud Translation, DeepL (API key)
  - Gemini (API key) and any OpenAI-compatible server (Ollama, LM Studio, OpenAI, OpenRouter) with a game description for better AI translation
- **Corrections:** press F5 to fix a bad translation once. Overlingo remembers it.
- **OCR:** Windows OCR; optional Tesseract as a second engine for hard-to-read text. UI noise such as FPS or stats panels is ignored.
- **OBS Browser Source** for streaming the translation without screen capture.
- First-run wizard, setup page with download and API-key links, subtitle history (F6), settings profiles, autostart options, diagnostics export (API keys are redacted).
- **Simple by default:** the home screen has a quick-setup checklist and a wizard; technical tabs (OCR, Hotkeys, Advanced, Diagnostics, Logs, Test) appear only after ticking *Show advanced settings*.
- **UI languages:** English (default) and Czech. More languages are one JSON file each, see [Languages/README.md](Overlingo.App/Languages/README.md).

## Install

1. Download `Overlingo-Setup-x.y.z.exe` (or the portable zip) from [Releases](../../releases).
2. Windows SmartScreen may warn about an unknown publisher because the installer is not code-signed: *More info* → *Run anyway*.
3. Install the **Windows OCR language pack** for the game's subtitle language: *Settings → Time & language → Language & region → Add a language* and keep *Optical character recognition* enabled. The *Initial setup* tab shows whether it is present.
4. Run Overlingo. The first-run wizard guides you through the source, languages and translator.

Requirements: Windows 10/11 x64. The release build is self-contained, no separate .NET install needed. For best results play in windowed or borderless mode.

## Quick start

1. Start the game and reach a scene with subtitles.
2. Pick the source, then press **F11** to mark the subtitle area (or use the graphical editor).
3. Press **F8** to start and stop translating.

| Key | Action |
|---|---|
| F5 | Correct the last translation |
| F6 | Subtitle history |
| F7 | Switch mode: conversation / everything on screen |
| F8 | Start / stop translation |
| F9 | Show / hide overlay |
| F10 | Pause / resume OCR |
| F11 | Select subtitle area |
| F12 | Open settings |

### Settings for the best translation

- Mark the subtitle area tightly; extra picture adds noise.
- Turn on *Cover original subtitles* so the translation sits on a black box over the original.
- Use **DeepL** or **Gemini** with your own key, and enable previous-subtitle context (DeepL).
- Add names to the glossary; add a one-line game description for AI translators.
- If subtitles are hard to read: raise *Upscale before OCR* to 2-3x, adjust the text brightness threshold, or install Tesseract.

## Build from source

Install the .NET 10 SDK, then run `build.cmd` (runs the tests and builds to `publish`) and `run.cmd`. `make-installer.cmd 1.0.0` creates a self-contained build, a portable zip and, if [Inno Setup 6](https://jrsoftware.org/isdl.php) is installed, the installer. A GitHub Actions workflow (`.github/workflows/release.yml`) builds releases for `v*` tags.

## Status

Beta. The core logic (pipeline, translators, glossary, corrections) is covered by automated tests (`Overlingo.Tests`). The Windows UI, capture-card detection, overlay placement, and the AI translators have had limited testing on real hardware, so expect rough edges. Please open an issue with the diagnostics zip (*Diagnostics → Export*).

## Privacy and legal

- The text of detected subtitles is sent to the translator you choose (Google, DeepL, Gemini, your AI server). Local models via Ollama or LM Studio send nothing to the internet.
- API keys are stored encrypted with Windows DPAPI and are never written to logs.
- The keyless Google endpoint is unofficial and may be restricted at any time. Check each provider's terms for your use.
- This is not legal advice. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [LICENSE](LICENSE).
