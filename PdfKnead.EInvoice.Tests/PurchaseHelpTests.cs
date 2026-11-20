using PdfKnead.EInvoice.Ui;
using PdfKnead.Tests;
using PdfKnead.Tests.Ui;
using PdfKnead.ViewModels;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The help for the buyer (KONZEPT.md, section 5.24, „Hilfen für den Einkäufer“; concept approved by the user 15.11.2026). E1 the box „Zahlung“:
/// payee, accounts with their check digits, amount, due date, terms, reference — and a warning where an account or the amount of the XML does
/// not stand visibly in the PDF (the pattern of invoice fraud), never „another IBAN in the PDF“ (P2). Only invented invoices and accounts.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PurchaseHelpTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pdfknead-einkauf-" + Guid.NewGuid().ToString("N"));

    public PurchaseHelpTests()
    {
        Directory.CreateDirectory(_folder);
        EInvoiceModule.Register();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The PDF of the locator tests with the full test invoice embedded and these lines visible on its page.</summary>
    private string Write(string name, string invoice, params string[] visible)
    {
        var objects = InvoiceLocatorTests.Pdf();
        objects[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 11 0 R>>>>/Annots[8 0 R]>>";
        objects[4] = ("", string.Concat(visible.Select((line, i) => $"BT /F1 10 Tf 72 {760 - 20 * i} Td ({line}) Tj ET\n")));
        objects[6] = ("/Type/EmbeddedFile/Subtype/text#2Fxml", InvoiceLocatorTests.Bytes(invoice));
        objects[11] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, TestFiles.Build(objects));
        return path;
    }

    private static async Task<EInvoicePanelViewModel> Check(MainViewModel vm, string path)
    {
        await vm.OpenAsync([path]);
        var doc = vm.SelectedDocument!;
        var tab = new EInvoiceTabViewModel(vm);
        tab.Shown(doc);
        await tab.Searching;
        tab.CheckCommand.Execute(null);
        var check = (EInvoicePanelViewModel)Assert.IsType<EInvoicePanel>(doc.ModulePanel).DataContext!;
        await check.Checking;
        return check;
    }

    [Fact]
    public void Payment_Facts_FromTheXml()
    {
        var payment = InvoicePayment.Of(InvoiceReaderTests.Read(InvoiceReaderTests.Cii));
        Assert.True(payment.HasContent);
        Assert.Equal(["DE02 1203 0000 0000 2020 51", "DE02 5001 0517 0137 0750 30"], payment.Accounts.Select(a => InvoicePayment.Grouped(a.Iban.Text!)).ToArray());
        Assert.All(payment.Accounts, a => Assert.True(a.Valid));
        Assert.Equal("34.50", payment.Amount?.Text);
        // net, VAT and gross beside it (decided on 15.11.2026); nothing paid already
        Assert.Equal(("30.00", "4.50", "34.50"), (payment.Net?.Text, payment.Vat?.Text, payment.Gross?.Text));
        Assert.Null(payment.Prepaid);
        Assert.Equal("RE-2026-0001", payment.Reference?.Text);
        Assert.True(payment.PayeeIsSeller);
        Assert.False(payment.DirectDebit);
    }

    /// <summary>
    /// The box on a page that shows the first account and the amount: the second account warned (not visible), the amount not; a page without
    /// the amount warns of it too; check digits that fail are said in any case.
    /// </summary>
    [Fact]
    public void PaymentBox_WarnsWhereAccountOrAmountAreNotVisible()
    {
        var shows = Write("rechnung.pdf", InvoiceReaderTests.Cii, "Rechnung RE-2026-0001 vom 13.11.2026", "IBAN DE02 1203 0000 0000 2020 51", "Gesamt 34,50 EUR");
        var bare = Write("ohne.pdf", InvoiceReaderTests.Cii, "Rechnung RE-2026-0001 vom 13.11.2026");
        var broken = Write("pruefziffer.pdf", InvoiceReaderTests.Cii.Replace("DE02500105170137075030", "DE03500105170137075030", StringComparison.Ordinal),
            "Rechnung RE-2026-0001", "IBAN DE02 1203 0000 0000 2020 51", "DE03 5001 0517 0137 0750 30", "Gesamt 34,50 EUR");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var check = await Check(vm, shows);
            Assert.True(check.HasPayment);
            Assert.Contains(check.PaymentRows, r => r.Label == "IBAN" && r.Value.StartsWith("DE02 1203 0000 0000 2020 51", StringComparison.Ordinal)
                                                      && r.Value.EndsWith(Tr.T("Prüfziffer stimmt"), StringComparison.Ordinal));
            Assert.Contains(check.PaymentRows, r => r.Label == Tr.T("Zu zahlen"));
            Assert.Contains(check.PaymentRows, r => r.Label == Tr.T("Verwendungszweck") && r.Value.Contains("RE-2026-0001", StringComparison.Ordinal));
            var warning = Assert.Single(check.PaymentWarnings);
            Assert.Contains("DE02 5001 0517 0137 0750 30", warning, StringComparison.Ordinal);

            check = await Check(vm, bare);
            Assert.Equal(3, check.PaymentWarnings.Count);   // both accounts and the amount
            var amountShown = check.PaymentRows.Single(r => r.Label == Tr.T("Zu zahlen")).Value;
            Assert.Contains(Tr.T("Der zu zahlende Betrag {0} der E-Rechnung steht nicht sichtbar in der PDF.", amountShown), check.PaymentWarnings);

            check = await Check(vm, broken);
            Assert.Equal(Tr.T("Die Prüfziffer der IBAN {0} stimmt nicht — so ist die Bankverbindung nicht gültig.", "DE03 5001 0517 0137 0750 30"), Assert.Single(check.PaymentWarnings));
        });
    }

    /// <summary>
    /// A click on a row of the box marks all places of its value and each further click goes to the next (decided on 15.11.2026: „ob der
    /// Betrag wirklich der Endbetrag ist“); a row whose value does not stand on the page is no button.
    /// </summary>
    [Fact]
    public void PaymentBox_Click_MarksAllPlaces()
    {
        var path = Write("zweimal.pdf", InvoiceReaderTests.Cii, "Rechnung RE-2026-0001", "Zwischensumme 34,50 EUR", "IBAN DE02 1203 0000 0000 2020 51",
            "Zu zahlen 34,50 EUR");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var check = await Check(vm, path);
            var doc = vm.SelectedDocument!;
            // the amounts in the order the buyer reads them
            Assert.Equal([Tr.T("Netto"), Tr.T("Umsatzsteuer"), Tr.T("Brutto"), Tr.T("Zu zahlen")],
                check.PaymentRows.Select(r => r.Label).Where(l => l == Tr.T("Netto") || l == Tr.T("Umsatzsteuer") || l == Tr.T("Brutto")
                                                                 || l == Tr.T("Bereits gezahlt") || l == Tr.T("Zu zahlen")).ToArray());
            var amount = Assert.Single(check.PaymentRows, r => r.Label == Tr.T("Zu zahlen"));
            Assert.True(amount.HasPlace);
            Assert.Equal(Tr.T("S. {0} ({1} Stellen)", "1", 2), amount.Page);
            amount.RevealCommand.Execute(null);
            Assert.Equal(2, doc.ModulePlaces.Count);
            var first = doc.ModulePlace;
            amount.RevealCommand.Execute(null);
            Assert.NotEqual(first, doc.ModulePlace);                       // the second click: the other place
            Assert.Equal(doc.ModulePlaces[1], doc.ModulePlace);
            // the second account of the test invoice is not on the page: no button
            var accounts = check.PaymentRows.Where(r => r.Label == "IBAN").ToList();
            Assert.Equal(2, accounts.Count);
            Assert.True(accounts[0].HasPlace);
            Assert.False(accounts[1].HasPlace);
            Assert.Equal("", accounts[1].Page);
        });
    }

    /// <summary>
    /// E2, the mandatory details of § 14 Abs. 4 UStG as a section under the box (decided on 16.11.2026). The test invoice is one of
    /// small amount (34,50 €): fewer details needed, said so; above 250 € the buyer's street is missing (the test invoice names none) — the
    /// row and the header in the warning colour, the section open by itself; a profile that is no e-invoice gets no list.
    /// </summary>
    [Fact]
    public void Mandatory_SmallAmount_Missing_ProfileWithoutList()
    {
        var small = Write("klein.pdf", InvoiceReaderTests.Cii, "Rechnung RE-2026-0001", "Gesamt 34,50 EUR");
        var large = Write("gross.pdf", InvoiceReaderTests.Cii.Replace("<ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>",
            "<ram:GrandTotalAmount>340.00</ram:GrandTotalAmount>", StringComparison.Ordinal), "Rechnung RE-2026-0001");
        var minimum = Write("minimum.pdf", InvoiceReaderTests.Cii.Replace("urn:cen.eu:en16931:2017", "urn:factur-x.eu:1p0:minimum", StringComparison.Ordinal),
            "Rechnung RE-2026-0001");
        var dated = Write("datum.pdf", InvoiceReaderTests.Cii.Replace("<ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>",
            "<ram:GrandTotalAmount>340.00</ram:GrandTotalAmount>", StringComparison.Ordinal).Replace("Vielen Dank für den Auftrag", "Leistung am 11.09.2026",
            StringComparison.Ordinal), "Rechnung RE-2026-0001");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var check = await Check(vm, small);
            Assert.True(check.HasMandatory);
            Assert.Equal(Tr.T("Kleinbetragsrechnung bis 250 €: es genügen weniger Angaben (§ 33 UStDV)."), check.MandatoryNote);
            Assert.Contains(check.MandatoryRows, r => r.Text == Tr.T("Name und Anschrift des Leistungsempfängers") && r.State == Tr.T("nicht nötig"));
            Assert.Equal(check.MandatoryRows.Any(r => r.Missing), check.HasMandatoryMissing);
            Assert.Equal(check.HasMandatoryMissing, check.MandatoryOpen);

            check = await Check(vm, large);
            var buyer = Assert.Single(check.MandatoryRows, r => r.Text == Tr.T("Name und Anschrift des Leistungsempfängers"));
            Assert.True(buyer.Missing);
            Assert.Contains(Tr.T("Straße"), buyer.State, StringComparison.Ordinal);
            Assert.True(check.HasMandatoryMissing);
            Assert.True(check.MandatoryOpen);                                   // open by itself
            Assert.Equal("", check.MandatoryNote);
            Assert.DoesNotContain(Tr.T("alle vorhanden"), check.MandatorySummary, StringComparison.Ordinal);

            // a date only in the text: said in the header too, so that „alle vorhanden“ hides nothing
            check = await Check(vm, dated);
            Assert.EndsWith(", " + Tr.T("1 Angabe selbst prüfen"), check.MandatorySummary, StringComparison.Ordinal);
            Assert.Contains(check.MandatoryRows, r => r.Text == Tr.T("Zeitpunkt der Lieferung oder Leistung") && !r.Missing);

            check = await Check(vm, minimum);
            Assert.Equal(Tr.T("Pflichtangaben (§ 14 UStG): im sichtbaren Teil prüfen"), check.MandatorySummary);
            Assert.Single(check.MandatoryRows);
            Assert.False(check.HasMandatoryMissing);
        });
    }

    /// <summary>
    /// The time of the supply needs no field of its own: a date in a note or a line's description makes it not checkable, not missing (found in
    /// the collections, 16.11.2026: a physiotherapist names the dates of treatment in the description); a note without a date leaves it missing.
    /// </summary>
    [Fact]
    public void Mandatory_DeliveryDateOnlyInText_NotCheckable()
    {
        var large = InvoiceReaderTests.Cii.Replace("<ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>", "<ram:GrandTotalAmount>340.00</ram:GrandTotalAmount>",
            StringComparison.Ordinal);
        MandatoryItem Delivery(string xml) => InvoiceMandatory.Check(InvoiceReaderTests.Read(xml)).Single(i => i.Key == "delivery");
        Assert.Equal(MandatoryState.Missing, Delivery(large).State);                  // „Vielen Dank für den Auftrag“: no date
        foreach (var note in new[] { "Leistung am 11.09.2026", "Leistungszeitraum September 2026", "Livraison le 2026-09-11", "Termine: 3/9/26" })
        {
            var item = Delivery(large.Replace("Vielen Dank für den Auftrag", note, StringComparison.Ordinal));
            Assert.Equal(MandatoryState.NotCheckable, item.State);
            Assert.Equal(["dateInText"], item.Missing);
        }
        Assert.False(InvoiceMandatory.HasDate("Bestellung 2026/4711, Preis 11.50"));
        Assert.False(InvoiceMandatory.HasDate("Mai-Aktion"));
    }

    /// <summary>
    /// E4: what a finding means for the buyer, after the BMF letter of 15.10.2025 — the schema is the format (Rn. 6a), a rule on a term of
    /// § 14 Abs. 4 or § 14a UStG a mandatory detail (Rn. 35a), the PDF around it and its metadata technical, the rest another business rule
    /// (of no account for VAT, Rn. 35a — the letter's own example: the buyer reference BT-10).
    /// </summary>
    [Fact]
    public void Meaning_ByRuleAndTerm()
    {
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii);
        InvoiceField Term(string id) => data.Root.AllFields().First(f => f.Id == id);
        InvoiceRuleViolation V(string rule, InvoiceField? field, InvoiceGroup? group = null, string description = "") =>
            new(new InvoiceRule(rule, "Test", InvoiceRuleSeverity.Fatal, description, ""), group ?? data.Root, field);
        Assert.Equal(InvoiceMeaning.Format, InvoiceFindingMeaning.Of(V("PDFK-XSD-01", Term("BT-1"))));          // the schema wins over the term
        Assert.Equal(InvoiceMeaning.Technical, InvoiceFindingMeaning.Of(V("BR-HYBRID-06", null)));
        Assert.Equal(InvoiceMeaning.Technical, InvoiceFindingMeaning.Of(V("PDFK-PDF-01", null)));
        Assert.Equal(InvoiceMeaning.Technical, InvoiceFindingMeaning.Of(V("PDFK-FX-02", Term("BT-1"))));
        Assert.Equal(InvoiceMeaning.MandatoryDetail, InvoiceFindingMeaning.Of(V("BR-CO-14", Term("BT-110"))));   // the VAT total
        Assert.Equal(InvoiceMeaning.MandatoryDetail, InvoiceFindingMeaning.Of(V("BR-06", Term("BT-27"))));       // the seller's name
        var breakdown = data.Root.AllGroups().First(g => g.Id == "BG-23");
        Assert.Equal(InvoiceMeaning.MandatoryDetail, InvoiceFindingMeaning.Of(V("BR-CO-17", null, breakdown)));
        // a rule on the group without a term: by the terms its description names (found in the collections, 16.11.2026)
        Assert.Equal(InvoiceMeaning.MandatoryDetail, InvoiceFindingMeaning.Of(V("BR-IC-11", null, breakdown, "verlangt das Lieferdatum (BT-72) oder den Rechnungszeitraum (BG-14)")));
        Assert.Equal(InvoiceMeaning.BusinessRule, InvoiceFindingMeaning.Of(V("BR-IC-12", null, breakdown, "verlangt das Lieferland (BT-80)")));
        Assert.Equal(InvoiceMeaning.BusinessRule, InvoiceFindingMeaning.Of(V("BR-DE-15", null)));                // e.g. the buyer reference
        Assert.Equal(InvoiceMeaning.BusinessRule, InvoiceFindingMeaning.Of(V("BR-DE-XX", Term("BT-83"))));       // the remittance information
        // the lines of „Was heißt das für Sie?“: the weightiest first, singular and plural, the kinds that may cost the input tax in the warning colour
        var rows = EInvoicePanelViewModel.Meanings([V("BR-HYBRID-06", null), V("BR-HYBRID-07", null), V("BR-CO-14", Term("BT-110"))]);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Warn);
        Assert.StartsWith(Tr.T("1 Auffälligkeit betrifft"), rows[0].Text, StringComparison.Ordinal);
        Assert.Contains("Rn. 35a", rows[0].Text, StringComparison.Ordinal);
        Assert.False(rows[1].Warn);
        Assert.StartsWith(Tr.T("{0} Auffälligkeiten betreffen", 2), rows[1].Text, StringComparison.Ordinal);
        // a profile that is no e-invoice: said first, nothing in the warning colour, no „counts as an other invoice“
        rows = EInvoicePanelViewModel.Meanings([V("PDFK-XSD-01", null), V("BR-CO-14", Term("BT-110"))], eInvoice: false);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(r.Warn));
        Assert.DoesNotContain(rows, r => r.Text.Contains("Rn. 6a", StringComparison.Ordinal));
        Assert.Empty(EInvoicePanelViewModel.Meanings([]));
    }

    /// <summary>
    /// E4 at the top: number, date, seller, buyer and tax number of the XML that stand nowhere visibly in the PDF — said there with what it
    /// means; what stands visibly is not named; a page without readable text says nothing of it.
    /// </summary>
    [Fact]
    public void Deviation_IdentityNotVisible_AtTheTop()
    {
        var other = Write("andere.pdf", InvoiceReaderTests.Cii, "Rechnung RE-2026-0001", "Gesamt 34,50 EUR");
        var same = Write("gleich.pdf", InvoiceReaderTests.Cii, "Rechnung RE-2026-0001 vom 13.11.2026", "Musterfirma Beispiel GmbH, Musterweg 1, 12345 Musterstadt",
            "An Max Mustermann, 54321 Beispielstadt", "USt-IdNr. DE123456789", "Gesamt 34,50 EUR");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var check = await Check(vm, other);
            Assert.True(check.HasDeviation);
            Assert.StartsWith(Tr.T("Nicht sichtbar in der PDF: {0}", "")[..10], check.DeviationText, StringComparison.Ordinal);
            Assert.DoesNotContain(Tr.T("Rechnungsnummer"), check.DeviationText, StringComparison.Ordinal);   // it stands on the page
            Assert.Contains(Tr.T("Verkäufer") + " Musterfirma Beispiel GmbH", check.DeviationText, StringComparison.Ordinal);
            Assert.Contains(Tr.T("USt-IdNr.") + " DE123456789", check.DeviationText, StringComparison.Ordinal);
            check = await Check(vm, same);
            Assert.False(check.HasDeviation, check.DeviationText);
        });
    }

    /// <summary>The test invoice without its payment means (no account in the XML).</summary>
    private static string WithoutAccounts() => System.Text.RegularExpressions.Regex.Replace(InvoiceReaderTests.Cii,
        @"<ram:SpecifiedTradeSettlementPaymentMeans>.*?</ram:SpecifiedTradeSettlementPaymentMeans>\s*", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>
    /// Found in the analysis of the collections (15.11.2026): an account number that is no IBAN gets no verdict on check digits; an XML without
    /// any account says so in a row and warns (decided on 15.11.2026: in the warning colour, a note in grey was overlooked).
    /// </summary>
    [Fact]
    public void PaymentBox_NoAccount_Warned_AccountNumber_NoVerdict()
    {
        var none = Write("ohne-konto.pdf", WithoutAccounts(), "Rechnung RE-2026-0001 vom 13.11.2026", "Gesamt 34,50 EUR");
        var number = Write("kontonummer.pdf", InvoiceReaderTests.Cii.Replace("DE02500105170137075030", "44421800", StringComparison.Ordinal),
            "Rechnung RE-2026-0001", "IBAN DE02 1203 0000 0000 2020 51", "Konto 44421800", "Gesamt 34,50 EUR");
        var minimum = Write("minimum-ohne-konto.pdf", WithoutAccounts().Replace("urn:cen.eu:en16931:2017", "urn:factur-x.eu:1p0:minimum", StringComparison.Ordinal),
            "Rechnung RE-2026-0001", "Gesamt 34,50 EUR");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var check = await Check(vm, none);
            // the row in its place (after the payee), in the warning colour (decided on 15.11.2026)
            var missing = Assert.Single(check.PaymentRows, r => r.Label == "IBAN" && r.Value == Tr.T("in der E-Rechnung nicht vorhanden"));
            Assert.True(missing.Warn);
            Assert.Equal(1, check.PaymentRows.ToList().IndexOf(missing));
            Assert.Contains(check.PaymentWarnings, w => w.Contains("IBAN", StringComparison.Ordinal));
            Assert.True(check.HasPaymentWarnings);                            // the box in the warning colour
            Assert.DoesNotContain(check.PaymentNotes, n => n.Contains("IBAN", StringComparison.Ordinal));
            check = await Check(vm, number);
            Assert.Contains(check.PaymentRows, r => r.Label == Tr.T("Konto") && r.Value.StartsWith("44421800", StringComparison.Ordinal) && !r.Warn);
            Assert.Empty(check.PaymentWarnings);
            // a profile that is no e-invoice (MINIMUM holds no account at all): the visible part counts — no warning (found in the collections)
            check = await Check(vm, minimum);
            var visible = Assert.Single(check.PaymentRows, r => r.Label == "IBAN");
            Assert.Equal(Tr.T("im sichtbaren Teil prüfen"), visible.Value);
            Assert.False(visible.Warn);
            Assert.Empty(check.PaymentWarnings);
        });
    }

    /// <summary>
    /// The amount due warned only where it stands nowhere visibly: a short number seen only out of its context (stage 3 cannot say it is this
    /// one) is no warning, nor an amount of 0,00 — found in the collections (15.11.2026: „1,00“ in the totals of a small invoice).
    /// </summary>
    [Fact]
    public void Issues_AmountOutOfContext_OrZero_NoWarning()
    {
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii);
        var payment = InvoicePayment.Of(data);
        var amount = payment.Amount!;
        InvoiceComparisonResult Result(bool outOfContext) => new([new InvoiceMatch(amount, InvoiceMatchStatus.NotFound, null, null, true) { VisibleOutOfContext = outOfContext }], 1, 1, true);
        Assert.DoesNotContain(payment.Issues(Result(true)), i => i.Kind == InvoicePaymentIssue.AmountNotVisible);
        Assert.Contains(payment.Issues(Result(false)), i => i.Kind == InvoicePaymentIssue.AmountNotVisible);
        var zero = InvoicePayment.Of(InvoiceReaderTests.Read(InvoiceReaderTests.Cii.Replace("<ram:DuePayableAmount>34.50</ram:DuePayableAmount>",
            "<ram:DuePayableAmount>0.00</ram:DuePayableAmount>", StringComparison.Ordinal)));
        Assert.DoesNotContain(zero.Issues(new([new InvoiceMatch(zero.Amount!, InvoiceMatchStatus.NotFound, null, null, true)], 1, 1, true)),
            i => i.Kind == InvoicePaymentIssue.AmountNotVisible);
        // no account at all: noted as such
        var bare = InvoicePayment.Of(InvoiceReaderTests.Read(WithoutAccounts()));
        Assert.Contains(bare.Issues(null), i => i.Kind == InvoicePaymentIssue.NoAccount);
        // no amount due (a ZUGFeRD 1 BASIC correction): the total instead; negative, nothing to pay, so no note on the account
        var correction = InvoicePayment.Of(InvoiceReaderTests.Read(System.Text.RegularExpressions.Regex.Replace(WithoutAccounts(),
            @"<ram:DuePayableAmount>[^<]*</ram:DuePayableAmount>", "").Replace("<ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>",
            "<ram:GrandTotalAmount>-8.79</ram:GrandTotalAmount>", StringComparison.Ordinal)));
        Assert.Null(correction.Amount);
        Assert.Equal("-8.79", correction.Gross?.Text);
        Assert.True(correction.HasContent);
        Assert.DoesNotContain(correction.Issues(null), i => i.Kind == InvoicePaymentIssue.NoAccount);
    }

    /// <summary>
    /// The box on real invoices, for the eye (decided on 15.11.2026: „auch außerhalb unserer Testpdfs“): only with PDFKNEAD_ECHTE_RECHNUNGEN
    /// (a text file, one PDF per line) and PDFKNEAD_SCREENSHOTS. Each file is opened in the main window, „Prüfen“ pressed, the box written to
    /// „kasten.txt“ (rows, warnings, notes) and the window saved as „kasten-NNN.png“. Asserts nothing about the invoices — they are looked at.
    /// </summary>
    [Fact]
    public void PaymentBox_RealInvoices_ForTheEye()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ECHTE_RECHNUNGEN") is not { Length: > 0 } list || UiSession.ScreenshotFolder() is not { } shots)
            return;
        var files = File.ReadAllLines(list).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToList();
        var report = new List<string>();
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new Views.MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                for (var i = 0; i < files.Count; i++)
                {
                    report.Add($"## {i + 1:000} {files[i]}");
                    try
                    {
                        var check = await Check(vm, files[i]);
                        if (!check.HasPayment) report.Add("kein Kasten");
                        foreach (var w in check.PaymentWarnings) report.Add("Warnung: " + w);
                        foreach (var r in check.PaymentRows) report.Add($"Zeile: {r.Label} = {r.Value}" + (r.HasPlace ? $"  [{r.Page}]" : "") + (r.Warn ? "  (orange)" : ""));
                        foreach (var n in check.PaymentNotes) report.Add("Hinweis: " + n);
                        if (check.HasDeviation) report.Add("Abweichung: " + check.DeviationText);
                        foreach (var m in check.MeaningRows) report.Add("Bedeutung" + (m.Warn ? " (orange)" : "") + ": " + m.Text);
                        if (check.HasMandatory) report.Add("Pflicht: " + check.MandatorySummary + (check.MandatoryNote.Length > 0 ? " — " + check.MandatoryNote : ""));
                        foreach (var m in check.MandatoryRows.Where(m => m.Missing || m.State.Contains("389", StringComparison.Ordinal)))
                            report.Add($"Pflicht fehlt: {m.Text} — {m.State}");
                        window.UpdateLayout();
                        UiSession.Capture(window).Save(Path.Combine(shots, $"kasten-{i + 1:000}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    }
                    catch (Exception e)
                    {
                        report.Add("Fehler: " + e.GetType().Name + " " + e.Message);
                    }
                    finally
                    {
                        if (vm.SelectedDocument is { } open) await vm.CloseDocumentCommand.ExecuteAsync(open);
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
        File.WriteAllLines(Path.Combine(shots, "kasten.txt"), report);
    }
}
