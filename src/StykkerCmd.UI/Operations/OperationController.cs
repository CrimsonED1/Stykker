using Avalonia.Threading;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Operations;
using StykkerCmd.UI.Dialogs;

namespace StykkerCmd.UI.Operations;

// Führt einen Auftrag aus: Fortschritt im Hauptfenster, Konfliktfragen und Abbruch.
// Der Lauf selbst läuft im Hintergrund; Dialoge und Fortschritt kommen auf den UI-Thread zurück.
public sealed class OperationController(IPlatformServices services, MainWindow owner)
{
    public async Task<OperationResult> RunAsync(OperationRequest request, string heading)
    {
        using var cancellation = new CancellationTokenSource();
        owner.ShowProgress(heading, cancellation.Cancel);

        // Progress<T> meldet auf dem Synchronisationskontext, der hier (auf dem UI-Thread) erfasst wird.
        var progress = new Progress<OperationProgress>(owner.UpdateProgress);

        ConflictHandler onConflict = conflict => Dispatcher.UIThread.InvokeAsync<ConflictAnswer>(async () =>
        {
            var answer = await ConflictDialog.ShowAsync(owner, conflict) ?? new ConflictAnswer(ConflictChoice.Cancel);
            owner.RestoreFocus(); // der Vordergrund wandert nach dem Schließen sonst an ein anderes Programm
            return answer;
        });

        var runner = new OperationRunner(services.FileSystem, services.Trash);
        try
        {
            return await Task.Run(() => runner.RunAsync(request, onConflict, progress, cancellation.Token));
        }
        finally
        {
            owner.HideProgress();
        }
    }
}
