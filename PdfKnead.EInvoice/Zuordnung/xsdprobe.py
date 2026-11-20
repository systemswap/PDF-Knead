#!/usr/bin/env python3
"""Differential test of the schema check of the e-invoice (KONZEPT.md, section 5.24, stage 4, part f): every invoice of a folder is checked
by PDF-Knead (its rules PDFK-XSD-01 to -06) and by the reference checker of the KoSIT, whose schema step is the XSD validator of the JDK
(Xerces) — run with a scenario file of our own per schema that does only that step, with the schemas the reference checkers use: UBL 2.1
and CII D16B of the KoSIT configuration, the profile schemas of the Factur-X 1.09.2 / ZUGFeRD 2.5.2 package; the schema chosen per file as
PDF-Knead chooses it. The error codes of the validator (cvc-…) are read as the kinds of PDF-Knead. Compared per file: valid or not, and
the kinds of finding. Local tool, nothing of it ships.

    python PdfKnead.EInvoice/Zuordnung/xsdprobe.py <folder of invoices (flat)> [<folder> …] [--ausgabe <folder>]

Needs Java and the KoSIT validator with its configuration (tools/pruefer/erechnung) and the package in Normen-ISO/erechnung/zugferd. Only
the Python standard library.
"""
import os
import re
import shutil
import subprocess
import sys
import urllib.parse
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
from regelmutation import FX_DIR, FX_PROFILES, find_java, head_of  # noqa: E402
from regelprobe import find_cli  # noqa: E402

KOSIT = os.path.join(REPO, 'tools', 'pruefer', 'erechnung', 'kosit', 'xrechnung-konfiguration', 'resources')
VALIDATOR = os.path.join(REPO, 'tools', 'pruefer', 'erechnung', 'kosit', 'validator-1.6.3-standalone.jar')
REPORT = os.path.join(KOSIT, 'default-report.xsl')
SCHEMAS = {
    'ubl-invoice': os.path.join(KOSIT, 'ubl', '2.1', 'xsd', 'maindoc', 'UBL-Invoice-2.1.xsd'),
    'ubl-creditnote': os.path.join(KOSIT, 'ubl', '2.1', 'xsd', 'maindoc', 'UBL-CreditNote-2.1.xsd'),
    'cii-d16b': os.path.join(KOSIT, 'cii', '16b', 'xsd', 'CrossIndustryInvoice_100pD16B.xsd'),
}
for key, (folder, name) in FX_PROFILES.items():
    # the schema is named by the folder (BASICWL), the style sheet by the profile (BASIC-WL)
    SCHEMAS['fx-' + key] = os.path.join(FX_DIR, folder, f'Factur-X_1.09.2_{folder.rsplit("_", 1)[1]}.xsd')
CII = b'urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100'
UBL_INVOICE = b'urn:oasis:names:specification:ubl:schema:xsd:Invoice-2'
UBL_CREDITNOTE = b'urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2'
SCENARIO = """<?xml version="1.0" encoding="UTF-8"?>
<scenarios xmlns="http://www.xoev.de/de/validator/framework/1/scenarios" frameworkVersion="1.0.0">
  <name>PDF-Knead Referenz XSD</name><author>PDF-Knead</author><date>2026-11-14</date>
  <description><p>Nur der Schemaschritt (Differenzialtest der Schemaprüfung).</p></description>
  <scenario>
    <name>XSD</name><description><p>{name}</p></description>
    <namespace prefix="rep">http://www.xoev.de/de/validator/varl/1</namespace>
    <match>exists(/*)</match>
    <validateWithXmlSchema><resource><name>{name}</name><location>{schema}</location></resource></validateWithXmlSchema>
    <createReport><resource><name>Bericht</name><location>{report}</location></resource></createReport>
    <acceptMatch>/rep:report/rep:assessment[1]/rep:accept[1]</acceptMatch>
  </scenario>
  <noScenarioReport><resource><name>Bericht</name><location>{report}</location></resource></noScenarioReport>
</scenarios>
"""


