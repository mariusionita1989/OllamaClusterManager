using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OllamaClusterManager.Config;
using OllamaClusterManager.Ollama;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;

namespace OllamaClusterManager.Manager
{
    public sealed class OllamaCluster
    {
        private readonly ConcurrentDictionary<int, OllamaInstance> _instances = new();
        private readonly CancellationTokenSource _cts = new();
        private ClusterConfig _config = new();
        private FileSystemWatcher? _watcher;
        private readonly HttpClient _httpClient = new();
        private readonly ConcurrentDictionary<string, int> _userRequestCounts = new();
        private double _clusterRps = 0;
        private const double ClusterAlpha = 0.2;
        private readonly Queue<double> _rpsHistory = new();

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public async Task StartAsync()
        {
            LoadConfig();
            SetupHotReload();

            for (int i = 0; i < _config.MinInstances; i++)
                StartInstance();

            _ = Task.Run(MonitorInstancesAsync);
            StartWebApi();
            await Task.CompletedTask;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Stop()
        {
            _cts.Cancel();
            foreach (var inst in _instances.Values)
                inst.Kill();
            _watcher?.Dispose();
            _httpClient.Dispose();
        }

        #region Config
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void LoadConfig()
        {
            const string path = "clusterconfig.json";
            if (!File.Exists(path))
            {
                _config = new ClusterConfig();
                File.WriteAllText(path,
                    System.Text.Json.JsonSerializer.Serialize(_config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            _config = System.Text.Json.JsonSerializer.Deserialize<ClusterConfig>(File.ReadAllText(path)) ?? new ClusterConfig();
            Console.WriteLine("[Config] Loaded cluster configuration.");
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void SetupHotReload()
        {
            _watcher = new FileSystemWatcher(Directory.GetCurrentDirectory())
            {
                Filter = "clusterconfig.json",
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (_, __) =>
            {
                try
                {
                    LoadConfig();
                    Console.WriteLine("[HotReload] Config updated.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HotReload] Failed to reload config: {ex.Message}");
                }
            };
        }
        #endregion

        #region Instances
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void StartInstance()
        {
            if (_instances.Count >= _config.MaxInstances) return;

            var inst = new OllamaInstance(_config.Model, _config.MaxConcurrency);
            _instances[inst.Port] = inst;
            inst.Start();
            Console.WriteLine($"[Instance] Started instance on port {inst.Port}.");
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void KillInstance(OllamaInstance instance)
        {
            instance.Kill();
            _instances.TryRemove(instance.Port, out _);
            Console.WriteLine($"[Instance] Killed instance on port {instance.Port}.");
        }
        #endregion

        #region RPS & Load
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private double GetClusterRps()
        {
            return _instances.Values.Sum(i => i.Rps);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void UpdateClusterRps()
        {
            double currentRps = GetClusterRps();
            _clusterRps = ClusterAlpha * currentRps + (1 - ClusterAlpha) * _clusterRps;

            _rpsHistory.Enqueue(_clusterRps);
            if (_rpsHistory.Count > _config.PredictiveRpsWindow)
                _rpsHistory.Dequeue();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private double GetRpsTrend()
        {
            if (_rpsHistory.Count < 2) return 0;
            var arr = _rpsHistory.ToArray();
            return arr[^1] - arr[0];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private OllamaInstance? GetLeastLoadedInstance(string? user = null)
        {
            var aliveInstances = _instances.Values
                .Where(i => i.IsAlive && !i.IsDisabled)
                .OrderBy(i => i.CompositeLoad)
                .ToArray();

            if (aliveInstances.Length == 0) return null;

            var selected = aliveInstances.First();

            if (!string.IsNullOrEmpty(user))
                _userRequestCounts.AddOrUpdate(user, 1, (_, count) => count + 1);

            return selected;
        }
        #endregion

        #region Monitor & Auto-Scaling
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private async Task MonitorInstancesAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var instancesArray = _instances.Values.ToArray();

                    foreach (var inst in instancesArray)
                    {
                        if (!inst.IsAlive && !inst.IsDisabled)
                        {
                            Console.WriteLine($"[HealthCheck] Instance {inst.Port} crashed, restarting...");
                            _instances.TryRemove(inst.Port, out _);
                            StartInstance();
                        }
                    }

                    UpdateClusterRps();
                    double rpsTrend = GetRpsTrend();
                    var aliveInstances = instancesArray.Where(i => i.IsAlive && !i.IsDisabled).ToArray();

                    if (aliveInstances.Length > 0)
                    {
                        var highestLoad = aliveInstances.Max(i => i.CompositeLoad);
                        if ((highestLoad >= _config.ScaleUpLoadThreshold || _clusterRps >= _config.ScaleUpRps) &&
                            _instances.Count < _config.MaxInstances)
                        {
                            Console.WriteLine($"[Scaling] High load ({highestLoad:P}) or RPS ({_clusterRps:F2}) detected. Starting new instance...");
                            StartInstance();
                        }

                        if (rpsTrend > _config.PredictiveRpsTrendThreshold && _instances.Count < _config.MaxInstances)
                        {
                            Console.WriteLine($"[PredictiveScaling] RPS rising ({rpsTrend:F2}/window). Starting new instance preemptively...");
                            StartInstance();
                        }
                    }

                    foreach (var inst in instancesArray)
                    {
                        if ((now - inst.LastUsed).TotalSeconds > _config.IdleTimeoutSeconds &&
                            inst.CompositeLoad <= _config.ScaleDownLoadThreshold &&
                            _instances.Count > _config.MinInstances)
                        {
                            Console.WriteLine($"[Scaling] Instance {inst.Port} idle/low load ({inst.CompositeLoad:P}). Removing...");
                            KillInstance(inst);
                        }
                    }

                    await Task.Delay(1000, _cts.Token);
                }
                catch { }
            }
        }
        #endregion

        #region Web API
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void StartWebApi()
        {
            Task.Run(() =>
            {
                var builder = WebApplication.CreateBuilder();
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen();
                var app = builder.Build();
                app.UseSwagger();
                app.UseSwaggerUI(c =>
                {
                    c.DocumentTitle = "Ollama Cluster Manager API";
                    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Cluster API v1");
                    c.RoutePrefix = "swagger";
                });

                app.MapGet("/", () => Results.Redirect("/swagger"));

                app.MapGet("/instances", () => Results.Json(_instances.Values.Select(i => new
                {
                    i.Port,
                    i.IsAlive,
                    i.IsDisabled,
                    i.CurrentRequests,
                    i.CpuUsage,
                    i.MemoryMb,
                    i.MovingAverageLoad,
                    i.CompositeLoad,
                    i.Rps
                })));

                app.MapPost("/instances/{port}/disable", (int port) =>
                {
                    if (_instances.TryGetValue(port, out var inst)) { inst.Disable(); return Results.Ok($"Instance {port} disabled."); }
                    return Results.NotFound($"Instance {port} not found.");
                });

                app.MapPost("/instances/{port}/enable", (int port) =>
                {
                    if (_instances.TryGetValue(port, out var inst)) { inst.Enable(); return Results.Ok($"Instance {port} enabled."); }
                    return Results.NotFound($"Instance {port} not found.");
                });

                app.MapGet("/cluster/status", () =>
                {
                    var instances = _instances.Values.ToArray();
                    if (instances.Length == 0) return Results.Problem("No instances available.");

                    return Results.Json(new
                    {
                        TotalInstances = instances.Length,
                        AliveInstances = instances.Count(i => i.IsAlive),
                        DisabledInstances = instances.Count(i => i.IsDisabled),
                        TotalRequests = instances.Sum(i => i.CurrentRequests),
                        AvgCpu = instances.Average(i => i.CpuUsage),
                        AvgMemoryMb = instances.Average(i => i.MemoryMb),
                        AvgLoad = instances.Average(i => i.MovingAverageLoad),
                        AvgCompositeLoad = instances.Average(i => i.CompositeLoad),
                        ClusterRps = _clusterRps,
                        RpsTrend = GetRpsTrend()
                    });
                });

                app.MapPost("/cluster/scale", async (HttpRequest request) =>
                {
                    var body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                    if (!body.TryGetProperty("action", out var actionProp)) return Results.BadRequest("Missing 'action' field ('up' or 'down').");
                    string action = actionProp.GetString() ?? "";
                    int count = body.TryGetProperty("count", out var countProp) ? countProp.GetInt32() : 1;

                    if (action == "up") { for (int i = 0; i < count; i++) StartInstance(); return Results.Ok($"Started {count} new instance(s)."); }
                    else if (action == "down") { var instances = _instances.Values.Take(count).ToArray(); foreach (var inst in instances) KillInstance(inst); return Results.Ok($"Stopped {instances.Length} instance(s)."); }

                    return Results.BadRequest("Invalid action. Use 'up' or 'down'.");
                });

                app.MapPost("/route", async (HttpRequest request) =>
                {
                    string user = request.Headers["X-User"].FirstOrDefault() ?? "anonymous";
                    var instance = GetLeastLoadedInstance(user);
                    if (instance == null) return Results.Problem("No alive instances available.");

                    var body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                    if (body.ValueKind == System.Text.Json.JsonValueKind.Undefined) return Results.BadRequest("Invalid JSON body.");

                    var url = $"http://localhost:{instance.Port}/api/prompt";
                    try
                    {
                        var resp = await _httpClient.PostAsJsonAsync(url, body);
                        resp.EnsureSuccessStatusCode();
                        var jsonResponse = await resp.Content.ReadAsStringAsync();
                        return Results.Content(jsonResponse, "application/json");
                    }
                    catch (Exception ex)
                    {
                        return Results.Problem($"Failed to forward to instance {instance.Port}: {ex.Message}");
                    }
                });

                app.MapGet("/health", () => _instances.Values.Any(i => i.IsAlive) ? Results.Ok("Cluster healthy") : Results.StatusCode(503));

                app.MapGet("/metrics", () =>
                {
                    var lines = _instances.Values.Select(i =>
                        $"ollama_instance_up{{port=\"{i.Port}\"}} {(i.IsAlive ? 1 : 0)}\n" +
                        $"ollama_instance_requests_inflight{{port=\"{i.Port}\"}} {i.CurrentRequests}\n" +
                        $"ollama_instance_cpu{{port=\"{i.Port}\"}} {i.CpuUsage}\n" +
                        $"ollama_instance_memory_mb{{port=\"{i.Port}\"}} {i.MemoryMb}\n" +
                        $"ollama_instance_load{{port=\"{i.Port}\"}} {i.MovingAverageLoad}\n" +
                        $"ollama_instance_composite_load{{port=\"{i.Port}\"}} {i.CompositeLoad}\n" +
                        $"ollama_instance_rps{{port=\"{i.Port}\"}} {i.Rps}"
                    );

                    foreach (var kvp in _userRequestCounts)
                        lines = lines.Append($"ollama_user_requests{{user=\"{kvp.Key}\"}} {kvp.Value}");

                    return Results.Text(string.Join("\n", lines));
                });

                app.MapPost("/users/reset", () => { _userRequestCounts.Clear(); return Results.Ok("All user request counts reset."); });
                Console.WriteLine("[WebAPI] Starting on http://localhost:5000");
                Console.WriteLine("[Swagger] Open http://localhost:5000/swagger for API docs");
                app.Run("http://localhost:5000");
            });
        }
        #endregion
    }
}
