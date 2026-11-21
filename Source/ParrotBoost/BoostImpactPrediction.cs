namespace ParrotBoost;

internal readonly struct BoostImpactPrediction(int minimumGainPercent, int maximumGainPercent, int confidencePercent, string summary, string details)
{
	public static readonly BoostImpactPrediction Pending = new BoostImpactPrediction(-1, -1, 0, "Sampling live bottlenecks for a realistic estimate.", "Waiting for telemetry...");

	public int MinimumGainPercent { get; } = minimumGainPercent;

	public int MaximumGainPercent { get; } = maximumGainPercent;

	public int ConfidencePercent { get; } = confidencePercent;

	public string Summary { get; } = summary;

	public string Details { get; } = details;

	public string RangeLabel
	{
		get
		{
			if (MinimumGainPercent < 0 || MaximumGainPercent < 0)
			{
				return "Analyzing";
			}
			if (MaximumGainPercent <= 0)
			{
				return "0%";
			}
			if (MinimumGainPercent > 0)
			{
				if (MinimumGainPercent != MaximumGainPercent)
				{
					return $"+{MinimumGainPercent}-{MaximumGainPercent}%";
				}
				return $"+{MinimumGainPercent}%";
			}
			return $"0-{MaximumGainPercent}%";
		}
	}
}
