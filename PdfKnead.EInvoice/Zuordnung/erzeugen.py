#!/usr/bin/env python3
"""Generates the mapping tables of the e-invoice module (KONZEPT.md, section 5.24, „Weg für Stufe 2“).

    python PdfKnead.EInvoice/Zuordnung/erzeugen.py <folder xsl of the visualization> [--pruefen]

Source of the facts: the XRechnung visualization of the KoSIT (Apache-2.0; release and checksum in
tools/pruefer/erechnung/HERKUNFT.txt). Its stylesheets map every business term (BT) and group (BG) of EN 16931 to the XML of CII and UBL;
only these facts are taken — which term, which group it belongs to, the path relative to the group, the condition of the node, the name —
nothing is executed. Written next to this script:

    cii.tsv, ubl-invoice.tsv, ubl-creditnote.tsv   parent  id  select  match  name  context
    bezeichnungen.tsv                              id  key  de  en

„context“ names what surrounds the mapping in the stylesheet besides the plain group (a condition, a choice, a grouping); such rows are
handled or checked by hand in the C# code (InvoiceMap) and listed by --pruefen. Any construct this script does not know stops it: the
tables never guess. Only the Python standard library.
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

XSL = 'http://www.w3.org/1999/XSL/Transform'
XR = 'urn:ce.eu:en16931:2017:xoev-de:kosit:standard:xrechnung-1'
HERE = os.path.dirname(os.path.abspath(__file__))
SOURCES = {'cii.tsv': 'cii-xr.xsl', 'ubl-invoice.tsv': 'ubl-invoice-xr.xsl', 'ubl-creditnote.tsv': 'ubl-creditnote-xr.xsl'}
# what may surround an apply-templates: plain structure, or a context the C# code handles (recorded in the column „context“)
STRUCTURE = {f'{{{XSL}}}template', f'{{{XSL}}}attribute', f'{{{XSL}}}sequence', f'{{{XSL}}}copy-of'}
CONTEXTS = {f'{{{XSL}}}if': 'if', f'{{{XSL}}}when': 'when', f'{{{XSL}}}otherwise': 'otherwise', f'{{{XSL}}}for-each-group': 'group',
            f'{{{XSL}}}for-each': 'for-each', f'{{{XSL}}}choose': 'choose', f'{{{XSL}}}variable': 'variable'}


# Rows written by hand after templates the visualization builds without apply-templates (value-of): UBL process control (BG-2). The
# notes of UBL (BG-1, grouped by a subject code in front of the note) cannot be a row; InvoiceReader builds them.
MANUAL = {
    'ubl-invoice.tsv': [
        ('ROOT', 'BG-2', '.', '', 'PROCESS_CONTROL', ''),
        ('BG-2', 'BT-23', './cbc:ProfileID', '', 'Business_process_type', ''),
        ('BG-2', 'BT-24', './cbc:CustomizationID', '', 'Specification_identifier', ''),
    ],
}
MANUAL['ubl-creditnote.tsv'] = MANUAL['ubl-invoice.tsv']
# CII: the VAT point date (BT-7) is written by a named template over the distinct dates of all breakdowns — a row with context „distinct“
MANUAL['cii.tsv'] = [
    ('ROOT', 'BT-7', './rsm:SupplyChainTradeTransaction/ram:ApplicableHeaderTradeSettlement/ram:ApplicableTradeTax/ram:TaxPointDate/udt:DateString[@format = \'102\']',
     '', 'Value_added_tax_point_date', 'distinct'),
]
# Conditions read as the validation artefacts of both syntaxes read them (FG 834): the tax scheme without regard to space and case, the
# charge indicator as a boolean (space taken off, also „1“ and „0“). The visualization compares them exactly; a seller VAT identifier with
# the scheme „vat“ would then be no term at all, and the rules would miss what the artefacts see.
REWRITES = [
    (re.compile(r"((?:following-sibling::)?cac:TaxScheme/cbc:ID) ?(!?=) ?'VAT'"), r"upper-case(normalize-space(\1)) \2 'VAT'"),
    (re.compile(r"((?:ancestor::cac:AllowanceCharge/|preceding-sibling::)?cbc:ChargeIndicator|ram:ChargeIndicator/udt:Indicator) ?= ?'false'"),
     r"normalize-space(\1) = ('false', '0')"),
    (re.compile(r"((?:ancestor::cac:AllowanceCharge/|preceding-sibling::)?cbc:ChargeIndicator|ram:ChargeIndicator/udt:Indicator) ?= ?'true'"),
     r"normalize-space(\1) = ('true', '1')"),
]


def rewrite(text):
    for pattern, replacement in REWRITES:
        text = pattern.sub(replacement, text)
    return text


# terms the stylesheets write that InvoiceReader builds by hand (no row): the groups of payment instructions and UBL notes
HANDBUILT = {
    'cii.tsv': {'BG-16'},
    'ubl-invoice.tsv': {'BG-1', 'BT-21', 'BT-22', 'BG-16'},
    'ubl-creditnote.tsv': {'BG-1', 'BT-21', 'BT-22', 'BG-16'},
}


def parse(path):
    data = open(path, 'rb').read()
    if b'<!DOCTYPE' in data or b'<!ENTITY' in data:
        raise SystemExit(f'{path}: DOCTYPE oder ENTITY — nicht gelesen')
    return ET.fromstring(data)


def norm(xpath):
    return ' '.join(xpath.split())


def steps(xpath):
    """The steps of a path without conditions and without „.“ — to compare a selection with the match of a template."""
    depth, plain = 0, []
    for ch in xpath:
        if ch == '[':
            depth += 1
        elif ch == ']':
            depth -= 1
        elif depth == 0:
            plain.append(ch)
    return [s for s in ''.join(plain).split('/') if s and s != '.']


def walk(node, parents, out):
    """apply-templates below a template, with the chain of surrounding elements."""
    for child in node:
        if child.tag == f'{{{XSL}}}apply-templates' and child.get('mode') and child.get('select'):
            out.append((child, list(parents)))
        walk(child, parents + [child], out)


def table(xsl):
    root = parse(xsl)
    templates = {}          # mode -> [(match, name)]
    owners = []             # (owner id, template element)
    for t in root.iter(f'{{{XSL}}}template'):
        mode, match = t.get('mode'), t.get('match')
        xr = next((c for c in t.iter() if isinstance(c.tag, str) and c.tag.startswith(f'{{{XR}}}')), None)
        name = xr.tag.split('}', 1)[1] if xr is not None else ''
        if mode and re.fullmatch(r'B[TG]-(DEX-)?\d+(-\d+)?', mode):   # also the XRechnung extension (BG-DEX, BT-DEX)
            templates.setdefault(mode, []).append((norm(match or ''), name))
            owners.append((mode, t))
        elif match and not mode and match.count('/') == 1:
            owners.append(('ROOT', t))    # the root template (/rsm:CrossIndustryInvoice, /ubl:Invoice …)
    rows = []
    for owner, t in owners:
        found = []
        walk(t, [], found)
        for apply, chain in found:
            mode, select = apply.get('mode'), norm(apply.get('select'))
            context = []
            for el in chain:
                if el.tag == f'{{{XSL}}}variable' and el.get('name') == 'bg-contents':
                    continue   # the stylesheet only collects the group's content to leave out empty groups: structure, no condition
                if el.tag in CONTEXTS:
                    test = el.get('test') or el.get('select') or el.get('name') or ''
                    if el.get('group-by'):
                        test += '#' + el.get('group-by')   # what the members are grouped by (payment means: their code)
                    context.append(f'{CONTEXTS[el.tag]}:{norm(test)}')
                elif el.tag not in STRUCTURE and not (isinstance(el.tag, str) and el.tag.startswith(f'{{{XR}}}')):
                    raise SystemExit(f'{xsl}: unbekannte Form {el.tag} um {mode} ({select})')
            # the template(s) of that mode: the match patterns tell the condition of the node
            candidates = templates.get(mode)
            if not candidates:
                # no template of its own: the field is written inline around the apply-templates (BT-149, BT-150 choose between net
                # and gross price) — the name comes from the enclosing element of the visualization, the condition from the context
                inline = next((el for el in reversed(chain) if isinstance(el.tag, str) and el.tag.startswith(f'{{{XR}}}')), None)
                if inline is None:
                    raise SystemExit(f'{xsl}: keine Vorlage für {mode}')
                candidates = [('', inline.tag.split('}', 1)[1])]
            # several templates of one mode (BT-29: ram:ID or ram:GlobalID): the one whose path ends like the selection belongs to it
            if len(candidates) > 1:
                tail = steps(select)
                fitting = [c for c in candidates if tail and steps(c[0])[-len(tail):] == tail]
                if not fitting:
                    raise SystemExit(f'{xsl}: keine passende Vorlage für {mode} ({select})')
                candidates = fitting
            for match, name in candidates:
                rows.append((owner, mode, select, match, name, ' | '.join(context)))
    return rows


def labels(folder):
    out = {}
    for lang in ('de', 'en'):
        root = parse(os.path.join(folder, 'l10n', f'{lang}.xml'))
        for e in root.iter('entry'):
            key, ident = e.get('key', ''), e.get('id', '')
            out.setdefault(key, {'id': ident})[lang] = norm(e.text or '')   # labels may run over lines in the source
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    folder = sys.argv[1]
    check = '--pruefen' in sys.argv
    for target, source in SOURCES.items():
        rows = table(os.path.join(folder, source)) + MANUAL.get(target, [])
        rows = [tuple(rewrite(c) if i in (2, 3, 5) else c for i, c in enumerate(r)) for r in rows]
        with open(os.path.join(HERE, target), 'w', encoding='utf-8', newline='\n') as f:
            f.write(f'# generated by erzeugen.py from {source} of the XRechnung visualization (KoSIT, Apache-2.0); do not edit by hand\n')
            # the prefixes the paths use, as the stylesheet declares them (ElementTree drops the declarations: read from the text)
            raw = open(os.path.join(folder, source), encoding='utf-8').read()
            for prefix, uri in sorted(set(re.findall(r'xmlns:(\w+)="([^"]+)"', raw))):
                if prefix not in ('xsl', 'xs', 'xd', 'xlink', 'saxon', 'xr'):
                    f.write(f'#ns\t{prefix}\t{uri}\n')
            f.write('# parent\tid\tselect\tmatch\tname\tcontext\n')
            for r in rows:
                if any('\t' in c or '\n' in c for c in r):
                    raise SystemExit(f'{source}: Tabulator oder Zeilenumbruch in {r[1]}')
                f.write('\t'.join(r) + '\n')
        special = [r for r in rows if r[5]]
        print(f'{target}: {len(rows)} Zeilen, davon {len(special)} mit Kontext')
        # every term the stylesheet writes must be in the table — or built by hand in InvoiceReader (listed in HANDBUILT)
        written = set(re.findall(r"xr:id\"\s+select=\"'((?:BT|BG)-[\w-]+)'\"", open(os.path.join(folder, source), encoding='utf-8').read()))
        missing = sorted(written - {r[1] for r in rows} - HANDBUILT.get(target, set()))
        if missing:
            raise SystemExit(f'{source}: ohne Zeile und nicht von Hand gebaut: {", ".join(missing)}')
        if check:
            for r in special:
                print(f'   {r[0]} -> {r[1]}  [{r[5][:140]}]  select={r[2][:80]}')
    lab = labels(folder)
    with open(os.path.join(HERE, 'bezeichnungen.tsv'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('# generated by erzeugen.py from l10n/de.xml and en.xml of the XRechnung visualization (KoSIT, Apache-2.0)\n')
        f.write('# id\tkey\tde\ten\n')
        for key, v in sorted(lab.items(), key=lambda kv: kv[0]):
            f.write(f"{v.get('id', '')}\t{key}\t{v.get('de', '')}\t{v.get('en', '')}\n")
    print(f'bezeichnungen.tsv: {len(lab)} Einträge')
    return 0


if __name__ == '__main__':
    sys.exit(main())
