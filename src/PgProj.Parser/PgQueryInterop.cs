using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PgProj.Parser;

internal static class PgQueryInterop
{
    private const string LibraryName = "libpg_query";

    [ModuleInitializer]
    internal static void InitializeNativeResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(PgQueryInterop).Assembly, ResolveLibrary);
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        var target = GetTarget(CurrentPlatform(), RuntimeInformation.ProcessArchitecture);

        if (target is null)
        {
            return IntPtr.Zero;
        }

        foreach (var path in CandidatePaths(AppContext.BaseDirectory, target))
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>The library file and runtime identifier for one platform (G-81).</summary>
    internal sealed record NativeLibraryTarget(string RuntimeIdentifier, string FileName);

    /// <summary>
    /// The runtime identifier and file name of libpg_query for <paramref name="platform"/> and <paramref name="architecture"/>, or null
    /// when none is packaged. Linux and macOS libraries are named as the platform names them (<c>libpg_query.so</c>,
    /// <c>libpg_query.dylib</c>); the Windows one is <c>libpg_query.dll</c>. Only the runtimes that <c>Npgquery</c> carries a library
    /// for load; an Intel Mac (<c>osx-x64</c>) has none.
    /// </summary>
    internal static NativeLibraryTarget? GetTarget(OSPlatform platform, Architecture architecture)
    {
        if (platform == OSPlatform.Windows)
        {
            return architecture switch
            {
                Architecture.X64 => new NativeLibraryTarget("win-x64", "libpg_query.dll"),
                Architecture.X86 => new NativeLibraryTarget("win-x86", "libpg_query.dll"),
                Architecture.Arm64 => new NativeLibraryTarget("win-arm64", "libpg_query.dll"),
                Architecture.Arm => new NativeLibraryTarget("win-arm", "libpg_query.dll"),
                _ => null,
            };
        }

        if (platform == OSPlatform.Linux)
        {
            return architecture switch
            {
                Architecture.X64 => new NativeLibraryTarget("linux-x64", "libpg_query.so"),
                Architecture.Arm64 => new NativeLibraryTarget("linux-arm64", "libpg_query.so"),
                _ => null,
            };
        }

        if (platform == OSPlatform.OSX)
        {
            return architecture == Architecture.Arm64 ? new NativeLibraryTarget("osx-arm64", "libpg_query.dylib") : null;
        }

        return null;
    }

    /// <summary>Where the library may be, in the order tried: beside the executable, then the two runtime folder layouts.</summary>
    internal static string[] CandidatePaths(string baseDirectory, NativeLibraryTarget target)
    {
        return
        [
            Path.Combine(baseDirectory, target.FileName),
            Path.Combine(baseDirectory, target.RuntimeIdentifier, target.FileName),
            Path.Combine(baseDirectory, "runtimes", target.RuntimeIdentifier, "native", target.FileName),
        ];
    }

    private static OSPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return OSPlatform.Linux;
        }

        return OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Create("UNSUPPORTED");
    }

    // The SQL is marshaled as UTF-8, which is what libpg_query reads; the default is the Windows ANSI code page (G-125).
    [DllImport(LibraryName, EntryPoint = "pg_query_parse", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeParseResult Parse([MarshalAs(UnmanagedType.LPUTF8Str)] string input);

    [DllImport(LibraryName, EntryPoint = "pg_query_parse_protobuf", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeProtobufParseResult ParseProtobuf([MarshalAs(UnmanagedType.LPUTF8Str)] string input);

    // PostgreSQL's own lexer (G-207): the tokens of the text with their byte offsets, as a ScanResult protobuf. Its result has the same
    // layout as a protobuf parse result.
    [DllImport(LibraryName, EntryPoint = "pg_query_scan", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeProtobufParseResult Scan([MarshalAs(UnmanagedType.LPUTF8Str)] string input);

    [DllImport(LibraryName, EntryPoint = "pg_query_free_scan_result", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeScanResult(NativeProtobufParseResult result);

    // PL/pgSQL (G-303a): the parse tree of the CREATE FUNCTION and CREATE PROCEDURE statements in the text, as JSON, with each embedded SQL expression and
    // query as text.
    [DllImport(LibraryName, EntryPoint = "pg_query_parse_plpgsql", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativePlpgsqlParseResult ParsePlpgsql([MarshalAs(UnmanagedType.LPUTF8Str)] string input);

    [DllImport(LibraryName, EntryPoint = "pg_query_free_plpgsql_parse_result", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreePlpgsqlParseResult(NativePlpgsqlParseResult result);

    [DllImport(LibraryName, EntryPoint = "pg_query_free_parse_result", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeParseResult(NativeParseResult result);

    [DllImport(LibraryName, EntryPoint = "pg_query_free_protobuf_parse_result", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeProtobufParseResult(NativeProtobufParseResult result);

    internal static string? ReadNullableUtf8String(IntPtr value)
    {
        return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
    }

    /// <summary>The JSON tree of <paramref name="sql"/>, with its positions counted in the string's characters (see <see cref="Utf8Offsets"/>).</summary>
    internal static string? ReadParseTree(string sql, NativeParseResult result)
    {
        return Utf8Offsets.ToCharacterOffsets(sql, ReadNullableUtf8String(result.ParseTree));
    }

    internal static byte[] ReadBytes(NativeProtobuf protobuf)
    {
        if (protobuf.Length == 0 || protobuf.Data == IntPtr.Zero)
        {
            return [];
        }

        if (protobuf.Length > int.MaxValue)
        {
            throw new InvalidOperationException($"Native protobuf payload too large to marshal: {protobuf.Length} bytes.");
        }

        var bytes = new byte[(int)protobuf.Length];
        Marshal.Copy(protobuf.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static NativeError? ReadError(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        var nativeError = Marshal.PtrToStructure<NativePgQueryError>(value);
        return new NativeError(
            ReadNullableUtf8String(nativeError.Message),
            ReadNullableUtf8String(nativeError.FunctionName),
            ReadNullableUtf8String(nativeError.FileName),
            nativeError.LineNumber,
            nativeError.CursorPosition,
            ReadNullableUtf8String(nativeError.Context));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeProtobuf
    {
        internal readonly nuint Length;
        internal readonly IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeParseResult
    {
        internal readonly IntPtr ParseTree;
        internal readonly IntPtr StandardErrorBuffer;
        internal readonly IntPtr Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativePlpgsqlParseResult
    {
        internal readonly IntPtr Functions;
        internal readonly IntPtr Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeProtobufParseResult
    {
        internal readonly NativeProtobuf ParseTree;
        internal readonly IntPtr StandardErrorBuffer;
        internal readonly IntPtr Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePgQueryError
    {
        internal readonly IntPtr Message;
        internal readonly IntPtr FunctionName;
        internal readonly IntPtr FileName;
        internal readonly int LineNumber;
        internal readonly int CursorPosition;
        internal readonly IntPtr Context;
    }

    internal sealed record NativeError(
        string? Message,
        string? FunctionName,
        string? FileName,
        int LineNumber,
        int CursorPosition,
        string? Context);
}
