#!/usr/bin/env python3
"""Mutation differential test of the business rules (KONZEPT.md, section 5.24, stage 4): the collections trigger only a few rules, so valid
invoices are changed systematically — every element removed, every number changed (larger, negative, zero, three decimals), every VAT
category code exchanged for each other one, every date moved into the past, every indicator flipped, every code made wrong, small or
padded, every scheme attribute removed or made wrong, every group that may repeat (VAT breakdown, allowance or charge, line, payment)
doubled, and a few pairs at once (reason and code of an allowance or charge, start and end of a period, all VAT identifiers of seller
and tax representative) — and every variant is checked by PDF-Knead and by the validation artefacts of EN 16931 (XSLT, only run, one Saxon
per syntax over the whole folder). Compared per variant are the identifiers of the rule families PDF-Knead implements; the differences
whose reason is known (REASONS, docs/ENTSCHEIDUNGEN.md) are counted apart. Local tool, nothing of it ships.

    python PdfKnead.EInvoice/Zuordnung/regelmutation.py <invoice.xml> [<invoice.xml> …] [--ausgabe <folder>] [--nur-vergleichen]

Needs what regelprobe.py needs. Only the Python standard library; the base invoices are read with ElementTree after a check for DOCTYPE.
"""
import copy
import glob
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor
from decimal import ROUND_FLOOR, Decimal, InvalidOperation

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
# the families compared: EN 16931, and of XRechnung 3.0 the national rules and those of Peppol (part d1), those of the extension, the
# temporary ones and those of CVD (part d2), the element rules of the profiles (FX-SCH-A, part e2) and the business rules of EXTENDED as
# the specification names them (BR-FXEXT, part e3) — not the warnings -08ini and -08rev of its artefacts (the specification lists only the
# rule -08b, which holds if either reading adds up), nor their date rules (CII-DT, of the syntax)
FAMILIES = re.compile(r'^(BR-(\d+|CO-\d+|DEC-\d+|B-\d+|CL-\d+|(S|Z|E|AE|IC|G|O|IG|IP)-\d+|DE-(TMP-|CVD-)?\d+(-[ab])?|DEX-\d+|TMP-(CVD-)?\d+'
                      r'|FXEXT-(\d+|BR-\d+(-\d)?|CO-\d+|(S|Z|E|AE|IC|G|O|IG|IP)-\d+b?))'
                      r'|PEPPOL-EN16931-R\d+(-\d)?|FX-SCH-A-\d+)$')
# rules PDF-Knead checks that the reference does not: of the standard BR-CO-25 (its artefacts 1.3.17 do not check it — those of EXTENDED
# do); of EXTENDED those the specification lists but its artefacts leave out (CO-22 and CO-24 ask what BR-38 and BR-44 ask)
NOT_IN_REFERENCE = {'BR-CO-25'}
NOT_IN_EXTENDED_REFERENCE = {'BR-FXEXT-CO-22', 'BR-FXEXT-CO-24'}


def not_in_reference(path, syntax):
    """The rules of PDF-Knead the reference does not check for this invoice (counted apart, not compared)."""
    return NOT_IN_EXTENDED_REFERENCE if syntax == 'cii' and fx_kind(path) == 'extended' else NOT_IN_REFERENCE


CATEGORIES = ['S', 'Z', 'E', 'AE', 'K', 'G', 'O', 'L', 'M']
CII = 'urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100'
# groups that may repeat: CII and UBL names
REPEATING = {'ApplicableTradeTax', 'SpecifiedTradeAllowanceCharge', 'IncludedSupplyChainTradeLineItem', 'SpecifiedTradePaymentTerms',
             'SpecifiedTradeSettlementPaymentMeans', 'AdditionalReferencedDocument', 'TaxSubtotal', 'AllowanceCharge', 'InvoiceLine',
             'CreditNoteLine', 'PaymentMeans', 'PaymentTerms', 'AdditionalDocumentReference'}


def reference_id(ident, syntax):
    """The artefacts of CII give the rules of IGIC (L) the identifiers BR-IP-n and those of IPSI (M) BR-IG-n — the other way round from
    the standard and from the artefacts of UBL. Compared is the identifier of the standard."""
    if syntax == 'cii' and re.match(r'^BR-I[GP]-\d+$', ident):
        return ('BR-IP-' if ident.startswith('BR-IG-') else 'BR-IG-') + ident[6:]
    # the artefacts of CII split PEPPOL-EN16931-R043 into -1 (document and line) and -2 (price): one rule of the specification
    if re.match(r'^PEPPOL-EN16931-R043-\d$', ident):
        return 'PEPPOL-EN16931-R043'
    return ident


