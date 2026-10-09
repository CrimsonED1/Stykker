using Microsoft.AspNetCore.Components.Server.Circuits;
using StykkerLlm.Core;

namespace StykkerLlm.Server;

// Eine offene Webseite (Blazor-Verbindung, auch die im Fenster StykkerUI und auf dem Handy) hält den Server am Leben
public sealed class HoldCircuits(EngineHost host) : CircuitHandler
{
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct) { host.Holds.CircuitUp(); return Task.CompletedTask; }
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct) { host.Holds.CircuitDown(); return Task.CompletedTask; }
}

// Warnungen und Fehler von ASP.NET Core ins Protokoll des Servers (StykkerLLM-Server.log)
public sealed class AppLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = $"{logLevel} {category}: {formatter(state, exception)}";
            if (exception != null) text += $"{Environment.NewLine}{exception}";
            AppLog.Write(text);
        }
    }
}
