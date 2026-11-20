#!/usr/bin/env python3
"""Generates the table of which business rules apply in which profile of ZUGFeRD / Factur-X (KONZEPT.md, section 5.24, stage 4, part e).

    python PdfKnead.EInvoice/Zuordnung/profile.py [<table of the specification .xlsx>] [--pruefregeln <folder of the profile schematrons>]

Source: the sheet „Business Rules“ of the table of the specification Factur-X 1.09.2 / ZUGFeRD 2.5.2 (FNFE-MPE and FeRD). Licence: the
technical artefacts are under Apache-2.0 per FeRD, the specification names itself Apache-2.0 (page 13), its text is protected and not
reproduced — the table may be shipped with the notice packaging/licenses/ZUGFeRD-Factur-X-Apache-2.0.txt (docs/PATENTE.md, researched
14.11.2026). Default: the copy in Normen-ISO/erechnung/zugferd (not in Git). Written next to this script:

    profile.tsv    rule  profiles in which it applies (MINIMUM, BASICWL, BASIC, EN16931, EXTENDED; comma-separated)

The identifiers are written as PDF-Knead writes them: two digits (BR-S-1 → BR-S-01), IGIC and IPSI as BR-IG and BR-IP (the sheet writes
them as the standard does, BR-AF and BR-AG). With --pruefregeln the sets are compared with the identifiers of the profile schematrons of
the package — a look for differences, not a source. Only the Python standard library; the workbook is read as ZIP with ElementTree after a
check for DOCTYPE.
"""
import glob
import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT = os.path.join(REPO, 'Normen-ISO', 'erechnung', 'zugferd', 'zugferd-2.5.2-factur-x-1.09.2-de',
                       '_Factur-X 1.09.2 Zugferd 2.5.2 - 2026 08 04 - FINAL DE', '3. FACTUR-X 1.09.2 - EN FR.xlsx')
PROFILES = [('MINIMUM', 'MINIMUM'), ('BASIC WL', 'BASICWL'), ('BASIC', 'BASIC'), ('EN 16931', 'EN16931'), ('EXTENDED', 'EXTENDED')]
M = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
R = '{http://schemas.openxmlformats.org/officeDocument/2006/relationships}'


def sheet(book, name):
    """The rows of one sheet as lists of cell texts (by column)."""
    z = zipfile.ZipFile(book)
    for part in z.namelist():
        if b'<!DOCTYPE' in z.read(part)[:4096]:
            raise SystemExit(f'{part}: DOCTYPE — nicht gelesen')
    shared = [''.join(t.text or '' for t in si.iter(M + 't')) for si in ET.fromstring(z.read('xl/sharedStrings.xml')).findall(M + 'si')]
    rels = {r.get('Id'): r.get('Target') for r in ET.fromstring(z.read('xl/_rels/workbook.xml.rels'))}
    for s in ET.fromstring(z.read('xl/workbook.xml')).find(M + 'sheets'):
        if s.get('name') != name:
            continue
        rows = []
        for row in ET.fromstring(z.read('xl/' + rels[s.get(R + 'id')].lstrip('/').replace('xl/', ''))).iter(M + 'row'):
            cells = {}
            for c in row.findall(M + 'c'):
                letters = re.match(r'[A-Z]+', c.get('r')).group(0)
                col = 0
                for ch in letters:
                    col = col * 26 + ord(ch) - 64
                v = c.find(M + 'v')
                text = shared[int(v.text)] if c.get('t') == 's' and v is not None else (v.text or '') if v is not None else ''
                cells[col - 1] = ' '.join(text.split())
            rows.append([cells.get(i, '') for i in range(max(cells) + 1)] if cells else [])
        return rows
    raise SystemExit(f'Blatt „{name}“ fehlt')


def norm(ident):
    """An identifier as PDF-Knead writes it: two digits, IGIC and IPSI as BR-IG and BR-IP."""
    i = ident.replace(' ', '')
    m = re.match(r'^BR-(?:(FXEXT)-)?(?:(S|Z|E|AE|IC|G|O|AF|AG|IG|IP|B|CO|DEC|CL|BR)-)?(\d+)([a-z]*(?:-\d+)?)$', i)
    if not m:
        return i
    ext, family, number, rest = m.groups()
    family = {'AF': 'IG', 'AG': 'IP'}.get(family, family)
    return 'BR-' + (ext + '-' if ext else '') + (family + '-' if family else '') + f'{int(number):02d}' + rest


def table(book):
    """rule -> profiles, from the sheet „Business Rules“: two blocks side by side (VAT rules, the others), each with its ID column and the
    five profile columns after it, found by the header row."""
    rows = sheet(book, 'Business Rules')
    header = next(r for r in rows if 'MINIMUM' in r and 'EXTENDED' in r and 'ID' in r)
    blocks = []
    for at in [i for i, cell in enumerate(header) if cell == 'ID']:
        columns = []
        for title, key in PROFILES:
            columns.append((header.index(title, at), key))
        blocks.append((at, columns))
    found = {}
    for row in rows[rows.index(header) + 1:]:
        for at, columns in blocks:
            ident = row[at] if at < len(row) else ''
            if not re.match(r'^(BR|PEPPOL)-', ident):
                continue
            marks = [key for col, key in columns if col < len(row) and row[col].strip().upper() == 'X']
            found[norm(ident)] = marks
    return found


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    checks = sys.argv[sys.argv.index('--pruefregeln') + 1] if '--pruefregeln' in sys.argv else None
    if checks in args:
        args.remove(checks)
    book = args[0] if args else DEFAULT
    found = table(book)
    lines = ['# generated by profile.py from the sheet „Business Rules“ of the table of the specification Factur-X 1.09.2 / ZUGFeRD 2.5.2',
             '# (FNFE-MPE, FeRD; Apache-2.0, notice in packaging/licenses/ZUGFeRD-Factur-X-Apache-2.0.txt); do not edit',
             '# rule\tprofiles']
    lines += [f'{rule}\t{",".join(profiles)}' for rule, profiles in sorted(found.items())]
    open(os.path.join(HERE, 'profile.tsv'), 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
    print(f'{len(found)} Regeln')
    if checks:
        for sch in sorted(glob.glob(os.path.join(checks, '*', 'FACTUR-X_*.sch'))):
            key = os.path.basename(sch)[9:-4].replace('-', '')
            ids = {norm(i) for i in re.findall(r'<(?:sch:)?assert[^>]*\bid="([^"]+)"', open(sch, encoding='utf-8').read())
                   if not i.startswith(('FX-SCH-A', 'CII-'))}
            spec = {rule for rule, profiles in found.items() if key in profiles}
            print(f'== {key}: Spezifikation {len(spec)}, Prüfregeln {len(ids)}')
            print('   nur Spezifikation:', ' '.join(sorted(spec - ids)))
            print('   nur Prüfregeln   :', ' '.join(sorted(ids - spec)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
