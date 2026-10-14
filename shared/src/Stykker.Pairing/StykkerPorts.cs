namespace Stykker.Pairing;

// Die festen Ports der Suite (AGENTS.md, Abschnitt „Ports and files“; docs/plan-stykkernode.md). Jeder Wert steht hier
// genau einmal. HUD, SYS, LLM und die Zentrale lesen ihn von dort. Die Datei liegt in Stykker.Pairing, weil alle drei
// Werkzeuge sie ohnehin brauchen und StykkerLLM nicht gegen Stykker.Shared gebaut wird.
// Feste Ports seit 2026-10-14: die Zentrale 17400, StykkerLLM 17401, StykkerSYS 17402; StykkerCMD ist nur reserviert
// (17403, noch nicht gebaut). Fremde Dienste (Weiterleitung) beginnen ab 17450; ihre Beispiele stehen in den Docs.
public static class StykkerPorts
{
    public const int Hub = 17400;   // die Zentrale (StykkerHUD-Server)
    public const int Llm = 17401;   // StykkerLLM: Web und API
    public const int Sys = 17402;   // StykkerSYS
}