# ------------------------------------------------------------------ known differences
# Where the validation artefacts depart from the standard, or the artefacts of CII and UBL from each other, PDF-Knead follows the standard —
# and what both artefacts do alike (docs/ENTSCHEIDUNGEN.md, stage 4, „Mutationstest“). Each such difference is recognised from the variant
# itself and counted with its reason; what is left is a difference to look at.
REASONS = {
    'aufschluesselung-ohne-posten': 'Aufschlüsselung ohne Posten ihrer Kategorie: die Prüfregeln verlangen auch die Umkehrung (CII bei Z, E, AE, K, G, O; '
                                    'UBL bei S, L, M), die Norm nur von den Posten her',
    'cii-zaehlt-posten': 'CII-Prüfregeln (S, L, M, O): zwei Posten der Kategorie gelten als Aufschlüsselung, bei O wird nur von der Aufschlüsselung her '
                         'geprüft — die Norm verlangt die Aufschlüsselung',
    'steuerart-fehlt': 'Steuerart fehlt (TypeCode, TaxScheme): PDF-Knead wertet die Kategorie in allen Regeln nicht als Umsatzsteuer, die '
                       'Prüfregeln nur in einem Teil',
    'behaelter-fehlt': 'Behälter fehlt ganz: die Prüfregeln prüfen die Pflichtangaben darin nicht, die Norm verlangt sie',
    'cii-anschrift': 'CII-Prüfregeln melden den Ländercode auch, wenn die ganze Anschrift fehlt (UBL und Norm: nur in einer Anschrift)',
    'leerer-behaelter': 'leeres Element bleibt stehen: die Prüfregeln melden die fehlende Angabe darin, PDF-Knead die fehlende Gruppe '
                        '(im Modell der Norm ist ein leeres Element keine Angabe)',
    'ungueltiges-datum': 'ungültiges Datum: die Prüfregeln vergleichen Zeichenketten, PDF-Knead vergleicht nur gültige Daten',
    'cii-verdeckt': 'CII-Prüfregeln: bei Aufschlüsselungen O, L, M verdeckt die Regel der Kategorie BR-45, BR-46, BR-CO-17 und BR-DEC-19/20 '
                    '(im Schematron greift je Muster nur die erste passende Regel)',
    'cii-l-satz': 'CII-Prüfregeln verlangen für L einen Satz größer null — die Norm (und UBL): null oder größer',
    'cii-lm-08': 'CII-Prüfregeln: 08 für L und M greift nie (Pfad zum Satz zeigt ins Leere)',
    'cii-o-11-13': 'CII-Prüfregeln zählen bei O-11 auch Positionen, bei O-13 auch Zuschläge (Norm: O-12, O-14); die Profil-Prüfregeln '
                   'ebenso bei O-12 und O-14',
    'fx-rundung': 'Profil-Prüfregeln runden bei den Summen (BR-CO-10 bis -15) auch den angegebenen Betrag; PDF-Knead vergleicht ihn wie '
                  'geschrieben, wie die EN-Prüfregeln (die dritte Nachkommastelle meldet BR-DEC)',
    'rundung-uneinheitlich': 'Rundung uneinheitlich: CII rundet oder lässt eine Einheit zu, UBL vergleicht genau oder rundet — PDF-Knead wie die '
                             'Norm genau',
    'grenze-eine-einheit': 'genau eine Einheit Abweichung: CII lässt sie zu, UBL nicht (PDF-Knead wie UBL, näher an der Norm)',
    'cii-co-15': 'CII-Prüfregeln: BR-CO-15 auch erfüllt, wenn Gesamtbetrag mit und ohne Umsatzsteuer gleich sind',
    'ubl-bt-110': 'UBL-Prüfregeln verlangen den Steuerbetrag (BT-110) bei BR-CO-14 und BR-CO-15, CII nicht (BT-110 ist optional; im UBL-Schema '
                  'ist TaxAmount Pflicht, Teil f)',
    'ubl-co-14': 'UBL-Prüfregeln prüfen BR-CO-14 nur, wenn es eine Aufschlüsselung gibt',
    'ubl-s-08': 'UBL-Prüfregeln verlangen zu jeder Aufschlüsselung S Posten mit ihrem Satz',
    'dec-13-15': 'Prüfregeln: BR-DEC-13 und BR-DEC-15 greifen in UBL nie, BR-DEC-13 in CII nicht neben einem zweiten Steuerbetrag',
    'dec-15-fehlt': 'Prüfregeln melden einen fehlenden BT-111 auch unter BR-DEC-15 (die Regel dafür ist BR-53)',
    'ubl-r055': 'XRechnung-Prüfregeln für UBL melden PEPPOL-EN16931-R055 auch ohne Steuerbetrag in Rechnungswährung (BT-110), die für CII '
                'nicht; die Spezifikation spricht von zwei Beträgen',
    'fx-co04-vat': 'Profil-Prüfregeln EXTENDED verlangen bei BR-FXEXT-CO-04 die Steuerart „VAT“ genau so geschrieben (bei der Kategorie '
                   'derselben Regel nicht); PDF-Knead liest die Steuerart überall ohne Groß-/Kleinschreibung — die Schreibweise meldet die '
                   'Elementregel des Profils (FX-SCH-A)',
    'fx-co15-sonst': 'Profil-Prüfregeln EXTENDED: BR-FXEXT-CO-15 schlägt ohne Steuerbetrag in Rechnungswährung immer an (ihr Sonst-Zweig '
                     'wird nie erreicht); die Spezifikation verlangt dann Gesamtbetrag mit gleich ohne Umsatzsteuer',
    'fx-01-gruppe': 'Profil-Prüfregeln EXTENDED: der Filter auf Positionen DETAIL in BR-FXEXT-…-01 sucht den Untertyp unter der Abrechnung '
                    'der Position und greift ins Leere — sie zählen auch Gruppen- und Informationspositionen; die Spezifikation nur DETAIL',
    'fx-12-ohne-kennung': 'Profil-Prüfregeln EXTENDED: BR-FXEXT-12 schlägt bei einer Gruppenposition ohne Kennung an (ohne Kennung finden '
                          'sie keine Unterposition und werten das als Fehler); die fehlende Kennung meldet BR-21',
    'dex-09-rundet':'XRechnung-Prüfregeln runden bei BR-DEX-09 beide Seiten auf den Cent; PDF-Knead rechnet genau wie bei BR-CO-16, dessen '
                     'Stelle die Regel in der Erweiterung einnimmt (Spezifikation: „entsprechen“)',
    'code-leerraum': 'Leerraum um einen Code: PDF-Knead nimmt ihn überall ab (wie die UBL-Prüfregeln der EN 16931, die Codelisten-Regeln '
                     'beider Syntaxen und das CII-Schema, xs:token), die Prüfregeln vergleichen in manchen Regeln genau (CII: BR-CO-04, BR-32, '
                     'BR-37, BR-47, BR-48, die Kategorieregeln; XRechnung: PEPPOL-EN16931-R101 und andere)',
}
TOTALS = {'SpecifiedTradeSettlementHeaderMonetarySummation', 'ApplicableHeaderTradeSettlement', 'SupplyChainTradeTransaction', 'LegalMonetaryTotal'}
CONTAINERS = {
    'BR-12': TOTALS, 'BR-13': TOTALS, 'BR-14': TOTALS, 'BR-15': TOTALS, 'BR-53': TOTALS,
    'BR-61': {'PayeePartyCreditorFinancialAccount', 'PayeeFinancialAccount'},
    'BR-CO-26': {'SellerTradeParty', 'ApplicableHeaderTradeAgreement', 'SupplyChainTradeTransaction', 'AccountingSupplierParty'},
    'BR-DE-2': {'SellerTradeParty', 'ApplicableHeaderTradeAgreement', 'SupplyChainTradeTransaction', 'AccountingSupplierParty'},
    # the profile rules ask BR-CO-15 only within the totals and BR-CO-18 only within the settlement
    'BR-CO-15': TOTALS, 'BR-CO-18': {'ApplicableHeaderTradeSettlement', 'SupplyChainTradeTransaction'},
}
EMPTY = {('BR-62', 'URIID'), ('BR-63', 'URIID'), ('BR-CO-09', 'CompanyID'), ('BR-52', 'IssuerAssignedID'), ('BR-29', 'DateTimeString'),
         ('BR-30', 'DateTimeString'), ('PEPPOL-EN16931-R110', 'DateTimeString'), ('PEPPOL-EN16931-R111', 'DateTimeString'),
         ('BR-55', 'InvoiceDocumentReference'), ('BR-52', 'ID'), ('BR-19', 'CountryID'), ('BR-20', 'CountryID'), ('BR-19', 'Country'), ('BR-20', 'Country'),
         ('BR-19', 'IdentificationCode'), ('BR-20', 'IdentificationCode'),
         # an address or a preceding invoice reference that held only this element (MINIMUM, BASIC)
         ('BR-08', 'CountryID'), ('BR-09', 'CountryID'), ('BR-10', 'CountryID'), ('BR-11', 'CountryID'), ('BR-55', 'IssuerAssignedID'),
         # a due date element left empty (the artefacts of EXTENDED check BR-CO-25 and see the element, not a date)
         ('BR-CO-25', 'DateTimeString')}
