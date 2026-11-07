# KubDHCP

Portabler DHCPv4-Server in C# und WinUI 3 zur Erstinbetriebnahme direkt angeschlossener Geräte. Der Server läuft ausschließlich innerhalb der geöffneten Anwendung. Er startet immer gestoppt und wird beim Schließen beendet.

## Starten

Die fertige Ausgabe ist **`dist/KubDHCP.exe`** für Windows x64. Nur diese EXE ist zur Weitergabe erforderlich. Es ist keine separate Installation von .NET, Windows App Runtime oder eines Windows-Dienstes erforderlich. Beim ersten Start entpackt die .NET-Einzeldateibereitstellung die enthaltenen Laufzeitdateien automatisch in ihren Cache.

1. EXE in einen beschreibbaren Ordner kopieren und starten.
2. Die gewünschte IPv4-LAN-Karte auswählen. Pool, Subnetzmaske, Gateway und DNS werden aus deren Konfiguration vorbelegt; die Lease-Dauer beginnt mit einer Stunde.
3. Vorschläge prüfen und bei Bedarf ändern. Der vorgeschlagene Pool spart lokale Adapter-, Gateway- und DNS-Adressen aus. Er ist keine Garantie, dass diese IP-Adressen nicht anderweitig im Netz verwendet werden.
4. Weitere Optionen über **Option hinzufügen** neben der Lease-Dauer konfigurieren.
5. **Server starten** wählen. Eine gegebenenfalls von Windows erforderliche Firewallfreigabe muss DHCP auf dem gewählten Netz ermöglichen. KubDHCP verändert keine Firewall-, Adapter-, Routing- oder Diensteinstellungen.
6. Die Tabelle zeigt vom Server bestätigte Leases, den Zeitpunkt der letzten Bestätigung, IP, MAC-Adresse, registrierten Hersteller und Restlaufzeit.

Gateway und DNS sind optional. Das Verteilen dieser Angaben stellt selbst keinen Router oder DNS-Server bereit. Es wird immer genau eine LAN-Karte bedient. Die Auswahl kann erst nach Stoppen geändert werden. Bei Adapterverlust oder Adressänderung stoppt der Server.

## Oberfläche

Die große Einleitungsüberschrift und ihr Untertitel sind entfernt. Der Button **Option hinzufügen** liegt in derselben Formularzeile wie **Lease-Dauer**, solange die Fensterbreite zwei oder mehr Spalten zulässt. Bei sehr schmalen Fenstern werden die Felder untereinander angeordnet; der Fensterinhalt ist scrollbar. Die Gerätetabelle bleibt unter dem Formular.

Der Optionsdialog bietet den lokalen IANA-Katalog, eigene Optionscodes, aktivierbare Einträge sowie Hinzufügen, Bearbeiten und Entfernen. Unterstützte Formate: ASCII-Text, IPv4, IPv4-Liste, UInt8/16/32, Boolean, Domainlisten (119), Routen (121/249) und Hex-Rohdaten. Herstelleroptionen können einschließlich ihrer Unterstrukturen als Hexdaten eingegeben werden. Protokollgesteuerte und clientseitige Optionen sind gegen zusätzliche Aussendung geschützt.

Zusatzoptionen werden standardmäßig nur bei Aufnahme in die Parameter Request List des Clients gesendet. Für Geräte, die Optionen ohne Anfrage benötigen, gibt es **Auch ohne Client-Anfrage senden**. Lange Werte werden nach RFC 3396 verkettet; überschreitet die gesamte Antwort die Clientgrenze, wird sie mit Diagnose verworfen und nicht still abgeschnitten. Option Overload wird beim Empfang ausgewertet, beim Senden nicht verwendet.

## Daten und Portabilität

Im EXE-Ordner wird **`KubDHCP.data`** angelegt:

- `settings.json`: zuletzt gültige Konfiguration und zusätzliche Optionen.
- `leases.json`: atomar gespeicherte Lease-Belegungen; vorhandene aktive Leases verhindern Doppelvergabe nach Neustarts.
- `.bak`-Dateien: vorheriger gültiger Speicherstand.
- `KubDHCP.log`: Netzwerk- und Fehlerdiagnose nach Serverstart.
- `Lizenzen.txt`: mitgelieferte Lizenztexte und Datenquellen.

