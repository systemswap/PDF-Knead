"""
Differenzialtest der Regeln für hybride Dokumente (Stufe 4, Teil h) gegen die PDF-Prüfung des zweiten Referenz-Prüfers.

Aufruf (aus dem Hauptordner des Projekts, ohne python -I):
    python PdfKnead.EInvoice/Zuordnung/pdfregelprobe.py <ausgabeordner> <sammlung> [<sammlung> …] [--nur-vergleichen]

Für jedes PDF der Sammlungen: der Bericht des Referenz-Prüfers (zwischengespeichert in <ausgabeordner>/referenz, ein Lauf je Datei)
und die eigenen Funde (`pdf-knead erechnung <sammlung> --pdfregeln-aus <ausgabeordner>/eigen/<n>`). Verglichen wird je Regel:
beide, nur eigen, nur Referenz — die Meldungen des Referenz-Prüfers über eine Zuordnung seiner Nummern zu den Kennungen der
Spezifikation; sein PDF/A-Ergebnis (veraPDF, „isCompliant“) gegen BR-HYBRID-02. Unbekannte Nummern werden mit Text aufgelistet.
Eigene Hinweise (PDFK-…) und Regeln ohne Gegenstück beim Referenz-Prüfer werden gezählt, nicht verglichen. Nichts davon kommt ins Git
außer diesem Skript.
"""
import glob
import os
import re
import shutil
import subprocess
import sys
from collections import Counter, defaultdict

# Nummern der Meldungen des Referenz-Prüfers (Abschnitt <pdf>) → Kennung der Spezifikation; nur Hinweise (notice) zählen nicht als Fund
MAP = {
    '11': 'BR-HYBRID-07', '12': 'BR-HYBRID-07',
    '13': 'BR-HYBRID-06', '14': 'BR-HYBRID-06',
    '15': 'BR-HYBRID-09', '16': 'BR-HYBRID-09',
    '19': 'BR-HYBRID-08', '21': 'BR-HYBRID-08',
}
# Begründete Abweichungen bei BR-HYBRID-02, die auch das eigenständige veraPDF nicht meldet (Datei → Grund)
KNOWN_PDFA = {
    'sample_inofficial_20190125_atgp_factur-x_v_1_0.pdf':
        'ISO 19005-2, 6.1.9: vor „endobj“ steht ein Leerzeichen statt eines Zeilenendes (3 Objekte); veraPDF prüft das nicht',
}
# Regeln, für die der Referenz-Prüfer kein Gegenstück meldet (nur gezählt)
NO_COUNTERPART = {'BR-HYBRID-03', 'BR-HYBRID-04', 'BR-HYBRID-05', 'BR-HYBRID-10', 'BR-HYBRID-11', 'BR-HYBRID-12', 'BR-HYBRID-13',
                  'BR-HYBRID-14', 'BR-HYBRID-15', 'BR-HYBRID-DE-01', 'BR-HYBRID-DE-02', 'BR-HYBRID-FR-01', 'BR-FX-DE-04'}


def find_java():
    for pattern in ('tools/pruefer/signaturen/*/java/bin/java.exe', 'tools/pruefer/signaturen/*/java/bin/java'):
        found = glob.glob(pattern)
        if found:
            return found[0]
    return shutil.which('java')


def find_cli():
    for pattern in ('PdfKnead.Cli/bin/Debug/net10.0/pdf-knead.exe', 'PdfKnead.Cli/bin/Debug/net10.0/pdf-knead',
                    'PdfKnead.Cli/bin/Release/net10.0/pdf-knead.exe', 'PdfKnead.Cli/bin/Release/net10.0/pdf-knead'):
        if os.path.exists(pattern):
            return os.path.abspath(pattern)   # Windows starts a relative path with „/“ not
    return None


def verapdf_fails(pdf):
    """Whether the standalone veraPDF (tools/pruefer/pdfa) finds the file not compliant with the part it declares."""
    tool = next(iter(glob.glob('tools/pruefer/pdfa/verapdf.bat') + glob.glob('tools/pruefer/pdfa/verapdf')), None)
    if tool is None:
        return False
    run = subprocess.run([os.path.abspath(tool), '--format', 'text', pdf], capture_output=True, timeout=600, shell=tool.endswith('.bat'))
    return run.stdout.decode('utf-8', 'replace').lstrip().startswith('FAIL')


def safe(rel):
    return rel.replace(os.sep, '__').replace('/', '__')


