# GHNotif 0.5.1

- macOS: nazwy kategorii w ustawieniach zmieniają się od razu po przełączeniu języka.

# GHNotif 0.5.0

- Przypomnienia o review: okno na środku ekranu z listą PR-ów o wybranych godzinach i w wybrane dni (macOS i Windows).
- Możliwość odłożenia przypomnienia o 15 minut; pominięte przypomnienie pokazuje się po wybudzeniu lub uruchomieniu aplikacji.
- Wybór języka interfejsu: polski, angielski lub systemowy (Ustawienia → Ogólne). Dotychczasowe instalacje pozostają po polsku.
- Ustawienia otwiera ikona zębatki w nagłówku panelu; otwarcie ustawień zamyka listę PR-ów.

# GHNotif 0.4.1

- Naprawiono powtarzające się powiadomienia o tej samej aktywności, gdy włączone jest automatyczne oznaczanie PR-ów po własnym review.

# GHNotif 0.4.0

- Powiadomienia pokazują konkretną aktywność na PR-ze: komentarze, zatwierdzenia, prośby o zmiany i nowe commity.
- Nowe komentarze/review/commity na już obserwowanych PR-ach też wywołują powiadomienie; to samo zdarzenie nie jest powtarzane przy odświeżeniach.
- Powiadomienia zbiorcze pokazują skrócone podsumowanie PR-ów i zmian.

# GHNotif 0.3.1

- Poprawiono układ wierszy PR: akcje odłożenia i oznaczenia są teraz bliżej PR-a, zamiast być rozciągnięte do prawej krawędzi okna.

# GHNotif 0.3.0

- Automatycznie oznaczaj PR jako załatwiony po własnym review aktualnego commita; nowy commit przywraca go na listę.
- Odkładaj PR na 2 godziny lub do jutra rano. Osobna lista odłożonych i powiadomienie po powrocie.
- Filtry wykluczające repozytoria i etykiety; sortowanie według aktualizacji, wieku lub rozmiaru zmian.
- W wierszu PR-a pokazuj liczbę dodanych/usuniętych linii (status CI i decyzja review były już dostępne).
- Import/eksport listy obserwowanych osób oraz import członków zespołu GitHub (token wymaga dostępu do członkostwa organizacji).
- Przełącznik autostartu przy logowaniu w ustawieniach macOS i Windows.
- Dodano publiczny Homebrew tap: `brew tap MichalFidor/ghnotif && brew install --cask ghnotif`. Aktualizacje: `brew upgrade --cask ghnotif`.
- Workflow wydania generuje cask z sumami paczek i może synchronizować go z tapem; wymaga sekretu i zmiennej repozytorium opisanych w README.

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
