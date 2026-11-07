# KubDHCP – Umsetzungsplan

Stand: 7. November 2026. Ursprünglicher Entwurf und ergänzte Umsetzungsvorgaben. Den implementierten Stand, Build und verbleibende Hardwareprüfungen beschreibt [README.md](../README.md).

Angenommene Layoutänderungen: Einleitungsüberschrift und Untertitel entfallen; **Option hinzufügen** ersetzt das frühere Optionsmenü und steht neben der Lease-Dauer. Die Hauptwerte werden passend zur ausgewählten LAN-Karte vorgeschlagen. Auslieferung als selbstenthaltene, komprimierte Einzel-EXE für x64 mit direktem WinUI-Komponentenpaket und Managed-Trimming.

## 1. Ziel und Umfang

KubDHCP ist ein manuell gestartetes Windows-Werkzeug zur Erstinbetriebnahme von Geräten in einem direkt angeschlossenen IPv4-Netz. Der DHCP-Server läuft asynchron innerhalb des Anwendungsprozesses. Es gibt keinen Windows-Dienst, keinen Autostart des Servers und keinen weiterlaufenden Hintergrundprozess nach dem Beenden.

Für die erste Version gelten: ein ausgewählter Adapter, ein IPv4-Adresspool und eine laufende Serversitzung. DHCPv6, DHCP-Relay, BOOTP, Routing, NAT, ein eigener DNS-Server und ein TFTP-/HTTP-Dateiserver gehören nicht dazu. Bootserver-Adressen können dennoch über DHCP-Optionen verteilt werden. DHCP-Pakete mit Relay-Adresse werden in dieser Version nicht bedient.

## 2. Hauptfenster und Bedienung

Fluent-Oberfläche mit WinUI 3, Systemschrift Segoe UI Variable, Unterstützung für helles und dunkles Windows-Design. Zielgröße etwa 1.100 × 780 DIP; bei schmaleren Fenstern werden Formularspalten umgebrochen. Die Gerätetabelle steht immer unter der Parametereingabe. Keine seitliche Navigation und keine zusätzlichen Dashboard-Kacheln.

Oben: Anwendungsname, Menü **Optionen → Weitere DHCP-Optionen…**, Status und **Server starten / Server stoppen**. Unterhalb folgen Adapterauswahl, Konfigurationsfelder und Tabelle. Ein dezenter Statusbereich nennt laufenden Adapter, letzte Aktion oder einen konkreten Fehler.

### Hauptfelder

| Feld | Eingabe und Verhalten |
| --- | --- |
| Netzwerkschnittstelle | ComboBox mit Adaptername, Beschreibung, Linkstatus und IPv4-Adresse. Bei mehreren IPv4-Adressen wird innerhalb dieses Auswahlfeldes die konkrete Adapteradresse ausgewählt. Identität über Adapter-GUID; Interface-Index beim Start neu ermitteln. |
| Start-IP | Erste Adresse des Pools, inklusive. |
| Stop-IP | Letzte Adresse des Pools, inklusive. In der UI zusätzlich als „Ende des Adressbereichs“ erläutern. |
| Subnetzmaske | IPv4-Maske mit zusammenhängenden gesetzten Bits. |
| Gateway | Optionale IPv4-Adresse; leer bedeutet: keine Router-Option senden. |
| Primärer DNS-Server | Optionale IPv4-Adresse. |
| Sekundärer DNS-Server | Optionale IPv4-Adresse; nur zusammen mit primärem DNS. Beide Werte ergeben eine geordnete Liste in Option 6. |
| Lease-Dauer | Positive ganze Zahl plus Einheit Sekunden, Minuten, Stunden oder Tage; Anzeige der umgerechneten Dauer. Endliche Lease bis 0xFFFFFFFE Sekunden; unendliche Lease zunächst nicht anbieten. |

Start-/Stop-IP und Adapterauswahl sind lokale Serverkonfiguration, keine DHCP-Optionen. Die vom Adapter abgelesene Server-IP wird nur angezeigt, nicht als weiteres Hauptfeld eingeführt. Das Werkzeug ändert die Windows-IP-Konfiguration nicht automatisch. Ein geeigneter, vorzugsweise statisch konfigurierter Adapter im Zielnetz ist Startvoraussetzung.

### Tabelle bezogener Adressen

Genau diese fünf sichtbaren Spalten:

