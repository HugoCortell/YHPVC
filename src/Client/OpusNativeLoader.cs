using System;
using System.IO;
using System.Runtime.InteropServices;
using OpusSharp.Core;
using Vintagestory.API.Common;

namespace YHPVC.Client;

internal static class OpusNativeLoader
{
	private static readonly object Sync = new();
	private static bool Initialized;
	private static IntPtr OpusHandle;

	public static void EnsureLoaded(ICoreAPI? api = null)
	{
		lock (Sync)
		{
			if (Initialized) return;

			string assemblyDirectory = Path.GetDirectoryName(typeof(OpusNativeLoader).Assembly.Location)
				?? throw new InvalidOperationException("[YHPVC] Could not resolve mod assembly directory.");

			string rid = GetRidOrThrow();
			string nativeDirectory = Path.Combine(assemblyDirectory, "native", rid);
			string nativePath = ResolveNativePath(nativeDirectory);

			try { OpusHandle = NativeLibrary.Load(nativePath); }
			catch (Exception ex) { throw new DllNotFoundException($"[YHPVC] Failed to load bundled Opus native library: {nativePath}", ex); }

			try
			{
				// OpusSharp.Core P/Invokes libopus by name. VS requires native libraries under native/<rid>/,
				// which is not part of the default OS loader search path, so explicitly resolve OpusSharp's imports.
				NativeLibrary.SetDllImportResolver(typeof(OpusEncoder).Assembly, (libraryName, assembly, searchPath) =>
				{
					if (IsOpusLibraryName(libraryName)) { return OpusHandle != IntPtr.Zero ? OpusHandle : NativeLibrary.Load(nativePath); }
					return IntPtr.Zero;
				});
			}
			catch (InvalidOperationException ex)
			{
				// A resolver may already exist if another loader initialized OpusSharp first. Keep the exact native
				// library loaded and allow OpusSharp to proceed.
				// Encoder construction will fail loudly if resolution still cannot happen.
				api?.Logger.Warning("[YHPVC] OpusSharp native resolver was already registered: {0}", ex.Message);
			}

			Initialized = true;
			api?.Logger.Notification("[YHPVC] Opus native library ready for {0}: {1}", rid, nativePath);
		}
	}

	private static string ResolveNativePath(string nativeDirectory)
	{
		string[] candidates = GetCandidateFileNames();

		for (int i = 0; i < candidates.Length; i++)
		{
			string candidate = Path.Combine(nativeDirectory, candidates[i]);
			if (File.Exists(candidate)) return candidate;
		}

		throw new FileNotFoundException($"[YHPVC] Missing Opus native library in {nativeDirectory}. Expected one of: {string.Join(", ", candidates)}");
	}

	private static bool IsOpusLibraryName(string libraryName)
	{
		string name = Path.GetFileName(libraryName);

		return name.Equals("opus", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("opus.dll", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("opus.so", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("libopus.so", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("opus.dylib", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("libopus.dylib", StringComparison.OrdinalIgnoreCase);
	}

	private static string[] GetCandidateFileNames()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))	{ return new[] { "opus.dll", "libopus.dll" }; }
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))		{ return new[] { "opus.so", "libopus.so" }; }
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))		{ return new[] { "opus.dylib", "libopus.dylib" }; }
		
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
