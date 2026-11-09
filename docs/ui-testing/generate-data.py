"""Generate encoding and volume variants into a disposable directory, without executing UI cases."""
import pathlib
import sys

if len(sys.argv) != 2:
    raise SystemExit('Usage: python docs/ui-testing/generate-data.py <empty output directory>')
target = pathlib.Path(sys.argv[1])
target.mkdir(parents=True, exist_ok=True)
if any(target.iterdir()):
    raise SystemExit('Output directory must be empty')

tsv = '_caseId\temail\tstatus\ncase-001\talice@example.test\t200\ncase-002\tbob@example.test\t404\n'
(target / 'valid.tsv').write_text(tsv, encoding='utf-8', newline='')
text = '_caseId,name\nencoding-1,caf\u00e9\nencoding-2,na\u00efve\n'
for name, encoding in [('utf8.csv', 'utf-8'), ('utf16le.csv', 'utf-16'), ('windows1252.csv', 'cp1252')]:
    (target / name).write_text(text, encoding=encoding, newline='')
with (target / 'large.csv').open('w', encoding='utf-8', newline='') as output:
    output.write('_caseId,email,status\n')
    for i in range(1000):
        output.write(f'large-{i:04d},qa{i:04d}@example.test,200\n')
print(f'Generated data in {target.resolve()}')