def profile_of(guideline):
    """The profile as InvoiceKind.ProfileOf reads BT-24: 'minimum' … 'extended', 'ctc-fr', or None for any other."""
    g = guideline.strip().lower()
    if not g or ':order:' in g or g.startswith('urn:ferd:'):
        return None
    if 'xeinkauf.de:kosit:xrechnung' in g or 'xeinkauf.de:kosit:extension:xrechnung' in g or 'xoev-de:kosit:standard:xrechnung' in g \
            or 'peppol.eu:2017:poacc:billing' in g:
        return None
    if 'extended-ctc-fr' in g:
        return 'ctc-fr'
    for suffix, profile in ((':minimum', 'minimum'), (':basicwl', 'basicwl'), (':basic', 'basic'), (':extended', 'extended')):
        if g.endswith(suffix):
            return profile
    if g in ('urn:cen.eu:en16931:2017', 'urn:cen.eu:en16931:2026') or g.endswith((':en16931', ':comfort')):
        return 'en16931'
    return None


def schema_of(path):
    """The schema PDF-Knead checks a file against (InvoiceRules.SchemaOf), or None (no schema, or no invoice of these syntaxes)."""
    data = head_of(path)
    root = re.search(rb'<(?:(\w+):)?(\w+)[\s>/]', re.sub(rb'^(\xef\xbb\xbf)?\s*', b'', data))
    if not root:
        return None
    prefix, local = root.group(1), root.group(2)
    declared = {p or b'': u for p, u in re.findall(rb'xmlns(?::(\w+))?\s*=\s*"([^"]*)"', data[:root.end() + 8000])}
    ns = declared.get(prefix or b'')
    if ns == UBL_INVOICE and local == b'Invoice':
        return 'ubl-invoice'
    if ns == UBL_CREDITNOTE and local == b'CreditNote':
        return 'ubl-creditnote'
    if ns == CII and local == b'CrossIndustryInvoice':
        found = re.search(rb'GuidelineSpecifiedDocumentContextParameter>\s*<(?:\w+:)?ID>([^<]*)</', data)
        profile = profile_of(found.group(1).decode('utf-8', 'replace') if found else '')
        return None if profile == 'ctc-fr' else 'fx-' + profile if profile else 'cii-d16b'
    return None


def kind(code):
    """An error code of the validator as the kind of PDF-Knead (PDFK-XSD-01 to -06); an unknown code as itself."""
    if code.startswith('cvc-complex-type.2.4.b'):
        return 'PDFK-XSD-02'
    if code.startswith(('cvc-complex-type.2.4', 'cvc-elt.1')):
        return 'PDFK-XSD-01'
    if code.startswith('cvc-complex-type.3.2'):
        return 'PDFK-XSD-04'
    if code.startswith('cvc-complex-type.4'):
        return 'PDFK-XSD-05'
    if code.startswith(('cvc-complex-type.2.1', 'cvc-complex-type.2.3')):
        return 'PDFK-XSD-06'
    if code.startswith(('cvc-datatype-valid', 'cvc-type.3.1.3', 'cvc-attribute.3', 'cvc-complex-type.2.2')) or re.match(
            r'^cvc-(pattern|enumeration|length|minLength|maxLength|totalDigits|fractionDigits|minInclusive|maxInclusive|minExclusive|'
            r'maxExclusive)-valid', code):
        return 'PDFK-XSD-03'
    return code or '?'


