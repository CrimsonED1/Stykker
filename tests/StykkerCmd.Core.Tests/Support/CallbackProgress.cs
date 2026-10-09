using StykkerCmd.Core.Operations;

namespace StykkerCmd.Core.Tests.Support;

// Synchroner Fortschritt für Tests. Progress<T> würde über den Synchronisationskontext laufen.
public sealed class CallbackProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
{
    public void Report(OperationProgress value) => action(value);
}
