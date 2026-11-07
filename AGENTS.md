# Projektregeln

Bei jeder neuen, vom Benutzer angeforderten Änderungsiteration die Version einmal mit `./scripts/Bump-Version.ps1` erhöhen. Fehlerkorrekturen und erneute Builds innerhalb derselben Iteration erhöhen die Version nicht erneut. Die aktuelle Iteration beginnt bei v1.0.2. Nach v1.0.100 folgt v1.1.0; die Patchnummer wird dann wieder ab 0 hochgezählt. Die Version in der csproj ist die gemeinsame Quelle für EXE und Anzeige.

Die portable WinUI-EXE muss `KubDHCP.exe` heißen. Versionsnummern nur im übergeordneten Ordner verwenden. Eine ausgelieferte EXE niemals für die Versionierung umbenennen. Der Build muss neben Kerntests und Transporttest auch den echten WinUI-Fensterstart prüfen.

Der Build muss zusätzlich die veröffentlichte TUI mit --tui-test prüfen: native Konsoleneingabe/-ausgabe, Bedienung und isolierter Loopbackverkehr. TUI und WinUI verwenden dieselbe Version und dieselbe DHCP-Implementierung.

`--tui` muss in der aufrufenden PowerShell-/SSH-Terminalsitzung laufen. Im produktiven TUI-Start niemals AllocConsole oder einen grafischen Dialog öffnen. Die Shell muss bis zum Beenden warten. Der Build prüft dies mit scripts/Test-TuiSession.ps1 in einer PowerShell/ConPTY-Sitzung. Änderungen an der TUI dürfen das WinUI-Layout nicht verändern.
