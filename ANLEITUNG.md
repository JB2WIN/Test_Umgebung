# Lernheft Studio + Lernheft Stift

Eine App fürs Surface, die alles kann – und ein iPad, das bei Bedarf zum Zeichentablett wird.

| Gerät | App | Aufgabe |
|---|---|---|
| **Surface** (Windows 11) | **Lernheft Studio** | Notizen, Fächer, Hausaufgaben, Karteikarten, Stundenplan, KI, Einstellungen – alles |
| **iPad** | **Lernheft Stift** | nur Zeichnen: Was du mit dem Pencil schreibst, erscheint sofort in der offenen Notiz am Surface |

Beide sind **neue Apps** – die alte Lernheft-App bleibt unangetastet, bis du sie selbst löschst.

---

## 1. Installieren

### Surface
1. `LernheftStudio.msi` doppelklicken. Kein Administrator nötig, landet unter
   `%LOCALAPPDATA%\Programs\Lernheft Studio`, mit Verknüpfung im Startmenü und auf dem Desktop.
2. Lernheft Studio öffnen. Beim ersten Start fragt Windows evtl. nach der **Firewall** →
   **Zulassen** (privates Netzwerk genügt). Ohne Freigabe klappt es meistens trotzdem, weil
   das Surface dann das iPad von sich aus anruft.

### iPad
`LernheftStift.ipa` wie gewohnt mit **Sideloadly** aufspielen. Beim ersten Verbinden fragt iOS, ob
Lernheft Stift im **lokalen Netzwerk** suchen darf → **Erlauben**. Für den QR-Code braucht die App
die **Kamera**.

Beide Dateien liegen auf GitHub unter **Releases** (neueste Version oben):
`LernheftStudio.msi` fürs Surface, `LernheftStift.ipa` fürs iPad.

---

## 2. iPad verbinden

1. Am Surface: **iPad verbinden** (links unten, auf der Übersicht oder oben in jeder Notiz).
2. Ein QR-Code und ein sechsstelliger Code erscheinen (10 Minuten gültig).
3. Auf dem iPad in Lernheft Stift: **QR-Code scannen** – oder **Code eingeben**.
4. Fertig. Ab jetzt verbindet sich das iPad **von allein**, sobald beide Apps offen und im
   selben WLAN sind. Ein neuer Code ist nicht nötig.

**Klappt nicht?**
- Beide Geräte müssen im **selben WLAN** sein.
- Im **Schul-WLAN** sehen sich Geräte oft nicht. Dann am Surface den **mobilen Hotspot**
  einschalten (Einstellungen → Netzwerk und Internet → Mobiler Hotspot) und das iPad damit verbinden.
- iPad → Einstellungen → Datenschutz → **Lokales Netzwerk** → Lernheft Stift einschalten.
- Am Surface im Kopplungsfenster: **Firewall-Freigabe einrichten**.

---

## 3. Schreiben

### Am Surface – wie in OneNote
- Irgendwo auf die Seite klicken und **lostippen**. Es entsteht ein Textfeld genau an der Stelle.
- Der Text sitzt auf den Linien des Papiers.
- Oben die Formatleiste: Schrift, Größe, **F**ett, *Kursiv*, Unterstrichen, Durchgestrichen,
  Farbe, Textmarker, Aufzählung, Nummerierung, Einzug, Hoch-/Tiefstellen.
- Textfelder am Balken oben **verschieben**, am rechten Rand **breiter/schmaler** ziehen,
  mit dem Papierkorb löschen.
- **Einfügen**: PDF, Bilder, Scans (Kamera), SVG, Textdateien – als ganze Seiten oder frei platziert.
- **Funktion zeichnen** (Taschenrechner-Symbol): Term eintippen, Graph erscheint als Handschrift.
- `Strg`+`Z` / `Strg`+`Y` rückgängig/wiederholen, `Strg`+`+`/`-`/`0` Zoom, `F11` Listen ausblenden,
  `Strg`+`N` neue Notiz, `Strg`+`F` suchen.

### Auf dem iPad – nur Zeichnen
Das iPad zeigt die Notiz, die **am Surface offen** ist – Papier, eingefügte Seiten und getippten
Text. Du schreibst mit dem Pencil darüber, und jeder Strich erscheint **schon während du schreibst**
auf dem Surface.

- **Werkzeuge** von Apple: Stift, Bleistift, Marker, Radierer, Lineal, Lasso (Verschieben).
- **Formen**: Große Linien, Kreise und Rechtecke werden beim Loslassen sauber (abschaltbar).
- **Radierer** wirkt auch auf eingefügte Seiten/Scans.
- **Auswählen** (Lasso-Symbol oben), Rahmen um die Handschrift ziehen, dann:
  - **Kreuze verbinden** – findet deine gezeichneten Kreuze und legt eine glatte Kurve hindurch
  - **Schönschrift** – macht aus der Handschrift sauberen Text an derselben Stelle
  - **In Text** – macht daraus ein normales Textfeld
  - **Mathe lösen** / **KI fragen** – der Ausschnitt geht an den KI-Helfer am Surface
  - **Glätten**, **Begradigen**, **Löschen**