Wenn der EXE-Ordner nicht beschreibbar ist, wird `%LOCALAPPDATA%\KubDHCP` verwendet und dieser Pfad angezeigt. Für einen vollständig mitnehmbaren Zustand EXE und Datenordner zusammen kopieren. Die EXE enthält die komplette Offline-Herstellerdatenbank (54.396 IEEE-MA-L/MA-M/MA-S-Präfixe, Abruf 07.11.2026). Es erfolgen keine Online-MAC-Abfragen. Lokal administrierte MAC-Adressen erhalten keinen vermeintlichen Hersteller.

Eine bestehende Lease bleibt nach dem Stoppen auf dem Client bis zu ihrem Ablauf gültig. Stoppen entzieht keine IP-Adresse. Bei beschädigter Leasedatei sperrt die Anwendung den Start; Sicherung und Diagnose prüfen, statt bei noch aktiven Clients blind alle Belegungen zu löschen.

## Protokoll und Umfang

DISCOVER/OFFER, REQUEST/ACK/NAK, Renew/Rebind, RELEASE, DECLINE und INFORM; Ethernet-Hardwareadressen, direkte IPv4-Netze, Serverport UDP 67 und Clientport UDP 68. Relay-Anfragen werden nicht bedient. DHCPv6, BOOTP, Reservierungsverwaltung, DNS, NAT, Routing und Dateiübertragung sind nicht enthalten.

Empfang über `IP_PKTINFO`: Pakete anderer oder nicht bestimmbarer Eingangsinterfaces werden nicht beantwortet. Senden über `WSASendMsg` mit expliziter Quell-IP und Interface-Index. Initiale Antworten verwenden den in RFC 2131 Abschnitt 4.1 erlaubten Broadcast-Fallback, weil normale UDP-Sockets keine zuverlässige L2-Unicastzustellung an noch unkonfigurierte Clients garantieren; Renewals mit gültiger Clientadresse werden per Unicast beantwortet. Dafür sind kein Paketerfassungstreiber und keine Änderung der Windows-Nachbartabelle erforderlich.

Eine ARP-Abfrage vor dem OFFER sperrt erkannte fremde Belegungen für zehn Minuten. Ein ausbleibendes ARP-Ergebnis beweist keine freie Adresse. Ein vorhandener fremder DHCP-Server wird von dieser Version nicht zuverlässig erkannt; das Werkzeug ist für ein bewusst gewähltes Inbetriebnahmenetz gedacht.

## Bauen und prüfen

.NET SDK **10.0.403** oder eine passende neuere Patchversion; Windows x64 und NuGet-Zugriff beziehungsweise vorhandener Paketcache. Visual Studio ist für den verwendeten Build nicht erforderlich. Die nötigen Build-Werkzeuge kommen als NuGet-Pakete.

```powershell
.\scripts\Build.ps1
```

Das Skript nutzt `.tools/dotnet/dotnet.exe`, wenn vorhanden, sonst das installierte SDK. Es führt Kerntests aus, veröffentlicht die EXE und prüft den nativen UDP-Sendepfad ausschließlich auf Loopback mit temporären Ports. Es startet keinen DHCP-Server im produktiven LAN.

Zur Größenreduktion: direkter Verweis auf `Microsoft.WindowsAppSDK.WinUI` 2.6.5 anstelle des SDK-Metapakets, vollständiges Managed-Trimming, komprimierte Einzeldatei, keine ReadyToRun-Duplikate und keine zusätzlichen UI-/MVVM-Pakete. Die nativen WinUI-Module bleiben erhalten, soweit das offizielle Komponentenpaket sie als Abhängigkeiten benötigt. Der Compiler meldet bekannte generische ABI-/Reflection-Trimmanalysen in den Microsoft-WinRT-Projektionen; eigene JSON-Persistenz verwendet Source Generation. Die veröffentlichte getrimmte EXE wird separat gestartet und ihr Optionsdialog geprüft.

## Validierungsstand

