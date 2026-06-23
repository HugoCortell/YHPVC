using System;
using System.IO;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;

namespace YHPVC.Client;

internal static class RNNoiseNativeLoader
{
	private static readonly object Sync = new();

	private static bool Initialized;
	private static bool Available;
	private static IntPtr RNNoiseHandle;
	private static string? ResolvedPath;

	public static bool TryEnsureLoaded(ICoreAPI? api = null)
	{
		lock (Sync)
		{
			if (Initialized) return Available;
			Initialized = true;

			try
			{
				string assemblyDirectory = Path.GetDirectoryName(typeof(RNNoiseNativeLoader).Assembly.Location)
					?? throw new InvalidOperationException("[YHPVC] Could not resolve mod assembly directory.");

				string rid = GetRidOrThrow();
				string nativeDirectory = Path.Combine(assemblyDirectory, "native", rid);
				string nativePath = ResolveNativePath(nativeDirectory);

				// Load the exact bundled file first. This avoids relying on PATH, LD_LIBRARY_PATH,
				// the game's executable directory, or the current working directory.
				RNNoiseHandle = NativeLibrary.Load(nativePath);
				ResolvedPath = nativePath;

				try { NativeLibrary.SetDllImportResolver(typeof(RNNoiseNativeLoader).Assembly, ResolveDllImport); }
				catch (InvalidOperationException ex)
				{
					// Another YHPVC loader already registered a resolver. Keep the exact native library loaded.
					// The first RNNoise P/Invoke below will prove whether resolution is healthy.
					api?.Logger.Warning("[YHPVC] RNNoise native resolver was already registered: {0}", ex.Message);
				}

				int frameSize = RNNoiseNative.rnnoise_get_frame_size();
				if (frameSize != RNNoiseDenoiser.RNNoiseFrameSamples)
				{
					api?.Logger.Warning
					(
						"[YHPVC] RNNoise native library returned unexpected frame size {0}; expected {1}. Noise suppression disabled.",
						frameSize, RNNoiseDenoiser.RNNoiseFrameSamples
					);
					return false;
				}

				Available = true;
				api?.Logger.Notification("[YHPVC] RNNoise native library ready for {0}: {1}", rid, nativePath);
				return true;
			}
			catch (Exception ex)
			{
				api?.Logger.Warning("[YHPVC] RNNoise unavailable; continuing without noise suppression: {0}", ex.Message);
				Available = false;
				return false;
			}
		}
	}

	private static IntPtr ResolveDllImport(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (!IsRNNoiseLibraryName(libraryName)) return IntPtr.Zero;
		if (RNNoiseHandle != IntPtr.Zero) return RNNoiseHandle;
		return ResolvedPath != null ? NativeLibrary.Load(ResolvedPath) : IntPtr.Zero;
	}

	private static string ResolveNativePath(string nativeDirectory)
	{
		string[] candidates = GetCandidateFileNames();

		for (int i = 0; i < candidates.Length; i++)
		{
			string candidate = Path.Combine(nativeDirectory, candidates[i]);
			if (File.Exists(candidate)) return candidate;
		}

		throw new FileNotFoundException($"[YHPVC] Missing RNNoise native library in {nativeDirectory}. Expected one of: {string.Join(", ", candidates)}");
	}

	private static bool IsRNNoiseLibraryName(string libraryName)
	{
		string name = Path.GetFileName(libraryName);

		return name.Equals("rnnoise", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("rnnoise.dll", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("librnnoise.dll", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("rnnoise.so", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("librnnoise.so", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("rnnoise.dylib", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("librnnoise.dylib", StringComparison.OrdinalIgnoreCase);
	}

	private static string[] GetCandidateFileNames()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return new[]
			{
				"rnnoise.dll",
				"librnnoise.dll",
				"RNNoise.dll",
				"libRNNoise.dll"
			};
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return new[]
			{
				"rnnoise.so",
				"librnnoise.so",
				"RNNoise.so",
				"libRNNoise.so"
			};
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return new[]
			{
				"rnnoise.dylib",
				"librnnoise.dylib",
				"RNNoise.dylib",
				"libRNNoise.dylib"
			};
		}

		throw new PlatformNotSupportedException($"[YHPVC] Unsupported OS: {RuntimeInformation.OSDescription}");
	}

	private static string GetRidOrThrow()
	{
		Architecture arch = RuntimeInformation.ProcessArchitecture;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return arch switch
			{
				Architecture.X64 => "win-x64",
				Architecture.Arm64 => "win-arm64",
				_ => throw new PlatformNotSupportedException($"[YHPVC] Unsupported Windows architecture: {arch}.")
			};
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return arch switch
			{
				Architecture.X64 => "linux-x64",
				Architecture.Arm64 => "linux-arm64",
				_ => throw new PlatformNotSupportedException($"[YHPVC] Unsupported Linux architecture: {arch}.")
			};
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return arch switch
			{
				Architecture.X64 => "osx-x64",
				Architecture.Arm64 => "osx-arm64",
				_ => throw new PlatformNotSupportedException($"[YHPVC] Unsupported macOS architecture: {arch}.")
			};
		}

		throw new PlatformNotSupportedException($"[YHPVC] Unsupported OS/architecture: {RuntimeInformation.OSDescription} / {arch}");
	}
}

internal static class RNNoiseNative
{
	private const string LibraryName = "rnnoise";

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern int rnnoise_get_frame_size();

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr rnnoise_create(IntPtr model);

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern void rnnoise_destroy(IntPtr state);

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern float rnnoise_process_frame(IntPtr state, float[] output, float[] input);
}