| Spalte | Definition |
| --- | --- |
| Zeitstempel | Datum und lokale Uhrzeit der letzten bestätigten Vergabe beziehungsweise Verlängerung, gespeichert als UTC. |
| IP | Bestätigte IPv4-Adresse. Ein OFFER allein erzeugt noch keinen Lease-Eintrag. |
| MAC-Adresse | Einheitlich mit Doppelpunkt formatierte Hardwareadresse. |
| Gerätehersteller | Zuordnung des MAC-Adresspräfixes aus lokalem IEEE-Verzeichnis. Bei fehlendem Treffer „Unbekannt“, bei lokal administrierter MAC „Lokal administriert“. |
| Rest-Leasezeit | Ablaufzeit minus aktuelle Zeit, mindestens null; Format `hh:mm:ss` beziehungsweise Tage plus Zeit. Abgelaufene und freigegebene Einträge erhalten einen eindeutigen Text in dieser Spalte. |

Eine Zeile pro Lease-Identität und Adresse; Renew aktualisiert die bestehende Zeile. Neueste Bestätigung zuerst. Abgelaufene beziehungsweise freigegebene Einträge bleiben als Sitzungsverlauf sichtbar; eine erneut vergebene IP kann deshalb auch in einer historischen Zeile vorkommen. Im Leasingkern wird vorrangig die Client-ID, ersatzweise Hardwaretyp plus Hardwareadresse verwendet, nicht allein die sichtbare MAC-Adresse.

Ein zentraler UI-Timer aktualisiert die Restzeiten; kein Timer pro Tabellenzeile. Die Datenquelle ist virtualisiert. Zeitstempel und Dauer werden numerisch und sprachunabhängig sortiert, nicht als formatierte Zeichenketten.

### Zustände

- **Gestoppt/Bereit:** Felder und Optionseditor bearbeitbar. Start erst nach erfolgreicher Validierung.
- **Startet:** Konfiguration einfrieren, Adapter und Transport prüfen, Socket öffnen. Doppelklick startet keine zweite Instanz.
- **Läuft:** Sichtbarer aktiver Adapter, Stoppen verfügbar; Konfigurationsänderungen gesperrt. Optionseditor bleibt lesbar.
- **Stoppt:** Empfang und Verarbeitung abbrechen, angefangene Arbeit kontrolliert abschließen und Ressourcen freigeben.
- **Fehler:** Verständlicher Grund und mögliche Behebung; Formulardaten bleiben erhalten. Kein irreführender „Läuft“-Status nach Socketfehler oder Adapterverlust.

Beim Stoppen bleiben bereits an Clients vergebene Leases bis zu ihrem Ablauf gültig; Stoppen zieht keine Adressen auf Geräten zurück. Die Tabelle und Countdown-Anzeige bleiben erhalten. Beim erneuten Start werden noch gültige Belegungen berücksichtigt. Beim Schließen wird der Server gestoppt; es gibt keine versteckte Tray-Fortsetzung.

## 3. Validierung und Startdiagnose

- IPv4-Werte strikt als vier dezimale Oktette prüfen; keine verkürzte Schreibweise akzeptieren.
- Start ≤ Stop, beide im gleichen durch die Maske bestimmten Subnetz; Netzwerk- und Broadcastadresse nicht vergeben. /31 und /32 sind für den klassischen Pool ausgeschlossen.
- Gewählte Serveradresse muss auf dem Adapter vorhanden und im Zielsubnetz sein. Netzwerkmaske und Adapterpräfix müssen zur gewünschten Konfiguration passen; andernfalls gezielte Fehlermeldung.
- Serveradresse, konfigurierte Gateway-Adresse und lokale Infrastrukturadressen nicht als Pooladressen vergeben. Liegen Gateway oder ein DNS-Server im Pool, verlangt die erste Version eine Anpassung des Pools. Externe DNS-Adressen dürfen außerhalb des Subnetzes liegen; der Gateway muss im lokalen Subnetz liegen.
- Gateway und DNS sind für ein isoliertes Inbetriebnahmenetz optional; ihre Verteilung erzeugt keine entsprechenden Dienste oder Internetverbindung.
- Vor Start: Adapter verfügbar, IPv4-Konfiguration geeignet, UDP 67 exklusiv nutzbar, vorhandene Leases zum Scope kompatibel. Adapterverlust stoppt den Server; kein automatischer Wechsel auf WLAN oder einen anderen Adapter.
- Eine Firewallfreigabe ist konkret auf Programm, UDP-Ports und ausgewählten Adapter auszurichten. Status und Fehler erklären eine fehlende Freigabe. Keine globale Abschaltung der Firewall. Erhöhte Rechte nur für tatsächlich erforderliche administrative Aktionen vorsehen; UDP 67 allein ist unter Windows kein pauschaler Grund für einen dauerhaft erhöhten Prozess.
- Bestehende DHCP-Server können Konflikte verursachen. Ein beobachteter fremder OFFER/ACK wird gemeldet. Ein ausbleibender Fund beweist kein konfliktfreies Netz. Kein automatisches Abschalten von ICS, Windows-DHCP oder anderen Diensten.

