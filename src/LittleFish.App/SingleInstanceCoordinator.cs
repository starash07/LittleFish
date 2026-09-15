using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LittleFish.App;

internal sealed record LaunchRequest(string? FilePath)
{
    public static LaunchRequest FromArguments(string[] arguments)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], "--seas-host-pipe", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            if (arguments[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var path = arguments[i].Trim('"');
            try
            {
                return new LaunchRequest(Path.GetFullPath(path));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new LaunchRequest(path);
            }
        }
        return new LaunchRequest(FilePath: null);
    }
}

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _ownsMutex;

    public SingleInstanceCoordinator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _pipeName = $"LittleFish.Standalone.{identity.User!.Value}.{Process.GetCurrentProcess().SessionId}";
        _mutex = new Mutex(false, $"Local\\{_pipeName}");
    }

    public bool TryBecomePrimary()
    {
        if (_ownsMutex) return true;
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        return _ownsMutex;
    }

    public void StartListening(Action<LaunchRequest> onRequest)
    {
        _ = ListenAsync(onRequest, _shutdown.Token);
    }

    private async Task ListenAsync(Action<LaunchRequest> onRequest, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString().AsMemory(), timeout.Token).ConfigureAwait(false);
                var json = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                var request = json is null ? null : JsonSerializer.Deserialize<LaunchRequest>(json);
                if (request is not null)
                {
                    onRequest(request);
                    await writer.WriteLineAsync("OK".AsMemory(), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException)
            {
                // A disconnected or incomplete client must not stop later launches.
            }
        }
    }

    public async Task<bool> ForwardAsync(LaunchRequest request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            if (int.TryParse(await reader.ReadLineAsync(timeout.Token), out var processId))
            {
                AllowSetForegroundWindow(processId);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token) == "OK";
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }
        _mutex.Dispose();
        _shutdown.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
