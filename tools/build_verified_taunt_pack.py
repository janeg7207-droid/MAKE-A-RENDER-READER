"""Build a deterministic recovery resource from fully decoded body records only."""
import collections
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--work', required=True, type=Path,
    help='Audit workspace containing the complete CRC, version, equivalence and production-decoder JSON reports.')
parser.add_argument('--evidence', type=Path, help='Raw evidence directory; defaults to WORK/evidence. Original machine paths in reports are rebased by basename.')
parser.add_argument('--output', type=Path, help='Destination ZIP; defaults to WORK/VerifiedTauntRecords.zip.')
args = parser.parse_args()
work = args.work.resolve()
evidence = (args.evidence or work / 'evidence').resolve()
output = (args.output or work / 'VerifiedTauntRecords.zip').resolve()
output.parent.mkdir(parents=True, exist_ok=True)

def evidence_path(recorded):
    target = evidence / Path(recorded).name
    if not target.is_file():
        raise FileNotFoundError('Required raw evidence file is unavailable: ' + str(target))
    return target
full = json.loads((work / 'stream_audit.json').read_text())
versions = json.loads((work / 'record_versions.json').read_text())
decoded = json.loads((work / 'production-decoder-audit.json').read_text())
equivalence = json.loads((work / 'record_semantic_byte_audit.json').read_text())
assert full['archive_integrity_verified'] and full['motion_scan_complete']
assert decoded['source_archive_sha256'] == full['archive_sha256'] == versions['source_archive_sha256']
assert not equivalence['differences_outside_8_9'] and not equivalence['unexpected_header8_values']
groups = collections.defaultdict(list)
for version in versions['versions']:
    groups[version['clip']].append(version)

def provenance(clip):
    sources = []
    for version in groups[clip]:
        source = version['source']
        sources.append({'archiveMember': source['archive_member'], 'containerCrc32': source['container_crc32'],
            'containerUncompressedOffset': source['container_uncompressed_offset'],
            'recordOffset': source['record_offset_in_container'], 'length': source['length'],
            'sha256': source['sha256'], 'headerId': source['header_id']})
    return sorted(sources, key=lambda item: (item['archiveMember'], item['recordOffset']))

def checked_record(row):
    clip = row['clip']
    raw = evidence_path(row['path']).read_bytes()
    assert len(raw) == row['size'] and hashlib.sha256(raw).hexdigest() == row['sha256']
    matches = [version for version in groups[clip] if version['path'] == row['path']]
    assert len(matches) == 1
    version = matches[0]
    assert version['source']['archive_member'].startswith('Animation/Acts/Basic/')
    assert version['source']['sha256'] == row['sha256']
    assert struct.unpack_from('<Q', raw)[0] == int(version['definition']['hash'], 16)
    assert struct.unpack_from('<H', raw, 8)[0] == version['definition']['id']
    tracks = struct.unpack_from('<I', raw, 0x20)[0]
    assert 0xf4043b2d54a71fc8 in struct.unpack_from('<' + 'Q' * tracks, raw, 0xa0)
    identity = raw[:8] + b'\0\0' + raw[10:]
    for other in groups[clip]:
        value = evidence_path(other['path']).read_bytes()
        assert value[:8] + b'\0\0' + value[10:] == identity
        assert hashlib.sha256(value).hexdigest() == other['source']['sha256']
    return raw, version

manifest = {'version': 1, 'archive': {'name': 'Animation.7z (89 parts)', 'sha256': full['archive_sha256'],
    'volumes': 89, 'memberCrcsVerified': full['members_crc_verified']}, 'camSha256': full['source_cam_sha256'],
    'bodySignature': {'bone': 'J_Hips', 'fnv64': 'f4043b2d54a71fc8'},
    'equivalence': {'ignoredOffsets': [8, 9],
        'rule': 'Only uint16@0x08 may differ: canonical logical ID versus 0xFFFF. Exact canonical FNV64 and every other byte must match. Raw bytes and integrity SHA256 are unchanged.',
        'evidenceSha256': hashlib.sha256((work / 'record_semantic_byte_audit.json').read_bytes()).hexdigest(),
        'canonicalIdsCompared': 1578, 'occurrencesCompared': 3281},
    'decoder': {'name': decoded['decoder'], 'windowsRenderVerified': False}, 'records': [], 'unsupported': []}
payloads = {}
for row in sorted(decoded['records'], key=lambda item: item['clip']):
    if row['supported'] and row['bundled_available']:
        continue
    raw, version = checked_record(row)
    base = {'clip': row['clip'], 'role': 'body', 'size': row['size'], 'sha256': row['sha256'],
        'names': version['definition']['names'], 'sources': provenance(row['clip'])}
    if row['supported']:
        entry = 'records/' + row['clip'] + '.clip'
        manifest['records'].append({**base, 'entry': entry, 'frames': row['frames'], 'bones': row['bones'], 'fps': row['fps']})
        payloads[entry] = raw
    else:
        manifest['unsupported'].append({**base, 'reason': row['error']})
assert len(manifest['records']) == decoded['supported_unbundled'] == 617
assert len(manifest['unsupported']) == decoded['unsupported'] == 71
temporary = output.with_suffix('.tmp')
try:
    with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        entries = {'manifest.json': (json.dumps(manifest, sort_keys=True, separators=(',', ':'), ensure_ascii=False) + '\n').encode()}
        entries.update(payloads)
        for name in sorted(entries):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, entries[name], compresslevel=9)
    with zipfile.ZipFile(temporary) as archive:
        assert archive.testzip() is None
        assert len(archive.namelist()) == len(payloads) + 1
        for row in manifest['records']:
            value = archive.read(row['entry'])
            assert len(value) == row['size'] and hashlib.sha256(value).hexdigest() == row['sha256']
    temporary.replace(output)
finally:
    temporary.unlink(missing_ok=True)
digest = hashlib.sha256(output.read_bytes()).hexdigest()
summary = {'path': str(output), 'sha256': digest, 'bytes': output.stat().st_size,
    'playableRecordCount': len(manifest['records']), 'unsupportedMetadataCount': len(manifest['unsupported']),
    'uncompressedRecordBytes': sum(len(value) for value in payloads.values()),
    'sourceArchiveSha256': full['archive_sha256']}
output.with_name(output.stem + '.summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary))
