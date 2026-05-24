using System;

namespace ProGPU.Samples;

public class LogItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double Latency { get; set; }
}