NAME = re.compile(r'^(?P<stem>.+?)-(?P<index>\d{4})-(?P<local>[A-Za-z]+)-(?P<kind>.+)$')
_bases = {}


def base_elements(inp, syntax, stem):
    """The elements of the unchanged invoice in document order (the index of a mutation counts in it) and their parents."""
    key = (syntax, stem)
    if key not in _bases:
        root = ET.parse(os.path.join(inp, syntax, f'{stem}-0000-unveraendert.xml')).getroot()
        _bases[key] = (list(root.iter()), {c: p for p in root.iter() for c in p})
    return _bases[key]


def categories(root, syntax):
    """VAT category codes of lines, of document level allowances and charges, of the breakdowns."""
    parents = {c: p for p in root.iter() for c in p}
    lines, charges, breakdowns = Counter(), Counter(), Counter()
    for e in root.iter():
        code = (e.text or '').strip()
        n, p = local(e.tag), parents.get(e)
        g = parents.get(p) if p is not None else None
        if p is None or g is None:
            continue
        if syntax == 'cii' and n == 'CategoryCode':
            if local(p.tag) == 'ApplicableTradeTax' and local(g.tag) == 'SpecifiedLineTradeSettlement':
                lines[code] += 1
            elif local(p.tag) == 'ApplicableTradeTax' and local(g.tag) == 'ApplicableHeaderTradeSettlement':
                breakdowns[code] += 1
            elif local(p.tag) == 'CategoryTradeTax' and local(g.tag) == 'SpecifiedTradeAllowanceCharge' and local(parents[g].tag) == 'ApplicableHeaderTradeSettlement':
                charges[code] += 1
        elif syntax == 'ubl' and n == 'ID':
            if local(p.tag) == 'ClassifiedTaxCategory':
                lines[code] += 1
            elif local(p.tag) == 'TaxCategory' and local(g.tag) == 'TaxSubtotal':
                breakdowns[code] += 1
            elif local(p.tag) == 'TaxCategory' and local(g.tag) == 'AllowanceCharge' and parents.get(g) is root:
                charges[code] += 1
    return lines, charges, breakdowns


def one_unit_apart(root):
    """Whether a VAT breakdown of a CII invoice has a tax exactly one unit away from taxable amount × rate / 100 (without signs, to the cent)."""
    for tax in root.iter():
        if local(tax.tag) != 'ApplicableTradeTax':
            continue
        values = {local(c.tag): (c.text or '').strip() for c in tax}
        if not {'CalculatedAmount', 'BasisAmount', 'RateApplicablePercent'} <= values.keys():
            continue
        try:
            t, b, r = (Decimal(values[k]) for k in ('CalculatedAmount', 'BasisAmount', 'RateApplicablePercent'))
        except InvalidOperation:
            continue
        # half a cent up, towards positive infinity, as the artefacts round
        expected = (abs(b) * r + Decimal('0.5')).to_integral_value(rounding=ROUND_FLOOR) / 100
        if abs(abs(t) - expected) == 1:
            return True
    return False


def total_in(root, syntax, currency):
    """Whether the invoice has a VAT total in the currency that element names (BT-110 with BT-5, BT-111 with BT-6), compared as written."""
    code = root.find(f'.//{{*}}{currency}')
    if code is None or code.text is None:
        return False
    totals = root.iter('{*}TaxTotalAmount') if syntax == 'cii' else (t for t in root.findall('{*}TaxTotal/{*}TaxAmount'))
    return any(t.get('currencyID') == code.text for t in totals)


def family_of(ident):
    """The VAT category code and the number of a rule of the categories (BR-S-01 → ('S', 1)); (None, 0) for the others."""
    family = re.match(r'^BR-([A-Z]+)-(\d+)$', ident)
    if not family:
        return None, 0
    code = {'S': 'S', 'Z': 'Z', 'E': 'E', 'AE': 'AE', 'IC': 'K', 'G': 'G', 'O': 'O', 'IG': 'L', 'IP': 'M'}.get(family.group(1))
    return code, int(family.group(2))


def guideline_of(root):
    """BT-24 of a CII invoice as written (stripped, lower case), '' if none."""
    for e in root.iter():
        if local(e.tag) == 'GuidelineSpecifiedDocumentContextParameter':
            for c in e:
                if local(c.tag) == 'ID':
                    return (c.text or '').strip().lower()
    return ''


