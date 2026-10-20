// @purpose: Enforces single-instance application execution using a named Windows Mutex and passes command-line file paths via NamedPipes.
// @depends: System.Threading.Mutex, System.IO.Pipes, user32.dll
// @exports: SingleInstanceManager

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenRadViewer.Core.Services;

public static class SingleInstanceManager
{
    private static Mutex? _mutex;
    private static CancellationTokenSource? _cts;
    private static readonly string MutexName = $"Parsec_SingleInstance_Mutex_{Environment.UserName.ToLowerInvariant()}";
    private static readonly string PipeName = $"Parsec_SingleInstance_Pipe_{Environment.UserName.ToLowerInvariant()}";

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;

    public static event Action<List<string>>? PathsReceived;

    /// <summary>
    /// Checks if this is the first (primary) instance.
    /// If yes, starts IPC listener and returns true.
    /// If no, forwards arguments to the primary instance and returns false.
    /// </summary>
    public static bool InitializeAsPrimaryOrForward(string[] args)
    {
        bool isFirstInstance;
        try
        {
            _mutex = new Mutex(true, MutexName, out isFirstInstance);
        }
        catch (AbandonedMutexException)
        {
            isFirstInstance = true;
        }

        if (isFirstInstance)
        {
            StartIpcServer();
            return true;
        }

        // Forward to primary instance and exit
        ForwardArgsToPrimary(args);
        return false;
    }

    private static void ForwardArgsToPrimary(string[] args)
    {
        var validPaths = args
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

        // Retry connecting in case the primary instance is currently launching
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(250);

                using var writer = new StreamWriter(client) { AutoFlush = true };
                string json = JsonSerializer.Serialize(validPaths);
                writer.WriteLine(json);
                return;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }
    }

    private static void StartIpcServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server);
                    string? line = await reader.ReadLineAsync(token);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var paths = JsonSerializer.Deserialize<List<string>>(line);
                        if (paths != null)
                        {
                            PathsReceived?.Invoke(paths);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SingleInstanceManager] Server loop: {ex.Message}");
                    await Task.Delay(150, token);
                }
            }
        }, token);
    }

    public static void Cleanup()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        catch { }

        try
        {
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch { }
    }
}
