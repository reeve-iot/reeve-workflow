#!/usr/bin/env python3
import itertools
import re
import sys

PATH = 'contracts/port-type-names.tsv'
MARKER = '# Nullable and constructor grammar rows'
RESERVED_MARKER = '# Rows for the names Any and Float'
BUILTIN_CANONICAL = {'string': 'String', 'number': 'Number', 'int': 'Int', 'boolean': 'Boolean', 'bool': 'Boolean',
                     'datetime': 'DateTime', 'json': 'Json'}
RESERVED = {'any': 'Any', 'float': 'Float'}
FIELDS = ("# fields: name; 'term' depth element canonical-under-Builtin, or 'syntax' Fault position. "
          "A kind prefixed 'map:' is a row of the Map<String,V> term: "
          "both readers grade it as the row of the kind after the prefix.")
sys.setrecursionlimit(20000)


class Fault(Exception):
    def __init__(self, fault, position):
        self.fault, self.position = fault, position


def ident_start(c):
    return 'A' <= c <= 'Z' or 'a' <= c <= 'z' or c == '_'


def ident_part(c):
    return ident_start(c) or '0' <= c <= '9'


def parse(text, declaration=False):
    if text == '':
        return ('err', 'Empty', 0)
    n = len(text)

    def ident(i):
        if i >= n or not ident_start(text[i]):
            raise Fault('IllegalCharacter', i)
        j = i + 1
        while j < n and ident_part(text[j]):
            j += 1
        if j < n and text[j] not in '<>?':
            raise Fault('IllegalCharacter', j)
        return text[i:j], j

    def nullable(i):
        if i < n and text[i] == '?':
            if i + 1 < n and text[i + 1] == '?':
                raise Fault('DoubleNullable', i + 1)
            return True, i + 1
        return False, i

    def map_key(i):
        if i >= n or text[i] in '<>?':
            raise Fault('ExpectedTypeName', i)
        if text[i] == '$':
            raise Fault('MapKey' if declaration else 'IllegalCharacter', i)
        if not ident_start(text[i]):
            raise Fault('IllegalCharacter', i)
        j = i + 1
        while j < n and ident_part(text[j]):
            j += 1
        if j < n and text[j] not in '<>?,':
            raise Fault('IllegalCharacter', j)
        if text[i:j].lower() != 'string' or (j < n and text[j] in '<?'):
            raise Fault('MapKey', i)
        if j >= n or text[j] == '>':
            raise Fault('MapWithoutValue', j)
        return text[i:j], j + 1

    def base(i):
        if i >= n or text[i] in '<>?':
            raise Fault('ExpectedTypeName', i)
        if text[i] == '$':
            if not declaration:
                raise Fault('IllegalCharacter', i)
            i += 1
            if i >= n or text[i] in '<>?':
                raise Fault('ExpectedTypeName', i)
            name, j = ident(i)
            if j < n and text[j] == '<':
                raise Fault('UnexpectedOpen', j)
            return ('$' + name, None, None), j
        name, j = ident(i)
        if name == 'Collection' and not (j < n and text[j] == '<'):
            raise Fault('CollectionWithoutBracket', i)
        if name != 'Collection' and name.lower().startswith('collection'):
            raise Fault('ReservedCollectionPrefix', i)
        key, start = None, j + 1
        if name == 'Map':
            if not (j < n and text[j] == '<'):
                raise Fault('MapWithoutBracket', i)
            key, start = map_key(j + 1)
        if j < n and text[j] == '<':
            argument, k = term(start)
            if k >= n:
                raise Fault('MissingClose', k)
            if text[k] != '>':
                raise Fault('UnexpectedOpen' if text[k] == '<' else 'IllegalCharacter', k)
            return (name, argument, key), k + 1
        return (name, None, None), j

    def term(i):
        (name, argument, key), j = base(i)
        question, k = nullable(j)
        return (question, name, argument, key), k

    try:
        root, end = term(0)
        if end != n:
            raise Fault({'>': 'UnexpectedClose', '<': 'UnexpectedOpen'}.get(text[end], 'IllegalCharacter'), end)
    except Fault as f:
        return ('err', f.fault, f.position)

    def shape(t):
        question, name, argument, key = t
        return ('?' if question else '') + name + (
            '' if argument is None else '(' + ('' if key is None else key + ',') + shape(argument) + ')')

    return ('ok', shape(root))


