using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace OllamaClusterManager.Ollama
{
    public sealed class OllamaInstance
    {
        private Process? _process;
        private readonly object _lock = new();
        private int _currentRequests;
        private double _movingAverageLoad = 0;
        public string Model { get; }
        public int MaxConcurrency { get; }
        public int Port { get; private set; }
        public bool IsDisabled { get; private set; }
        public DateTime LastUsed { get; private set; } = DateTime.UtcNow;
        public double CpuUsage { get; private set; } = 0;
        public double MemoryMb { get; private set; } = 0;
        public double MovingAverageLoad => _movingAverageLoad;
        public double CompositeLoad => (_currentRequests / (double)MaxConcurrency + CpuUsage / 100.0) / 2.0;
        private int _requestsInWindow = 0;
        public double Rps => _requestsInWindow / 2.0; // approximate over 2s window

        public int CurrentRequests => _currentRequests;
        public bool IsAlive => _process != null && !_process.HasExited;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public OllamaInstance(string model, int maxConcurrency)
        {
            Model = model;
            MaxConcurrency = maxConcurrency;
            Port = GetAvailablePortWithRetry();
            _ = Task.Run(UpdateMetricsAsync);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Start()
        {
            lock (_lock)
            {
                if (_process != null && !_process.HasExited) return;

                _process = new Process();
                _process.StartInfo.FileName = "ollama";
                _process.StartInfo.Arguments = "serve";
                _process.StartInfo.UseShellExecute = false;
                _process.StartInfo.RedirectStandardOutput = true;
                _process.StartInfo.RedirectStandardError = true;
                _process.StartInfo.CreateNoWindow = true;
                _process.StartInfo.EnvironmentVariables["OLLAMA_HOST"] = $"127.0.0.1:{Port}";

                _process.EnableRaisingEvents = true;
                _process.Exited += (_, __) => Console.WriteLine($"[Instance {Port}] Exited.");

                _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[Ollama {Port}] {e.Data}"); };
                _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[Ollama {Port} ERROR] {e.Data}"); };

                try
                {
                    _process.Start();
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                    Console.WriteLine($"[Instance {Port}] Started.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Instance {Port}] Failed to start: {ex.Message}");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Kill()
        {
            lock (_lock)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _process.Kill(true);
                        _process.WaitForExit();
                        Console.WriteLine($"[Instance {Port}] Killed.");
                    }
                }
                catch { }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Enable() => IsDisabled = false;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Disable() => IsDisabled = true;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public async Task<T> ExecuteAsync<T>(Func<Task<T>> func)
        {
            try
            {
                Interlocked.Increment(ref _currentRequests);
                Interlocked.Increment(ref _requestsInWindow);
                return await func();
            }
            finally
            {
                Interlocked.Decrement(ref _currentRequests);
                LastUsed = DateTime.UtcNow;
                UpdateLoad();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void UpdateLoad()
        {
            double instantLoad = (double)CurrentRequests / MaxConcurrency;
            _movingAverageLoad = 0.8 * _movingAverageLoad + 0.2 * instantLoad;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private async Task UpdateMetricsAsync()
        {
            while (true)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        CpuUsage = GetCpuUsage();
                        MemoryMb = _process.WorkingSet64 / (1024.0 * 1024.0);
                    }

                    // reset RPS window
                    _requestsInWindow = 0;
                    await Task.Delay(2000);
                }
                catch { }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private double GetCpuUsage()
        {
            try
            {
                if (_process == null) return 0;
                var startTime = _process.TotalProcessorTime;
                var start = DateTime.UtcNow;
                Thread.Sleep(100);
                var endTime = _process.TotalProcessorTime;
                var end = DateTime.UtcNow;

                double cpuUsedMs = (endTime - startTime).TotalMilliseconds;
                double totalMsPassed = (end - start).TotalMilliseconds * Environment.ProcessorCount;

                return Math.Round(cpuUsedMs / totalMsPassed * 100.0, 2);
            }
            catch { return 0; }
        }

        #region Port Utilities
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int GetAvailablePortWithRetry(int maxRetries = 10)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                int port = GetAvailablePort();
                if (IsPortFree(port)) return port;
            }
            throw new InvalidOperationException("Failed to find an available TCP port.");
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static bool IsPortFree(int port)
        {
            try
            {
                using TcpListener? listener = new(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch { return false; }
        }
        #endregion
    }
}
