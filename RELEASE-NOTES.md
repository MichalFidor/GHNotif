# GHNotif 0.1.1

Pierwsze publiczne wydanie aplikacji do śledzenia PR-ów wybranych osób.

- Natywna aplikacja na pasku macOS i w zasobniku Windows.
- Edytowalna lista obserwowanych osób, repozytoria z różnych organizacji, obsługa SSO.
- Lokalne odhaczanie PR-ów; powrót po nowych commitach, komentarzach i review.
- Zwijane grupy repozytoriów z licznikami.
- Logo na pasku, tooltipy, test powiadomień macOS.
- Informacja w aplikacji o nowych wydaniach z linkiem do pobrania (bez automatycznej instalacji).

## Instalacja

- **Mac Apple Silicon:** `GHNotif-macos-arm64.zip`.
- **Mac Intel:** `GHNotif-macos-x86_64.zip`.
- **Windows:** `GHNotif-win-x64.zip` lub `GHNotif-win-arm64.zip`.

Rozpakuj ZIP. Na macOS przenieś `GHNotif.app` do Applications; na Windows uruchom
`GHNotif.Windows.exe` z rozpakowanego folderu. Windows nie wymaga instalacji .NET.
Wymagany macOS 15+ lub Windows 10/11.

Paczki nie są podpisane certyfikatem wydawcy ani notaryzowane. System może ostrzec
przy pierwszym uruchomieniu. Zezwalaj na uruchomienie tylko po zweryfikowaniu źródła
i sumy SHA-256 (plik `SHA256SUMS`). Na macOS można użyć
Ustawienia systemowe → Prywatność i ochrona → Otwórz mimo to.

Zaloguj się przez `gh auth login` lub podaj PAT w ustawieniach aplikacji, a następnie
dodaj obserwowane osoby. Paczki nie zawierają kont, tokenów ani prywatnych danych.
Ustawienia istniejącej instalacji są zachowywane przy wymianie aplikacji.
