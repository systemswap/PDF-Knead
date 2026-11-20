#!/usr/bin/env python3
"""Compares the terms PDF-Knead reads (stage 2) with those the XRechnung visualization of the KoSIT writes (KONZEPT.md, section 5.24,
„Gegenprobe“). Local tool, nothing of it ships.

    python PdfKnead.EInvoice/Zuordnung/gegenprobe.py <folder of invoices> [--ausgabe <folder>]

Needs, as on the developer's machine (tools/pruefer, not in Git): the visualization (tools/pruefer/erechnung/xrechnung-visualisierung/
entpackt/xsl), the Saxon inside the KoSIT validator (tools/pruefer/erechnung/kosit/validator-*-standalone.jar), a Java ≥ 11 (the one of the
DSS bundle in tools/pruefer/signaturen, or JAVA on the path) and a built command line (PdfKnead.Cli/bin/Release or Debug).
Every loose XML of the folder whose root is a CII invoice, a UBL invoice or a UBL credit note is transformed by Saxon into the intermediate
format of the visualization (xr:…, with xr:id per term) and read by „pdf-knead erechnung --werte“. Per file and term the values are compared:
dates by their digits, numbers by their value, text with white space collapsed, attachments by their bytes. Report: per term how often the
sides differ, and examples. Only the Python standard library.
"""
import base64
import glob
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor
from decimal import Decimal, InvalidOperation

HERE = os.path.dirname(os.path.abspath(__file__))
MAX_BYTES = 5 * 1024 * 1024
JOINED = {'BT-9', 'BT-20'}   # terms the visualization joins with „;“ when an invoice has several (EXTENDED)
REPO = os.path.dirname(os.path.dirname(HERE))
XR = 'urn:ce.eu:en16931:2017:xoev-de:kosit:standard:xrechnung-1'
KINDS = {
    'cii': (b'urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100', 'cii-xr.xsl'),
    'ubl-invoice': (b'urn:oasis:names:specification:ubl:schema:xsd:Invoice-2', 'ubl-invoice-xr.xsl'),
    'ubl-creditnote': (b'urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2', 'ubl-creditnote-xr.xsl'),
}


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
    head = re.sub(rb'<!--.*?-->|<\?.*?\?>', b'', open(path, 'rb').read(1 << 18), flags=re.S)
    if b'<!DOCTYPE' in head:
        return None
    for kind, (ns, _) in KINDS.items():
        # the root's namespace: it is declared on the first element
        root = re.search(rb'<([A-Za-z_][\w.-]*:)?(CrossIndustryInvoice|Invoice|CreditNote)\b[^>]*>', head)
        if root and ns in root.group(0):
            return kind
    return None


def norm(value):
    v = ' '.join((value or '').split())
    if re.fullmatch(r'\d{4}-?\d{2}-?\d{2}', v):
        return 'date:' + v.replace('-', '')
    try:
        d = Decimal(v)
        if d.is_finite() and re.fullmatch(r'[+-]?\d+(\.\d+)?', v):
            return 'num:' + format(d.normalize(), 'f')
    except InvalidOperation:
        pass
    return v


def xr_values(path):
    """(id, normalized value) of the terms the visualization wrote; attachments by their bytes."""
    data = open(path, 'rb').read()
    if b'<!DOCTYPE' in data:
        raise SystemExit(f'{path}: DOCTYPE')
    root = ET.fromstring(data)
    out = []
    for e in root.iter():
        ident = e.get(f'{{{XR}}}id')
        if not ident or not ident.startswith('BT') or len(e):
            continue
        text = e.text or ''
        if e.tag.endswith('Attached_document'):
            try:
                out.append((ident, 'bytes:' + base64.b64decode(''.join(text.split())).hex()))
                continue
            except ValueError:
                pass
        if ident in JOINED and ';' in text:
            # several payment terms (EXTENDED): the visualization joins them with „;“, PDF-Knead shows each — a reasoned exception
            out.extend((ident, norm(part)) for part in text.split(';') if part.strip())
        elif text.strip():
            out.append((ident, norm(text)))
    return out