def explain_content(root, syntax, ident, only_reference):
    """The reason of a known difference that the invoice alone shows (also for the collections, regelprobe.py), or None."""
    if syntax == 'cii':
        g = guideline_of(root)
        # the CII artefacts ask for the country code also where the whole address of the party is missing (seen in the invoice itself,
        # not only in a mutation that removed it)
        party = {'BR-09': 'SellerTradeParty', 'BR-11': 'BuyerTradeParty', 'BR-20': 'SellerTaxRepresentativeTradeParty'}.get(ident)
        if only_reference and party and any(local(e.tag) == party and not any(local(c.tag) == 'PostalTradeAddress' for c in e)
                                            for e in root.iter()):
            return 'cii-anschrift'
    if syntax == 'cii' and only_reference and ident == 'BR-FXEXT-CO-15':
        currency = next((e.text for e in root.iter() if local(e.tag) == 'InvoiceCurrencyCode'), None)
        if not any(local(e.tag) == 'TaxTotalAmount' and currency is not None and e.get('currencyID') == currency for e in root.iter()):
            return 'fx-co15-sonst'
    if syntax == 'cii' and only_reference and ident == 'BR-FXEXT-12':
        for item in (e for e in root.iter() if local(e.tag) == 'IncludedSupplyChainTradeLineItem'):
            doc = next((c for c in item if local(c.tag) == 'AssociatedDocumentLineDocument'), None)
            status = [c.text for c in (doc if doc is not None else []) if local(c.tag) == 'LineStatusReasonCode']
            ids = [(c.text or '').strip() for c in (doc if doc is not None else []) if local(c.tag) == 'LineID']
            if status == ['GROUP'] and not any(ids) and any(local(e.tag) == 'LineTotalAmount' for e in item.iter()):
                return 'fx-12-ohne-kennung'
    # a breakdown without items of its category: the artefacts of EXTENDED ask the other way round as well (BR-FXEXT-…-01), as those of
    # the standard do — the specification asks only from the items
    fxext = re.match(r'^BR-FXEXT-([A-Z]+)-01$', ident)
    if fxext and only_reference:
        code = {'IC': 'K', 'IG': 'L', 'IP': 'M'}.get(fxext.group(1), fxext.group(1))
        lines, charges, breakdowns = categories(root, syntax)
        if breakdowns[code] >= 1 and lines[code] + charges[code] == 0:
            return 'aufschluesselung-ohne-posten'
        # a group or information line of the category
        for item in (e for e in root.iter() if local(e.tag) == 'IncludedSupplyChainTradeLineItem'):
            status = [c.text for d in item if local(d.tag) == 'AssociatedDocumentLineDocument' for c in d if local(c.tag) == 'LineStatusReasonCode']
            taxes = [(c.findtext('{*}CategoryCode') or '').strip() for s in item if local(s.tag) == 'SpecifiedLineTradeSettlement'
                     for c in s if local(c.tag) == 'ApplicableTradeTax']
            if status and status != ['DETAIL'] and code in taxes:
                return 'fx-01-gruppe'
    code, number = family_of(ident)
    if code and number == 1:
        lines, charges, breakdowns = categories(root, syntax)
        if only_reference and breakdowns[code] >= 1 and lines[code] + charges[code] == 0:
            return 'aufschluesselung-ohne-posten'
        if not only_reference and syntax == 'cii' and breakdowns[code] == 0 and (
                code == 'O' or code in ('S', 'L', 'M') and lines[code] != 1 and charges[code] != 1):
            return 'cii-zaehlt-posten'
    if only_reference and syntax == 'cii' and ident in ('BR-IG-05', 'BR-IG-06', 'BR-IG-07'):
        return 'cii-l-satz'
    if not only_reference and syntax == 'cii' and ident in ('BR-IG-08', 'BR-IP-08'):
        return 'cii-lm-08'
    if only_reference and syntax == 'cii' and ident in ('BR-O-11', 'BR-O-13'):
        return 'cii-o-11-13'
    # the profile rules ask O-12 of every tax element and O-14 of every allowance and charge alike
    if only_reference and syntax == 'cii' and ident in ('BR-O-12', 'BR-O-14') and guideline_of(root).endswith((':minimum', ':basicwl', ':basic')):
        return 'cii-o-11-13'
    if not only_reference and syntax == 'cii' and ident in ('BR-CO-17', 'BR-S-09') and one_unit_apart(root):
        return 'grenze-eine-einheit'
    if not only_reference and syntax == 'cii' and ident == 'BR-CO-15':
        grand, net = root.find('.//{*}GrandTotalAmount'), root.find('.//{*}TaxBasisTotalAmount')
        try:
            if grand is not None and net is not None and Decimal(grand.text.strip()) == Decimal(net.text.strip()):
                return 'cii-co-15'
        except (InvalidOperation, AttributeError):
            pass
    if not only_reference and ident in ('BR-DEC-13', 'BR-DEC-15'):
        return 'dec-13-15'
    if only_reference and ident == 'BR-DEC-15' and not total_in(root, syntax, 'TaxCurrencyCode'):
        return 'dec-15-fehlt'
    if only_reference and syntax == 'ubl' and ident == 'BR-CO-15' and not total_in(root, syntax, 'DocumentCurrencyCode'):
        return 'ubl-bt-110'
    if only_reference and syntax == 'ubl' and ident == 'PEPPOL-EN16931-R055' and not total_in(root, syntax, 'DocumentCurrencyCode'):
        return 'ubl-r055'
    return None


