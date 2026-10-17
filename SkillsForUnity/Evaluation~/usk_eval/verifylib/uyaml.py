"""Minimal reader for Unity's YAML asset files (prefab, material, physic material, ScriptableObject).

Disk is an evaluation channel independent of the plugin's own read skills: references carry
GUIDs here, which the REST readers do not expose for assets. Only the subset Unity writes is
handled: "--- !u!<classID> &<fileID>" documents, block mappings, block sequences ("- " at the
parent key's indent), flow mappings such as {fileID: 0, guid: ..., type: 2} and empty [] / {}.
"""
import re

DOC_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)( stripped)?\s*$")


class Doc(object):
    def __init__(self, class_id, file_id, stripped, type_name, body):
        self.class_id = class_id
        self.file_id = file_id
        self.stripped = stripped
        self.type_name = type_name
        self.body = body

    def get(self, *keys, default=None):
        node = self.body
        for key in keys:
            if isinstance(node, dict) and key in node:
                node = node[key]
            elif isinstance(node, list) and isinstance(key, int) and -len(node) <= key < len(node):
                node = node[key]
            else:
                return default
        return node

    def __repr__(self):
        return "Doc(%s &%s %s)" % (self.class_id, self.file_id, self.type_name)


def _split_flow(text):
    parts, depth, cur = [], 0, []
    for ch in text:
        if ch in "{[":
            depth += 1
        elif ch in "}]":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    if "".join(cur).strip():
        parts.append("".join(cur))
    return parts


def scalar(text):
    text = text.strip()
    if text.startswith("{") and text.endswith("}"):
        out = {}
        for part in _split_flow(text[1:-1]):
            if ":" in part:
                key, value = part.split(":", 1)
                out[key.strip()] = scalar(value)
        return out
    if text.startswith("[") and text.endswith("]"):
        inner = text[1:-1].strip()
        return [scalar(p) for p in _split_flow(inner)] if inner else []
    if len(text) >= 2 and text[0] == text[-1] and text[0] in "'\"":
        return text[1:-1]
    return text


def _indent(line):
    return len(line) - len(line.lstrip(" "))


def _parse_block(lines, i, indent):
    """Parse a mapping or sequence whose entries sit at exactly `indent` columns."""
    if i < len(lines) and _indent(lines[i]) == indent and lines[i].lstrip().startswith("- "):
        seq = []
        while i < len(lines) and _indent(lines[i]) == indent and lines[i].lstrip().startswith("- "):
            item = lines[i].lstrip()[2:]
            if ":" in item and not item.startswith("{"):
                # mapping item: first key on the dash line, the rest at indent + 2
                sub = [" " * (indent + 2) + item] + []
                j = i + 1
                while j < len(lines) and _indent(lines[j]) > indent:
                    sub.append(lines[j])
                    j += 1
                value, _ = _parse_block(sub, 0, indent + 2)
                seq.append(value)
                i = j
            else:
                seq.append(scalar(item))
                i += 1
        return seq, i
    mapping = {}
    while i < len(lines):
        line = lines[i]
        ind = _indent(line)
        if ind < indent or not line.strip():
            if not line.strip():
                i += 1
                continue
            break
        if ind > indent:
            i += 1          # continuation of a wrapped scalar; Unity rarely emits these for checked fields
            continue
        stripped = line.strip()
        if stripped.startswith("- "):
            break
        key, _, rest = stripped.partition(":")
        rest = rest.strip()
        i += 1
        if rest:
            mapping[key] = scalar(rest)
        elif i < len(lines) and (_indent(lines[i]) > indent or
                                 (_indent(lines[i]) == indent and lines[i].lstrip().startswith("- "))):
            child_indent = _indent(lines[i])
            mapping[key], i = _parse_block(lines, i, child_indent)
        else:
            mapping[key] = None
    return mapping, i


def _join_wrapped(lines):
    """Unity wraps long flow mappings ({fileID: ..., guid: ...,\n      type: 3}); rejoin them."""
    out = []
    for line in lines:
        if out and out[-1].count("{") > out[-1].count("}"):
            out[-1] = out[-1] + " " + line.strip()
        else:
            out.append(line)
    return out


def load(path):
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        text = handle.read().replace("\r\n", "\n")
    return loads(text)


def loads(text):
    docs = []
    current = None
    buf = []

    def flush():
        if current is None:
            return
        class_id, file_id, stripped = current
        body_lines = _join_wrapped([l for l in buf if l.strip()])
        if not body_lines:
            docs.append(Doc(class_id, file_id, stripped, None, {}))
            return
        type_name = body_lines[0].strip().rstrip(":")
        body, _ = _parse_block(body_lines[1:], 0, _indent(body_lines[1]) if len(body_lines) > 1 else 2)
        docs.append(Doc(class_id, file_id, stripped, type_name, body if isinstance(body, dict) else {}))

    for line in text.split("\n"):
        match = DOC_RE.match(line)
        if match:
            flush()
            current = (int(match.group(1)), int(match.group(2)), bool(match.group(3)))
            buf = []
        elif current is not None and not line.startswith("%"):
            buf.append(line)
    flush()
    return docs


def by_type(docs, type_name):
    return [d for d in docs if d.type_name == type_name]


def index(docs):
    return {d.file_id: d for d in docs}


def meta_guid(asset_path):
    with open(asset_path + ".meta", "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if line.startswith("guid:"):
                return line.split(":", 1)[1].strip()
    return None


def material_colors(doc):
    """{_BaseColor: (r, g, b, a), ...} from a Material document's m_SavedProperties.m_Colors."""
    out = {}
    for entry in doc.get("m_SavedProperties", "m_Colors", default=[]) or []:
        if isinstance(entry, dict):
            for name, value in entry.items():
                if isinstance(value, dict):
                    out[name] = tuple(float(value.get(k, 0)) for k in ("r", "g", "b", "a"))
    return out

# Producer:Betsy
