"""Patch assertions that still treat a DocumentEditResult as the old string return value.

Engines that used to return the updated source text as a string now return DocumentEditResult;
the text moved to .UpdatedText. This reads the failure sites out of a .trx and rewrites
`Assert.That(<subject>, ...)` to `Assert.That(<subject>.<field>, ...)` on exactly those lines.

Usage: python scripts/patch_docedit_asserts.py <results.trx> <message-substring> [field]
"""
import collections
import re
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
FRAME = re.compile(r' in (?P<file>[A-Za-z]:\\[^\n]*?\.cs):line (?P<line>\d+)')
ASSERT = re.compile(r'(Assert\.That\(\s*)(?P<subject>[A-Za-z_][A-Za-z0-9_]*)(\s*,)')


def sites(trx, needle):
    root = ET.parse(trx).getroot()
    found = collections.defaultdict(set)
    for result in root.iter('{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult'):
        if result.get('outcome') != 'Failed':
            continue
        msg_el = result.find('.//t:Output/t:ErrorInfo/t:Message', NS)
        if needle not in ((msg_el.text if msg_el is not None else '') or ''):
            continue
        st_el = result.find('.//t:Output/t:ErrorInfo/t:StackTrace', NS)
        m = FRAME.search((st_el.text if st_el is not None else '') or '')
        if m:
            found[m.group('file')].add(int(m.group('line')))
    return found


def main(trx, needle, field='UpdatedText'):
    patched = 0
    skipped = []
    for path, lines in sites(trx, needle).items():
        with open(path, 'rb') as fh:
            raw = fh.read()
        nl = b'\r\n' if raw.count(b'\r\n') >= raw.count(b'\n') - raw.count(b'\r\n') else b'\n'
        text = raw.decode('utf-8').replace('\r\n', '\n').split('\n')
        for ln in sorted(lines):
            idx = ln - 1
            if idx >= len(text):
                skipped.append('%s:%d (out of range)' % (path, ln))
                continue
            line = text[idx]
            m = ASSERT.search(line)
            if not m or ('.' + field) in line:
                skipped.append('%s:%d %s' % (path, ln, line.strip()[:90]))
                continue
            text[idx] = line[:m.end('subject')] + '.' + field + line[m.end('subject'):]
            patched += 1
        out = '\n'.join(text)
        if nl == b'\r\n':
            out = out.replace('\n', '\r\n')
        with open(path, 'wb') as fh:
            fh.write(out.encode('utf-8'))
    print('patched %d assertion(s) to .%s' % (patched, field))
    if skipped:
        print('skipped %d:' % len(skipped))
        for s in skipped[:40]:
            print('  ' + s)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else 'UpdatedText')
