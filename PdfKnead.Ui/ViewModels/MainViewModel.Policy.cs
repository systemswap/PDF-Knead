using PdfKnead.Core.Policy;

namespace PdfKnead.ViewModels;

/// <summary>
/// The function file of an organization (KONZEPT §5.27, <see cref="ActivePolicy"/>): functions the administrator switched off cannot be
/// started — greyed out on the ribbon (<see cref="Controls.PolicyLock"/>) and checked again by the commands. Viewing always works.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Whether the function file allows a function; says so when it does not.</summary>
    public bool EnsureAllowed(string id)
    {
        if (ActivePolicy.Allows(id)) return true;
        ShowNotice(PolicyRefusal(id));
        return false;
    }

    /// <summary>Why a function cannot be started.</summary>
    public static string PolicyRefusal(string id) =>
        Tr.T("„{0}“ ist durch die Funktionsdatei Ihrer Organisation gesperrt.", PolicyName(id));

    /// <summary>The name of a function as in the program.</summary>
    public static string PolicyName(string id) => PolicyCatalog.All.FirstOrDefault(f => f.Id == id) is { } function ? Tr.T(function.Name) : id;

    /// <summary>One line about the function file (settings, the window „Berechtigungen“).</summary>
    public static string PolicySummary()
    {
        var denied = PolicyCatalog.All.Count(f => !ActivePolicy.Allows(f.Id));
        var organization = ActivePolicy.Organization;
        return ActivePolicy.Current.State switch
        {
            PolicyLoadState.Loaded when organization.Length > 0 =>
                Tr.T("Festgelegt von {0} (Funktionsdatei neben dem Programm): {1} Funktionen gesperrt.", organization, denied),
            PolicyLoadState.Loaded => Tr.T("Festgelegt durch die Funktionsdatei neben dem Programm: {0} Funktionen gesperrt.", denied),
            PolicyLoadState.Unreadable => Tr.T("Die Funktionsdatei neben dem Programm ist nicht lesbar — zur Sicherheit sind alle einschränkbaren Funktionen gesperrt. Bitte wenden Sie sich an Ihre IT."),
            _ => Tr.T("Keine Funktionsdatei: alle Funktionen sind erlaubt."),
        };
    }

    /// <summary>Shows the permissions that apply (set by the view): everybody may see them.</summary>
    public Func<Task>? ShowPermissions { get; set; }

    /// <summary>Chooses a function file (set by the view); null when cancelled.</summary>
    public Func<Task<string?>>? PickPolicyFile { get; set; }

    /// <summary>The folder the function file is laid into: the one of the program (set by the tests).</summary>
    internal Func<string> PolicyFolder { get; set; } = () => Core.Updates.UpdateStart.ProgramLocation().Folder;

    /// <summary>Starts the program anew with the files to open (set by the tests); true when it started.</summary>
    internal Func<IReadOnlyList<string>, bool> RestartProgram { get; set; } = files => Core.Updates.ProgramRestart.Start(files);

    /// <summary>
    /// „Funktionsdatei einspielen …“: lays a chosen function file beside the program, so that nobody has to copy it by hand (needs write
    /// permission in the program folder, usually administrator rights). The file is read at the start: the program then starts anew by
    /// itself and opens the files again that were open — unless a document has unsaved changes; then it asks to save first.
    /// </summary>
    public async Task ImportPolicyAsync()
    {
        if (PickPolicyFile is null || await PickPolicyFile() is not { } source) return;
        var (result, detail) = FeaturePolicy.Install(source, PolicyFolder());
        if (result == PolicyInstallResult.Installed)
        {
            if (Services.OpenWindows.AnyUnsaved() || Documents.Any(d => d.IsModified))
            {
                ShowNotice(Tr.T("Funktionsdatei eingespielt. Sie gilt nach einem Neustart — bitte zuerst die Änderungen speichern und PDF-Knead dann neu starten."));
                return;
            }
            // every open window: its files open again in the new start
            var files = Services.OpenWindows.Main.Append(this).Distinct().SelectMany(vm => vm.Documents)
                .Where(d => !d.IsUntitled).Select(d => d.Path).ToList();
            if (RestartProgram(files))
            {
                (ShutDownForUpdate ?? Services.OpenWindows.ShutDown)();
                return;
            }
            ShowNotice(Tr.T("Funktionsdatei eingespielt ({0}). Sie gilt ab dem nächsten Start von PDF-Knead.", detail));
            return;
        }
        ShowNotice(result switch
        {
            PolicyInstallResult.NotAFunctionFile => Tr.T("Das ist keine lesbare Funktionsdatei — nichts geändert."),
            PolicyInstallResult.NoPermission => Tr.T("Im Programmordner darf nicht geschrieben werden ({0}). PDF-Knead dafür einmal als Administrator starten oder die Datei von der IT ablegen lassen.", detail),
            _ => Tr.T("Die Funktionsdatei ließ sich nicht einspielen: {0}", detail),
        });
    }

    // the function a tool belongs to
    private static string? PolicyIdOf(ViewTool tool) => tool switch
    {
        ViewTool.InsertText => "insert.content",
        ViewTool.InsertImage => "insert.images",
        ViewTool.InsertShape => "insert.shapes",
        ViewTool.InsertCode => "insert.codes",
        ViewTool.InsertNote => "insert.notes",
        ViewTool.InsertSignature => "signature.sign",
        ViewTool.InsertRedaction => "secure.redact-area",
        ViewTool.RedactText => "secure.redact-text",
        ViewTool.RedactHidden => "secure.redact-hidden",
        ViewTool.DesignForm or ViewTool.InsertFormField or ViewTool.InsertField => "form.all",
        ViewTool.Edit => "start.edit",
        ViewTool.Measure => "start.measure",
        _ => null,
    };
}
