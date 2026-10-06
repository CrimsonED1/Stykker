# winget-Manifest für StykkerLLM

Das Manifest ist als **Template** hinterlegt (`CrimsonED1.StykkerLLM.template.yaml`) und wird
von `build/make-winget.ps1` mit Version, Installer-URL und dem echten SHA256 des Zip-Installers gefüllt.
**Von hier aus wird nie automatisch veröffentlicht** — der PR an `microsoft/winget-pkgs` ist ein manueller Schritt.

## Ablauf (nachdem ein Release mit Zip-Installer existiert)

1. SHA256 des selbstenthaltenden Zips holen (es im Release herunterladen oder `SHA256SUMS.txt` lesen):

   ```powershell
   (Get-FileHash .\StykkerLLM-0.1.0-win-x64.zip -Algorithm SHA256).Hash.ToLower()
   ```

2. Manifest erzeugen:

   ```powershell
   powershell -ExecutionPolicy Bypass -File build\make-winget.ps1 -Version 0.1.0 -Sha256 <hash>
   ```

   Ergebnis: `build/out/winget/manifests/C/CrimsonED1/StykkerLLM/0.1.0/CrimsonED1.StykkerLLM.yaml`

3. Prüfen (optional, mit winget-CLI):

   ```powershell
   winget manifest validate build\out\winget\manifests\C\CrimsonED1\StykkerLLM\0.1.0\CrimsonED1.StykkerLLM.yaml
   ```

4. Fork von `microsoft/winget-pkgs` erstellen und den Ordner
   `manifests/c/CrimsonED1/StykkerLLM/<version>/` (kleingeschrieben) mit genau dieser einen
   YAML-Datei committen. PR-Titel-Form: `New version: CrimsonED1.StykkerLLM version 0.1.0`.
   PR-Vorlage und Prüfregeln: https://github.com/microsoft/winget-pkgs

## Hinweise

- `NestedInstallerType: portable` + Alias `stykker-llm`: nach `winget install` steht der
  Monitor als Kommandozeilenbefehl bereit; die exe landet im winget-Portable-Paketordner.
- Der Installer zeigt auf das Release-Zip mit dem App-Ordner `StykkerLLM\` (selbstenthaltend, kein .NET nötig,
  `NestedInstallerFiles` zeigt auf `StykkerLLM\StykkerUI.exe`) und muss unter der
  Release-URL `https://github.com/CrimsonED1/Stykker-LLM/releases/...` erreichbar sein.
- Vorabversionen (z. B. `0.1.0-beta`) sind für winget-pkgs ungeeignet — nur stabile `x.y.z`-Versionen einreichen.
- Jede neue Version = ein neuer PR mit einem neuen Ordner pro Version.