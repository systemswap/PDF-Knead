#!/usr/bin/env python3
"""Differential test of the business rules (KONZEPT.md, section 5.24, stage 4): the rules PDF-Knead finds violated against those the
validation artefacts of EN 16931 (CEN TC 434, Schematron as XSLT, EUPL-1.2 — only run, never translated) find violated. Local tool, nothing
of it ships.

    python PdfKnead.EInvoice/Zuordnung/regelprobe.py <folder of invoices> [--ausgabe <folder>] [--nur-vergleichen]

Needs, as on the developer's machine (tools/pruefer, not in Git): the EN 16931 XSLT (tools/pruefer/erechnung/en16931/{cii,ubl}/xslt), the
Saxon inside the KoSIT validator, a Java ≥ 11 and a built command line. Every loose XML of the folder whose root is a CII invoice, a UBL
invoice or a UBL credit note, and every invoice embedded in a PDF of the folder (put out by the command line first), is checked by both;
compared per file are the identifiers of the rule families PDF-Knead implements (EN 16931; for an invoice of XRechnung 3.0 also the
XRechnung rules of the reference checker, FAMILIES in regelmutation.py). Report: per rule how often only one side finds it, with
examples. Only the Python standard library.
"""
import glob
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor

from regelmutation import FAMILIES, REASONS, expected_ids, explain_content, fx_kind, fx_styles, head_of, not_in_reference, xr_kind

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
MAX_BYTES = 5 * 1024 * 1024
# the families compared (FAMILIES) and the rules the scenarios of the reference checker count only as information (DEMOTED) come from
# regelmutation.py, so that both compare alike
# rules the reference does not check (not_in_reference in regelmutation.py: BR-CO-25 of the standard, two rules of EXTENDED): PDF-Knead's
# findings are counted apart, not as differences
KINDS = {
    'cii': (b'urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100', 'cii'),
    'ubl-invoice': (b'urn:oasis:names:specification:ubl:schema:xsd:Invoice-2', 'ubl'),
    'ubl-creditnote': (b'urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2', 'ubl'),
}


def reference_id(ident, syntax):
    """The artefacts of CII give the rules of IGIC (L) the identifiers BR-IP-n and those of IPSI (M) BR-IG-n — the other way round from
    the standard and from the artefacts of UBL. Compared is the identifier of the standard."""
    if syntax == 'cii' and re.match(r'^BR-I[GP]-\d+$', ident):
        return ('BR-IP-' if ident.startswith('BR-IG-') else 'BR-IG-') + ident[6:]
    # the artefacts of CII split PEPPOL-EN16931-R043 into -1 (document and line) and -2 (price): one rule of the specification
    if re.match(r'^PEPPOL-EN16931-R043-\d$', ident):
        return 'PEPPOL-EN16931-R043'
    return ident


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


def kind_of(path):
    # the start without comments: a long licence comment before the root (official examples of ZUGFeRD 2.5.2) must not hide it (FG 840)
    head = head_of(path)
    if b'<!DOCTYPE' in head:
        return None
    root = re.search(rb'<([A-Za-z_][\w.-]*:)?(CrossIndustryInvoice|Invoice|CreditNote)\b[^>]*>', head)
    for kind, (ns, _) in KINDS.items():
        if root and ns in root.group(0):
            return kind
    return None