## 4. Editor für weitere DHCP-Optionen

Öffnung ausschließlich über das Menü; die Hauptansicht erhält keine zusätzlichen Optionsfelder. Umsetzung als eigener Optionsdialog beziehungsweise Editoransicht. Liste mit Code, Bezeichnung, Typ, Wert und Aktivierung; neue Einträge über einen auswählbaren Optionskatalog. Die Auswahl zeigt Beschreibung, Richtung und RFC-Referenz. Bearbeitbare Werte werden vor dem Übernehmen geprüft.

Der Katalog umfasst die registrierten **DHCPv4**-Optionen des IANA-Verzeichnisses als versionierten lokalen Datenbestand. Die Code-Liste allein reicht nicht zur Implementierung der jeweiligen Datenstruktur: Metadaten und Encoder werden anhand der referenzierten RFCs gepflegt. Veraltete, unzugewiesene und reservierte Codes werden entsprechend gekennzeichnet. Aktualisierungen erfolgen bewusst und versioniert; die Anwendung benötigt zum Start keine Internetverbindung.

### Typen und Grenzen

- Typisierte Eingaben für IPv4, IPv4-Listen, Text, Boolean, Ganzzahlen und Binärdaten. Zahlenbreite, Byte-Reihenfolge und Textkodierung pro Option definieren.
- Beispiele: 15 Domainname, 42 NTP-Server, 66 TFTP-Servername und 67 Bootdateiname.
- Strukturierte Optionen wie 119 Domain Search, 121 Classless Static Routes oder 43/125 Vendor-Daten brauchen eigene RFC-konforme Encoder; bis zur jeweiligen Freigabe ist ein ausdrücklich als solcher gekennzeichneter Hex-Editor vorgesehen. Rohdatenunterstützung ist keine Zusage vollständiger semantischer Unterstützung aller RFC-Funktionen.
- Option 43 und 125 mit ihren untergeordneten Herstellerfeldern nicht als unabhängige Top-Level-Codes behandeln. Registrierung kann durch Kontext beziehungsweise Enterprise-ID ergänzt werden.
- Eine generische Rohdatenoption ermöglicht auch spezialisierte Geräteoptionen. Hexsyntax und Länge werden geprüft; gültige Bedeutung bleibt ohne spezialisierten Encoder Verantwortung des eingegebenen Formats.
- Optionen 1, 3, 6 und 51 stammen ausschließlich aus dem Hauptformular. Kein zweiter Eintrag im Editor.
- Protokollfelder wie Message Type 53 und Server Identifier 54 sowie Pad/End werden vom Server erzeugt. Clientseitige Angaben wie Requested IP 50 und Parameter Request List 55 sind keine frei konfigurierbaren Serverantworten. Solche Einträge bleiben im Gesamtkatalog sichtbar, sind aber mit Erklärung nicht als zusätzliche Sendeoption auswählbar.
- Regeln für „auf Clientanfrage senden“ und ausdrücklich konfigurierte „immer senden“-Ausnahmen definieren. Pflichtoptionen und vorgesehene Nachrichtenarten bleiben protokollgesteuert. T1/T2 müssen zur Lease passen und dürfen, sofern bearbeitbar, nur widerspruchsfrei übernommen werden.
- Kein starrer globaler Grenzwert von 255 Bytes pro logischem Optionswert: TLV-Grenzen, RFC-3396-Verkettung, Client-Maximalgröße und gegebenenfalls Option Overload korrekt berücksichtigen. Zu große Antwort nicht still kürzen.

