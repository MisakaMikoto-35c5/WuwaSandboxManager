using System.Globalization;
using System.Text;

namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// The one-line commands the host drops into the read side's input queue
/// (<c>input-*.txt</c>). Version 2 of the mirror protocol adds the structured
/// grammar (<see cref="Key"/>, Unicode <see cref="Text"/>, counted <see cref="Click"/>);
/// the lossy legacy grammar (<see cref="Keys"/>) stays for read sides that publish
/// <see cref="ContainerDesktopMirror.LegacyVersion"/>.
/// </summary>
public static class MirrorInputCommands
{
    /// <summary>Moves the pointer: <c>move x,y</c>.</summary>
    public static string MovePointer(int x, int y)
        => string.Create(CultureInfo.InvariantCulture, $"move {x},{y}");

    /// <summary>Clicks: <c>click x,y,button[,count]</c> (button: left, right, middle).</summary>
    public static string Click(int x, int y, string button = "left", int count = 1)
        => count > 1
            ? string.Create(CultureInfo.InvariantCulture, $"click {x},{y},{button},{count}")
            : string.Create(CultureInfo.InvariantCulture, $"click {x},{y},{button}");

    /// <summary>Scrolls: <c>wheel delta</c>.</summary>
    public static string Wheel(int delta)
        => string.Create(CultureInfo.InvariantCulture, $"wheel {delta}");

    /// <summary>One structured key event: <c>key &lt;vk-decimal&gt; &lt;down|up&gt;</c> (version 2).</summary>
    public static string Key(int virtualKey, bool down)
        => string.Create(CultureInfo.InvariantCulture, $"key {virtualKey} {(down ? "down" : "up")}");

    /// <summary>Unicode text: <c>text &lt;string&gt;</c> (version 2 injects KEYEVENTF_UNICODE).</summary>
    public static string Text(string text)
        => "text " + text;

    /// <summary>Legacy key chord: <c>keys CTRL+S</c> (version 1 read sides).</summary>
    public static string Keys(string chord)
        => "keys " + chord;

    /// <summary>Brings a container window to the foreground: <c>activate id</c>.</summary>
    public static string Activate(long id)
        => string.Create(CultureInfo.InvariantCulture, $"activate {id}");

    /// <summary>Moves a container window: <c>movewindow id x,y</c>.</summary>
    public static string MoveWindow(long id, int x, int y)
        => string.Create(CultureInfo.InvariantCulture, $"movewindow {id} {x},{y}");

    /// <summary>Resizes a container window: <c>resize id w,h</c>.</summary>
    public static string ResizeWindow(long id, int width, int height)
        => string.Create(CultureInfo.InvariantCulture, $"resize {id} {width},{height}");

    /// <summary>Asks a container window to close: <c>close id</c>.</summary>
    public static string CloseWindow(long id)
        => string.Create(CultureInfo.InvariantCulture, $"close {id}");

    /// <summary>Starts a program in the container's session: <c>launch exe [args]</c>.</summary>
    public static string Launch(string command)
        => "launch " + command;

    /// <summary>
    /// Sets the session clipboard: <c>clipboard &lt;base64-utf8&gt;</c>. The text travels
    /// base64-encoded so newlines and arbitrary Unicode survive the file transport.
    /// </summary>
    public static string Clipboard(string text)
        => "clipboard " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Reads a <see cref="Clipboard"/> command back; false for any other command.</summary>
    public static bool TryParseClipboard(string? command, out string text)
    {
        text = string.Empty;
        const string prefix = "clipboard ";
        if (command is null || !command.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(command[prefix.Length..].Trim()));
            return true;
        }
        catch (FormatException)
        {
            text = string.Empty;
            return false;
        }
    }
}
