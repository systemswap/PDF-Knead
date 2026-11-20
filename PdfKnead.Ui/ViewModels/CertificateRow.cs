using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Platform;

namespace PdfKnead.ViewModels;

/// <summary>The groups of the certificate list (docs/ABLAUFPLAN.md, 9.4.0): own certificate, certificate files, signature card, Windows store.</summary>
public enum CertificateGroup
{
    Own,
    File,
    Card,
    Store,
}

/// <summary>
/// One certificate in the list of the window „Zertifikat“ (docs/ABLAUFPLAN.md, 9.4.0, step 1; docs/FEHLERGEDAECHTNIS.md, No. 721): name, where it
/// comes from, valid until, level, why it cannot sign (then it cannot be chosen) and a hint; exactly one row is the default.
/// </summary>
public sealed partial class CertificateRow : ObservableObject
{
    /// <param name="group">The group.</param>
    /// <param name="name">The holder (or the file name of a file not opened yet).</param>
    /// <param name="origin">Where it comes from: the file, the card, the issuer.</param>
    /// <param name="details">Valid until and level, as one line; null if not known (a file is opened only when signing).</param>
    /// <param name="problem">Why it cannot sign now; null if it can.</param>
    /// <param name="hint">What to know about the level; null if nothing.</param>
    /// <param name="choice">What is stored when it becomes the default.</param>
    /// <param name="item">The certificate of the store or the card behind the row; for an own certificate the path of its file; null for files.</param>
    public CertificateRow(CertificateGroup group, string name, string origin, string? details, string? problem, string? hint, SigningCertificateChoice choice,
        object? item = null)
    {
        Group = group;
        Name = name;
        Origin = origin;
        Details = details;
        Problem = problem;
        Hint = hint;
        Choice = choice;
        Item = item;
    }

    public CertificateGroup Group { get; }

    /// <summary>The heading of the group, shown above its first row.</summary>
    public string GroupTitle => Group switch
    {
        CertificateGroup.Own => Tr.T("Eigene Zertifikate"),
        CertificateGroup.File => Tr.T("Zertifikatsdateien"),
        CertificateGroup.Card => Tr.T("Signaturkarte oder Token"),
        _ => Tr.T("Zertifikatsspeicher von Windows"),
    };

    /// <summary>Whether the heading of the group is shown (the first row of its group).</summary>
    public bool FirstOfGroup { get; init; }

    public string Name { get; }

    public string Origin { get; }

    public string? Details { get; }

    public string? Problem { get; }

    public string? Hint { get; }

    /// <summary>Whether it can be chosen (nothing keeps it from signing).</summary>
    public bool Selectable => Problem is null;

    public bool HasDetails => !string.IsNullOrEmpty(Details);

    public bool HasProblem => Problem is not null;

    public bool HasHint => Hint is not null;

    public SigningCertificateChoice Choice { get; }

    /// <summary>The certificate of the store (<see cref="SigningCertificate"/>) or of the card (<c>TokenCertificate</c>) behind the row.</summary>
    public object? Item { get; }

    /// <summary>The level its signature can reach; null if not known.</summary>
    public Core.Pdf.Signatures.SignatureLevel? Level { get; init; }

    /// <summary>Whether a file can be taken off the list (the file itself stays).</summary>
    public bool IsFile => Group == CertificateGroup.File;

    /// <summary>Whether it is the own certificate (with export, password and removal in its menu).</summary>
    public bool IsOwn => Group == CertificateGroup.Own;

    /// <summary>Whether the row has buttons of its own (export, password and removal of an own certificate; a file off the list).</summary>
    public bool HasMenu => IsOwn || IsFile;

    /// <summary>The file of an own certificate (several are possible, decided on 11.11.2026); null for the other groups.</summary>
    public string? OwnFile => IsOwn ? Item as string : null;

    /// <summary>Whether an own certificate is protected by a password of its own.</summary>
    public bool Locked { get; init; }

    /// <summary>„Exportieren …“ of the own certificate (the command of the window; a menu does not reach the window's data).</summary>
    public System.Windows.Input.ICommand? Export { get; init; }

    /// <summary>„Passwort …“ of the own certificate.</summary>
    public System.Windows.Input.ICommand? Password { get; init; }

    /// <summary>„Entfernen“ of the own certificate.</summary>
    public System.Windows.Input.ICommand? Remove { get; init; }

    /// <summary>„Aus der Liste nehmen“ of a file (with the row as parameter).</summary>
    public System.Windows.Input.ICommand? Forget { get; init; }

    /// <summary>The default for new signatures (one option button for the whole list).</summary>
    [ObservableProperty] public partial bool IsDefault { get; set; }
}
