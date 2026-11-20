#!/usr/bin/env python3
"""Look into compiled Schematron validation artefacts (XSL) before writing a rule (KONZEPT.md, section 5.24, stage 4): where a rule looks
(context), what it tests, and which rule can shadow which — in a Schematron pattern only the first rule whose context fits a node is
asked. Local tool, nothing of it ships; it prints parts of the artefacts (EUPL-1.2) on the console only, never into the repository.

    python PdfKnead.EInvoice/Zuordnung/prueferegeln.py <artefact.xsl> --muster
    python PdfKnead.EInvoice/Zuordnung/prueferegeln.py <artefact.xsl> <rule id> [<rule id> …]

--muster lists per pattern the rules in the order they are asked, with the identifiers of their assertions (SchXslt-compiled artefacts,
as those of XRechnung). With rule identifiers: the context and the test of each assertion of that identifier (SchXslt and the ISO skeleton
of the EN 16931 artefacts). Only the Python standard library; the artefact is read as text.
"""
import re
import sys


def flat(text, limit):
    return re.sub(r'\s+', ' ', text)[:limit]


def patterns(text):
    """Per pattern the rules (priority, context, assertion identifiers), highest priority first (SchXslt)."""
    found = {}
    for m in re.finditer(r'<xsl:template match="([^"]*)" priority="(\d+)" mode="[^"]+">(.*?)</xsl:template>', text, re.S):
        body = m.group(3)
        pattern = re.search(r'<schxslt:rule pattern="([^"]+)">', body)
        ids = re.findall(r'<svrl:failed-assert[^>]*\bid="([^"]+)"', body)
        found.setdefault(pattern.group(1) if pattern else '?', []).append((int(m.group(2)), m.group(1), ids))
    return {p: sorted(rules, key=lambda r: -r[0]) for p, rules in found.items()}


def assertions(text, wanted):
    """(identifier, context, test) of the wanted assertions, the test as the assertion states it (what must hold) — SchXslt (the failed
    assertion carries it as its test attribute) and the ISO skeleton (fired rule, then a failed assertion with its test and an id)."""
    for m in re.finditer(r'<xsl:template match="([^"]*)"[^>]*>(.*?)</xsl:template>', text, re.S):
        context, body = m.group(1), m.group(2)
        for a in re.finditer(r'<svrl:failed-assert[^>]*?\bid="([^"]+)">\s*<xsl:attribute name="test">([^<]*)</xsl:attribute>', body):
            if a.group(1) in wanted:
                yield a.group(1), context, a.group(2)
        fired = re.search(r'<svrl:fired-rule context="([^"]*)"', body)
        for a in re.finditer(r'<svrl:failed-assert test="([^"]*)"[^>]*>\s*<xsl:attribute name="id">([^<]+)</xsl:attribute>', body):
            if a.group(2).strip() in wanted:
                yield a.group(2).strip(), fired.group(1) if fired else context, a.group(1)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    text = open(sys.argv[1], encoding='utf-8').read()
    if sys.argv[2] == '--muster':
        for pattern, rules in patterns(text).items():
            print(f'-- Muster {pattern}')
            for priority, context, ids in rules:
                print(f'   {priority:3d} {flat(context, 160)}')
                print(f'       {" ".join(ids)}')
        return 0
    wanted = set(sys.argv[2:])
    seen = set()
    for ident, context, test in assertions(text, wanted):
        seen.add(ident)
        print(ident)
        print('  Kontext:', flat(context, 600))
        print('  Prüfung:', flat(test, 900))
    for ident in sorted(wanted - seen):
        print(f'{ident}: nicht gefunden')
    return 0


if __name__ == '__main__':
    sys.exit(main())
