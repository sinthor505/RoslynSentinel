"""Restore each modified file's original line-ending style.

Scripted edits during the RoslynSentinel.Tests migration flipped CRLF <-> LF in some files,
which inflates the diff. For every path given, compare the dominant line ending in the git HEAD
blob against the working copy and rewrite the working copy to match HEAD.
"""
import subprocess
import sys

CR = b'\r'
LF = b'\n'
CRLF = b'\r\n'


def dominant(data):
    crlf = data.count(CRLF)
    lf = data.count(LF) - crlf
    return CRLF if crlf >= lf else LF


def main(paths):
    for path in paths:
        try:
            head = subprocess.run(['git', 'show', 'HEAD:' + path],
                                  capture_output=True, check=True).stdout
        except subprocess.CalledProcessError:
            print('skip (not in HEAD): ' + path)
            continue
        with open(path, 'rb') as fh:
            cur = fh.read()
        want = dominant(head)
        have = dominant(cur)
        if want == have:
            continue
        normalized = cur.replace(CRLF, LF)
        if want == CRLF:
            normalized = normalized.replace(LF, CRLF)
        with open(path, 'wb') as fh:
            fh.write(normalized)
        print('%s: %s -> %s' % (path,
                                'CRLF' if have == CRLF else 'LF',
                                'CRLF' if want == CRLF else 'LF'))


if __name__ == '__main__':
    main(sys.argv[1:])
