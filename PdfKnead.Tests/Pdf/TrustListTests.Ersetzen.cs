using PdfKnead.Core.Pdf.Signatures.TrustLists;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Replacing the kept trusted lists by freshly fetched ones (docs/FEHLERGEDAECHTNIS.md, No. 1093): the full test run after a restart failed
/// once with „access denied“ when the fresh folder was moved into place — a virus scanner still reading the new files. The kept lists had
/// been deleted before the move and the fresh ones after it, so nothing was left.
/// </summary>
public sealed partial class TrustListTests
{
    /// <summary>A refused move is tried again; when it stays refused, the kept lists are there as before and nothing is left aside.</summary>
    [Fact]
    public void Replace_MoveRefused_TheKeptListsStay()
    {
        var (folder, fresh) = Folders();
        var (move, wait) = (TrustListStore.Move, TrustListStore.RetryWait);
        var waits = 0;
        try
        {
            TrustListStore.RetryWait = _ => waits++;
            TrustListStore.Move = (from, to) =>
            {
                if (from == fresh) throw new IOException("verweigert");
                move(from, to);
            };
            Assert.Throws<IOException>(() => TrustListStore.Replace(fresh, folder));
            Assert.Equal(5, waits);
            Assert.Equal("alt", File.ReadAllText(Path.Combine(folder, "liste.xml")));
            Assert.False(Directory.Exists(folder + ".alt"));
        }
        finally
        {
            (TrustListStore.Move, TrustListStore.RetryWait) = (move, wait);
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>A move refused twice and then allowed: the fresh lists in place, the kept ones removed.</summary>
    [Fact]
    public void Replace_MoveRefusedForAMoment_TheFreshListsInPlace()
    {
        var (folder, fresh) = Folders();
        var (move, wait) = (TrustListStore.Move, TrustListStore.RetryWait);
        var refused = 0;
        try
        {
            TrustListStore.RetryWait = _ => { };
            TrustListStore.Move = (from, to) =>
            {
                if (from == fresh && refused++ < 2) throw new UnauthorizedAccessException("verweigert");
                move(from, to);
            };
            TrustListStore.Replace(fresh, folder);
            Assert.Equal(3, refused);
            Assert.Equal("neu", File.ReadAllText(Path.Combine(folder, "liste.xml")));
            Assert.False(Directory.Exists(fresh));
            Assert.False(Directory.Exists(folder + ".alt"));
        }
        finally
        {
            (TrustListStore.Move, TrustListStore.RetryWait) = (move, wait);
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>Kept lists left aside by an interruption between the two moves come back before the next replacement.</summary>
    [Fact]
    public void Replace_KeptListsLeftAside_ComeBackFirst()
    {
        var (folder, fresh) = Folders();
        var (move, wait) = (TrustListStore.Move, TrustListStore.RetryWait);
        try
        {
            Directory.Move(folder, folder + ".alt");
            TrustListStore.RetryWait = _ => { };
            TrustListStore.Move = (from, to) =>
            {
                if (from == fresh) throw new IOException("verweigert");
                move(from, to);
            };
            Assert.Throws<IOException>(() => TrustListStore.Replace(fresh, folder));
            Assert.Equal("alt", File.ReadAllText(Path.Combine(folder, "liste.xml")));
        }
        finally
        {
            (TrustListStore.Move, TrustListStore.RetryWait) = (move, wait);
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    private static (string Folder, string Fresh) Folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"ersetzen-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "eu-listen");
        var fresh = folder + ".neu";
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "liste.xml"), "alt");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "liste.xml"), "neu");
        return (folder, fresh);
    }
}
