using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Mesen.Mcp
{
#pragma warning disable SYSLIB1054
	/// <summary>
	/// MCP over stdio requires stdout to carry only JSON-RPC frames. The native emulator core
	/// (Debugger::Log, MessageManager::Log, ...) writes plain text with std::cout, which corrupts
	/// the protocol stream (e.g. "File: Illusion of Gaia.smc" printed during load_rom makes MCP
	/// clients drop the connection). This guard keeps a private duplicate of the original stdout
	/// for the protocol and points the process-level stdout (fd 1) at stderr.
	/// </summary>
	internal static class McpStdoutGuard
	{
		private const int StdOutputHandle = -11;
		private const int StdErrorHandle = -12;
		private const uint DuplicateSameAccess = 2;

		// Keeps the NUL fallback handle alive for the lifetime of the process.
		private static SafeFileHandle? _nullSink;

		/// <summary>
		/// Returns a stream bound to the original stdout, then redirects native stdout to stderr.
		/// Must be called before any native Mesen code is loaded.
		/// </summary>
		public static Stream ProtectProtocolStream()
		{
			try {
				Stream stream = OperatingSystem.IsWindows() ? RedirectWindows() : RedirectUnix();
				try { Console.SetOut(Console.Error); } catch { }
				McpDebugLog.Info("Native stdout redirected to stderr; protocol stream isolated.");
				return stream;
			} catch(Exception ex) {
				McpDebugLog.Error("Failed to isolate stdout; falling back to default stream.", ex);
				return Console.OpenStandardOutput();
			}
		}

		private static Stream RedirectWindows()
		{
			IntPtr stdout = GetStdHandle(StdOutputHandle);
			if(stdout == IntPtr.Zero || stdout == new IntPtr(-1)) {
				throw new InvalidOperationException("stdout handle is not available.");
			}

			if(!DuplicateHandle(GetCurrentProcess(), stdout, GetCurrentProcess(), out IntPtr protocolHandle, 0, false, DuplicateSameAccess)) {
				throw new InvalidOperationException($"DuplicateHandle failed (error {Marshal.GetLastWin32Error()}).");
			}
			FileStream protocolStream = new FileStream(new SafeFileHandle(protocolHandle, true), FileAccess.Write, 1, false);

			IntPtr sink = GetStdHandle(StdErrorHandle);
			if(sink == IntPtr.Zero || sink == new IntPtr(-1)) {
				_nullSink = File.OpenHandle("NUL", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
				sink = _nullSink.DangerousGetHandle();
				SetStdHandle(StdErrorHandle, sink);
			}

			// Covers native modules that read the Win32 standard handle when they initialize.
			SetStdHandle(StdOutputHandle, sink);

			// Covers native modules sharing the already-initialized UCRT (fd 1 -> stderr).
			try {
				if(_dup2(2, 1) != 0) {
					McpDebugLog.Info("_dup2(2, 1) returned an error; relying on SetStdHandle only.");
				}
			} catch(Exception ex) {
				McpDebugLog.Error("_dup2 unavailable; relying on SetStdHandle only.", ex);
			}

			return protocolStream;
		}

		private static Stream RedirectUnix()
		{
			int saved = dup(1);
			if(saved < 0) {
				throw new InvalidOperationException("dup(1) failed.");
			}
			FileStream protocolStream = new FileStream(new SafeFileHandle((IntPtr)saved, true), FileAccess.Write, 1, false);
			if(dup2(2, 1) < 0) {
				throw new InvalidOperationException("dup2(2, 1) failed.");
			}
			return protocolStream;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr GetStdHandle(int stdHandle);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetStdHandle(int stdHandle, IntPtr handle);

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentProcess();

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

		[DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
		private static extern int _dup2(int fdSource, int fdTarget);

		[DllImport("libc", SetLastError = true)]
		private static extern int dup(int fd);

		[DllImport("libc", SetLastError = true)]
		private static extern int dup2(int oldFd, int newFd);
	}
#pragma warning restore SYSLIB1054
}