def reserved_name(text):
    parsed = parse(text)
    if parsed[0] != 'ok':
        return None
    for token in re.findall(r'[A-Za-z_][A-Za-z0-9_]*', parsed[1]):
        if token != 'Collection' and token.lower() in RESERVED:
            return RESERVED[token.lower()]
    return '-'


def canonical_of(depth, element):
    atom = BUILTIN_CANONICAL.get(element.lower(), element)
    return 'Collection<' * depth + atom + '>' * depth


def element_of(name, depth):
    return name[len('Collection<') * depth:len(name) - depth]


def unescape(field):
    return re.sub(r'\\u([0-9a-fA-F]{4})', lambda m: chr(int(m.group(1), 16)), field)


def escape(text):
    return ''.join('\\u%04x' % ord(c) if ord(c) < 0x20 or ord(c) > 0x7e or c in '\\|,\t' else c for c in text)


TOKENS = ['Int', 'Collection<', 'Gauge<', '>', '?', '$T']
EXTRA = ['Json?', 'Expression<Boolean>', 'Collection<Collection<Int?>?>?', 'int?', '_x9?', 'Collection?', 'Collection<Int>>?',
         '?Int', 'Int?>', 'Int?<', '$', '$$T', '$1', '$T<X>', '$T??', '$Collection', '$collectionX', '$COLLECTION', '$Collection?',
         'Collection<$Collection>', 'Gauge<$collectionX?>?', '$_', '$T?', '$Tx9', 'Collection<$T?>?', 'Gauge<Expression<$T>>',
         'Gauge<Expression<$T?>?>?', 'Int ?', 'Int? ', 'Int?\n', 'Collection<Int?', 'Collection<Int>?>', 'Collections?',
         'collection<Int>?', 'COLLECTION<Int>', 'Collection<>?', 'Gauge<>', 'Gauge<?>', 'Gauge<Int?>>', 'Opaque<Collection<Opaque<Int?>?>>',
         'A?B?', 'Int?Int', 'Int??Int', 'Gauge<Int??>', 'Gauge<Int>??', 'Collection<Gauge<Int?>?>?', 'Any?', 'Float?', 'Gauge<Any>',
         'Int\u00e9?', 'Int?\u00e9', '$\u00e9']

MAP_MARKER = '# Map<String,V> term rows'
MAP_SECTION = [
    MAP_MARKER + ". Written by tools/grammar/port-type-names.py like the rows above, each kind prefixed 'map:'. A key is the atom String in any case and nothing else (MapKey); "
    "a comma after any other head, a third argument and a bare Map are refused as the other syntax faults.",
]
MAP_KEYS = ['String', 'string', 'STRING', 'Int', 'String?', 'Collection<String>', '$T', 'Gauge<String>', 'Json', 'Map<String,String>',
            'collection', 'Foo', 'Str', '']
MAP_VALUES = ['Int', 'Int?', 'Collection<Int>', 'Map<String,Int>', 'Gauge<Int>', '$T', '$T?', 'Json', 'Json?',
              'Collection<Map<String,Int>>', '']
