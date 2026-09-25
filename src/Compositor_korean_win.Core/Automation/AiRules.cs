namespace Compositor_korean_win.Core;

/// <summary>
/// The person's own rules for an assistant working in this editor: a text file they can edit,
/// handed to every assistant with the guide (<see cref="LiveRequests.Guide"/>) and with the MCP
/// server's instructions.
/// </summary>
/// <remarks>
/// <para>
/// The tools and how to call them are built in, so nothing a person does to a file can break them.
/// The rules are the part that is a matter of taste — how to name and group layers, what to ask
/// before doing — so they live in a file, start as the defaults, and are read again on every
/// request: an edit applies to the next one without restarting anything.
/// </para>
/// <para>
/// A missing or unreadable file falls back to the defaults rather than to no rules. Deleting the
/// file is how a person gets the defaults back; the window writes them out again when it starts.
/// </para>
/// </remarks>
public sealed class AiRules(string path, Func<string> defaults)
{
    /// <summary>Where the rules are kept.</summary>
    public string Path { get; } = path;

    /// <summary>The rules as the file has them now, or the defaults.</summary>
    public string Text
    {
        get
        {
            try
            {
                if (File.Exists(Path))
                {
                    string text = File.ReadAllText(Path).Trim();
                    if (text.Length > 0) return text;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return defaults().Trim();
        }
    }

    /// <summary>When the file last changed, so a changed rule can be read again before the next edit.</summary>
    public DateTime Version
    {
        get
        {
            try
            {
                return File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;
            }
            catch (IOException)
            {
                return DateTime.MinValue;
            }
        }
    }

    /// <summary>Writes the defaults out when there is no file yet. True when the file is there afterwards.</summary>
    public bool Ensure()
    {
        try
        {
            if (File.Exists(Path)) return true;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, defaults().Trim() + Environment.NewLine);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