Für die erste produktive Version: vollständiger Katalog, sichere Behandlung verwalteter Codes, generischer Hexpfad und typisierte häufige Optionen. Weitere komfortable Spezialeditoren können anschließend folgen. Volle semantische Unterstützung jeder registrierten Erweiterung wäre ein eigener, wesentlich größerer Umfang.

## 5. Technische Struktur

Empfehlung: C# mit .NET 10 LTS, WinUI 3 und einer unterstützten stabilen Windows-App-SDK-Version. Beim Projektstart genaue kompatible Paketversionen festlegen und sperren; keine Preview-Abhängigkeit für die Lease-Tabelle. Ziel zunächst Windows 11 x64. ARM64 kann später als zusätzlicher Build folgen. Ein selbstenthaltenes, entpackbares Desktop-Paket ist für das mobile Inbetriebnahmewerkzeug sinnvoll; Größe und native Abhängigkeiten beim Deployment prüfen.

MVVM mit CommunityToolkit.Mvvm, Dependency Injection und strukturiertem Logging. Für die fünf festen Spalten genügt eine virtualisierte ListView mit gemeinsamem Spaltenraster und Tabellenkopf; Tabellenzugänglichkeit über AutomationPeers prüfen. Ein externes DataGrid ist erst bei zusätzlichem Bedarf erforderlich.

```text
KubDHCP.sln
src/
  KubDHCP.App/                 WinUI/XAML, ViewModels, Dispatcher, Composition Root
  KubDHCP.Core/                DHCP-Pakete, Zustandsautomat, Leases, Optionsmodell
  KubDHCP.Infrastructure/      Windows-UDP-Transport, Adapter, JSON, IEEE-Daten, Log
tests/
  KubDHCP.Core.Tests/          Parser, Optionen, Lease-/Zustandslogik
  KubDHCP.IntegrationTests/    Transport und Mehradapter-Szenarien im Testnetz
docs/
  Umsetzungsplan.md
```

Der Kern kennt keine WinUI-Typen. `IDhcpTransport`, `ILeaseStore`, `IOuiLookup` und `TimeProvider` trennen Transport, Persistenz, Herstellerzuordnung und Uhr. Ein `DhcpServerController` steuert Start/Stop und publiziert Ereignisse. Der UI-Thread erhält zusammengefasste Updates über `DispatcherQueue`; keine Socketarbeit im UI-Thread.

### Modelle

- `ServerConfiguration`: Adapter-ID, Server-IPv4, Poolgrenzen, Subnetzmaske, optionaler Gateway, geordnete DNS-Liste, Lease-Dauer, Optionskonfiguration.
- `LeaseRecord`: Scope-ID, Client-Key, Hardwareadresse, IP, erste/letzte Bestätigung, Ablauf UTC und Zustand.
- `DhcpOptionDefinition`: Code, Kontext, Name, Richtung, Kodierung, Längenregeln, Referenz, Systemverwaltung.
- `DhcpOptionValue`: Definition, Aktivierung, typisierter Wert oder Rohbytes, Senderegel.
- `ServerState`: Stopped, Starting, Running, Stopping, Faulted.

## 6. DHCP-Kern und Windows-Netzwerk

### Früher Machbarkeitsnachweis

Zuerst einen kleinen Transportprototyp im isolierten Testnetz erstellen. Die kritische Anforderung ist, DHCP-Broadcasts auf einem mehrfach angebundenen Windows-PC korrekt zu empfangen und ausschließlich auf dem gewählten Adapter zu beantworten.

Ein schlichtes Binden an die Adapter-IP darf nicht ungeprüft als ausreichend gelten. Geplanter Ansatz: UDP-Socket mit Paketinformationen (`IP_PKTINFO` / `ReceiveMessageFromAsync`) und Filter auf den tatsächlichen Eingangs-Interface-Index. Ausgehenden Adapter und Quell-IP gezielt festlegen. Falls die verwaltete API diese Kontrolle nicht ausreichend erlaubt, kleinen Winsock-Interop-Adapter mit WSARecvMsg/WSASendMsg vorsehen. `IP_UNICAST_IF` wählt den ausgehenden Pfad und ersetzt keine Eingangsfilterung. Globales Listening darf keinesfalls Antworten auf andere Adapter verursachen.

