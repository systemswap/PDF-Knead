using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>The window that invites support, after a successful save (<see cref="SupportPrompt"/>).</summary>
public partial class MainViewModel
{
    /// <summary>Shows the window that invites support (set by the view): true after „Unterstützen“, otherwise false or null.</summary>
    public Func<Task<bool?>>? ShowSupportInvitation { get; set; }

    /// <summary>The day that counts for the window (tests set their own).</summary>
    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// Right after a successful „Speichern“ or „Speichern unter“: the window, when it is due. „Unterstützen“ opens the page in the browser of
    /// the system; the window then waits 18 months, after „Vielleicht später“ or closing 9.
    /// </summary>
    private async Task OfferSupportAsync()
    {
        var today = Today();
        if (ShowSupportInvitation is not { } show || !SupportPrompt.Due(today)) return;
        var supported = await show() == true;
        SupportPrompt.Answered(today, supported);
        if (supported) await DonateAsync();
    }
}