- 33 automatisierte Kerntests: Paket-/TLV-Prüfung, Optionen einschließlich 119/121/3396, Poolgrenzen, Nebenläufigkeit, Ablauf, Renew, Release, Decline, Neustart und beschädigte Persistenz.
- Nativer Windows-Loopbacktest von `WSASendMsg`/`IP_PKTINFO` mit Prüfung des Empfangsinterfaces und vollständigem DISCOVER/OFFER/REQUEST/ACK-Ablauf samt Lease-Persistenz, auch aus der veröffentlichten Einzel-EXE.
- Native Oberfläche und Optionsdialog der getrimmten Einzel-EXE gestartet; Vorbelegung anhand der tatsächlichen LAN-Karte sowie Hinzufügen und Speichern einer Option geprüft.
- **Noch offen:** DHCP-Vergabe an reale neue Geräte und Paketmitschnitt im isolierten Netz mit zwei aktiven LAN-Karten. Diese Hardwareprüfung kann durch die lokalen Tests nicht ersetzt werden.

Quellen und RFC-Verweise stehen im [Umsetzungsplan](docs/Umsetzungsplan.md). Der Plan dokumentiert den ursprünglichen Entwurf; dieses README beschreibt den tatsächlich implementierten Stand.

## Bedienung ab v1.0.2

Zusatzoptionen mit ihren Werten stehen direkt unter den Lease-Einstellungen. Die Anzahl aktiver Leases steht rechts über der Tabelle, die Version rechts darunter. Ein Rechtsklick auf einen Tabellenwert kopiert genau diesen Wert in die Zwischenablage.

Mit **Bereich speichern** wird die gesamte aktuelle Konfiguration einschließlich Netzwerkschnittstelle, Lease-Dauer und Zusatzoptionen unter einem Namen gespeichert. Vorhandene Namen werden aktualisiert. Das Dropdown **Gespeicherte Bereiche** lädt die Konfiguration bei gestopptem Server; die zugehörige Schnittstelle muss mit passender IPv4-Konfiguration verfügbar sein. Die Bereiche liegen portabel in `KubDHCP.data/ranges.json`.

**Lease Reset** löscht bei gestopptem Server alle gespeicherten Leases. Die Windows-IP-Konfiguration der Clients wird dadurch nicht sofort verändert; Clients behalten ihre Adresse bis zur Erneuerung oder zum Ablauf. Die Bereichsvorlagen bleiben erhalten.

Die Startgröße berücksichtigt die Windows-DPI-Skalierung. Bei kleinerer verfügbarer Bildschirmfläche startet das Fenster maximiert. Für umfangreiche Optionslisten bleibt die Oberfläche scrollbar.

Die Version wird einmal pro Änderungsiteration mit `scripts/Bump-Version.ps1` erhöht, nicht pro Testbuild. Auf `1.0.100` folgt `1.1.0`. `-Preview` zeigt die nächste Version ohne Änderung an.

## Bedienung ab v1.0.3

**Netzwerkeinstellungen** direkt hinter der Schnittstellenauswahl öffnet die Windows-Eigenschaften genau des gewählten Adapters. Dort können unter TCP/IPv4 Adresse, Maske, Gateway und DNS geändert werden. Beim Zurückkehren werden die Schnittstellen neu eingelesen; bei geänderter Adapteradresse werden passende DHCP-Vorgaben vorbelegt. Der Server muss dafür gestoppt sein.

Zusatzoptionen können direkt in der Hauptansicht bearbeitet werden. Gültige Werte werden beim Verlassen des Feldes gespeichert; ungültige Werte werden am Feld markiert und verhindern das Starten und Speichern eines Bereichs. **Entfernen** löscht die gesamte Option aus der aktuellen Konfiguration. Bereits gespeicherte Bereichsvorlagen werden erst durch erneutes Speichern unter ihrem Namen aktualisiert. Bei laufendem Server sind Bearbeiten und Entfernen gesperrt.

Das Programm startet maximiert, damit die zusätzlichen Eingabefelder, Leasetabelle und Versionsanzeige möglichst vollständig sichtbar sind. Umfangreiche Listen bleiben scrollbar.

## Oberfläche ab v1.0.4

Bereich speichern, Netzwerkeinstellungen und Option entfernen verwenden Symbolbuttons (Speichern, Zahnrad, X) mit Tooltips und zugänglichen Beschriftungen. Die native Titelleiste einschließlich Fensterbuttons passt sich dem aktuellen Hell-/Dunkelmodus an; Theme-Wechsel werden während der Laufzeit übernommen.

## Startkorrektur v1.0.5