MAP_EXTRA = ['Map', 'Map?', 'Map<', 'Map<>', 'Map<,>', 'Map<String', 'Map<String,', 'Map<String,Int', 'Map<String>', 'Map<String>>',
             'Map<String,Int,Int>', 'Map<String,Int,>', 'Map<String,,Int>', 'Map<String, Int>', 'Map< String,Int>', 'Map<String,Int >',
             'Map<Int>', 'Map<String?>', 'Map<String?,Int>', 'Map<String<Int>,Int>', 'Map<$T,Int>', 'Map<$T,$T>', 'Map<?,Int>',
             'Map<String,?>', 'Map<String,Int>>', 'Map<String,Int>?>', 'Map<String,Int>??', 'Map<String,Int??>', 'Map<String,Int?>?',
             'MAP<String,Int>', 'map<String,Int>', 'mapx<String,Int>', 'Maps<String,Int>', 'Mapx', 'map', 'MAP', 'Gauge<Int,Int>',
             'Gauge<Int,Map<String,Int>>', 'Gauge<Map<String,Int>,Int>', 'Gauge<Map<String,Int>>', 'Collection<Int,Int>', 'Collection<Int,>',
             'Collection<Map<String,Int>,Int>', 'Int,Int', 'Int,', 'Map<String,Int>,Int',
             'Map<String,Map<String,Map<String,Map<String,Int>>>>', 'Collection<Collection<Map<String,Collection<Int>>>>',
             'Map<String,Collection<Map<String,Int?>?>?>?', 'Expression<Map<String,Int>>', 'Map<String,Expression<Int>>',
             'Map<String,Any>', 'Map<String,Float>', 'Map<string,int>', 'Map<String\u00e9,Int>', 'Map<\u00e9,Int>', 'Map<String,\u00e9>',
             'Map<String,List<Int>>', 'Collection<Map<String,List<Int>>>', 'Collection<Collection<Map<String,List<Int>>>>']

RESERVED_SECTION = [
    RESERVED_MARKER + ", which the vocabulary does not hold. 'reserved' rows: a well-formed document term and the reserved name it holds, outermost first ('-' for none).",
    "# A stored term, a registration or a plugin declaration holding a reserved name is refused as port-type-alias, in any case and at any depth. A 'term' row's canonical name above is the vocabulary's: neither Float nor Any is an alias of anything, so each stays as written.",
]
RESERVED_ATOMS = ['Any', 'any', 'ANY', 'aNy', 'Float', 'float', 'FLOAT', 'fLoAt', 'Anything', 'Floats', 'Int', 'Number']
RESERVED_WRAPS = ['%s', '%s?', 'Collection<%s>', 'Collection<%s>?', 'Collection<%s?>', 'Collection<Collection<%s>>', 'Gauge<%s>',
                 'Gauge<%s>?', 'Gauge<Collection<%s>>', 'Collection<Gauge<%s>>', '%s<Int>']
RESERVED_EXTRA = ['Any<Float>', 'Float<Any>', 'Gauge<Any<Int>>', 'Collection<Float>?', 'Gauge<Float>', 'Collection<Collection<Collection<float>>>']

SECTION = [
    MARKER + '. Both grammars read one lexer; shapes write a nullable level with a leading \'?\', a constructor as Name(argument) and a parameter as $Name.',
    "# name; 'doc' shape (well-formed in both grammars) | 'decl' shape DocFault DocPosition (well-formed only under the declaration grammar) | 'refused' Fault position (refused by both, alike) | 'declrefused' DeclFault DeclPosition DocFault DocPosition.",
    "# Rows after this line are written by tools/grammar/port-type-names.py from the grammar in docs/reference/port-types.md (Names, Grammar), never from either half. A fault position is the index of the character that stops the sentence; after a '$' the identifier's index. Every 'term' and 'syntax' row above reads the same under the declaration grammar unless its name holds a '$'.",
]


def section_rows(seen, given=None):
    names = []
    for n in range(1, 5) if given is None else []:
        names += [''.join(c) for c in itertools.product(TOKENS, repeat=n)]
    rows, done = [], set()
    for name in (names + EXTRA if given is None else given):
        if name in seen or name in done:
            continue
        done.add(name)
        d, c = parse(name), parse(name, True)
        if d[0] == 'ok':
            assert c == d
            rows.append([escape(name), 'doc', d[1]])
        elif c[0] == 'ok':
            rows.append([escape(name), 'decl', c[1], d[1], str(d[2])])
        elif c == d:
            rows.append([escape(name), 'refused', d[1], str(d[2])])
        else:
            rows.append([escape(name), 'declrefused', c[1], str(c[2]), d[1], str(d[2])])
    return ['\t'.join(r) for r in rows]