def our_values(path):
    out = []
    for line in open(path, encoding='utf-8'):
        ident, _, value = line.rstrip('\n').partition('\t')
        if value.startswith('base64:'):
            out.append((ident, 'bytes:' + base64.b64decode(value[7:]).hex()))
        elif ident in JOINED and ';' in value:
            out.extend((ident, norm(part)) for part in value.split(';') if part.strip())   # split like the reference side
        elif value.strip():
            out.append((ident, norm(value)))
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    source = sys.argv[1]
    out = sys.argv[sys.argv.index('--ausgabe') + 1] if '--ausgabe' in sys.argv else os.path.join(
        os.environ.get('LOCALAPPDATA', os.path.expanduser('~/.local/share')), 'pdf-knead-build', 'erechnung', 'gegenprobe')
    xsl = os.path.join(REPO, 'tools', 'pruefer', 'erechnung', 'xrechnung-visualisierung', 'entpackt', 'xsl')
    jar = (glob.glob(os.path.join(REPO, 'tools', 'pruefer', 'erechnung', 'kosit', 'validator-*-standalone.jar')) or [None])[0]
    java = find_java()
    if not (os.path.isdir(xsl) and jar and java):
        raise SystemExit('Visualisierung, Validator (Saxon) oder Java fehlt')
    cli = find_cli()
    again = '--nur-vergleichen' in sys.argv   # reuse the transformations of the last run
    if not again:
        shutil.rmtree(out, ignore_errors=True)
    files = defaultdict(list)
    # the invoices embedded in PDFs: put out by the command line first, then compared like the loose ones
    embedded = os.path.join(out, 'eingebettet')
    os.makedirs(embedded, exist_ok=True)
    if not again:
        subprocess.run([cli, 'erechnung', source, '--xml', embedded], check=False, capture_output=True)
    candidates = sorted(glob.glob(os.path.join(source, '**', '*.xml'), recursive=True)) + sorted(glob.glob(os.path.join(embedded, '*.xml')))
    for path in candidates:
        kind = kind_of(path)
        if kind:
            files[kind].append(path)
    per_id = Counter()
    examples = defaultdict(list)
    compared = differing = 0
    skipped, timed_out, no_reference = [], [], []
    for kind, paths in files.items():
        inp, xr_out, ours = (os.path.join(out, kind, d) for d in ('ein', 'xr', 'pdfknead'))
        for d in (inp, xr_out, ours):
            os.makedirs(d, exist_ok=True)
        for i, p in enumerate(paths):
            if os.path.getsize(p) > MAX_BYTES:
                skipped.append(os.path.basename(p))   # the same structure many times over; the corpus tests read them
                continue
            if again:
                continue
            # an ASCII name: the reference cannot open a file name with combined characters on Windows (FG 840)
            shutil.copyfile(p, os.path.join(inp, f'{i:04d}-' + re.sub(r'[^\w.-]', '_', os.path.basename(p), flags=re.ASCII)))
        style = os.path.join(xsl, KINDS[kind][1])

        def transform(name):
            # one Saxon per file with a time limit: a run over the whole folder once stood still on a small file
            try:
                subprocess.run([java, '-cp', jar, 'net.sf.saxon.Transform', f'-s:{os.path.join(inp, name)}', f'-xsl:{style}',
                                f'-o:{os.path.join(xr_out, name)}'], check=False, capture_output=True, timeout=120)
                return None
            except subprocess.TimeoutExpired:
                return name

        if not again:
            with ThreadPoolExecutor(max_workers=4) as pool:
                timed_out.extend(n for n in pool.map(transform, sorted(os.listdir(inp))) if n)
        subprocess.run([cli, 'erechnung', inp, '--werte', ours], check=False, capture_output=True)
        for name in sorted(os.listdir(inp)):
            xr_file, our_file = os.path.join(xr_out, name), os.path.join(ours, name + '.tsv')
            if not os.path.exists(xr_file):
                # the reference made nothing of it (an encoding declaration Saxon follows and PDF-Knead deliberately does not, FG 818)
                no_reference.append(f'{kind}/{name}')
                continue
            if not os.path.exists(our_file):
                per_id['(PDF-Knead ohne Ergebnis)'] += 1
                examples['(PDF-Knead ohne Ergebnis)'].append(f'{kind}/{name}')
                differing += 1
                continue
            compared += 1
            theirs, mine = Counter(xr_values(xr_file)), Counter(our_values(our_file))
            if theirs == mine:
                continue
            differing += 1
            for (ident, value), n in (theirs - mine).items():
                per_id[f'{ident} fehlt bei PDF-Knead'] += n
                examples[f'{ident} fehlt bei PDF-Knead'].append(f'{kind}/{name}: {value[:80]}')
            for (ident, value), n in (mine - theirs).items():
                per_id[f'{ident} nur bei PDF-Knead'] += n
                examples[f'{ident} nur bei PDF-Knead'].append(f'{kind}/{name}: {value[:80]}')
    lines = [f'{compared} Dateien verglichen ({", ".join(f"{k} {len(v)}" for k, v in files.items())}), {differing} mit Abweichung; '
             f'ausgelassen über 5 MB: {len(skipped)} ({", ".join(skipped)}); Zeitgrenze überschritten: {len(timed_out)} ({", ".join(timed_out)}); '
             f'Referenz ohne Ergebnis: {len(no_reference)} ({", ".join(no_reference)})']
    for key, n in per_id.most_common():
        lines.append(f'  {n:5}  {key}')
        lines.extend(f'           {e}' for e in examples[key][:3])
    report = '\n'.join(lines)
    os.makedirs(out, exist_ok=True)
    open(os.path.join(out, 'bericht.txt'), 'w', encoding='utf-8').write(report + '\n')
    print(report)
    return 0 if differing == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
