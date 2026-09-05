"""Validate actual Windows.Data.Pdf pixels produced by the C# regression suite."""
import json
import math
import shutil
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw
from pypdf import PdfReader

root = Path(__file__).resolve().parents[2]
temp, output = root / 'tmp/pdfs', root / 'output/pdf'
output.mkdir(parents=True, exist_ok=True)
original = Image.open(temp / 'windows-original.png').convert('RGB')
page = PdfReader(temp / 'middle-delete.pdf').pages[0]
sx, sy = original.width / float(page.cropbox.width), original.height / float(page.cropbox.height)
names = ['middle-delete', 'insert-one', 'insert-two', 'insert-three', 'reopen-insert']
checked = 0
for name in names:
    layout = json.loads((temp / f'{name}-layout.json').read_text(encoding='utf-8'))
    rendered = Image.open(temp / f'windows-{name}.png').convert('RGB')
    assert rendered.size == original.size
    for glyph in layout['Glyphs']:
        if glyph['IsVirtual'] or glyph['Text'].isspace():
            continue
        box = glyph['Bounds']
        pixels = (math.floor(box['X'] * sx), math.floor(box['Y'] * sy),
                  math.ceil((box['X'] + box['Width']) * sx), math.ceil((box['Y'] + box['Height']) * sy))
        crop = rendered.crop(pixels).convert('L')
        ink = sum(count for value, count in enumerate(crop.histogram()) if value < 200)
        assert ink > max(2, crop.width * crop.height * .02), (name, glyph['TextIndex'], glyph['Text'], pixels, ink)
        checked += 1
    # Editing the first physical line must not touch other title lines or body.
    difference = ImageChops.difference(original, rendered)
    first_line = layout['Lines'][0]
    top = math.floor((first_line['Y'] - 1) * sy)
    bottom = math.ceil((first_line['Y'] + first_line['Height'] + 1) * sy)
    ImageDraw.Draw(difference).rectangle((0, top, rendered.width, bottom), fill='black')
    assert difference.getbbox() is None, (name, 'pixels changed outside edited line', difference.getbbox())
    shutil.copy2(temp / f'{name}.pdf', output / f'{name}.pdf')

crop_box = (int(original.width * .36), int(original.height * .07),
            int(original.width * .93), int(original.height * .11))
rows = []
for name in ['original'] + names:
    crop = Image.open(temp / f'windows-{name}.png').convert('RGB').crop(crop_box)
    row = Image.new('RGB', (crop.width, crop.height + 28), 'white')
    ImageDraw.Draw(row).text((5, 5), name, fill='black')
    row.paste(crop, (0, 28)); rows.append(row)
comparison = Image.new('RGB', (rows[0].width, sum(row.height for row in rows)), 'white')
y = 0
for row in rows:
    comparison.paste(row, (0, y)); y += row.height
comparison.save(output / 'windows-keystrokes.png')
print(f'PASS Windows renderer: ink at all {checked} expected glyph positions; all pixels outside edited line unchanged')
