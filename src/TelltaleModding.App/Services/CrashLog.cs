using System.Text;

namespace TelltaleModding.App.Services;

/// <summary>Writes unhandled errors to crash.log in local app data.</summary>
public static class CrashLog
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelltaleModdingStudio", "crash.log");

    private static readonly object Gate = new();

    /// <summary>Hooks unhandled exceptions on every thread and in unobserved tasks.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.IsTerminating ? "unhandled (terminating)" : "unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Appends an entry. Never throws.</summary>
    public static void Write(string context, Exception? exception)
    {
        try
        {
            var entry = new StringBuilder()
                .Append("==== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · ").Append(context)
                .Append(" · v").Append(typeof(CrashLog).Assembly.GetName().Version).AppendLine()
                .AppendLine(exception?.ToString() ?? "(no exception object)")
                .AppendLine();
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, entry.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