Die portable EXE muss **KubDHCP.exe** heißen. Das Umbenennen in `KubDHCP-v1.0.4.exe` hat die WinUI-Ressourcenauflösung beim Fensterstart beschädigt. Versionierte Veröffentlichungen verwenden daher einen Ordner wie `v1.0.5/KubDHCP.exe`. Falls die bisherige EXE läuft, legt der Build die neue Version unter diesem Ordner ab. Ein erneuter Build prüft nun neben dem Netzwerktransport auch das tatsächliche Laden des WinUI-Fensters (`--startup-test`). Einstellungen und Leases bleiben unverändert im jeweiligen `KubDHCP.data`-Ordner.

## Bereiche und Netzwerk-Monitor ab v1.0.6

Das **X neben dem Speichern-Symbol** entfernt den im Dropdown ausgewählten gespeicherten Bereich. Aktuelle Formularwerte und Leases bleiben erhalten.

**Monitor** rechts neben **Lease Reset** schaltet eine rechte Terminalspalte über die gesamte Höhe von Konfiguration und Leasetabelle ein oder vollständig aus. Bei normaler Fenstergröße wird das Fenster für den Monitor verbreitert und beim Ausblenden wieder zurückgesetzt. Die Ausgabe zeigt Zeitstempel, empfangene DHCP-Pakete, Client-MAC, Transaktionskennung, angefragte Optionen und deren Werte, Antworten einschließlich Ziel, Broadcast/Unicast und tatsächlich gesendeter Optionen, ARP-Belegungsprüfungen, Gründe für ausbleibende Antworten sowie Fehler und Start/Stopp-Ereignisse. Erfasst wird die vom DHCP-Server selbst empfangene und gesendete Kommunikation sowie seine ARP-Prüfungen.

Die Anzeige folgt neuen Einträgen automatisch, solange man am Ende steht. Hochscrollen ermöglicht das Nachlesen; am unteren Ende wird die Live-Ansicht fortgesetzt. Bis zu 2.000 Meldungen bleiben im laufenden Programm erhalten, auch während die Spalte ausgeblendet ist. Bei Überlast werden ausgelassene Meldungen kenntlich gemacht.

Das Programm startet wieder als normales, zentriertes Fenster innerhalb der verfügbaren Arbeitsfläche, unter Berücksichtigung der Windows-Skalierung. Auf kleinen Bildschirmen bleiben umfangreiche Formularinhalte scrollbar. Der automatische WinUI-Starttest prüft auch Ein-/Ausblenden und die Monitoranzeige einer echten DISCOVER/OFFER/REQUEST/ACK-Folge über isolierte Loopback-Ports.

## Monitorbedienung ab v1.0.7

Die Trennleiste links vom Terminal lässt sich ziehen, um dessen Breite anzupassen. Titel, Status, Hinweise und Ausgabe verwenden Consolas als Monospace-Schrift; Statussymbole verwenden die Windows-Symbolschrift.

Beim Hochscrollen wechselt die Anzeige rechts im Monitorkopf auf ein rotes Pause-Symbol und blinkendes **PAUSE**. Neue Meldungen werden währenddessen gepuffert. Am Ende der Ausgabe erscheinen ein grünes Play-Symbol und **Live**, und gepufferte Meldungen werden nachgeliefert. Die bestehenden Puffergrenzen gelten weiterhin.

Das Ein-/Ausblenden verändert die Fensterposition nicht mehr. Bei Bedarf wird das Fenster nur nach rechts verbreitert; beim Ausschalten wird ausschließlich die zusätzlich angefügte Breite abgezogen. Zwischenzeitliches Verschieben und Änderungen der Fensterhöhe bleiben erhalten.

## Lease-Tabelle und Monitorsteuerung ab v1.0.8

**Live/PAUSE** im Monitorkopf ist anklickbar: Live pausiert die Ausgabe, PAUSE setzt sie fort und zeigt neue Meldungen wieder an. Eine ausdrücklich angeklickte Pause bleibt auch beim Scrollen bestehen, bis sie per Klick aufgehoben wird. Die automatische Lesepause durch Hochscrollen wird weiterhin am Ende der Ausgabe beendet.

**Lease Reset** steht rechts über der Leasetabelle, direkt hinter der Anzahl aktiver Leases. Das X hinter jedem Lease-Balken entfernt ausschließlich diesen Eintrag, auch bei laufendem Server. Der laufende Server aktualisiert seinen Lease-Speicher unter derselben Sperre wie die Paketverarbeitung. Falls der Eintrag inzwischen erneuert wurde, wird die Tabelle aktualisiert und der neuere Lease bleibt bestehen. Das Löschen sendet keinen Widerruf an den Client; dessen lokale IP-Konfiguration wird dadurch nicht sofort verändert.

