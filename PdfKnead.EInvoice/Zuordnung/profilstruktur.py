#!/usr/bin/env python3
"""Generates the element rules of the profiles of ZUGFeRD / Factur-X (KONZEPT.md, section 5.24, stage 4, part e2): which elements a profile
asks for and allows (how often), which attributes it requires and which code values it takes.

    python PdfKnead.EInvoice/Zuordnung/profilstruktur.py [<folder of the profile schematrons>]

Source: the Schematron validation artefacts of the five profiles of Factur-X 1.09.2 / ZUGFeRD 2.5.2 (FACTUR-X_<profile>.sch and their code
databases FACTUR-X_<profile>_codedb.xml; FNFE-MPE and FeRD, Apache-2.0 — notice in packaging/licenses/ZUGFeRD-Factur-X-Apache-2.0.txt,
docs/PATENTE.md). Default: the copy in Normen-ISO/erechnung/zugferd (not in Git). Only the element rules FX-SCH-A are taken, with the rules
of their patterns in their order (in a Schematron pattern only the first rule whose context fits a node is asked) and the reports
„not used in the given context“ (hints of part g); the business rules come from the specification (profile.py). Written next to this
script:

    profilstruktur.tsv  profile  pattern  rule  context  kind  id  severity  n  path  list
                        kind: exactly | atmost | atleast (count of path n times), attribute (path = @name required), code (the value
                        of path — „.“ or @name — in the code list of the profile, an empty value passes), unused (path present: not used
                        in the profile), empty (a rule without these, kept for the order)
    profilcodes.tsv     profile  list  value
                        the lists of the code databases by number; and the list BR-FXEXT-04 of EXTENDED (the codes of an item attribute
                        the profile recommends, part e3), taken from the test of that rule — the only business rule read here

The paths stay in the form of the artefacts (XPath, namespace prefixes rsm, ram, udt, qdt), only with single quotes; PdfKnead reads them with
its narrow path language (InvoicePath) and refuses any other form when the table is loaded. Any test of another form is refused here.
Only the Python standard library; the artefacts are read with ElementTree after a check for DOCTYPE.
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT = os.path.join(REPO, 'Normen-ISO', 'erechnung', 'zugferd', 'zugferd-2.5.2-factur-x-1.09.2-de',
                       '_Factur-X 1.09.2 Zugferd 2.5.2 - 2026 08 04 - FINAL DE', '4. FACTUR-X_1.09.2_XSD_SCHEMATRON')
PROFILES = {'MINIMUM': 'MINIMUM', 'BASIC-WL': 'BASICWL', 'BASIC': 'BASIC', 'EN16931': 'EN16931', 'EXTENDED': 'EXTENDED'}
S = '{http://purl.oclc.org/dsdl/schematron}'
COUNT = re.compile(r'^count\((?P<path>.+)\)\s*(?P<op>=|<=|>=)\s*(?P<n>\d+)$', re.S)
CODE = re.compile(r"^string-length\(\$(?P<v>codeValue\d+)\)\s*=\s*0\s+or\s+document\('[^']+'\)/codedb/cl\[@id=(?P<list>\d+)\]"
                  r"/enumeration\[@value=\$(?P=v)\]$")
ATTRIBUTE = re.compile(r'^@[\w-]+$')


def read(path):
    data = open(path, 'rb').read()
    if b'<!DOCTYPE' in data or b'<!ENTITY' in data:
        raise SystemExit(f'{path}: DOCTYPE oder ENTITY — nicht gelesen')
    return ET.fromstring(data)


def xpath(text):
    """A path in single quotes, without the space XPath allows after a function name."""
    t = ' '.join(text.split())
    t = re.sub(r'"([^"\']*)"', r"'\1'", t)
    return re.sub(r'\b(not|normalize-space)\s+\(', r'\1(', t)


def rows_of(sch, profile):
    root = read(sch)
    rows = []
    patterns = root.findall(S + 'pattern')
    for p, pattern in enumerate(patterns):
        rules = pattern.findall(S + 'rule')
        if not any(a.get('id', '').startswith('FX-SCH-A') for r in rules for a in r.findall(S + 'assert')) \
                and not any(r.findall(S + 'report') for r in rules):
            continue   # a pattern of business rules only
        for i, rule in enumerate(rules):
            context = xpath(rule.get('context'))
            lets = {l.get('name'): l.get('value') for l in rule.findall(S + 'let')}
            found = False
            for a in rule.findall(S + 'assert'):
                ident = a.get('id', '')
                if not ident.startswith('FX-SCH-A'):
                    continue
                severity = 'warning' if a.get('flag') == 'warning' else 'fatal'
                test = ' '.join(a.get('test').split())
                if m := COUNT.match(test):
                    kind = {'=': 'exactly', '<=': 'atmost', '>=': 'atleast'}[m.group('op')]
                    rows.append((profile, p, i, context, kind, ident, severity, m.group('n'), xpath(m.group('path')), ''))
                elif m := CODE.match(test):
                    value = lets.get(m.group('v'))
                    if value not in ('.',) and not (value and ATTRIBUTE.match(value)):
                        raise SystemExit(f'{sch}: {ident}: Wert {value!r} in unbekannter Form')
                    rows.append((profile, p, i, context, 'code', ident, severity, '', value, m.group('list')))
                elif ATTRIBUTE.match(test):
                    rows.append((profile, p, i, context, 'attribute', ident, severity, '', test, ''))
                else:
                    raise SystemExit(f'{sch}: {ident}: Prüfung in unbekannter Form: {test}')
                found = True
            for r in rule.findall(S + 'report'):
                test = ' '.join(r.get('test').split())
                if test == 'true()':
                    rows.append((profile, p, i, context, 'unused', '', 'information', '', '.', ''))
                elif ATTRIBUTE.match(test):
                    rows.append((profile, p, i, context, 'unused', '', 'information', '', test, ''))
                else:
                    raise SystemExit(f'{sch}: Hinweis in unbekannter Form: {test}')
                found = True
            if not found:
                rows.append((profile, p, i, context, 'empty', '', '', '', '', ''))
    return rows


ITEM_ATTRIBUTE = re.compile(r"^not\(contains\(normalize-space\(\.\), ' '\)\) and contains\(' (?P<list>[A-Z0-9_ ]+) ', "
                            r"concat\(' ', normalize-space\(\.\), ' '\)\)$")


def item_attribute_codes(sch):
    """The codes of an item attribute (BT-X-11) the profile EXTENDED recommends: the list of the rule BR-FXEXT-04 (a warning), on the type
    code of each item attribute. Refused if the rule has another context or test."""
    root = read(sch)
    found = [(r, a) for r in root.iter(S + 'rule') for a in r.findall(S + 'assert') if a.get('id') == 'BR-FXEXT-04']
    if len(found) != 1:
        raise SystemExit(f'{sch}: BR-FXEXT-04 nicht genau einmal')
    rule, a = found[0]
    if xpath(rule.get('context')) != '//ram:ApplicableProductCharacteristic/ram:TypeCode' or a.get('flag') != 'warning':
        raise SystemExit(f'{sch}: BR-FXEXT-04 mit anderem Kontext oder Gewicht')
    m = ITEM_ATTRIBUTE.match(' '.join(a.get('test').split()))
    if not m:
        raise SystemExit(f'{sch}: BR-FXEXT-04: Prüfung in unbekannter Form')
    values = m.group('list').split()
    if len(values) != len(set(values)):
        raise SystemExit(f'{sch}: BR-FXEXT-04: Code doppelt')
    return [('EXTENDED', 'BR-FXEXT-04', v) for v in values]


def codes_of(db, profile):
    root = read(db)
    return [(profile, cl.get('id'), e.get('value')) for cl in root.iter('cl') for e in cl.iter('enumeration')]


def main():
    base = sys.argv[1] if len(sys.argv) > 1 else DEFAULT
    rows, codes = [], []
    for folder, profile in PROFILES.items():
        sch = glob.glob(os.path.join(base, f'*_{folder.replace("-", "")}', f'FACTUR-X_{folder}.sch'))
        db = glob.glob(os.path.join(base, f'*_{folder.replace("-", "")}', f'FACTUR-X_{folder}_codedb.xml'))
        if len(sch) != 1 or len(db) != 1:
            raise SystemExit(f'Prüfregeln oder Codeliste von {folder} nicht gefunden')
        rows += rows_of(sch[0], profile)
        codes += codes_of(db[0], profile)
        if profile == 'EXTENDED':
            codes += item_attribute_codes(sch[0])
    # one identifier, one rule: where several profiles share it, it must ask the same
    seen = {}
    for r in rows:
        if r[5]:
            key = (r[3], r[4], r[7], r[8])
            if seen.setdefault(r[5], key) != key:
                raise SystemExit(f'{r[5]}: in zwei Profilen verschieden')
    for v in (c[2] for c in codes):
        if '\t' in v or '\n' in v:
            raise SystemExit(f'Codewert mit Tabulator oder Zeilenende: {v!r}')
    head = ['# generated by profilstruktur.py from the Schematron validation artefacts of Factur-X 1.09.2 / ZUGFeRD 2.5.2 (FNFE-MPE, FeRD;',
            '# Apache-2.0, notice in packaging/licenses/ZUGFeRD-Factur-X-Apache-2.0.txt); do not edit']
    open(os.path.join(HERE, 'profilstruktur.tsv'), 'w', encoding='utf-8', newline='\n').write('\n'.join(
        head + ['# profile\tpattern\trule\tcontext\tkind\tid\tseverity\tn\tpath\tlist'] + ['\t'.join(map(str, r)) for r in rows]) + '\n')
    open(os.path.join(HERE, 'profilcodes.tsv'), 'w', encoding='utf-8', newline='\n').write('\n'.join(
        head + ['# profile\tlist\tvalue'] + ['\t'.join(c) for c in codes]) + '\n')
    for profile in PROFILES.values():
        mine = [r for r in rows if r[0] == profile]
        kinds = {}
        for r in mine:
            kinds[r[4]] = kinds.get(r[4], 0) + 1
        print(profile, len({r[5] for r in mine if r[5]}), 'Kennungen', kinds, len([c for c in codes if c[0] == profile]), 'Codes')
    return 0


if __name__ == '__main__':
    sys.exit(main())
