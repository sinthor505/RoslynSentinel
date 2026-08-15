"""One-shot migration helper: remap tool-class constructor arguments in RoslynSentinel.Tests.

The Basic/Advanced server split (commit 6e12ab2) changed the constructor signatures of the
Sentinel*Tools classes. This script rewrites the positional argument lists in the legacy test
project to the current signatures. It is idempotent: a call is only rewritten when its argument
count still matches the pre-split shape.
"""
import os
import re
import sys

BACKSLASH = chr(92)


def split_args(s):
    """Split a C# argument list on top-level commas."""
    out, depth, cur, i = [], 0, [], 0
    instr = False
    verbat = False
    while i < len(s):
        c = s[i]
        if instr:
            cur.append(c)
            if verbat:
                if c == '"':
                    if i + 1 < len(s) and s[i + 1] == '"':
                        cur.append(s[i + 1])
                        i += 2
                        continue
                    instr = False
                    verbat = False
            else:
                if c == BACKSLASH and i + 1 < len(s):
                    cur.append(s[i + 1])
                    i += 2
                    continue
                if c == '"':
                    instr = False
            i += 1
            continue
        if c == '"':
            instr = True
            if cur and cur[-1] == '@':
                verbat = True
            cur.append(c)
            i += 1
            continue
        if c in '(<[{':
            depth += 1
        elif c in ')>]}':
            depth -= 1
        if c == ',' and depth == 0:
            out.append(''.join(cur).strip())
            cur = []
        else:
            cur.append(c)
        i += 1
    if ''.join(cur).strip():
        out.append(''.join(cur).strip())
    return out


def find_calls(src, ctor):
    """Return (arg_start, arg_end, argstring) for each `new <ctor>(...)` in src."""
    res = []
    for m in re.finditer(r'new\s+' + ctor + r'\s*\(', src):
        i = m.end() - 1
        depth = 0
        j = i
        for j in range(i, len(src)):
            if src[j] == '(':
                depth += 1
            elif src[j] == ')':
                depth -= 1
                if depth == 0:
                    break
        res.append((m.end(), j, src[m.end():j]))
    return res


VALIDATION = ('new ValidationEngine(NullLogger<ValidationEngine>.Instance, '
              '_workspaceManager, new DiffEngine(_workspaceManager))')

# ctor name -> (pre-split argument count, rebuild function)
SPECS = {
    # dropped: performance, security, logicOpt, asyncSafety, asyncOpt, pathDrivenTest, asyncBatch
    # added:   msToolAugmentEngine
    'SentinelQualityTools': (
        17,
        lambda a: [a[2], a[3], a[5], a[7], a[9], a[10], a[11], a[13],
                   'new MsToolAugmentEngine(_workspaceManager)', a[15], a[16]],
    ),
    # dropped: breakingChangeEngine, cloneDetectionEngine
    'SentinelIntelligenceTools': (21, lambda a: a[:16] + a[18:]),
    # dropped: advancedStructural, advancedLogic, refinement, advancedType, advancedRefactoring,
    #          logicOpt, modernization, outParam
    # added:   validationEngine (before config)
    'SentinelRefactoringTools': (
        22,
        lambda a: [a[0], a[1], a[3], a[4], a[5], a[9], a[10], a[11],
                   a[16], a[17], a[18], a[19], VALIDATION, a[20], a[21]],
    ),
    # dropped: asyncOptimizationEngine, apiIntegrationEngine
    'SentinelGenerationTools': (6, lambda a: [a[0], a[1], a[4], a[5]]),
    # added: projectConsistencyEngine (before config)
    'SentinelWorkspaceTools': (
        9,
        lambda a: a[:7] + ['new ProjectConsistencyEngine(_workspaceManager)'] + a[7:],
    ),
}


def main(paths):
    for path in paths:
        src = open(path, encoding='utf-8').read()
        changed = False
        for ctor, (n_old, build) in SPECS.items():
            while True:
                target = None
                for (s, e, argstr) in find_calls(src, ctor):
                    args = split_args(argstr)
                    if len(args) == n_old:
                        target = (s, e, args)
                        break
                if target is None:
                    break
                s, e, args = target
                src = src[:s] + (',' + os.linesep + '            ').join(build(args)) + src[e:]
                changed = True
        if changed:
            open(path, 'w', encoding='utf-8', newline='').write(src)
            print('updated ' + os.path.basename(path))


if __name__ == '__main__':
    main(sys.argv[1:])