Besonders früh testen: initiale Clients mit 0.0.0.0, Broadcast-Flag gesetzt oder nicht gesetzt, sowie spätere Unicast-Renewals. Initiales Unicast an einen noch unkonfigurierten Client kann L2-/Neighbor-Unterstützung erfordern. Ob die normale UDP-Implementierung genügt oder ein enger nativer Zusatz erforderlich ist, wird im Prototyp anhand echter Clients entschieden. Eine Lösung, die nur an Clients mit Broadcast-Flag funktioniert, erfüllt die Freigabe nicht. Keine vorschnelle Abhängigkeit von Npcap oder einem Treiber einführen.

### Protokoll und Vergabe

Grundlage sind RFC 2131 und 2132 einschließlich relevanter Aktualisierungen. Serverport UDP 67, Clientport UDP 68. Implementieren: DISCOVER/OFFER, REQUEST/ACK/NAK, Renew, Rebind, RELEASE, DECLINE und INFORM. SELECTING, INIT-REBOOT und RENEWING dürfen nicht dieselben Prüfregeln erhalten. REQUEST an eine fremde Server-ID wird nicht als eigene Vergabe bestätigt. NAK nur in den hierfür vorgesehenen Situationen; fremde Scopes nicht blind zurückweisen.

Pakete mit ungültiger Länge, Cookie oder TLV-Struktur verwerfen und begrenzt protokollieren. Transaktions-ID und Clientidentität korrekt übernehmen; Client-ID-Verhalten einschließlich relevanter RFC-Aktualisierungen berücksichtigen. Parameter Request List, Optionengrößen, vorgeschriebene Reihenfolge und nachrichtenspezifische Pflichtoptionen prüfen.

Angebote erhalten eine kurze Reservierung mit Ablauf. Adressauswahl und Zustandsänderungen sind atomar, damit gleichzeitige DISCOVER/REQUEST keine Doppelvergabe erzeugen. Leases werden vor erfolgreicher Vergabe dauerhaft gesichert; Versandfehler werden nachvollziehbar behandelt. DHCPDECLINE sperrt eine Adresse zeitweise. Eine vorgeschaltete Konfliktprüfung, vorzugsweise anhand geeigneter ARP-/Neighbor-Verfahren, und ihre Grenzen werden im Netzprototyp geklärt; ein erfolgloser Ping allein beweist keine freie Adresse. T1/T2 konsistent zur gewählten Lease berechnen.

### Datenhaltung und Neustart

Einstellungen und Optionen als versionierte JSON-Dateien unter dem lokalen Benutzerprofil. Lease-Zustand separat, atomar und pro Scope speichern; Backup beziehungsweise Erkennung beschädigter Dateien vorsehen. Noch nicht abgelaufene Leases nach Neustart wieder belegen. Bei beschädigtem oder zum Scope unpassendem Lease-Zustand keine stillschweigende Neuvergabe. Konfiguration bleibt bearbeitbar, Start verlangt eine nachvollziehbare Auflösung.

Kein Lease-Ende aus einem UI-Countdown ableiten: maßgeblich ist die gespeicherte Ablaufzeit. UTC für Persistenz, monotone Zeitmessung während einer Sitzung soweit sinnvoll; Systemzeitänderungen explizit behandeln.

## 7. Herstellerzuordnung

IEEE-MA-L-, MA-M- und MA-S-Daten lokal vorhalten, möglichst ergänzt um historische IAB-Zuordnungen. Längstes passendes Präfix gewinnt: 36 vor 28 vor 24 Bit. Lokal administrierte und zufällige Adressen nicht einem vermeintlichen Hersteller zuordnen. Die Anzeige bezeichnet den registrierten Adressblockinhaber; daraus lässt sich das konkrete Gerätemodell nicht sicher ableiten.

Ein eingebauter Datenstand erlaubt Offlinebetrieb. Eine gesonderte Aktion **OUI-Datenbank aktualisieren** kann später Dateien laden, prüfen und atomar austauschen. Quellen, Datenstand und Nutzungs-/Weitergabebedingungen der verwendeten IEEE-Daten vor Auslieferung dokumentieren. Die UI wartet nie auf einen Online-Lookup.

## 8. Umsetzung in prüfbaren Schritten

