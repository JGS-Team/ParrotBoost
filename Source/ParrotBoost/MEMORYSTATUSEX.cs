using System.Runtime.InteropServices;

namespace ParrotBoost;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct MEMORYSTATUSEX
{
	public uint dwLength;

	public uint dwMemoryLoad;

	public ulong ullTotalPhys;

	public ulong ullAvailPhys;

	public ulong ullTotalPageFile;

	public ulong ullAvailPageFile;

	public ulong ullTotalVirtual;

	public ulong ullAvailVirtual;

	public ulong ullAvailExtendedVirtual;

	public void Init()
	{
		dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
	}
}
