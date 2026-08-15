"""List the source line of every failing assertion in a .trx whose message matches a pattern.

Usage: python scripts/trx_failure_sites.py <results.trx> <substring> [--apply]

Prints "file:line<TAB>source" for each distinct failure site, so the offending assertions can
be inspected or patched directly.
"""
import collections
import re
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
FRAME = re.compile(r' in (?P<file>[A-Za-z]:\\[^\n]*?\.cs):line (?P<line>\d+)')


def main(path, needle):
    root = ET.parse(path).getroot()
    sites = collections.Counter()
    for result in root.iter('{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult'):
        if result.get('outcome') != 'Failed':
            continue
        msg_el = result.find('.//t:Output/t:ErrorInfo/t:Message', NS)
        msg = msg_el.text if msg_el is not None else ''
        if needle not in (msg or ''):
            continue
        st_el = result.find('.//t:Output/t:ErrorInfo/t:StackTrace', NS)
        st = st_el.text if st_el is not None else ''
        m = FRAME.search(st or '')
        if m:
            sites[(m.group('file'), int(m.group('line')))] += 1

    print('%d distinct sites, %d failures' % (len(sites), sum(sites.values())))
    for (f, ln), n in sorted(sites.items()):
        try:
            with open(f, encoding='utf-8') as fh:
                src = fh.readlines()[ln - 1].strip()
        except (OSError, IndexError):
            src = '<unreadable>'
        print('%s:%d\t%d\t%s' % (f, ln, n, src))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
