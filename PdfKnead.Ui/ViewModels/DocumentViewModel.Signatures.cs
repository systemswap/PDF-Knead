using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.ViewModels;

/// <summary>
/// Digital signatures of the file (KONZEPT.md, section 5.17, step S4): checked in the background after opening and saving, shown
/// as a mark in the document's tab (a click opens the „Signaturen“ window); a hint at the first change that saving would invalidate them.
/// </summary>
public partial class DocumentViewModel
{
    private CancellationTokenSource? _signatureCheck;
    private int _signatureGeneration;
    private int? _intactSignatures;

    /// <summary>The results of the last check; empty if the file has no digital signatures (or the check is still running).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignatures), nameof(SignatureSummary), nameof(SignaturesValid),
        nameof(SignatureMarkGood), nameof(SignatureMarkBad), nameof(SignatureMarkTip))]
    public partial IReadOnlyList<SignatureCheck> SignatureChecks { get; private set; } = [];

    /// <summary>The signatures are being checked.</summary>
    [ObservableProperty]
    public partial bool CheckingSignatures { get; private set; }

    /// <summary>
    /// Whether a new digital signature could certify the document: it has no signature yet, and none waiting to be signed certifies it.
    /// </summary>
    public bool CanCertify => CanCertifyWith(null);

    /// <summary>Like <see cref="CanCertify"/>, for a signature chosen anew: its own certification does not count against it.</summary>
    /// <param name="editing">The signature being chosen anew; null for a new one.</param>
    /// <returns>true if it may certify.</returns>
    public bool CanCertifyWith(PageElement? editing) =>
        DigitalSignatures.Find(Document).Count == 0
        && !History.Pages.Any(p => p.Elements.Any(e => e.Digital?.Certify is not null && !ReferenceEquals(e, editing)));

    /// <summary>The file has digital signatures.</summary>
    public bool HasSignatures => SignatureChecks.Count > 0;

    /// <summary>All signatures are valid (also with unconfirmed identity).</summary>
    public bool SignaturesValid => SignatureChecks.All(SignatureTexts.IsValid);

    /// <summary>Green mark in the tab: signed, and all signatures valid.</summary>
    public bool SignatureMarkGood => HasSignatures && SignaturesValid;

    /// <summary>Red mark in the tab: a signature is invalid, changed after signing or could not be checked.</summary>
    public bool SignatureMarkBad => HasSignatures && !SignaturesValid;

    /// <summary>Tooltip of the mark in the tab.</summary>
    public string SignatureMarkTip => HasSignatures ? SignatureSummary + "\n" + Tr.T("Klicken für Einzelheiten") : "";

    /// <summary>The result in one line, for example „Digital unterschrieben – 2 gültig“ (tooltip of the mark, screen readers).</summary>
    public string SignatureSummary
    {
        get
        {
            var checks = SignatureChecks;
            if (checks.Count == 0) return "";
            var unconfirmed = checks.Count(c => c.Outcome == SignatureOutcome.Unconfirmed && SignatureTexts.IsValid(c));
            var valid = checks.Count(SignatureTexts.IsValid) - unconfirmed;
            var changed = checks.Count(c => c.Outcome == SignatureOutcome.ChangedAfterSigning);
            var invalid = checks.Count(c => c.Outcome == SignatureOutcome.Invalid);
            var parts = new List<string>();
            if (valid > 0) parts.Add(Tr.T("{0} gültig", valid));
            if (unconfirmed > 0) parts.Add(Tr.T("{0} nicht bestätigt", unconfirmed));
            if (changed > 0) parts.Add(Tr.T("{0} nach dem Unterschreiben geändert", changed));
            if (invalid > 0) parts.Add(Tr.T("{0} ungültig", invalid));
            if (checks.Count - valid - unconfirmed - changed - invalid is > 0 and var open) parts.Add(Tr.T("{0} nicht geprüft", open));
            var text = Tr.T("Digital unterschrieben – {0}", string.Join(", ", parts));
            // Valid, but nothing confirms who signed: say so here, not only in the details
            return valid > 0 && valid == checks.Count && checks.All(c => !c.IdentityConfirmed)
                ? text + " · " + Tr.T("Identität nicht bestätigt") : text;
        }
    }

    /// <summary>
    /// Checks the signatures of the file in the background; the result replaces <see cref="SignatureChecks"/>. The check opens the file
    /// once more for itself, so closing or saving the document meanwhile does not affect it.
    /// </summary>
    /// <returns>A task that completes when the result is shown (tests wait for it).</returns>
    public Task CheckSignaturesAsync()
    {
        _signatureCheck?.Cancel();
        _intactSignatures = null;
        var generation = ++_signatureGeneration;
        // also fields that only look signed: the check lists them as broken (the red mark warns of a forged look)
        if (IsUntitled || DigitalSignatures.Find(Document).Count == 0 && DigitalSignatures.Unusable(Document).Count == 0)
        {
            SignatureChecks = [];
            CheckingSignatures = false;
            return Task.CompletedTask;
        }
        var cancel = _signatureCheck = new CancellationTokenSource();
        var (path, password) = (Path, _password);
        CheckingSignatures = true;
        return Run();

        async Task Run()
        {
            IReadOnlyList<SignatureCheck> checks;
            try
            {
                checks = await Task.Run(() =>
                {
                    using var file = PdfDocument.Open(path, password);
                    return SignatureVerifier.Verify(file, password, cancel.Token);
                }, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfException or InvalidDataException)
            {
                checks = [];   // the file is gone or unreadable meanwhile: nothing to show
            }
            if (generation != _signatureGeneration || _closed) return;
            SignatureChecks = checks;
            CheckingSignatures = false;
        }
    }

    /// <summary>
    /// After a change: if the file carries intact signatures, says once per document that saving over it invalidates them — unless the
    /// change can be appended (filling in fields, comments, signature fields, digital signatures).
    /// </summary>
    private void NoteSignedChange()
    {
        if (_signedEditHintShown || !History.IsModified || IsUntitled) return;
        _intactSignatures ??= DigitalSignatures.CountIntact(Document);
        if (_intactSignatures == 0 || AppendsToSigned(null, out var reason)) return;
        _signedEditHintShown = true;
        var hint = Tr.T("Das Dokument ist digital unterschrieben: Speichern macht die Unterschrift ungültig. Mit „Speichern unter“ bleibt das unterschriebene Original erhalten.");
        Notify(reason is null ? hint : reason + " " + hint);
    }

    /// <summary>Stops a running check (closing).</summary>
    private void CancelSignatureCheck()
    {
        _signatureCheck?.Cancel();
        _signatureGeneration++;
    }
}
