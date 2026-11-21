using System;
using System.Diagnostics;

namespace ParrotBoost;

internal static class CommandTemperatureReader
{
	internal sealed class CommandResult
	{
		public bool Success { get; init; }

		public string Output { get; init; } = string.Empty;

		public string? Error { get; init; }
	}

	public static CommandResult TryRun(string fileName, string arguments, int timeoutMs)
	{
		try
		{
			using Process process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = fileName,
					Arguments = arguments,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};
			process.Start();
			var outTask = process.StandardOutput.ReadToEndAsync();
			var errTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(timeoutMs))
			{
				try
				{
					process.Kill();
				}
				catch
				{
				}
				return new CommandResult
				{
					Error = fileName + " timed out while reading temperatures."
				};
			}
			string output = outTask.GetAwaiter().GetResult();
			string text = errTask.GetAwaiter().GetResult();
			return new CommandResult
			{
				Success = (process.ExitCode == 0),
				Output = output,
				Error = (string.IsNullOrWhiteSpace(text) ? null : text.Trim())
			};
		}
		catch (Exception ex)
		{
			return new CommandResult
			{
				Error = ex.Message
			};
		}
	}
}
