using System.Text;
using PdfKnead.Core.Pdf;

namespace PdfKnead.Tests;

public sealed class PdfHeaderTests
{
    [Theory]
    [InlineData("%PDF-1.7\n", "1.7")]
    [InlineData("%PDF-2.0\r\n%âãÏÓ", "2.0")]
    [InlineData("﻿%PDF-1.4\n", "1.4")]                 // preceded by a byte-order mark
    [InlineData("From: x@y\r\n\r\n%PDF-1.5\n", "1.5")]       // preceded by remnants of an e-mail header
    public void FindsVersion(string start, string expected) =>
        Assert.Equal(expected, PdfHeader.FindVersion(Encoding.UTF8.GetBytes(start)));

    [Theory]
    [InlineData("Hallo Welt")]
    [InlineData("%PDF-")]
    [InlineData("%PDF-x.y")]
    [InlineData("")]
    public void RejectsNonPdf(string start) => Assert.Null(PdfHeader.FindVersion(Encoding.UTF8.GetBytes(start)));

    [Fact]
    public void IgnoresMarkerAfterFirstKilobyte()
    {
        var bytes = Encoding.ASCII.GetBytes(new string(' ', PdfHeader.SearchLength) + "%PDF-1.7");
        Assert.Null(PdfHeader.FindVersion(bytes));
    }

    [Fact]
    public void ReadsFile() => Assert.Equal("1.7", PdfHeader.FindVersion(TestFiles.MinimalPdf()));
}

public sealed class AppSettingsTests
{
    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-settings-").FullName, "settings.json");
        var settings = new AppSettings("en", ThemeChoice.Dark, RibbonCollapsed: true, SidebarVisible: false)
            .WithRecent("/a.pdf").WithRecent("/b.pdf");
        settings.Save(file);

        var loaded = AppSettings.Load(file);
        Assert.Equal("en", loaded.Language);
        Assert.Equal(ThemeChoice.Dark, loaded.Theme);
        Assert.True(loaded.RibbonCollapsed);
        Assert.False(loaded.SidebarVisible);
        Assert.Equal(["/b.pdf", "/a.pdf"], loaded.Recent);
    }

    [Fact]
    public void BrokenFile_GivesDefaults()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-settings-").FullName, "settings.json");
        File.WriteAllText(file, "{ kaputt");
        Assert.Equal(new AppSettings().Theme, AppSettings.Load(file).Theme);
    }

    [Fact]
    public void Recent_NoDuplicates_NewestFirst_Limited()
    {
        var s = new AppSettings();
        for (var i = 0; i < AppSettings.MaxRecentFiles + 5; i++) s = s.WithRecent($"/{i}.pdf");
        s = s.WithRecent("/3.pdf");

        Assert.Equal(AppSettings.MaxRecentFiles, s.Recent.Count);
        Assert.Equal("/3.pdf", s.Recent[0]);
        Assert.Single(s.Recent, f => f == "/3.pdf");
    }

    [Fact]
    public void Version_IsShownWithoutHyphen() => Assert.Equal("0.1.5 beta", AppVersion.ForDisplay("0.1.5-beta"));
}

/// <summary>The settings shared by several windows of the program (FG 867); with the interface tests, which use the same settings file.</summary>
[Collection(nameof(Ui.UiCollection))]
public sealed class UserSettingsSharingTests
{
    /// <summary>
    /// Two windows of the program write the same file (FG 867): a change goes onto the file as it is now, so that one window does not undo
    /// what another changed; an unreadable file is no reason to write the defaults.
    /// </summary>
    [Fact]
    public void Update_GoesOntoTheFile_NotOntoTheCopyOfTheWindow()
    {
        var before = PdfKnead.Services.UserSettings.File;
        var file = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-settings-").FullName, "settings.json");
        try
        {
            PdfKnead.Services.UserSettings.File = file;
            new AppSettings(Theme: ThemeChoice.Dark).Save(file);
            PdfKnead.Services.UserSettings.Load();
            // another window switches „Dateien öffnen“ meanwhile …
            (AppSettings.Load(file) with { OpenInRunningWindow = true }).Save(file);
            // … and this window saves its recent files afterwards: the switch stays
            PdfKnead.Services.UserSettings.Update(s => s.WithRecent("/a.pdf"));
            var saved = AppSettings.Load(file);
            Assert.True(saved.OpenInRunningWindow);
            Assert.Equal(["/a.pdf"], saved.Recent);
            Assert.Equal(ThemeChoice.Dark, saved.Theme);
            // the file unreadable just now (another window writing): the copy of this window counts, not the defaults
            File.WriteAllText(file, "{ halb geschrieb");
            PdfKnead.Services.UserSettings.Update(s => s.WithRecent("/b.pdf"));
            saved = AppSettings.Load(file);
            Assert.Equal(ThemeChoice.Dark, saved.Theme);
            Assert.True(saved.OpenInRunningWindow);
            Assert.Equal(["/b.pdf", "/a.pdf"], saved.Recent);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.neu"));   // no temporary file left
        }
        finally
        {
            PdfKnead.Services.UserSettings.File = before;
            PdfKnead.Services.UserSettings.Load();
        }
    }
}
