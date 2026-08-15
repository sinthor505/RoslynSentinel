"""Group failing tests in a VSTest .trx file by the first line of their error message.

Usage: python scripts/triage_trx.py <results.trx> [top_n]
"""
import collections
import re
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def normalize(msg):
    """Collapse a failure message to a stable bucket key."""
    line = (msg or '').strip().splitlines()
    line = line[0] if line else '(no message)'
    line = re.sub(r"'[^']*'", "'X'", line)
    line = re.sub(r'\b[0-9a-f]{8}-[0-9a-f-]{27}\b', 'GUID', line)
    line = re.sub(r'\d+', 'N', line)
    return line[:200]


def main(path, top_n=40):
    root = ET.parse(path).getroot()
    buckets = collections.Counter()
    examples = {}
    classes = collections.Counter()
    for result in root.iter('{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult'):
        if result.get('outcome') != 'Failed':
            continue
        name = result.get('testName', '')
        msg_el = result.find('.//t:Output/t:ErrorInfo/t:Message', NS)
        key = normalize(msg_el.text if msg_el is not None else None)
        buckets[key] += 1
        examples.setdefault(key, name)
        classes[name.split('(')[0].rsplit('.', 1)[0]] += 1

    total = sum(buckets.values())
    print('FAILED: %d' % total)
    print()
    print('=== failure buckets ===')
    for key, n in buckets.most_common(top_n):
        print('%5d  %s' % (n, key))
        print('       e.g. %s' % examples[key])
    print()
    print('=== failures by fixture (top 25) ===')
    for cls, n in classes.most_common(25):
        print('%5d  %s' % (n, cls))


if __name__ == '__main__':
    main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 40)
