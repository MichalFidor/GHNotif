# GHNotif 0.2.0

- Logo w pasku menu macOS jest widoczne obok licznika (wcześniej był tylko licznik).
- Wzmianki: otwarte PR-y, w których ktoś Cię wspomniał (`@`), dowolnego autora (także spoza listy i Twoje). Domyślnie włączone.
- Opcjonalnie prośby o Twoje review (tylko osobiste, nie zespołowe). Domyślnie wyłączone, żeby nie zalewać listy.
- Ustawienia w stylu Apple: menu po lewej (Osoby / Wyszukiwanie / Ogólne / Token), treść po prawej; lista osób domyślnie zwinięta, z licznikiem i filtrem. Na Windows analogiczna nawigacja po lewej.
- Poprawka: po zwinięciu wszystkich repozytoriów lista nie „ucieka” już na środek okna — panel zmniejsza się i zostaje przy pasku menu / zasobniku.
- Starsze pliki konfiguracji są wczytywane bez zmian, nowe opcje dostają wartości domyślne.

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