- **Werkzeuge** (Kacheln oben): **Funktion zeichnen**, **Punkte verbinden** (Punkte antippen,
  glatte Kurve oder Geraden, mit oder ohne Kreuze), **Seite hinzufügen**, **Seitenbreite**,
  **Zum Ausschnitt am Surface**.
- **Zoom sperren** (Schloss), damit beim Schreiben nichts verrutscht.
- Ist am Surface keine Notiz offen, zeigt das iPad die zuletzt bearbeiteten – antippen öffnet sie
  auf beiden Geräten. Neue Notizen legst du dort auch an.

Rückgängig/Wiederholen gibt es auf beiden Geräten.

---

### Dateien vom iPad einfügen
In Lernheft Stift unter **Werkzeuge** (Kacheln oben):

- **Datei einfügen** – PDF, Bild oder Text aus der Dateien-App, auch aus iCloud Drive,
- **Foto einfügen** – aus deinen Fotos (auch mehrere),
- **Seiten scannen** – mit der Kamera: Blattkanten werden erkannt und begradigt, alle Seiten kommen als ein PDF.

Danach fragt das iPad: **Als neue Seiten** oder **Hier auf der Seite** (an der Stelle, die du gerade siehst).
Das Surface fügt ohne Rückfrage ein, und die Seiten erscheinen gleich auch auf dem iPad zum Beschreiben.
Ist am Surface keine Notiz offen, geht das auch von der Notizauswahl aus – dann entsteht eine neue Notiz.

**Aus der Vorschau-App (oder jeder anderen App):** PDF in der Vorschau öffnen, markieren, unterschreiben,
zuschneiden … dann **Teilen → Lernheft Stift** (steht es nicht in der Reihe, unter **Mehr** suchen).
Lernheft Stift öffnet sich und fragt, wohin die Datei soll: in die offene Notiz (**als neue Seiten** oder
**hier**) oder **als neue Notiz**. Ist das Surface gerade nicht verbunden, wartet die Datei, bis die
Verbindung steht.

### Dateien aus iCloud Drive
Einmal die kostenlose App **iCloud für Windows** (Microsoft Store) installieren, mit der Apple-ID
anmelden und **iCloud Drive** anhaken. Danach zeigt Lernheft Studio deine iCloud-Dateien von selbst:

- links **iCloud Drive** anklicken – oder in einer Notiz **Einfügen → Aus iCloud Drive**,
- **Zuletzt geändert** zeigt die neuesten PDFs, Bilder und Texte quer durch alle Ordner,
  **Ordner** zum Durchklicken, oben die Suche,
- Doppelklick fügt die Datei ein. Dateien mit Wolken-Symbol liegen nur in der Cloud und werden
  dabei automatisch geladen. Ist keine Notiz offen, entsteht eine neue mit dem Dateinamen als Titel.

Liegt iCloud Drive woanders, kannst du den Ordner im Fenster mit **Ordner ändern …** wählen.

## 4. Alte Notizen übernehmen

Am Surface: **Einstellungen → Sicherung & Umzug → Umzug starten** (oder das Angebot
beim ersten Start annehmen). Vier Wege:

1. **Von diesem Surface** – was die alte Windows-App gespeichert hat. Ein Klick.
2. **Vom iPad** (empfohlen, dort liegen die Originale der Handschrift):
   iPad verbinden → in Lernheft Stift **Einstellungen → Alte Notizen übertragen → Ordner wählen**
   → **Auf meinem iPad → Lernheft**. Die Handschrift wird dabei ins neue Format übersetzt.
3. **Vom alten Lernheft-Server** – Adresse und Zugangsschlüssel wie früher.
4. **Aus einem Ordner oder einer Sicherung** der alten App.

Übernommen werden Fächer, Notizen, Handschrift, eingescannte Seiten, Hausaufgaben,
Karteikarten und Stundenplan. Getippter Text wird zu Textfeldern. Was schon da ist, wird
übersprungen – du kannst den Umzug gefahrlos wiederholen oder mehrere Wege nacheinander nutzen.

---

## 5. Alles andere – am Surface

- **Übersicht**: Tagesgruß, offene Hausaufgaben, fällige Karteikarten, heutiger Stundenplan.
- **Stundenplan**: aus WebUntis (Anmeldung im Fenster) oder einem Kalender-Link (ICS), oder von Hand.
- **Hausaufgaben**: von Hand oder von der KI aus deinen Notizen gefunden (auch automatisch pro Fach).
- **Karteikarten**: Stapel, Lernen mit Leertaste und 1–4, Wiederholung nach Boxen.
- **KI-Helfer** (Gemini): Rechtschreibung, Mathe, Karteikarten, Zusammenfassung, Fragen.
  Hauptschlüssel und Ersatzschlüssel, Guthaben & Verbrauch mit Budget.
- **Darstellung**: hell, dunkel oder wie Windows.
- **Sicherung**: alles in eine Datei, zurückspielen auf jedem Surface.

Deine Daten liegen in `%LOCALAPPDATA%\Lernheft Studio`. Schlüssel werden mit Windows verschlüsselt.
