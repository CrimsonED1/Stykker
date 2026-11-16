using System.Text.Json.Serialization;
using StykkerFirewall.Core.Rules;

namespace StykkerFirewall.Core.Ipc;

// Protokoll zwischen Dienst (läuft als SYSTEM) und Oberfläche (läuft als angemeldeter Nutzer) über eine Named Pipe.
// Eine Nachricht je Zeile, JSON, das Feld "type" bestimmt die Art.

public sealed record PendingPrompt(Guid Id, ConnectionAttempt Attempt, int Repeats);

public sealed record ActivityEntry(ConnectionAttempt Attempt, GateVerdict Verdict);

public sealed record FirewallSnapshot(
    BackendStatus Status,
    IReadOnlyList<FirewallRule> Rules,
    IReadOnlyList<PendingPrompt> Pending,
    IReadOnlyList<ActivityEntry> Recent,
    DateTimeOffset? ModeRevertAt = null);   // gesetzt: Ask ist noch nicht bestätigt und fällt zu diesem Zeitpunkt zurück

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
// Oberfläche → Dienst
[JsonDerivedType(typeof(GetSnapshot), "get-snapshot")]
[JsonDerivedType(typeof(Decide), "decide")]
[JsonDerivedType(typeof(DeleteRule), "delete-rule")]
[JsonDerivedType(typeof(SetMode), "set-mode")]
[JsonDerivedType(typeof(ConfirmMode), "confirm-mode")]
// Dienst → Oberfläche
[JsonDerivedType(typeof(Snapshot), "snapshot")]
[JsonDerivedType(typeof(PromptRaised), "prompt-raised")]
[JsonDerivedType(typeof(ActivityAdded), "activity-added")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record IpcMessage;

public sealed record GetSnapshot : IpcMessage;

public sealed record Decide(Guid PromptId, PromptDecision Decision) : IpcMessage;

public sealed record DeleteRule(Guid RuleId) : IpcMessage;

public sealed record SetMode(ProtectionMode Mode) : IpcMessage;

public sealed record ConfirmMode : IpcMessage;

public sealed record Snapshot(FirewallSnapshot State) : IpcMessage;

public sealed record PromptRaised(PendingPrompt Prompt) : IpcMessage;

public sealed record ActivityAdded(ActivityEntry Entry) : IpcMessage;

public sealed record ErrorMessage(string Text) : IpcMessage;

public static class PipeNames
{
    public const string Default = "StykkerFirewall";
}
