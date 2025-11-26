namespace OllamaClusterManager.Config
{
    public class ClusterConfig
    {
        public string Model { get; set; } = "qwen2.5-coder:3b";
        public int StartPort { get; set; } = 5001;
        public int MinInstances { get; set; } = 2;
        public int MaxInstances { get; set; } = 10;
        public int MaxConcurrency { get; set; } = 4;
        public int IdleTimeoutSeconds { get; set; } = 30;
        public double ScaleUpLoadThreshold { get; set; } = 0.8;
        public double ScaleDownLoadThreshold { get; set; } = 0.2;
        public double ScaleUpRps { get; set; } = 50.0;
        public int PredictiveRpsWindow { get; set; } = 10;
        public double PredictiveRpsTrendThreshold { get; set; } = 5.0;
    }
}
