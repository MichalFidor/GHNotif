# GHNotif

Aplikacja w pasku menu macOS, która pokazuje otwarte (nie-draft) PR-y wybranych osób ze wszystkich repozytoriów widocznych dla Twojego tokenu GitHub.

## Instalacja

```bash
./scripts/build-app.sh            # buduje build/GHNotif.app
./scripts/build-app.sh --install  # kopiuje do ~/Applications i uruchamia
```

Wymaga macOS 15+ i Swift 6 (wystarczą Command Line Tools). Autostart: Ustawienia systemowe → Ogólne → Rzeczy otwierane podczas logowania → dodaj `GHNotif.app`.

## Autoryzacja

- Domyślnie używany jest token z `gh auth login` (aplikacja wywołuje `gh auth token`).
- Opcjonalnie w ustawieniach możesz zapisać PAT (Keychain). Ma pierwszeństwo przed `gh`.
- Organizacje z SAML SSO: jeśli token nie jest dla nich autoryzowany, GitHub ukrywa ich PR-y. Aplikacja pokazuje wtedy ostrzeżenie z linkiem do autoryzacji.

## Konfiguracja

Ustawienia (pasek menu → „Ustawienia…”): dodawanie/usuwanie obserwowanych osób (login jest weryfikowany w GitHub), dodatkowe kwalifikatory wyszukiwania (np. `org:my-org`), interwał odświeżania, powiadomienia. Nowa instalacja zaczyna z pustą listą osób.

Konfiguracja jest zapisywana w `~/Library/Application Support/GHNotif/config.json`.

## Załatwione PR-y

Przycisk z haczykiem po prawej stronie PR-a oznacza go jako załatwiony. PR znika z listy wymagającej uwagi i licznika. Nie wraca po odświeżeniu, ponownym uruchomieniu ani kolejnych zmianach w PR-ze; nie wywołuje też kolejnych powiadomień.

Przycisk z haczykiem w nagłówku przełącza na listę załatwionych, nadal otwartych PR-ów. Strzałka przy PR-ze przywraca go na główną listę. To wyłącznie lokalny stan aplikacji, nie review ani zamknięcie PR-a na GitHubie. Identyfikatory są przechowywane w preferencjach macOS, poza repozytorium.

## Jak to działa

Zamiast jednego zapytania z `OR` (API wyszukiwania GitHub dopuszcza maks. 5 operatorów logicznych) autorzy są dzieleni na paczki po 10; powtórzone kwalifikatory `author:` działają jak OR. Każda paczka to zapytanie GraphQL `is:pr is:open draft:false author:… author:…`, wyniki są łączone i deduplikowane.

## Testy

```bash
swift run CoreChecks
bash scripts/check-app.sh
```

Testy są zwykłym programem wykonywalnym, bo Command Line Tools nie zawierają XCTest ani makr Swift Testing.
Testy aplikacji sprawdzają ukrywanie, przywracanie i trwałość stanu oraz brak ponownych powiadomień. Działają z izolowaną konfiguracją i atrapami GitHuba oraz powiadomień.