def explain(inp, syntax, name, ident, only_reference):
    """The reason of a known difference of a variant — from its mutation or from its content — or None."""
    m = NAME.match(name[:-4])
    if not m or m.group('kind') == 'unveraendert':
        return None
    elements, parents = base_elements(inp, syntax, m.group('stem'))
    index, kind, element = int(m.group('index')), m.group('kind'), m.group('local')
    parent = parents.get(elements[index])
    parent_local = local(parent.tag) if parent is not None else ''
    code, number = family_of(ident)
    if (element == 'TypeCode' and parent_local in ('ApplicableTradeTax', 'CategoryTradeTax', 'AppliedTradeTax') or element == 'TaxScheme'
            or element == 'ID' and parent_local == 'TaxScheme') and kind in ('weg', 'code-falsch'):
        return 'steuerart-fehlt'
    if kind == 'code-raum':
        return 'code-leerraum'
    if only_reference and ident == 'BR-FXEXT-CO-04' and element == 'TypeCode' and parent_local == 'ApplicableTradeTax' and kind == 'code-klein':
        return 'fx-co04-vat'
    if not only_reference and kind == 'weg' and element in CONTAINERS.get(ident, ()):
        return 'behaelter-fehlt'
    if only_reference and syntax == 'cii' and ident in ('BR-09', 'BR-11', 'BR-20') and kind == 'weg' and element in (
            'PostalTradeAddress', 'SellerTradeParty', 'BuyerTradeParty', 'SellerTaxRepresentativeTradeParty', 'ApplicableHeaderTradeAgreement',
            'SupplyChainTradeTransaction'):
        return 'cii-anschrift'
    if kind == 'weg' and (ident, element) in EMPTY:
        return 'leerer-behaelter'
    if ident in ('BR-29', 'BR-30', 'PEPPOL-EN16931-R110', 'PEPPOL-EN16931-R111') and element in ('DateTimeString', 'StartDate', 'EndDate') and kind in ('negativ', 'null', 'mehr', 'dreistellig'):
        return 'ungueltiges-datum'
    if not only_reference and syntax == 'cii' and ident in ('BR-45', 'BR-46', 'BR-CO-17', 'BR-DEC-19', 'BR-DEC-20') and parent_local == 'ApplicableTradeTax' \
            and (parent.findtext('{*}CategoryCode') or '').strip() in ('O', 'L', 'M'):
        return 'cii-verdeckt'
    if kind == 'dreistellig' and (number == 8 and code or ident in ('BR-CO-13', 'BR-CO-16')):
        return 'rundung-uneinheitlich'
    if not only_reference and kind == 'dreistellig' and ident == 'BR-DEX-09':
        return 'dex-09-rundet'
    if not only_reference and kind == 'dreistellig' and re.match(r'^BR-CO-1[0-5]$', ident) and fx_kind(os.path.join(inp, syntax, name)):
        return 'fx-rundung'
    if only_reference and syntax == 'ubl' and ident == 'BR-CO-14' and (
            element in ('TaxTotal', 'TaxAmount') and parent_local in ('Invoice', 'CreditNote', 'TaxTotal')):
        return 'ubl-bt-110'
    if not only_reference and syntax == 'ubl' and ident == 'BR-CO-14' and element == 'TaxSubtotal' and kind == 'weg':
        return 'ubl-co-14'
    if only_reference and syntax == 'ubl' and ident == 'BR-S-08' and kind.startswith('kat-'):
        return 'ubl-s-08'
    return explain_content(ET.parse(os.path.join(inp, syntax, name)).getroot(), syntax, ident, only_reference)


XR_IDS = ('urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0',
          'urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0#conformant#urn:xeinkauf.de:kosit:extension:xrechnung_3.0',
          'urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0#compliant#urn:xeinkauf.de:kosit:xrechnung:cvd_0.9')


XR_KINDS = dict(zip(XR_IDS, ('cius', 'extension', 'cvd')))
# the scenarios of the reference checker count these rules of EN 16931 only as information (customLevel): in the extension BR-DEX-01 and
# BR-DEX-04 to -08 take their place, in UBL also BR-DEX-09 that of BR-CO-16 (specification XRechnung 3.0.2, chapter 17.2), in CVD
# BR-TMP-CVD-01 that of BR-CL-13 — PDF-Knead asks the rule that takes the place instead
DEMOTED = {
    ('extension', 'cii'): {'BR-CL-10', 'BR-CL-11', 'BR-CL-21', 'BR-CL-24', 'BR-CL-25', 'BR-CL-26'},
    ('extension', 'ubl'): {'BR-CL-10', 'BR-CL-11', 'BR-CL-21', 'BR-CL-24', 'BR-CL-25', 'BR-CL-26', 'BR-CO-16'},
    ('cvd', 'cii'): {'BR-CL-13'},
    ('cvd', 'ubl'): {'BR-CL-13'},
}


def head_of(path, size=1 << 18):
    """The start of an XML file without its comments and processing instructions: a long licence comment before the root (the official
    examples of ZUGFeRD 2.5.2 begin with one of several kilobytes) must not hide the root or the specification identifier (FG 840)."""
    data = open(path, 'rb').read(size)
    return re.sub(rb'<!--.*?-->|<\?.*?\?>', b'', data, flags=re.S)


def xr_kind(path):
    """Which specification of XRechnung 3.0 the identifier (BT-24) of an invoice names — 'cius', 'extension', 'cvd' — or None (no scenario
    of the reference checker for XRechnung)."""
    data = head_of(path)
    found = re.search(rb'<(?:cbc:CustomizationID|ram:ID)>([^<]*)</', data[:20000])
    if b'GuidelineSpecifiedDocumentContextParameter' in data[:20000]:
        found = re.search(rb'GuidelineSpecifiedDocumentContextParameter>\s*<ram:ID>([^<]*)</ram:ID>', data[:20000])
    return XR_KINDS.get(found.group(1).decode('utf-8', 'replace')) if found else None


# the profile rules of ZUGFeRD 2.5.2 / Factur-X 1.09.2 (XSLT of the package, only run locally): for an invoice of a profile whose rules
# PDF-Knead narrows (part e1) they are the reference for the business rules; the code list rules still come from the EN artefacts, where
# the specification lists them for the profile (not for MINIMUM)
FX_DIR = os.path.join(REPO, 'Normen-ISO', 'erechnung', 'zugferd', 'zugferd-2.5.2-factur-x-1.09.2-de',
                      '_Factur-X 1.09.2 Zugferd 2.5.2 - 2026 08 04 - FINAL DE', '4. FACTUR-X_1.09.2_XSD_SCHEMATRON')
FX_PROFILES = {'minimum': ('0. Factur-X_1.09.2_MINIMUM', 'MINIMUM'), 'basicwl': ('1. Factur-X_1.09.2_BASICWL', 'BASIC-WL'),
               'basic': ('2. Factur-X_1.09.2_BASIC', 'BASIC'), 'en16931': ('3. Factur-X_1.09.2_EN16931', 'EN16931'),
               'extended': ('4. Factur-X_1.09.2_EXTENDED', 'EXTENDED')}
# the profiles whose business rules are narrowed (parts e1, e3): their profile rules are the reference for all; for EN16931 only the
# element rules FX-SCH-A (part e2) come from the profile rules, the business rules from the EN artefacts. The code list rules (BR-CL) come
# from the EN artefacts where the specification lists them for the profile
FX_NARROWED = {'minimum', 'basicwl', 'basic', 'extended'}
FX_CODE_LISTS = {'basicwl', 'basic', 'extended'}


