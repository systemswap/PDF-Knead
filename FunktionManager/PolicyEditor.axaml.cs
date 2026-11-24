using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PdfKnead.Core.Policy;

namespace FunktionManager;

/// <summary>
/// Writes and reads the function file of an organization (<see cref="FeaturePolicy"/>): a card per ribbon tab with its functions, a tick
/// = allowed. Depends only on the core and the styles of the window.
/// </summary>
public sealed partial class PolicyEditor : UserControl
{
    private static readonly (PolicyUsage Usage, string Name)[] Usages =
    [
        (PolicyUsage.Commercial, "Gewerblich (Firma, auch selbstständig)"),
        (PolicyUsage.Authority, "Behörde oder öffentliche Stelle"),
    ];

    // where each tab stands: three columns of about the same height
    private static readonly Dictionary<string, int> Columns = new()
    {
        ["Start"] = 0, ["Formular"] = 0, ["E-Rechnung"] = 0,
        ["Seiten"] = 1, ["Einfügen"] = 1, ["Unterschrift"] = 1,
        ["Sicher"] = 2,
    };

    private readonly Dictionary<string, CheckBox> _boxes = [];
    private readonly List<(CheckBox Header, PolicyFunction[] Functions)> _headers = [];
    private HashSet<string> _unknown = [];
    private bool _updating;

    /// <summary>A message for the status line of the window: text, whether it is a warning.</summary>
    public event Action<string, bool>? Said;

    public PolicyEditor()
    {
        InitializeComponent();
        UsageBox.ItemsSource = Usages.Select(u => u.Name).ToList();
        UsageBox.SelectedIndex = 0;
        BuildTabs();
    }

    /// <summary>The function file as set in the editor.</summary>
    public FeaturePolicy Policy => new()
    {
        Usage = Usages[Math.Max(0, UsageBox.SelectedIndex)].Usage,
        Organization = (OrganizationBox.Text ?? "").Trim(),
        Denied = _boxes.Where(b => b.Value.IsChecked != true).Select(b => b.Key).Concat(_unknown).ToHashSet(StringComparer.Ordinal),
    };

