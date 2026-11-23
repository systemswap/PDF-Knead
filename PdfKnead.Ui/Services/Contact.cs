using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace PdfKnead.Services;

/// <summary>
/// The contact address of the project, shown in the window that invites support, in „Über PDF-Knead“ and on the start page. A click opens
/// the mail program of the system with the address; the program itself sends nothing. The context menu copies the address, for those
/// without a mail program.
/// </summary>
public static class Contact
{
    public const string Address = "pdf-knead@tuta.io";

    public static Uri MailTo { get; } = new("mailto:" + Address);

    /// <summary>Opens an address in the system (tests set their own).</summary>
    public static Func<TopLevel, Uri, Task> Launch { get; set; } = async (top, uri) =>
    {
        if (top.Launcher is { } launcher) await launcher.LaunchUriAsync(uri);
    };

    /// <summary>Puts a text on the clipboard (tests set their own).</summary>
    public static Func<TopLevel, string, Task> Copy { get; set; } = async (top, text) =>
    {
        if (top.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    };

    public static Task WriteAsync(TopLevel top) => Launch(top, MailTo);

    public static Task CopyAsync(TopLevel top) => Copy(top, Address);
}
