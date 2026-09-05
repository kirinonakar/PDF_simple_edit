"""Check paragraph edits and marquee isolation using the app's Windows renderer."""
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
page = PdfReader(temp / 'body-insert.pdf').pages[0]
sx, sy = original.width / float(page.cropbox.width), original.height / float(page.cropbox.height)
body = next(b for b in json.loads((temp / 'native-original.json').read_text(encoding='utf-8')) if b['Text'].startswith('To prospectively'))
def pixels(box, pad=2):
    return (math.floor((box['X']-pad)*sx), math.floor((box['Y']-pad)*sy),
            math.ceil((box['Right']+pad)*sx), math.ceil((box['Bottom']+pad)*sy))

panels = [('original', 'Original'), ('body-insert', 'Insert: words flow down'),
          ('body-delete', 'Delete: words pull up'), ('body-region-delete', 'Marquee: only selected line removed')]
crop = pixels(body['Bounds'], 8)
width, height = crop[2]-crop[0], crop[3]-crop[1]
comparison = Image.new('RGB', (width*2, (height+32)*2), 'white')
draw = ImageDraw.Draw(comparison)
for i, (name, label) in enumerate(panels):
    rendered = Image.open(temp / f'windows-{name}.png').convert('RGB')
    if name != 'original':
        diff = ImageChops.difference(original, rendered)
        allowed = body['Lines'][1] if name == 'body-region-delete' else body['Bounds']
        ImageDraw.Draw(diff).rectangle(pixels(allowed), fill='black')
        assert diff.getbbox() is None, (name, 'changed pixels outside selection', diff.getbbox())
        shutil.copy2(temp / f'{name}.pdf', output / f'{name}.pdf')
    x, y = (i % 2)*width, (i // 2)*(height+32)
    draw.text((x+8, y+8), label, fill='black')
    comparison.paste(rendered.crop(crop), (x, y+32))
comparison.save(output / 'paragraph-reflow.png')
print('PASS paragraph rendering: unchanged pixels outside edited paragraph / marquee line')