| Schritt | Ergebnis | Abnahme |
| --- | --- | --- |
| 1. Netzwerkprototyp | Kopflose Paketverarbeitung mit festem Testpool auf einem gewählten Adapter. | Mindestens zwei aktive Adapter; Vergabe und Renew am Zieladapter, nachweislich keine Antwort am anderen Adapter. Clients mit beiden Broadcast-Flag-Zuständen. |
| 2. WinUI-Grundgerüst | Hauptfenster, MVVM, Formular, Status und Tabelle mit Demodaten. | Alle geforderten Felder und fünf Spalten vorhanden; Tastaturbedienung, hell/dunkel und 125–200 % Skalierung lesbar. |
| 3. Leasingkern | Zustandsautomat, atomare Poolvergabe, Ablauf, Release/Decline, Persistenz. | Wiederholte Requests, parallele Clients, voller Pool und Neustart ohne Doppelvergabe. |
| 4. Verbindung mit UI | Echtzeitzeilen, gesperrte Laufzeitkonfiguration, Start/Stop und Diagnose. | Reale Geräte beziehen Adressen; keine UI-Blockade; Stoppen und Adapterverlust schließen die Sockets. |
| 5. Optionseditor und OUI | Vollständiger Katalog, typisierte Standardoptionen, Hexpfad und Offlinezuordnung. | Paketmitschnitt zeigt korrekte Bytes; ungültige/widersprüchliche Optionen werden verhindert; Präfixvorrang funktioniert. |
| 6. Paket und Praxisabnahme | Entpackbare Releaseausgabe, lokale Einstellungen, Diagnoseanleitung. | Frisches Windows-Testsystem ohne Entwicklungsumgebung; wiederholte Inbetriebnahme mit mehreren realen Gerätetypen. |

## 9. Prüfstrategie

Automatisierte Kerntests für Paketgrenzen, defekte TLVs, Network Byte Order, Subnetzberechnungen, Poolerschöpfung, Reservierungsablauf, Renew/Rebind und DECLINE-Quarantäne. Zeitabhängige Tests mit kontrollierter Uhr; konkurrierende Vergaben müssen eindeutige IPs ergeben. Für Optionen: Referenzbytes für Listen, lange Werte, strukturierte Kodierungen und maximale Antwortgrößen. Für OUI: 24/28/36-Bit-Vorrang und lokale MACs.

Windows-Integrationstests im isolierten Netz mit Paketmitschnitt: DISCOVER von 0.0.0.0, Flagvarianten, Unicast-Renew, mehrere NICs, Adapterziehen, belegter Port, Firewallblock und Wiederanlauf mit aktiven Leases. Zusätzlich echte Zielgeräte unterschiedlicher Hersteller testen; reine PC-Clients decken deren Eigenheiten nicht ab.

Das Oberflächenmockup simuliert lediglich Zustand, Daten und Bedienung. Es öffnet keine Ports und ersetzt diese Integrationstests nicht.

## 10. Offene Entscheidungen vor Implementierungsstart

Empfohlene Ausgangswerte sind Windows 11 x64, genau ein aktiver IPv4-Scope, manuell konfigurierter Adapter, persistierte Leases und selbstenthaltene Desktop-Verteilung. Falls Windows 10, gleichzeitige Adapter, feste Reservierungen oder vollständige semantische Umsetzung sämtlicher DHCP-Erweiterungen benötigt werden, erweitert das den Umfang. Der Transportprototyp ist der Freigabepunkt für die endgültige Windows-Socketstrategie und eventuell nötige administrative Hilfsaktionen.

## 11. Primärquellen

- [RFC 2131 – Dynamic Host Configuration Protocol](https://www.rfc-editor.org/info/rfc2131/) – Zustände, Nachrichten und Zustellung; Aktualisierungen und Errata mit berücksichtigen.
- [RFC 2132 – DHCP Options and BOOTP Vendor Extensions](https://www.rfc-editor.org/rfc/rfc2132.html) – grundlegende Optionskodierungen.
- [IANA – BOOTP/DHCP Parameters](https://www.iana.org/assignments/bootp-dhcp-parameters) – Optionskatalog und Verweise auf weiterführende RFCs.
- [RFC 3396 – Encoding Long Options](https://www.rfc-editor.org/rfc/rfc3396.html) – Verkettung langer Optionswerte.
- [Microsoft – IPPROTO_IP socket options](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options) – Interface-Auswahl und Paketinformationen.
- [Microsoft – Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels) – stabile gegenüber experimentellen APIs.
- [Microsoft – .NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview) – LTS-Basis.
- [IEEE Registration Authority](https://standards.ieee.org/products-programs/regauth/) – öffentliche MA-L-, MA-M-, MA-S- und IAB-Verzeichnisse.