def fx_styles():
    return {p: os.path.join(FX_DIR, folder, f'_XSLT_{name}', f'FACTUR-X_{name}.xslt') for p, (folder, name) in FX_PROFILES.items()}


def fx_kind(path):
    """The profile of ZUGFeRD / Factur-X of a CII invoice ('minimum', 'basicwl', 'basic', 'en16931', 'extended'), read from BT-24 as
    InvoiceKind reads it (also the forms of ZUGFeRD 2.0 and Factur-X 1.0); None for any other."""
    data = head_of(path)
    if CII.encode() not in data[:20000]:
        return None
    found = re.search(rb'GuidelineSpecifiedDocumentContextParameter>\s*<(?:\w+:)?ID>([^<]*)</', data)
    g = found.group(1).decode('utf-8', 'replace').strip().lower() if found else ''
    if not g or ':order:' in g or g.startswith('urn:ferd:') or 'xrechnung' in g or 'peppol.eu' in g or 'extended-ctc-fr' in g:
        return None
    for suffix, profile in ((':minimum', 'minimum'), (':basicwl', 'basicwl'), (':basic', 'basic'), (':extended', 'extended')):
        if g.endswith(suffix):
            return profile
    if g in ('urn:cen.eu:en16931:2017', 'urn:cen.eu:en16931:2026') or g.endswith((':en16931', ':comfort')):
        return 'en16931'
    return None


def fx_id(ident):
    """An identifier of the profile rules as PDF-Knead writes it — and the specification: IGIC and IPSI as BR-IG and BR-IP (the profiles
    write BR-AF and BR-AG, as the standard does, also in BR-FXEXT-AF-01); the rule of the standard rate of EXTENDED as BR-FXEXT-S-08b (its
    artefact writes BR-FXEXT-S08b)."""
    if ident == 'BR-FXEXT-S08b':
        return 'BR-FXEXT-S-08b'
    m = re.match(r'^BR-(FXEXT-)?(AF|AG)-(\d+\w*)$', ident)
    return f'BR-{m.group(1) or ""}{"IG" if m.group(2) == "AF" else "IP"}-{m.group(3)}' if m else ident


def is_xrechnung(path):
    """Whether the specification identifier (BT-24) of an invoice is that of XRechnung 3.0 — the scenarios of the reference checker."""
    return xr_kind(path) is not None


def xr_styles(repo):
    base = os.path.join(repo, 'tools', 'pruefer', 'erechnung', 'kosit', 'xrechnung-konfiguration', 'resources', 'xrechnung', '3.0.2', 'xsl')
    return {s: os.path.join(base, f'XRechnung-{s.upper()}-validation.xsl') for s in ('cii', 'ubl')}


def find_java():
    for pattern in ('tools/pruefer/signaturen/*/java/bin/java.exe', 'tools/pruefer/signaturen/*/java/bin/java'):
        hits = glob.glob(os.path.join(REPO, pattern))
        if hits:
            return hits[0]
    return shutil.which('java')


def find_cli():
    for config in ('Release', 'Debug'):
        for name in ('pdf-knead.exe', 'pdf-knead'):
            path = os.path.join(REPO, 'PdfKnead.Cli', 'bin', config, 'net10.0', name)
            if os.path.exists(path):
                return path
    raise SystemExit('Kommandozeile nicht gebaut')


def local(tag):
    return tag.rsplit('}', 1)[-1]


def is_category(element, parent):
    # CII: ram:CategoryCode; UBL: cbc:ID inside a tax category
    return local(element.tag) == 'CategoryCode' or local(element.tag) == 'ID' and parent is not None and local(parent.tag) in (
        'TaxCategory', 'ClassifiedTaxCategory')


def mutations(root):
    """(name, mutate) pairs: mutate(copy of the tree) changes one thing."""
    parents = {c: p for p in root.iter() for c in p}
    elements = list(root.iter())
    out = []
    for index, e in enumerate(elements):
        parent = parents.get(e)
        name = f'{index:04d}-{local(e.tag)}'
        text = (e.text or '').strip()
        if parent is not None and index > 0:
            out.append((f'{name}-weg', ('remove', index)))
        if text and not len(e):
            try:
                value = Decimal(text)
                if value.is_finite() and re.fullmatch(r'-?\d+(\.\d+)?', text):
                    out.append((f'{name}-mehr', ('text', index, format(value + Decimal('1.37'), 'f'))))
                    out.append((f'{name}-negativ', ('text', index, format(-abs(value) - 1, 'f'))))
                    out.append((f'{name}-dreistellig', ('text', index, format(value.quantize(Decimal('0.001')) + Decimal('0.001'), 'f'))))
                    out.append((f'{name}-null', ('text', index, '0')))
            except InvalidOperation:
                pass
            if re.fullmatch(r'\d{8}', text):
                out.append((f'{name}-frueher', ('text', index, '19990101')))
            if re.fullmatch(r'\d{4}-\d{2}-\d{2}', text):
                out.append((f'{name}-frueher', ('text', index, '1999-01-01')))
        if is_category(e, parent) and text in CATEGORIES + ['B']:
            for other in CATEGORIES + ['B']:
                if other != text:
                    out.append((f'{name}-kat-{other}', ('text', index, other)))
        if text in ('true', 'false') and not len(e):
            out.append((f'{name}-umgekehrt', ('text', index, 'false' if text == 'true' else 'true')))
        # codes (stage 4, part c): a code no list has, the code in small letters, the code with space around it (still the code)
        # (not in numbers and indicators: there a text stops the artefacts with an error, and it is no code question)
        # (and the longer codes of elements named so, such as VATEX-EU-AE)
        if (re.fullmatch(r'[A-Za-z0-9]{1,6}', text) or local(e.tag).endswith('Code') and re.fullmatch(r'[A-Za-z0-9._-]{1,30}', text)) \
                and not len(e) and not re.fullmatch(r'\d{5,}', text) and not re.search(r'(Amount|Percent|Quantity|Numeric|Indicator)$', local(e.tag)):
            out.append((f'{name}-code-falsch', ('text', index, 'QQX')))
            if text.lower() != text:
                out.append((f'{name}-code-klein', ('text', index, text.lower())))
            out.append((f'{name}-code-raum', ('text', index, f'  {text} ')))
        for attribute in ('schemeID', 'listID', 'currencyID', 'unitCode', 'mimeCode'):
            if attribute in e.attrib:
                out.append((f'{name}-ohne-{attribute}', ('noattr', index, attribute)))
                out.append((f'{name}-falsch-{attribute}', ('setattr', index, attribute, 'QQX')))
        if parent is not None and local(e.tag) in REPEATING:
            out.append((f'{name}-doppelt', ('double', index)))
        # two things at once, for the rules about a pair: reason and reason code of an allowance or charge, start and end of a period
        if local(e.tag) in ('SpecifiedTradeAllowanceCharge', 'AllowanceCharge'):
            out.append((f'{name}-grund-weg', ('removechildren', index, REASONS_OF)))
        if local(e.tag) in ('BillingSpecifiedPeriod', 'InvoicePeriod'):
            out.append((f'{name}-zeitraum-leer', ('removechildren', index, PERIOD_ENDS)))
        # every VAT identifier of seller and tax representative (the rules 02 to 04 of the categories)
        if index == 0:
            out.append((f'{name}-ohne-ust', ('novat', 0)))
    return out


