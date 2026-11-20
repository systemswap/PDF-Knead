namespace PdfKnead.Core.Platform;

/// <summary>
/// A key that signs each signature exactly once — a signature card (each signing may ask for the PIN), later a remote signing service or a
/// wallet (each signing is a release by the user; docs/ABLAUFPLAN.md, 9.4.2). For such a key the room of the signature block is sized before
/// the first signing (<see cref="Pdf.Signatures.CmsSignature.SizeFor"/>); if the block does not fit after all, nothing is written and it is
/// not signed again.
/// </summary>
public interface IRemoteKey
{
    /// <summary>Told before a save how many signatures it will make (a remote service can ask for one release for all of them).</summary>
    /// <param name="signatures">The number of signatures of this save.</param>
    void Expect(int signatures)
    {
    }
}
