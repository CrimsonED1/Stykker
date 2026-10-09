#!/usr/bin/env bash
# Linux-Prüfung für StykkerCMD (Meilenstein 1 aus PLAN.md): SDK, Build, Tests, Start.
# Aufruf aus dem Repo-Wurzelordner: bash scripts/linux-smoke.sh
set -euo pipefail

cd "$(dirname "$0")/.."

echo "== .NET-SDK =="
dotnet --version

echo "== Build =="
dotnet build StykkerCmd.slnx -nologo

echo "== Tests (Linux-Tests laufen, Windows-Tests werden übersprungen) =="
dotnet test StykkerCmd.slnx -nologo

echo "== Start: die Oberfläche soll 15 Sekunden ohne Absturz laufen =="
set +e
timeout 15 dotnet run --project src/StykkerCmd.UI -f net10.0 -- --left "$HOME" --right "$HOME"
rc=$?
set -e
if [ "$rc" -eq 124 ]; then
    echo "Start: OK, die Oberfläche lief 15 Sekunden ohne Absturz."
else
    echo "Start: FEHLGESCHLAGEN (Exit-Code $rc). Meist fehlt eine Anzeige (X11 oder WSLg) oder eine Bibliothek."
    exit 1
fi

echo "== Fertig. Themenwechsel (F9), Tab und Dateiaktionen bitte von Hand prüfen. =="
