using OllamaClusterManager.Manager;
using System.Runtime.CompilerServices;

class Program
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static async Task Main()
    {
        var cluster = new OllamaCluster();

        try
        {
            await cluster.StartAsync();
            Console.WriteLine("[Cluster] Ollama Cluster running.");

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("[Cluster] Stopping Ollama Cluster...");
                cluster.Stop();
                Environment.Exit(0);
            };

            await Task.Delay(-1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fatal] Failed to start cluster: {ex.Message}");
            cluster.Stop();
        }
    }
}