REASONS_OF = ('Reason', 'ReasonCode', 'AllowanceChargeReason', 'AllowanceChargeReasonCode')
PERIOD_ENDS = ('StartDateTime', 'EndDateTime', 'StartDate', 'EndDate')


def vat_identifiers(tree, parents):
    """The elements that hold a VAT identifier of seller or tax representative: (parent, element) pairs."""
    out = []
    for e in tree.iter():
        p = parents.get(e)
        if p is None:
            continue
        g = parents.get(p)
        if local(e.tag) == 'SpecifiedTaxRegistration' and local(p.tag) in ('SellerTradeParty', 'SellerTaxRepresentativeTradeParty'):
            out.append((p, e))
        elif local(e.tag) == 'PartyTaxScheme' and (local(p.tag) == 'TaxRepresentativeParty'
                                                   or local(p.tag) == 'Party' and g is not None and local(g.tag) == 'AccountingSupplierParty'):
            out.append((p, e))
    return out


def apply(root, change):
    tree = copy.deepcopy(root)
    elements = list(tree.iter())
    parents = {c: p for p in tree.iter() for c in p}
    kind, index = change[0], change[1]
    e = elements[index]
    if kind == 'remove':
        parents[e].remove(e)
    elif kind == 'text':
        e.text = change[2]
    elif kind == 'noattr':
        del e.attrib[change[2]]
    elif kind == 'setattr':
        e.attrib[change[2]] = change[3]
    elif kind == 'double':
        parent = parents[e]
        parent.insert(list(parent).index(e) + 1, copy.deepcopy(e))
    elif kind == 'removechildren':
        for child in [c for c in e if local(c.tag) in change[2]]:
            e.remove(child)
    elif kind == 'novat':
        for parent, child in vat_identifiers(tree, parents):
            parent.remove(child)
    return tree


def svrl_ids(path, syntax, profile=False):
    """The identifiers of the failed assertions (only the families compared), as PDF-Knead writes them; profile: a report of the profile
    rules of ZUGFeRD / Factur-X (they name IGIC and IPSI as the standard does, and the CII rules of IGIC and IPSI the right way round)."""
    data = open(path, 'rb').read()
    if b'<!DOCTYPE' in data:
        raise SystemExit(f'{path}: DOCTYPE')
    root = ET.fromstring(data)
    ids = {(e.get('id') or '').strip() for e in root.iter() if local(e.tag) == 'failed-assert'}
    if profile:
        return {fx_id(i) for i in ids if FAMILIES.match(fx_id(i))}
    return {reference_id(i, syntax) for i in ids if FAMILIES.match(i)}


