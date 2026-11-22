# Jazyky rozhraní / UI languages

Každý jazyk je jeden soubor `xx.json` v této složce (xx = kód jazyka, např. `de`, `es`).
Each language is one `xx.json` file in this folder.

```json
{
  "code": "de",
  "name": "Deutsch",
  "machineTranslated": true,
  "strings": {
    "Překlad": "Übersetzung",
    "Uložit": "Speichern"
  }
}
```

* Klíč = původní český text v programu, hodnota = překlad. Pomocí `en.json` vidíte všechny klíče.
* Chybějící klíč se zobrazí česky – nic se nerozbije. Texty s `{0}` ponechte s `{0}`.
* `machineTranslated: true` označí jazyk v nabídce jako „čeká na kontrolu komunitou“. Až ho zkontroluje rodilý mluvčí, nastavte `false`.
* Překlady opravujte přes Pull Request. Díky!

The key is the original Czech text, the value is your translation. Missing keys fall back to Czech.
