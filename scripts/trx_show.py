"""Print full message + stack trace for failing tests matching a name or message substring.

Usage: python scripts/trx_show.py <results.trx> <substring> [count]
"""
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def main(path, needle, count=3):
    root = ET.parse(path).getroot()
    shown = 0
    for result in root.iter('{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult'):
        if result.get('outcome') != 'Failed':
            continue
        name = result.get('testName', '')
        msg_el = result.find('.//t:Output/t:ErrorInfo/t:Message', NS)
        st_el = result.find('.//t:Output/t:ErrorInfo/t:StackTrace', NS)
        msg = (msg_el.text if msg_el is not None else '') or ''
        st = (st_el.text if st_el is not None else '') or ''
        if needle not in name and needle not in msg:
            continue
        print('=' * 78)
        print('TEST: ' + name)
        print('MSG : ' + msg.strip()[:900])
        print('STACK:')
        for line in st.strip().splitlines()[:8]:
            print('   ' + line.strip())
        shown += 1
        if shown >= count:
            return


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 3)