def map_rows(seen):
    names = ['Map<%s,%s>' % (k, v) for k in MAP_KEYS for v in MAP_VALUES] + ['Map<String,%s>?' % v for v in MAP_VALUES]
    rows = []
    for row in section_rows(seen, names + MAP_EXTRA):
        fields = row.split('\t')
        fields[1] = 'map:' + fields[1]
        rows.append('\t'.join(fields))
    return rows


def reserved_rows():
    names, done = [], set()
    for atom in RESERVED_ATOMS:
        for wrap in RESERVED_WRAPS:
            names.append(wrap % atom)
    for name in names + RESERVED_EXTRA:
        if name in done or parse(name)[0] != 'ok':
            continue
        done.add(name)
    return ['\t'.join([escape(n), 'reserved', reserved_name(n)]) for n in sorted(done)]


def rewrite_canonical(line):
    if line.startswith('# fields:'):
        return FIELDS
    f = line.split('\t')
    if len(f) > 4 and f[1] == 'term':
        f[3] = escape(element_of(unescape(f[0]), int(f[2])))
        f[4] = canonical_of(int(f[2]), f[3])
    return '\t'.join(f)


def check(lines):
    bad = 0
    for number, line in enumerate(lines, 1):
        if not line or line[0] == '#':
            continue
        f = line.split('\t')
        name, kind = unescape(f[0]), f[1]
        if kind.startswith('map:'):
            kind = kind[4:]
        d, c = parse(name), parse(name, True)
        err = lambda r: 'ok' if r[0] == 'ok' else '%s@%s' % (r[1], r[2])
        if kind == 'reserved':
            ok = d[0] == 'ok' and c == d and reserved_name(name) == f[2]
        elif kind == 'term':
            ok = (d[0] == 'ok' and c[0] == 'ok' and f[3] == escape(element_of(name, int(f[2])))
                  and f[4] == canonical_of(int(f[2]), f[3]))
        elif kind == 'syntax':
            ok = err(d) == '%s@%s' % (f[2], f[3]) and ('$' in name or c == d)
        elif kind == 'doc':
            ok = d == ('ok', f[2]) and c == d
        elif kind == 'decl':
            ok = c == ('ok', f[2]) and err(d) == '%s@%s' % (f[3], f[4])
        elif kind == 'refused':
            ok = err(d) == '%s@%s' % (f[2], f[3]) and c == d
        elif kind == 'declrefused':
            ok = err(c) == '%s@%s' % (f[2], f[3]) and err(d) == '%s@%s' % (f[4], f[5])
        else:
            ok = False
        if not ok:
            bad += 1
            print('line %d differs: %r  doc=%s decl=%s' % (number, line[:100], d, c))
    return bad


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else '--check'
    lines = open(PATH, encoding='utf8').read().split('\n')
    if lines and lines[-1] == '':
        lines.pop()
    if mode == '--check':
        bad = check(lines)
        print('%d rows checked, %d differ' % (len([l for l in lines if l and l[0] != '#']), bad))
        sys.exit(1 if bad else 0)
    if mode == '--write':
        cut = next(i for i, l in enumerate(lines) if l.startswith(MARKER))
        head = [rewrite_canonical(l) for l in lines[:cut]]
        seen = {unescape(l.split('\t')[0]) for l in head if l and l[0] != '#'}
        open(PATH, 'w', encoding='utf8').write(
            '\n'.join(head + SECTION + section_rows(seen) + MAP_SECTION + map_rows(seen) + RESERVED_SECTION + reserved_rows()) + '\n')
        return
    if mode == '--write-map':
        start = next((i for i, l in enumerate(lines) if l.startswith(MAP_MARKER)), None)
        end = next(i for i, l in enumerate(lines) if l.startswith(RESERVED_MARKER))
        seen = {unescape(l.split('\t')[0]) for l in lines[:start if start is not None else end]
                if l and l[0] != '#'}
        lines[(start if start is not None else end):end] = MAP_SECTION + map_rows(seen)
        open(PATH, 'w', encoding='utf8').write('\n'.join(lines) + '\n')
        return
    sys.exit('usage: port-type-names.py --check | --write | --write-map')


if __name__ == '__main__':
    main()