def svrl_ids(path, syntax):
    """The identifiers of the failed assertions of an SVRL report (only the families compared), as the standard numbers them."""
    data = open(path, 'rb').read()
    if b'<!DOCTYPE' in data:
        raise SystemExit(f'{path}: DOCTYPE')
    root = ET.fromstring(data)
    ids = set()
    for e in root.iter():
        if e.tag.endswith('}failed-assert') or e.tag == 'failed-assert':
            ident = (e.get('id') or '').strip()
            if FAMILIES.match(ident):
                ids.add(reference_id(ident, syntax))
    return ids


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    source = sys.argv[1]
    out = sys.argv[sys.argv.index('--ausgabe') + 1] if '--ausgabe' in sys.argv else os.path.join(
        os.environ.get('LOCALAPPDATA', os.path.expanduser('~/.local/share')), 'pdf-knead-build', 'erechnung', 'regelprobe')
    jar = (glob.glob(os.path.join(REPO, 'tools', 'pruefer', 'erechnung', 'kosit', 'validator-*-standalone.jar')) or [None])[0]
    java = find_java()
    styles = {s: os.path.join(REPO, 'tools', 'pruefer', 'erechnung', 'en16931', s, 'xslt', f'EN16931-{s.upper()}-validation.xslt') for s in ('cii', 'ubl')}
    if not (jar and java and all(os.path.exists(p) for p in styles.values())):
        raise SystemExit('Prüfregeln (XSLT), Validator (Saxon) oder Java fehlt')
    cli = find_cli()
    again = '--nur-vergleichen' in sys.argv
    if not again:
        shutil.rmtree(out, ignore_errors=True)
    embedded = os.path.join(out, 'eingebettet')
    inp, ref, ours = (os.path.join(out, d) for d in ('ein', 'referenz', 'pdfknead'))
    ref_xr = os.path.join(out, 'referenz-xrechnung')
    ref_fx = os.path.join(out, 'referenz-profil')
    xr = xr_styles(REPO)
    fx = fx_styles()
    for d in (embedded, inp, ref, ours, ref_xr, ref_fx):
        os.makedirs(d, exist_ok=True)
    if not again:
        subprocess.run([cli, 'erechnung', source, '--xml', embedded], check=False, capture_output=True)
        candidates = sorted(glob.glob(os.path.join(source, '**', '*.xml'), recursive=True)) + sorted(glob.glob(os.path.join(embedded, '*.xml')))
        skipped = []
        for i, p in enumerate(candidates):
            if kind_of(p) and os.path.getsize(p) <= MAX_BYTES:
                # an ASCII name: the reference cannot open a file name with combined characters on Windows
                shutil.copyfile(p, os.path.join(inp, f'{i:04d}-' + re.sub(r'[^\w.-]', '_', os.path.basename(p), flags=re.ASCII)))
            else:
                skipped.append(os.path.relpath(p, source) if not p.startswith(embedded) else p)
        # what was left out is named, never dropped silently (FG 840: 61 official examples were left out unseen)
        open(os.path.join(out, 'uebersprungen.txt'), 'w', encoding='utf-8').write(''.join(n + '\n' for n in skipped))
    names = sorted(os.listdir(inp))

    def check(name):
        style = styles[KINDS[kind_of(os.path.join(inp, name))][1]]
        try:
            subprocess.run([java, '-cp', jar, 'net.sf.saxon.Transform', f'-s:{os.path.join(inp, name)}', f'-xsl:{style}',
                            f'-o:{os.path.join(ref, name + ".svrl")}'], check=False, capture_output=True, timeout=180)
            return None
        except subprocess.TimeoutExpired:
            return name

    timed_out = []
    if not again:
        with ThreadPoolExecutor(max_workers=4) as pool:
            timed_out = [n for n in pool.map(check, names) if n]

    def check_xr(name):
        # the XRechnung rules of the reference checker, for an invoice of XRechnung 3.0 — also on a comparison only, where still missing
        style = xr[KINDS[kind_of(os.path.join(inp, name))][1]]
        try:
            subprocess.run([java, '-cp', jar, 'net.sf.saxon.Transform', f'-s:{os.path.join(inp, name)}', f'-xsl:{style}',
                            f'-o:{os.path.join(ref_xr, name + ".svrl")}'], check=False, capture_output=True, timeout=180)
            return None
        except subprocess.TimeoutExpired:
            return name

    xr_names = [n for n in names if xr_kind(os.path.join(inp, n))]
    with ThreadPoolExecutor(max_workers=4) as pool:
        timed_out += [n for n in pool.map(check_xr, [n for n in xr_names if not os.path.exists(os.path.join(ref_xr, n + '.svrl'))]) if n]

    def check_fx(name):
        # the profile rules of ZUGFeRD / Factur-X for an invoice of a narrowed profile — also on a comparison only, where still missing
        try:
            subprocess.run([java, '-cp', jar, 'net.sf.saxon.Transform', f'-s:{os.path.join(inp, name)}', f'-xsl:{fx[fx_kind(os.path.join(inp, name))]}',
                            f'-o:{os.path.join(ref_fx, name + ".svrl")}'], check=False, capture_output=True, timeout=180)
            return None
        except subprocess.TimeoutExpired:
            return name

    fx_names = [n for n in names if fx_kind(os.path.join(inp, n))]
    with ThreadPoolExecutor(max_workers=4) as pool:
        timed_out += [n for n in pool.map(check_fx, [n for n in fx_names if not os.path.exists(os.path.join(ref_fx, n + '.svrl'))]) if n]
    subprocess.run([cli, 'erechnung', inp, '--regeln-aus', ours], check=False, capture_output=True)
    per_rule, explained = Counter(), Counter()
    examples = defaultdict(list)
    compared = differing = 0
    no_reference, no_result = [], []
    for name in names:
        ref_file, our_file = os.path.join(ref, name + '.svrl'), os.path.join(ours, name + '.txt')
        if not os.path.exists(ref_file):
            no_reference.append(name)
            continue
        if not os.path.exists(our_file):
            no_result.append(name)
            continue
        syntax = KINDS[kind_of(os.path.join(inp, name))][1]
        # as the reference checker sees the file: EN 16931; XRechnung (without what its scenario counts only as information, with its
        # rules); for a narrowed profile of ZUGFeRD / Factur-X its profile rules
        theirs = expected_ids(svrl_ids(ref_file, syntax), os.path.join(inp, name), syntax, os.path.join(ref_xr, name + '.svrl'),
                              lambda _: os.path.join(ref_fx, name + '.svrl'))
        if theirs is None:
            no_reference.append(name)
            continue
        compared += 1
        # the families compared — the schema check (PDFK-XSD) has a differential test of its own (xsdprobe.py), the hints of PDF-Knead none
        mine = {line.strip() for line in open(our_file, encoding='utf-8') if FAMILIES.match(line.strip())}
        apart = not_in_reference(os.path.join(inp, name), syntax)
        for ident in mine & apart:
            per_rule[f'{ident} (prüft die Referenz nicht, begründete Ausnahme)'] += 1
        mine -= apart
        if theirs == mine:
            continue
        # the known differences the invoice itself shows (the same reasons as in the mutation test, docs/ENTSCHEIDUNGEN.md)
        root = ET.parse(os.path.join(inp, name)).getroot()
        open_here = False
        for ident, only_reference in [(i, True) for i in sorted(theirs - mine)] + [(i, False) for i in sorted(mine - theirs)]:
            reason = explain_content(root, syntax, ident, only_reference)
            if reason:
                explained[reason] += 1
                continue
            open_here = True
            key = f'{ident} nur bei der Referenz' if only_reference else f'{ident} nur bei PDF-Knead'
            per_rule[key] += 1
            examples[key].append(name)
        differing += open_here
    skipped_file = os.path.join(out, 'uebersprungen.txt')
    skipped = [l for l in open(skipped_file, encoding='utf-8').read().splitlines() if l] if os.path.exists(skipped_file) else []
    lines = [f'{compared} Dateien verglichen, {differing} mit offener Abweichung; nicht als Rechnung erkannt oder zu groß: {len(skipped)} '
             f'(uebersprungen.txt); Zeitgrenze überschritten: {len(timed_out)} ({", ".join(timed_out)}); '
             f'Referenz ohne Ergebnis: {len(no_reference)} ({", ".join(no_reference[:5])}); PDF-Knead ohne Ergebnis: {len(no_result)} '
             f'({", ".join(no_result[:5])})']
    lines.extend(f'  {n:5}  begründet: {REASONS[key]}' for key, n in explained.most_common())
    for key, n in per_rule.most_common():
        lines.append(f'  {n:5}  {key}')
        lines.extend(f'           {e}' for e in examples[key][:4])
    report = '\n'.join(lines)
    open(os.path.join(out, 'bericht.txt'), 'w', encoding='utf-8').write(report + '\n')
    print(report)
    return 0 if differing == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
