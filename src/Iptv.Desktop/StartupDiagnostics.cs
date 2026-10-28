using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Iptv.Desktop;

internal enum StartupStage { Enter, WindowConstructed, WindowOpened }

internal sealed class StartupDiagnostics : IDisposable
{
    internal static StartupDiagnostics? Current { get; set; }
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    internal StartupDiagnostics(string? logPath, bool software)
    {
        if (logPath is not null) _writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Write($"runtime={RuntimeInformation.ProcessArchitecture}; rendering={(software ? "software" : "default")}");
    }
    internal static (bool Software, string? LogPath) Parse(string[] args)
    {
        bool software = false;
        string? log = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--software-rendering" && !software) software = true;
            else if (args[i] == "--startup-log" && log is null && ++i < args.Length)
            {
                if (string.IsNullOrWhiteSpace(args[i]) || !Path.IsPathFullyQualified(args[i]) || args[i].Any(char.IsControl))
                    throw new ArgumentException("Invalid startup arguments");
                log = Path.GetFullPath(args[i]);
            }
            else throw new ArgumentException("Invalid startup arguments");
        }
        return (software, log);
    }
    internal void Stage(StartupStage stage) => Write("stage=" + (stage switch
    {
        StartupStage.Enter => "Enter", StartupStage.WindowConstructed => "WindowConstructed", StartupStage.WindowOpened => "WindowOpened", _ => "Unknown"
    }));
    internal void Failure(Exception exception)
    {
        var current = exception;
        for (var depth = 0; current is not null && depth < 4; depth++, current = current.InnerException)
        {
            var type = current.GetType();
            var ownOrFramework = AllowedAssembly(type.Assembly.GetName().Name);
            Write($"exception={(ownOrFramework ? type.FullName : "ExternalException")}; hresult=0x{current.HResult:X8}");
            foreach (var frame in (new StackTrace(current, false).GetFrames() ?? Array.Empty<StackFrame>()).Take(16))
            {
                var method = frame.GetMethod();
                if (method?.DeclaringType is not { } declaring || !AllowedAssembly(declaring.Assembly.GetName().Name)) continue;
                // Method metadata only, never StackTrace.ToString()/file names/exception messages.
                Write("method=" + declaring.Name + "." + method.Name);
            }
        }
    }
    private static bool AllowedAssembly(string? name) => name == "Iptv.Desktop" || name == "Iptv.Core" || name == "System.Private.CoreLib"
        || name?.StartsWith("System.", StringComparison.Ordinal) == true || name?.StartsWith("Avalonia", StringComparison.Ordinal) == true;
    private void Write(string text)
    {
        lock (_gate)
        {
            try { if (_writer is not null) _writer.WriteLine(text); else Console.Error.WriteLine(text); }
            catch { try { Console.Error.WriteLine("Startup diagnostic log write failed"); } catch { } }
        }
    }
    public void Dispose() { try { _writer?.Dispose(); } catch { } }
}
