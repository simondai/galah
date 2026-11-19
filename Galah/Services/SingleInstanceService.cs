using System.IO.Pipes;
using System.Threading;
using System.Windows.Threading;

namespace Galah.Services;

/// <summary>
/// Single-instance gate (design §1.1 S2, spec §5.1-1): a named mutex decides the primary process;
/// every other start connects to the named pipe, sends <c>TOGGLE</c> and exits immediately.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    /// <summary>Mutex name (session-local).</summary>
    public const string MutexName = @"Local\Galah.SingleInstance.v1";

    /// <summary>Named pipe used for the TOGGLE command.</summary>
    public const string PipeName = "Galah.Toggle.v1";

    /// <summary>Wire command a secondary process sends.</summary>
    public const string ToggleCommand = "TOGGLE";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Task? _listener;

    /// <summary>True when this process owns the mutex and therefore hosts the UI.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>Raised on the UI thread when a second process asked to toggle the panel.</summary>
    public event Action? RemoteToggle;

    /// <summary>Try to become the primary instance.</summary>
    /// <returns>True when this process is the only one (mutex acquired).</returns>
    public bool TryBecomePrimary()
    {
        var createdNew = false;
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                IsPrimary = false;
                return false;
            }
            IsPrimary = true;
            return true;
        }
        catch (AbandonedMutexException)
        {
            // A crashed primary leaves the mutex abandoned; take ownership and continue normally.
            IsPrimary = true;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            IsPrimary = true;
            return true;
        }
    }

    /// <summary>
    /// Start the pipe server. Messages are marshalled to <paramref name="dispatcher"/> so the panel
    /// is toggled on the UI thread.
    /// </summary>
    /// <param name="dispatcher">UI dispatcher of the primary process.</param>
    public void StartListening(Dispatcher dispatcher)
    {
        if (!IsPrimary || _listener is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _listener = Task.Run(() => ListenLoopAsync(dispatcher, token), token);
    }

    private async Task ListenLoopAsync(Dispatcher dispatcher, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (string.Equals(line?.Trim(), ToggleCommand, StringComparison.OrdinalIgnoreCase))
                    _ = dispatcher.BeginInvoke(() => RemoteToggle?.Invoke()); // fire-and-forget marshal: the pipe loop must not block on the UI thread
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }   // the disposal race: token outlives the CTS
            catch (IOException) { /* transient pipe error: retry */ }
            catch (UnauthorizedAccessException) { break; }
            finally
            {
                try { server?.Dispose(); } catch (IOException) { }
            }
            if (token.IsCancellationRequested) break;
            await Task.Delay(30, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Send TOGGLE to the running primary instance.
    /// </summary>
    /// <param name="timeoutMs">Connection timeout; the caller exits either way.</param>
    /// <returns>True when the primary instance received the command.</returns>
    public static bool NotifyPrimary(int timeoutMs = 800)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(ToggleCommand + "\n");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Stop the pipe server and release the mutex.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Wait(200); } catch (Exception) { /* shutdown race is harmless */ }
        // Deliberately not disposing the CTS here: the listener may still be awaiting on its
        // token, and disposing while an await is registered throws ObjectDisposedException on
        // the way out. The process is ending; the GC reclaims it.
        if (IsPrimary) { try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex?.Dispose();
        _mutex = null;
    }
}
