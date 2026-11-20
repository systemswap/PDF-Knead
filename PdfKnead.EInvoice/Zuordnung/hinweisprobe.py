#!/usr/bin/env python3
"""Differential test of the hints of the e-invoice (KONZEPT.md, section 5.24, stage 4, part g) against the validation artefacts of the
profiles of ZUGFeRD / Factur-X, place by place: what the profile does not use (PDFK-FX-02 — the reports of the artefacts, which carry no
identifier, only a location) and the empty elements (PDFK-XML-01 — the warnings PEPPOL-EN16931-R008 of the artefacts of BASIC, EN16931 and
EXTENDED). Uses the reports the runs of regelprobe.py and regelmutation.py left (their output folders); PDF-Knead is run again. A place is
compared by its local names, with the number of a repeated element (the first one without), and „/@name“ for an attribute. Local tool,
nothing of it ships.

    python PdfKnead.EInvoice/Zuordnung/hinweisprobe.py <output folder of regelprobe.py or regelmutation.py> […] [--ausgabe <folder>]

Only the Python standard library.
"""
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from regelmutation import fx_kind  # noqa: E402
from regelprobe import find_cli  # noqa: E402

WITH_R008 = {'basic', 'en16931', 'extended'}

# Differences with a reason (counted, not open): the artefacts warn of every empty element; PDF-Knead leaves out the one the schema demands
# even when there is nothing to say — the delivery of CII, as R008 of XRechnung does — for there „leave it out“ would be wrong advice.
REASONS = {
    'leer-lieferung': 'leere Lieferung (ApplicableHeaderTradeDelivery): das Schema verlangt sie; wie R008 der XRechnung ausgenommen',
}


def reason(what, place, only_reference):
    """The reason of a difference, or None if it is open."""
    if what == 'leer' and only_reference and place.endswith('/ApplicableHeaderTradeDelivery'):
        return 'leer-lieferung'
    return None


def ours_place(place):
    """A place as PDF-Knead writes it („/rsm:A/ram:B[2]/@x“) in the compared form („/A/B[2]/@x“)."""
    return re.sub(r'\[1\]', '', re.sub(r'/(\{[^}]*\}|[A-Za-z]\w*:)', '/', place))


def their_place(location, test=''):
    """A location of the artefacts („/*:A[namespace-uri()='…'][1]/*:B[…][2]“) in the compared form; an attribute from the test („@x“)."""
    steps = re.findall(r"/\*:([\w.-]+)(?:\[namespace-uri\(\)='[^']*'\])?\[(\d+)\]", location)
    place = ''.join(f'/{name}' + (f'[{n}]' if n != '1' else '') for name, n in steps)
    return place + (f'/{test.strip()}' if test.strip().startswith('@') else '')


def reference(svrl):
    """The places the artefacts report as not used, and the places of their warnings R008."""
    root = ET.parse(svrl).getroot()
    unused, empty = set(), set()
    for e in root.iter():
        tag = e.tag.split('}')[-1]
        if tag == 'successful-report':
            unused.add(their_place(e.get('location') or '', e.get('test') or ''))
        elif tag == 'failed-assert' and (e.get('id') or '').strip() == 'PEPPOL-EN16931-R008':
            empty.add(their_place(e.get('location') or ''))
    return unused, empty


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    out = sys.argv[sys.argv.index('--ausgabe') + 1] if '--ausgabe' in sys.argv else os.path.join(
        os.environ.get('LOCALAPPDATA', os.path.expanduser('~/.local/share')), 'pdf-knead-build', 'erechnung', 'hinweisprobe')
    if out in args:
        args.remove(out)
    if not args:
        print(__doc__)
        return 2
    cli = find_cli()
    counts, examples = Counter(), {}
    compared = 0
    for base in args:
        # the layout of regelmutation.py (ein/cii, referenz-profil/<profile>/cii/<name>) or of regelprobe.py (ein, referenz-profil/<name>.svrl)
        mutation = os.path.isdir(os.path.join(base, 'ein', 'cii'))
        inputs = os.path.join(base, 'ein', 'cii') if mutation else os.path.join(base, 'ein')
        label = os.path.basename(os.path.normpath(base))
        ours_dir = os.path.join(out, 'pdfknead', label)
        shutil.rmtree(ours_dir, ignore_errors=True)
        os.makedirs(ours_dir)
        subprocess.run([cli, 'erechnung', inputs, '--stellen-aus', ours_dir], check=False, capture_output=True)
        for name in sorted(os.listdir(inputs)):
            path = os.path.join(inputs, name)
            profile = fx_kind(path)
            if not profile:
                continue
            svrl = os.path.join(base, 'referenz-profil', profile, 'cii', name) if mutation else os.path.join(base, 'referenz-profil', name + '.svrl')
            mine_file = os.path.join(ours_dir, name + '.txt')
            if not os.path.exists(svrl) or os.path.getsize(svrl) == 0 or not os.path.exists(mine_file):
                counts['ohne Ergebnis'] += 1
                continue
            compared += 1
            unused, empty = reference(svrl)
            mine = [line.rstrip('\n').split('\t', 1) for line in open(mine_file, encoding='utf-8') if '\t' in line]
            my_unused = {ours_place(p) for i, p in mine if i == 'PDFK-FX-02'}
            my_empty = {ours_place(p) for i, p in mine if i == 'PDFK-XML-01'}
            counts['Stellen nicht benutzt (Referenz)'] += len(unused)
            pairs = [('nicht benutzt', my_unused, unused)]
            if profile in WITH_R008:
                counts['leere Elemente (Referenz R008)'] += len(empty)
                pairs.append(('leer', my_empty, empty))
            for what, a, b in pairs:
                for only_reference, places in ((True, b - a), (False, a - b)):
                    for place in sorted(places):
                        why = reason(what, place, only_reference)
                        key = f'begründet: {REASONS[why]}' if why else f'{what}: nur bei ' + ('der Referenz' if only_reference else 'PDF-Knead')
                        counts[key] += 1
                        if not why:
                            examples.setdefault(key, []).append(f'{label}/{name} {place}')
    lines = [f'{compared} Rechnungen der Profile verglichen']
    lines += [f'  {n:7d}  {k}' for k, n in sorted(counts.items())]
    for key, items in examples.items():
        lines.append(f'{key}:')
        lines += [f'    {x}' for x in items[:20]]
    os.makedirs(out, exist_ok=True)
    open(os.path.join(out, 'bericht.txt'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    print('\n'.join(lines))
    return 1 if any('nur bei' in k for k in counts) else 0


if __name__ == '__main__':
    sys.exit(main())