def expected_ids(en_ids, path, syntax, ref_xr_file, ref_fx_of):
    """What the reference finds for a variant or a file: the EN artefacts; for XRechnung without what its scenario counts only as
    information, with the XRechnung rules; for an invoice of a narrowed profile the profile rules (ref_fx_of: profile → its report),
    with the code list rules of the EN artefacts where the specification lists them for the profile. None if the reference of the
    profile gave no result."""
    theirs = set(en_ids)
    kind = xr_kind(path)
    theirs -= DEMOTED.get((kind, syntax), set())
    if kind and ref_xr_file and os.path.exists(ref_xr_file) and os.path.getsize(ref_xr_file) > 0:
        theirs |= svrl_ids(ref_xr_file, syntax)
    profile = fx_kind(path) if syntax == 'cii' else None
    if profile:
        ref_fx_file = ref_fx_of(profile)
        if not (ref_fx_file and os.path.exists(ref_fx_file) and os.path.getsize(ref_fx_file) > 0):
            return None
        # the warning R008 of the artefacts of the profiles (empty elements) is no rule of the specification: PDF-Knead gives the hint
        # PDFK-XML-01 for it, compared place by place in hinweisprobe.py
        profile_ids = svrl_ids(ref_fx_file, syntax, profile=True) - {'PEPPOL-EN16931-R008'}
        if profile in FX_NARROWED:
            codes = {i for i in theirs if i.startswith('BR-CL-')} if profile in FX_CODE_LISTS else set()
            theirs = profile_ids | codes
        else:
            theirs |= {i for i in profile_ids if i.startswith('FX-SCH-A-')}
    return theirs


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    if '--ausgabe' in sys.argv:
        args.remove(sys.argv[sys.argv.index('--ausgabe') + 1])
    if not args:
        print(__doc__)
        return 2
    out = sys.argv[sys.argv.index('--ausgabe') + 1] if '--ausgabe' in sys.argv else os.path.join(
        os.environ.get('LOCALAPPDATA', os.path.expanduser('~/.local/share')), 'pdf-knead-build', 'erechnung', 'regelmutation')
    jar = (glob.glob(os.path.join(REPO, 'tools', 'pruefer', 'erechnung', 'kosit', 'validator-*-standalone.jar')) or [None])[0]
    java = find_java()
    styles = {s: os.path.join(REPO, 'tools', 'pruefer', 'erechnung', 'en16931', s, 'xslt', f'EN16931-{s.upper()}-validation.xslt') for s in ('cii', 'ubl')}
    if not (jar and java and all(os.path.exists(p) for p in styles.values())):
        raise SystemExit('Prüfregeln (XSLT), Validator (Saxon) oder Java fehlt')
    cli = find_cli()
    again = '--nur-vergleichen' in sys.argv
    xr = xr_styles(REPO)
    inp, ref, ours = (os.path.join(out, d) for d in ('ein', 'referenz', 'pdfknead'))
    ref_xr = os.path.join(out, 'referenz-xrechnung')
    # the profile rules of ZUGFeRD / Factur-X, one folder per profile, run over the CII variants (each variant compared with the profile
    # its own BT-24 names)
    fx = fx_styles()
    ref_fx = os.path.join(out, 'referenz-profil')
    if not again:
        shutil.rmtree(out, ignore_errors=True)
        for d in [ours] + [os.path.join(x, s) for x in (inp, ref, ref_xr) for s in styles] + [os.path.join(ref_fx, p, 'cii') for p in fx]:
            os.makedirs(d, exist_ok=True)
        for b, base in enumerate(args):
            data = open(base, 'rb').read()
            if b'<!DOCTYPE' in data:
                raise SystemExit(f'{base}: DOCTYPE')
            for prefix, uri in {m[0]: m[1] for m in re.findall(rb'xmlns:([\w-]+)="([^"]+)"', data)}.items():
                ET.register_namespace(prefix.decode(), uri.decode())
            root = ET.fromstring(data)
            syntax = 'cii' if root.tag.startswith('{' + CII + '}') else 'ubl'   # by the root, not by the first bytes (FG 840)
            stem = os.path.join(inp, syntax, f'b{b}-' + os.path.splitext(os.path.basename(base))[0][:30])
            ET.ElementTree(root).write(f'{stem}-0000-unveraendert.xml', encoding='utf-8', xml_declaration=True)
            for name, change in mutations(root):
                ET.ElementTree(apply(root, change)).write(f'{stem}-{name}.xml', encoding='utf-8', xml_declaration=True)
    names = {s: sorted(os.listdir(os.path.join(inp, s))) for s in styles}
    print(f'{sum(map(len, names.values()))} Varianten')

    def check(job):
        # one Saxon over the folder: the style sheet is compiled once, every file is put out under its name
        syntax, sheet, target = job
        return subprocess.run([java, '-Xmx2g', '-cp', jar, 'net.sf.saxon.Transform', f'-s:{os.path.join(inp, syntax)}', f'-xsl:{sheet}',
                               f'-o:{os.path.join(target, syntax)}'], check=False, capture_output=True, timeout=7200).returncode

    if not again:
        jobs = [(s, styles[s], ref) for s in styles if names[s]] + [(s, xr[s], ref_xr) for s in styles if names[s]]
        jobs += [('cii', fx[p], os.path.join(ref_fx, p)) for p in fx if names['cii'] and os.path.exists(fx[p])]
        with ThreadPoolExecutor(max_workers=2) as pool:
            print('Referenz:', list(pool.map(check, jobs)))
    # PDF-Knead every time (it is the side that changes); the reference only on a new run
    shutil.rmtree(ours, ignore_errors=True)
    os.makedirs(ours)
    for syntax in styles:
        if names[syntax]:
            subprocess.run([cli, 'erechnung', os.path.join(inp, syntax), '--regeln-aus', ours], check=False, capture_output=True)
    per_rule, examples = Counter(), defaultdict(list)
    fired, explained = Counter(), Counter()
    no_reference = []
    compared = differing = 0
    for syntax, name in ((s, n) for s in styles for n in names[s]):
        ref_file, our_file = os.path.join(ref, syntax, name), os.path.join(ours, name + '.txt')
        if not (os.path.exists(ref_file) and os.path.exists(our_file)) or os.path.getsize(ref_file) == 0:
            # the reference stopped on this variant with an error (its report is empty): nothing to compare
            no_reference.append(name)
            continue
        # the reference as the reference checker sees the variant (BT-24 as written in it): XRechnung, profile of ZUGFeRD / Factur-X
        theirs = expected_ids(svrl_ids(ref_file, syntax), os.path.join(inp, syntax, name), syntax, os.path.join(ref_xr, syntax, name),
                              lambda p: os.path.join(ref_fx, p, syntax, name))
        if theirs is None:
            no_reference.append(name)
            continue
        compared += 1
        fired.update(theirs)
        # the families compared — the schema check (PDFK-XSD) has a differential test of its own (xsdprobe.py), the hints of PDF-Knead none
        mine = {line.strip() for line in open(our_file, encoding='utf-8') if FAMILIES.match(line.strip())} \
            - not_in_reference(os.path.join(inp, syntax, name), syntax)
        if theirs == mine:
            continue
        open_here = False
        for ident, only_reference in [(i, True) for i in sorted(theirs - mine)] + [(i, False) for i in sorted(mine - theirs)]:
            reason = explain(inp, syntax, name, ident, only_reference)
            if reason:
                explained[reason] += 1
                continue
            open_here = True
            key = f'{ident} nur bei der Referenz' if only_reference else f'{ident} nur bei PDF-Knead'
            per_rule[key] += 1
            examples[key].append(name)
        differing += open_here
    lines = [f'{compared} Varianten verglichen, {differing} mit offener Abweichung; von der Referenz ausgelöste Regeln: {len(fired)}; '
             f'Referenz ohne Ergebnis: {len(no_reference)} ({", ".join(no_reference[:3])})',
             f'begründete Abweichungen (Regel je Variante): {sum(explained.values())}']
    lines.extend(f'  {n:5}  {REASONS[key]}' for key, n in explained.most_common())
    lines.append('offen:' if per_rule else 'offen: keine')
    for key, n in per_rule.most_common():
        lines.append(f'  {n:5}  {key}')
        lines.extend(f'           {e}' for e in examples[key][:3])
    lines.append('ausgelöst: ' + ' '.join(sorted(fired)))
    report = '\n'.join(lines)
    open(os.path.join(out, 'bericht.txt'), 'w', encoding='utf-8').write(report + '\n')
    print(report)
    return 0 if differing == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