Die neue, ebenfalls verstellbare und per Rechtsklick kopierbare Spalte **Hostname** übernimmt den vom Client gelieferten DHCP-Namen (Option 12) oder FQDN (Option 81). Ein bereits während DISCOVER gemeldeter Name wird bei der Bestätigung übernommen und bei Erneuerungen ohne erneute Namensangabe beibehalten. Bestehende Lease-Dateien bleiben lesbar; fehlt die Namensangabe, zeigt die Tabelle „—“. Namen erscheinen bei bestehenden Clients, sobald sie durch eine neue DHCP-Anfrage bekannt werden.

## Symbolbuttons und Client-ID ab v1.0.9

Der Serverbutton zeigt ausschließlich ein Play-Symbol bei gestopptem Server und ein Stopp-Symbol bei laufendem Server. Der Monitorbutton verwendet ein Terminalfenster-Symbol. Die Tooltips und zugänglichen Beschriftungen benennen die jeweilige Aktion.

Die neue Spalte **Client-ID** steht hinter der MAC-Adresse. Sie zeigt die vollständig übermittelte DHCP-Client-ID (Option 61) als Hexwert einschließlich Typbyte. Ohne übermittelte Client-ID steht dort „—“; eine nur intern aus der MAC-Adresse abgeleitete Kennung wird nicht als Client-ID ausgegeben. Vorhandene gespeicherte IDs werden direkt aus den bestehenden Lease-Daten gelesen. Die Spalte ist verstellbar und ihr Wert per Rechtsklick kopierbar.

## Einheitliche Symbolbuttons ab v1.0.10

Die Symbolbuttons sind einheitlich 40 × 32 XAML-Pixel groß und verwenden 16-Punkt-Symbole. Die Buttons im Konfigurationsbereich stehen mit 8 Pixeln Abstand in zwei bündigen Spalten: Play/Stopp über Speichern und Zahnrad, Monitor über Bereich entfernen und Aktualisieren. Die Entfernen-Symbole für Zusatzoptionen und einzelne Leases verwenden dieselbe Buttongröße.
Die Überschrift der Leasetabelle lautet ab v1.0.10 kurz „Leases“.

## TUI ab v1.0.11

Dieselbe portable EXE bietet zusätzlich eine Terminaloberfläche:

```powershell
.\KubDHCP.exe --tui
```

Ohne Parameter startet weiterhin WinUI 3. Ab v1.0.12 läuft die TUI ausschließlich in der aufrufenden PowerShell-/Terminalsitzung und öffnet kein eigenes Fenster. Sie übernimmt die Anordnung der Konfiguration, Symbolbuttons, Zusatzoptionen, Leasetabelle und des seitlichen Netzwerkmonitors in einem Zeichenraster. Kleine Fenster erlauben Scrollen und passen die Formularspalten an.

- **Tab / Shift+Tab:** zwischen Eingabefeldern und Aktionen wechseln; **Enter / Leertaste:** die gewählte Aktion ausführen.
- Alle Buttons und Auswahllisten sind mit der **Maus** bedienbar. Eingabefelder unterstützen **Strg+A/C/V**.
- **Rechtsklick auf einen Tabellenwert:** genau diesen Wert kopieren. Spaltenbreiten und Monitorbreite lassen sich durch Ziehen ändern.
- **Mausrad:** Formular, Tabelle und Monitor scrollen. Hochscrollen im Monitor pausiert die Ausgabe; **Live/PAUSE** ist anklickbar.
- Zusatzoptionen lassen sich direkt bearbeiten und entfernen. Ein Klick auf ihren Titel öffnet den vollständigen Optionseditor einschließlich Format und Versandregeln.
- **F1:** Bedienhilfe; **F10 / Strg+Q:** beenden und Server stoppen; **Escape:** Dialog schließen.

Bereiche, Einstellungen und Leases verwenden denselben Datenordner und dieselbe DHCP-Implementierung wie WinUI. Pro EXE-Pfad kann nur eine Instanz laufen, unabhängig von der gewählten Oberfläche. Das Zahnrad öffnet in der TUI einen Terminaldialog für IPv4, Subnetzmaske, Gateway, DNS und DHCP. Übernehmen benötigt Administratorrechte in der aktuellen Sitzung; es wird kein UAC-Fenster gestartet. Änderungen am Verbindungsadapter können eine SSH-Sitzung unterbrechen. Die WinUI-Adaptereigenschaften bleiben unverändert.

