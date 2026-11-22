# Overlingo

**CS** | [EN](README.md)

Živý překladač titulků ze hry (OCR → překlad → průhledný overlay). Určeno pro hráče, kteří chtějí rozumět titulkům ve hrách
(původně vzniklo pro GTA na PS5 přes záznamovou kartu). Neoficiální komunitní nástroj, MIT licence.

## Jak to funguje
Zachytí obraz (okno, program, celou obrazovku nebo záznamovou kartu) → přečte oblast s titulky (Windows OCR, volitelně Tesseract) → přeloží (Google bez klíče / Google Cloud / DeepL) → zobrazí překlad v průhledném okně nad hrou, které nelze kliknout.

## Požadavky
Windows 10/11 x64, .NET 10 Runtime (pro sestavení SDK), jazykový balíček Windows OCR pro jazyk titulků (angličtina: *English (United States)* s funkcí OCR).
Žlutý okraj záchytu okna zmizí jen na Windows 11.

## Sestavení a spuštění
1. Nainstalujte .NET 10 SDK.
2. `build.cmd` (spustí testy a sestaví do složky `publish`), potom `run.cmd`.

## Hlavní funkce
- Zdroj obrazu: **okno** (ze seznamu), **program** (podle názvu procesu, po restartu hry se připojí znovu), **celá obrazovka**, **záznamová karta** (Elgato).
- Jazyk titulků a cílový jazyk (35 jazyků), slovník jmen a frází (např. „Vice City“ se nepřeloží).
- Režim *Jen konverzace* (oblast titulků) a *Vše na obrazovce*.
- Overlay nad původními titulky, nebo s černým pozadím, které originál zakryje.
- **OBS Browser Source**: *Pokročilé → OBS*, v OBS přidejte zdroj Prohlížeč s adresou `http://127.0.0.1:8787/`. Pro stream bez překrytí vypněte „Skrýt overlay před záchytem“.
- **Překladače:** Google (bez klíče), Google Cloud, DeepL, **Gemini** a **AI přes Ollama / LM Studio / OpenAI** (s popisem hry pro lepší překlad).
- **Grafický editor umístění** (čtení titulků + umístění překladu, presety) a **průvodce prvním spuštěním**.
- Spouštění s Windows, automatický start překladu při startu aplikace nebo při spuštění vybraného programu.
- Historie titulků (F6), profily nastavení, export diagnostiky (zip bez API klíčů).
- Zkratky: F6 historie, F7 režim, F8 start/stop, F9 overlay, F10 pauza, F11 oblast, F12 nastavení.

## Upozornění
Google Translate bez klíče používá **neoficiální** endpoint a může kdykoli přestat fungovat. Pro stabilní provoz použijte vlastní klíč Google Cloud nebo DeepL. API klíče se ukládají šifrovaně (DPAPI). Viz `THIRD-PARTY-NOTICES.md`.
Projekt není spojen s Rockstar Games ani s žádným jiným vydavatelem.

---

## English
Live game-subtitle translator: capture → OCR → translate → click-through transparent overlay. Unofficial community tool, MIT licensed.

**Sources:** window (pick from list), program (by process name, re-attaches after restart), whole screen, capture card (Elgato).
**Languages:** 35 target languages, per-game glossary (names kept untranslated), source language for OCR needs the matching Windows OCR language pack.
**OBS:** enable *Advanced → OBS Browser Source*, add a Browser source in OBS pointing to `http://127.0.0.1:8787/` (style via URL parameters, e.g. `?size=44&color=ffff00&english=1`).
**Translators:** Google (keyless), Google Cloud, DeepL, Gemini, any OpenAI-compatible server (Ollama, LM Studio, OpenAI). **Graphical placement editor**, first-run wizard, autostart options.
**Also:** subtitle history (F6), profiles, diagnostics export (zip, API keys redacted), optional Tesseract second OCR (not bundled, Apache-2.0).

**Build:** install .NET 10 SDK, run `build.cmd`, then `run.cmd`. Windows 10/11 x64.

**Disclaimer:** Not affiliated with Rockstar Games or any publisher. The keyless Google endpoint is unofficial and may stop working; use your own Google Cloud/DeepL key for reliability. See `THIRD-PARTY-NOTICES.md`.


## Jazyky rozhraní a vydání / UI languages and releases

* Výchozí jazyk rozhraní je angličtina, k dispozici je i čeština. Další jazyky = jeden soubor `Overlingo.App/Languages/xx.json`, viz [Languages/README.md](Overlingo.App/Languages/README.md). Jazyk se volí v *Pokročilé → Jazyk rozhraní*, výchozí je jazyk Windows.
* GitHub Actions (`.github/workflows/release.yml`) při tagu `v*` spustí testy, sestaví samostatnou (self-contained) verzi, ZIP a instalátor (Inno Setup, `installer/overlingo.iss`) a přiloží je k vydání. Nic z toho nebylo zatím spuštěno na GitHubu.
* The UI is available in Czech and English (machine-translated, awaiting review). Add a language by dropping a `xx.json` file into `Languages/`.