    /// <summary>The folder the FunktionManager lies in — beside PDF-Knead in every package, where the function file belongs.</summary>
    internal static string ProgramFolder => Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } folder ? folder : AppContext.BaseDirectory;

    /// <summary>
    /// At the start: the function file beside the FunktionManager (berechtigungen.json — it lies beside PDF-Knead, the same folder) is read
    /// and shown, so that an admin changes the file in force instead of starting from nothing; a damaged one is said, the ticks then show
    /// the default (everything allowed).
    /// </summary>
    /// <param name="folder">The folder to look in.</param>
    public void LoadBeside(string folder)
    {
        var load = FeaturePolicy.Load(folder);
        switch (load.State)
        {
            case PolicyLoadState.Loaded:
                Show(load.Policy!);
                Said?.Invoke($"Gelesen: {FeaturePolicy.FileName} neben dem FunktionManager — {load.Policy!.Denied.Count} Funktionen gesperrt.", false);
                break;
            case PolicyLoadState.Unreadable:
                Said?.Invoke($"{FeaturePolicy.FileName} neben dem FunktionManager ist nicht lesbar — die Häkchen zeigen die Vorgabe (alles erlaubt).", true);
                break;
        }
    }

    /// <summary>Shows a function file.</summary>
    public void Show(FeaturePolicy policy)
    {
        OrganizationBox.Text = policy.Organization;
        UsageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Usages, u => u.Usage == policy.Usage));
        _updating = true;
        // a part is ticked as its own entry says (its parent may be off: then it is only greyed)
        foreach (var (id, box) in _boxes) box.IsChecked = !policy.Denied.Contains(id);
        _updating = false;
        // ids of a later version: kept as they are when saving again
        _unknown = policy.Denied.Where(id => !_boxes.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        RefreshParts();
        RefreshHeaders();
    }

    private void BuildTabs()
    {
        var stacks = Enumerable.Range(0, 3).Select(i =>
        {
            var stack = new StackPanel { Spacing = 14 };
            Grid.SetColumn(stack, i * 2);
            TabGrid.Children.Add(stack);
            return stack;
        }).ToArray();
        var next = 0;
        foreach (var (tab, functions) in PolicyCatalog.Tabs)
        {
            var column = Columns.TryGetValue(tab, out var c) ? c : next++ % 3;
            var list = new StackPanel();
            // the tab itself: ticks or clears all its functions, shows a dash when only some are allowed
            var header = new CheckBox { Content = new TextBlock { Text = tab, Classes = { "tab" } }, IsChecked = true, MinHeight = 26 };
            header.Click += (_, _) => SetAll(functions, header.IsChecked == true);
            _headers.Add((header, functions));
            list.Children.Add(header);
            // every function its own box under the tab (decided on 22.11.2026: „Programm“ alone did not say that it switches the donation
            // window); only a tab whose one function is the whole tab (Formular: „Alle Funktionen“, an id ending in „.all“) is switched by its heading
            if (!HeadingIsSwitch(functions))
                foreach (var function in functions)
                {
                    // a part of another function stands indented under it and can be set only while that one is allowed
                    var box = new CheckBox
                    {
                        Content = new TextBlock { Text = function.Name, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        IsChecked = true, MinHeight = 24, Margin = new Thickness(function.Parent is null ? 18 : 40, 0, 0, 0),
                    };
                    box.IsCheckedChanged += (_, _) =>
                    {
                        RefreshParts();
                        if (!_updating) RefreshHeaders();
                    };
                    _boxes[function.Id] = box;
                    list.Children.Add(box);
                }
            else
                // a tab with one switch (Formular: everything at once): the tab heading is the switch
                _boxes[functions[0].Id] = header;
            stacks[column].Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(14, 8, 14, 8), Child = list });
        }
    }

    /// <summary>Whether the tab heading itself is the switch: a tab whose one function stands for the whole tab (Formular).</summary>
    private static bool HeadingIsSwitch(PolicyFunction[] functions) => functions is [{ Id: var id }] && id.EndsWith(".all", StringComparison.Ordinal);

    private void SetAll(IEnumerable<PolicyFunction> functions, bool allowed)
    {
        _updating = true;
        foreach (var function in functions) _boxes[function.Id].IsChecked = allowed;
        _updating = false;
        RefreshHeaders();
    }

    /// <summary>A part of a function (<see cref="PolicyFunction.Parent"/>) can be set only while its parent is ticked.</summary>
    private void RefreshParts()
    {
        foreach (var function in PolicyCatalog.All)
            if (function.Parent is { } parent && _boxes.TryGetValue(function.Id, out var box) && _boxes.TryGetValue(parent, out var parentBox))
                box.IsEnabled = parentBox.IsChecked == true;
    }

    private void RefreshHeaders()
    {
        foreach (var (header, functions) in _headers)
        {
            if (HeadingIsSwitch(functions)) continue;
            var allowed = functions.Count(f => _boxes[f.Id].IsChecked == true);
            header.IsChecked = allowed == functions.Length ? true : allowed == 0 ? false : null;
        }
    }

    private void OnAllowAll(object? sender, RoutedEventArgs e) => SetAll(PolicyCatalog.All, true);

    private void OnDenyAll(object? sender, RoutedEventArgs e) => SetAll(PolicyCatalog.All, false);

    private static readonly FilePickerFileType JsonFile = new("Funktionsdatei") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Funktionsdatei öffnen", FileTypeFilter = [JsonFile], AllowMultiple = false });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            if (FeaturePolicy.Parse(copy.ToArray()) is not { } policy)
            {
                Said?.Invoke("Das ist keine lesbare Funktionsdatei (oder eine einer neueren Fassung).", true);
                return;
            }
            Show(policy);
            Said?.Invoke($"Geöffnet: {files[0].Name} — {policy.Denied.Count} Funktionen gesperrt.", false);
        }
        catch (IOException ex)
        {
            Said?.Invoke("Die Datei ließ sich nicht lesen: " + ex.Message, true);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Funktionsdatei speichern",
            // where it belongs: beside PDF-Knead, the folder of the FunktionManager
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(ProgramFolder),
            SuggestedFileName = FeaturePolicy.FileName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonFile],
        });
        if (file is null) return;
        try
        {
            var policy = Policy;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(policy.ToJson());
            var hint = file.Name == FeaturePolicy.FileName ? "" : $" — PDF-Knead liest sie nur unter dem Namen {FeaturePolicy.FileName}";
            Said?.Invoke($"Gespeichert: {file.Name}, {policy.Denied.Count} Funktionen gesperrt{hint}.", hint.Length > 0);
        }
        catch (IOException ex)
        {
            Said?.Invoke("Die Datei ließ sich nicht speichern: " + ex.Message, true);
        }
        catch (UnauthorizedAccessException)
        {
            Said?.Invoke("Dort darf nicht geschrieben werden — erst woanders speichern, dann mit Administratorrechten an seinen Platz kopieren.", true);
        }
    }
}