Der Build prüft zusätzlich die veröffentlichte TUI: Layout, Tab-Navigation, Mausaktionen, persistente Bereiche und Formularwerte, Optionsbearbeitung, Lease-Löschung, Monitorsteuerung sowie native Unicode-Konsolenausgabe und Eingabeereignisse. Der DHCP-Ablauf wird dafür ausschließlich über isolierte Loopback-Ports ausgeführt. Der aktuelle Stand umfasst 59 bestandene Kerntests sowie bestandene WinUI-, Transport- und TUI-Prüfungen.

## PowerShell und SSH ab v1.0.12

`.\KubDHCP.exe --tui` läuft im vorhandenen Terminal. PowerShell wartet bis zum Beenden; danach werden der vorherige Bildschirm, die Eingabemodi und der Cursor wiederhergestellt. Terminalgröße, Schrift und Farbpalette der Sitzung werden nicht verändert. Ohne interaktives Terminal meldet die Anwendung einen Fehler und öffnet kein Ersatzfenster.

Für eine interaktive SSH-Verbindung zu einem Windows-Rechner ein Terminal anfordern, beispielsweise `ssh -t benutzer@rechner`, dann dort `& 'C:\Tools\KubDHCP\KubDHCP.exe' --tui` ausführen. Erforderlich ist ein SSH-Server mit Windows-Pseudokonsolenunterstützung und ein Terminal mit VT-Unterstützung. Tastatur, Terminalgrößenänderungen und SGR-Mausereignisse werden verarbeitet. Bei aktivierter Mausweitergabe bedient ein Klick die TUI; die lokale Terminalauswahl ist üblicherweise mit Shift erreichbar. Die Firewall-/Netzwerkrechte gelten auf dem entfernten Windows-Rechner.

Einfügen über die Einfügefunktion des lokalen Terminals überträgt den Text auch per SSH. Kopieren per Rechtsklick verwendet in SSH-Sitzungen OSC 52 für die lokale Zwischenablage; das verwendete Terminal muss dies erlauben. Es wird nicht in die Desktop-Zwischenablage des entfernten Servers geschrieben.

Der Start verwendet das Konsolensubsystem, damit PowerShell die TUI als Vordergrundanwendung behandelt. Das Manifest verhindert unter Windows 11 24H2 / Server 2025 und neuer eine Konsole beim grafischen Start. Auf älteren Windows-Versionen wird die automatisch zugewiesene Konsole vor dem WinUI-Start abgetrennt; dort kann beim grafischen Start kurz eine Konsole erscheinen. Layout und Bedienung der WinUI-3-Oberfläche wurden nicht geändert.

Der zusätzliche Buildtest startet die ausgelieferte EXE direkt innerhalb von PowerShell in einer echten ConPTY-Sitzung und prüft Ausgabe, Maus, Tastatur, Größenänderung, Warten der Shell sowie deren Rückkehr nach dem Beenden. Eine echte SSH-Verbindung wurde lokal nicht geprüft.

## TUI-Navigation ab v1.0.13

Die Pfeiltasten wählen das nächste bedienbare Element in der jeweiligen Richtung. Ein ausgewähltes Eingabefeld nimmt erst nach **Enter** Text an; ein Klick wählt es ebenfalls zunächst nur aus. Der Eingabemodus zeigt einen Cursor und den Hinweis **EINGABE** am unteren Rand. Während der Eingabe bewegen die Pfeile den Textcursor. **Enter** übernimmt die Eingabe und kehrt zur Navigation zurück, **Escape** verwirft die Änderungen dieser Eingabe. Mehrzeilige Felder übernehmen mit **Strg+Enter**, während Enter dort einen Zeilenumbruch einfügt.

Buttons und Dropdowns werden weiterhin mit Enter oder per Maus betätigt. In geöffneten Auswahllisten wählen Hoch/Runter den Eintrag. Tabelle und Monitor lassen sich mit Enter für ihre interne Tastaturbedienung aktivieren und mit Escape wieder verlassen. Tab/Shift+Tab bleiben als zusätzliche Bedienmöglichkeit erhalten. Die WinUI-Oberfläche ist unverändert.
