"""Regeneriert die eingebetteten Register aus offiziellen lokalen CSV-Dateien."""
import csv, gzip, json
from pathlib import Path
root = Path(__file__).resolve().parent.parent
definitions = []
with (root / 'src/KubDHCP.Core/Data/options.csv').open(encoding='utf-8-sig', newline='') as f:
    for row in csv.DictReader(f):
        tag = row['Tag'].strip()
        if tag.isdigit() and 0 <= int(tag) <= 255:
            definitions.append({'Code': int(tag), 'Name': row['Name'], 'Description': row.get('Meaning', ''), 'Reference': row.get('Reference', '')})
(root / 'src/KubDHCP.Core/Data/options.json').write_text(json.dumps(definitions, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
vendors = {}
for filename in ('oui.csv', 'mam.csv', 'oui36.csv'):
    with (root / '.tools' / filename).open(encoding='utf-8-sig', newline='') as f:
        for row in csv.DictReader(f):
            vendors[row['Assignment'].upper()] = row['Organization Name'].replace('\t', ' ').replace('\n', ' ').strip()
payload = '\n'.join(f'{prefix}\t{vendor}' for prefix, vendor in sorted(vendors.items())).encode('utf-8')
(root / 'src/KubDHCP.Core/Data/oui.tsv.gz').write_bytes(gzip.compress(payload, compresslevel=9, mtime=0))
print(f'{len(definitions)} option definitions; {len(vendors)} manufacturer prefixes; {len(payload):,} bytes before compression')