def referenz(java, jar, pdf, target):
    if not os.path.exists(target):
        source = pdf
        if not pdf.isascii():
            # the reference checker does not find a path with combining characters („File not found“): a copy with a plain name
            source = os.path.join(os.path.dirname(target), 'kopie.pdf')
            shutil.copyfile(pdf, source)
        run = subprocess.run([java, '-jar', jar, '--action', 'validate', '--source', source, '--no-notices'],
                             capture_output=True, timeout=600)
        with open(target, 'wb') as f:
            f.write(run.stdout)
    with open(target, 'rb') as f:
        text = f.read().decode('utf-8', 'replace')
    part = re.search(r'<pdf>(.*?)</pdf>', text, re.S)
    part = part.group(1) if part else ''
    compliant = re.search(r'isCompliant=(true|false)', part)
    errors = re.findall(r'<error type="(\d+)">([^<]*)</error>', part)
    return (compliant.group(1) == 'true') if compliant else None, errors


def main():
    sys.stdout.reconfigure(encoding='utf-8')   # file names of the collections hold characters the Windows console code page lacks
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    only_compare = '--nur-vergleichen' in sys.argv
    if len(args) < 2:
        print(__doc__)
        return 2
    out, collections = args[0], args[1:]
    java, cli = find_java(), find_cli()
    jar = next(iter(glob.glob('tools/pruefer/erechnung/*/*-CLI-*.jar')), None)   # the command line of the second reference checker
    if not (java and jar and cli):
        print('Java, Referenz-Prüfer oder pdf-knead fehlt (Kommandozeile vorher bauen).')
        return 2
    os.makedirs(os.path.join(out, 'referenz'), exist_ok=True)
    both, ours_only, theirs_only, counted, justified = Counter(), Counter(), Counter(), Counter(), Counter()
    unknown = defaultdict(lambda: [0, ''])
    offen = []
    for n, coll in enumerate(collections):
        eigen = os.path.join(out, 'eigen', str(n))
        if not only_compare:
            shutil.rmtree(eigen, ignore_errors=True)
            subprocess.run([cli, 'erechnung', coll, '--pdfregeln-aus', eigen], capture_output=True)
        for pdf in sorted(glob.glob(os.path.join(coll, '**', '*.pdf'), recursive=True)):
            rel = os.path.relpath(pdf, coll)
            mine_file = os.path.join(eigen, safe(rel) + '.txt')
            if not os.path.exists(mine_file):
                continue   # kein Rechnungs-XML oder kein hybrides Dokument: keine eigenen Regeln, nichts zu vergleichen
            with open(mine_file, encoding='utf-8') as f:
                mine = {line.split('\t')[0] for line in f if line.strip()}
            compliant, errors = referenz(java, jar, pdf, os.path.join(out, 'referenz', f'{n}-{safe(rel)}.xml'))
            theirs = set()
            for code, text in errors:
                if code in MAP:
                    theirs.add(MAP[code])
                else:
                    unknown[code][0] += 1
                    unknown[code][1] = text
            if compliant is False:
                theirs.add('BR-HYBRID-02')
            for rule in mine:
                if rule.startswith('PDFK-') or rule in NO_COUNTERPART:
                    counted[rule] += 1
            for rule in (mine | theirs) - NO_COUNTERPART:
                if rule.startswith('PDFK-'):
                    continue
                if rule in mine and rule in theirs:
                    both[rule] += 1
                    continue
                side = 'nur eigen' if rule in mine else 'nur Referenz'
                (ours_only if rule in mine else theirs_only)[rule] += 1
                reason = None
                if 'PDFK-PDF-03' in mine and rule != 'BR-HYBRID-02' and rule in theirs:
                    reason = 'ZUGFeRD 1: die Werte im XMP misst PDF-Knead nicht an der Spezifikation 2.5.2 (PDFK-PDF-03)'
                elif rule == 'BR-HYBRID-02' and rule in mine:
                    if verapdf_fails(pdf):
                        reason = 'das eigenständige veraPDF meldet die Abweichung auch (das im Referenz-Prüfer nicht)'
                    elif os.path.basename(pdf) in KNOWN_PDFA:
                        reason = KNOWN_PDFA[os.path.basename(pdf)]
                if reason:
                    justified[reason] += 1
                else:
                    offen.append((rel, rule, side))
    print('Verglichen (beide / nur eigen / nur Referenz):')
    for rule in sorted(set(both) | set(ours_only) | set(theirs_only)):
        print(f'  {rule}: {both[rule]} / {ours_only[rule]} / {theirs_only[rule]}')
    print('Nur eigen gezählt (kein Gegenstück beim Referenz-Prüfer):')
    for rule, c in sorted(counted.items()):
        print(f'  {rule}: {c}')
    print('Begründet:')
    for reason, c in sorted(justified.items()):
        print(f'  {c}× {reason}')
    if unknown:
        print('Nummern des Referenz-Prüfers ohne Zuordnung:')
        for code, (c, text) in sorted(unknown.items(), key=lambda kv: int(kv[0])):
            print(f'  {code} ({c}×): {text.strip()[:120]}')
    print(f'Offen: {len(offen)}')
    for rel, rule, side in offen[:60]:
        print(f'  {rule} {side}: {rel}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
