using System.Windows;
using System.Windows.Automation;
using Microsoft.Terminal.Wpf;
using GabCode.Windows.Terminal.Conpty;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class TerminalHostedSession : IAsyncDisposable
{
    private readonly ConptyTerminalConnection connection;
    private readonly ITerminalMultilinePasteConfirmationService pasteConfirmation = new TerminalMultilinePasteConfirmationService();
    private readonly TerminalSafePasteController safePaste;
    private TerminalNativePasteInterceptor? nativePasteInterceptor;
    private Task? startTask;
    private Task? closeTask;
    private bool controlConnected;

    internal TerminalHostedSession(
        TerminalSessionKind kind,
        string workingDirectory,
        TerminalProfileResolution profile)
    {
        Kind = kind;
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Control = new TerminalControl
        {
            AutoResize = true,
            Focusable = true,
        };
        AutomationProperties.SetName(Control, $"{kind.GetDisplayName()} content");
        connection = new ConptyTerminalConnection(new TerminalProcessOptions(
            profile.ExecutablePath,
            profile.Arguments,
            workingDirectory,
            TimeSpan.FromSeconds(2),
            profile.EnvironmentOverrides));
        connection.StateChanged += Connection_StateChanged;
        safePaste = new TerminalSafePasteController(pasteConfirmation);
    }

    internal event EventHandler<TerminalSessionState>? StateChanged;

    internal TerminalSessionKind Kind { get; }

    internal TerminalProfileResolution Profile { get; }

    internal TerminalControl Control { get; }

    internal ConptyTerminalConnection Connection => connection;

    internal TerminalSessionState State => connection.State;

    internal int? ProcessId => connection.ProcessId;

    internal Exception? Failure => connection.Failure;

    internal bool IsActive => State is TerminalSessionState.Starting or TerminalSessionState.Running or TerminalSessionState.Closing;

    internal Task StartAsync() => startTask ??= StartCoreAsync();

    internal Task CloseAsync() => closeTask ??= CloseCoreAsync();

    internal ValueTask WriteInputAsync(string data, CancellationToken cancellationToken = default) =>
        connection.WriteInputAsync(data, cancellationToken);

    internal Task<int> WaitForExitAsync() => connection.WaitForExitAsync();

    internal Task ResizeAsync(uint rows, uint columns, CancellationToken cancellationToken = default) =>
        Control.ResizeAsync(rows, columns, cancellationToken);

    internal string GetSelectedText() => Control.GetSelectedText();

    internal void Focus() => _ = Control.Focus();

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }

    private async Task StartCoreAsync()
    {
        await WaitUntilLoadedAsync(Control);
        Control.SetTheme(TerminalThemeFactory.CreateDefault(), "Cascadia Mono", 12);
        Control.Connection = connection;
        controlConnected = true;
        nativePasteInterceptor = new TerminalNativePasteInterceptor(
            Control,
            PasteClipboardSnapshot,
            ShowClipboardReadFailure,
            Control.GetSelectedText);
        nativePasteInterceptor.Attach();
        await connection.StartAsync();
    }

    private async Task CloseCoreAsync()
    {
        nativePasteInterceptor?.Dispose();
        nativePasteInterceptor = null;
        if (controlConnected)
        {
            Control.Connection = null!;
            controlConnected = false;
        }

        await connection.CloseAsync();
        connection.StateChanged -= Connection_StateChanged;
    }

    private void PasteClipboardSnapshot(string clipboardSnapshot)
    {
        var owner = Window.GetWindow(Control);
        if (connection.State != TerminalSessionState.Running)
        {
            pasteConfirmation.ShowTerminalUnavailable(owner);
            return;
        }

        try
        {
            safePaste.Paste(owner, clipboardSnapshot, connection.WriteInput);
        }
        catch (InvalidOperationException)
        {
            pasteConfirmation.ShowTerminalUnavailable(owner);
        }
    }

    private void ShowClipboardReadFailure() => pasteConfirmation.ShowClipboardReadFailure(Window.GetWindow(Control));

    private void Connection_StateChanged(object? sender, TerminalSessionState state) =>
        StateChanged?.Invoke(this, state);

    private static Task WaitUntilLoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            element.Loaded -= loaded;
            completion.TrySetResult();
        };
        element.Loaded += loaded;
        return completion.Task;
    }
}