def reference(java, schema, members, work):
    """The kinds the reference finds per file name ('NICHT-LESBAR' where its reader refused the file before the schema step)."""
    folder = os.path.join(work, 'ein')
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(folder)
    for f in members:
        try:
            os.link(f, os.path.join(folder, os.path.basename(f)))
        except OSError:
            shutil.copyfile(f, os.path.join(folder, os.path.basename(f)))

    def rel(p):
        # a location of the scenario file is a URI: space and the like percent-encoded
        return urllib.parse.quote(os.path.relpath(p, REPO).replace(os.sep, '/'), safe='/')
    open(os.path.join(work, 'scenarios.xml'), 'w', encoding='utf-8').write(
        SCENARIO.format(name=os.path.basename(SCHEMAS[schema]), schema=rel(SCHEMAS[schema]), report=rel(REPORT)))
    subprocess.run([java, '-jar', VALIDATOR, '-s', os.path.join(work, 'scenarios.xml'), '-r', REPO, '-o', os.path.join(work, 'aus'), folder],
                   check=False, capture_output=True)
    found = {}
    for f in members:
        name = os.path.basename(f)
        report = os.path.join(work, 'aus', os.path.splitext(name)[0] + '-report.xml')
        if not os.path.exists(report):
            found[name] = 'NICHT-LESBAR'
            continue
        steps = [e for e in ET.parse(report).getroot().iter() if e.tag.endswith('}validationStepResult')]
        xsd = [s for s in steps if s.get('id') == 'val-xsd']
        if not xsd:
            found[name] = 'NICHT-LESBAR'
            continue
        kinds = {kind(m.get('code') or '') for s in xsd for m in s if m.tag.endswith('}message') and m.get('level') == 'error'}
        found[name] = ','.join(sorted(kinds)) if kinds else '-'
    return found


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    out = sys.argv[sys.argv.index('--ausgabe') + 1] if '--ausgabe' in sys.argv else os.path.join(
        os.environ.get('LOCALAPPDATA', os.path.expanduser('~/.local/share')), 'pdf-knead-build', 'erechnung', 'xsdprobe')
    if out in args:
        args.remove(out)
    if not args:
        print(__doc__)
        return 2
    java, cli = find_java(), find_cli()
    total, compared, unread, verdicts, kinds = 0, 0, Counter(), [], []
    per_kind = Counter()
    for folder in args:
        # the folder with its parents: the input folders of the probes are called „ein“ or „ein/cii“
        label = '-'.join(os.path.normpath(folder).split(os.sep)[-3:])
        files = sorted(os.path.join(folder, f) for f in os.listdir(folder) if f.lower().endswith('.xml'))
        ours_dir = os.path.join(out, 'pdfknead', label)
        shutil.rmtree(ours_dir, ignore_errors=True)
        os.makedirs(ours_dir)
        subprocess.run([cli, 'erechnung', folder, '--regeln-aus', ours_dir], check=False, capture_output=True)
        groups = defaultdict(list)
        for f in files:
            schema = schema_of(f)
            if schema:
                groups[schema].append(f)
            else:
                unread['ohne Schema'] += 1
        theirs = {}
        for schema, members in groups.items():
            theirs.update(reference(java, schema, members, os.path.join(out, 'referenz', label, schema)))
        for f in files:
            name = os.path.basename(f)
            if name not in theirs:
                continue
            total += 1
            if theirs[name] == 'NICHT-LESBAR':
                unread['Referenz: nicht lesbar'] += 1
                continue
            our_file = os.path.join(ours_dir, name + '.txt')
            if not os.path.exists(our_file):
                unread['PDF-Knead: nicht lesbar'] += 1
                continue
            compared += 1
            mine = {line.strip() for line in open(our_file, encoding='utf-8') if re.match(r'^PDFK-XSD-0[1-6]$', line.strip())}
            theirs_set = set() if theirs[name] == '-' else set(theirs[name].split(','))
            per_kind.update(theirs_set)
            if bool(mine) != bool(theirs_set):
                verdicts.append((f'{label}/{name}', sorted(mine), sorted(theirs_set)))
            elif mine != theirs_set:
                kinds.append((f'{label}/{name}', sorted(mine), sorted(theirs_set)))
    lines = [f'{total} Dateien mit Schema, {compared} verglichen; Urteil verschieden: {len(verdicts)}; Befundarten verschieden: {len(kinds)}; '
             + '; '.join(f'{k}: {n}' for k, n in sorted(unread.items())),
             'Befundarten der Referenz (Dateien): ' + ', '.join(f'{k} {n}' for k, n in sorted(per_kind.items()))]
    for title, items in (('Urteil verschieden', verdicts), ('Befundarten verschieden', kinds)):
        if items:
            lines.append(f'{title}:')
            lines += [f'  {n}: PDF-Knead {",".join(a) or "gültig"} — Referenz {",".join(b) or "gültig"}' for n, a, b in items[:80]]
    os.makedirs(out, exist_ok=True)
    open(os.path.join(out, 'bericht.txt'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    print('\n'.join(lines))
    return 0


if __name__ == '__main__':
    sys.exit(main())
