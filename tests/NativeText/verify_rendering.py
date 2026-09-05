"""Run after dotnet run --project tests/NativeText. Requires Poppler and Pillow."""
import json
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

root = Path(__file__).resolve().parents[2]
source = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('D:/ASUNA/test/3D knee.pdf')
temp = root / 'tmp/pdfs'
output = root / 'output/pdf'
output.mkdir(parents=True, exist_ok=True)


def render(pdf, name, all_pages=False):
    command = ['pdftoppm', '-scale-to', '1600', '-png']
    if not all_pages:
        command += ['-f', '1', '-singlefile']
    subprocess.run(command + [str(pdf), str(temp / name)], check=True, capture_output=True)
    return sorted(p for p in temp.glob(f'{name}-*.png') if p.stem.removeprefix(name + '-').isdigit()) if all_pages else [temp / f'{name}.png']


originals = render(source, 'verify-original', True)
edits = render(temp / 'native-edit.pdf', 'verify-native', True)
assert len(originals) == len(edits) == 12
for page, (original, edited) in enumerate(zip(originals, edits), 1):
    before, after = Image.open(original).convert('RGB'), Image.open(edited).convert('RGB')
    assert before.size == after.size
    bbox = ImageChops.difference(before, after).getbbox()
    if page == 1:
        assert bbox is not None
        # At this render size the source word Knee occupies this small rectangle.
        assert 640 <= bbox[0] <= bbox[2] <= 730 and 120 <= bbox[1] <= bbox[3] <= 165, bbox
        word_bbox = bbox
    else:
        assert bbox is None, f'Unedited page {page} changed pixels: {bbox}'

previews = {}
for name in ['native-fixed-layout', 'native-move', 'native-delete', 'legacy-edit']:
    previews[name] = render(temp / f'{name}.pdf', f'verify-{name}')[0]
for name in ['native-edit', 'native-fixed-layout', 'native-move', 'native-delete', 'legacy-edit']:
    shutil.copy2(temp / f'{name}.pdf', output / f'{name}.pdf')
shutil.copy2(edits[0], output / 'native-edit-page-1.png')

samples = [
    ('Original', originals[0]),
    ('Preserve original: Knee -> Keen', edits[0]),
    ('Preserve original: two edits, fixed line positions', previews['native-fixed-layout']),
    ('Legacy: redraw with natural glyph advances', previews['legacy-edit']),
]
crop = (430, 110, 1214, 385)
comparison = Image.new('RGB', (1568, 620), 'white')
draw = ImageDraw.Draw(comparison)
for index, (label, path) in enumerate(samples):
    x, y = index % 2 * 784, index // 2 * 310
    draw.text((x + 12, y + 8), label, fill='black')
    comparison.paste(Image.open(path).convert('RGB').crop(crop), (x, y + 30))
comparison.save(output / 'comparison.png')
report = {'source': str(source), 'pages': len(originals), 'render_height': 1600,
          'changed_word_pixel_bounds': word_bbox, 'unedited_pages_pixel_identical': 11}
(output / 'render-verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report))
print(f'PASS rendering; examples and comparison saved to {output}')
