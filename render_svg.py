from svglib.svglib import svg2rlg
from reportlab.graphics import renderPM
from PIL import Image
import io
import os

svg_path = r'D:\Cloud\roman@heimman.com\OneDrive - RH\Documents\Apps\WasabiDrive\wasabi_logo_icon_170229.svg'

print(f'Reading SVG: {svg_path}')

# Render at multiple sizes
sizes = [256, 128, 64, 48, 32, 16]
images = []

for size in sizes:
    # Reload and scale for each size (reportlab needs fresh drawing)
    drawing = svg2rlg(svg_path)
    scale = size / drawing.width
    drawing.width = size
    drawing.height = size
    drawing.scale(scale, scale)
    
    # Render to PNG bytes with transparent background
    png_bytes = renderPM.drawToString(drawing, fmt='PNG', bg=0x00000000)
    img = Image.open(io.BytesIO(png_bytes)).convert('RGBA')
    
    # Make white background transparent
    data = img.getdata()
    new_data = []
    for item in data:
        # Convert white (or near-white) background to transparent
        if item[0] > 240 and item[1] > 240 and item[2] > 240:
            new_data.append((255, 255, 255, 0))
        else:
            new_data.append(item)
    img.putdata(new_data)
    
    images.append(img)
    print(f'  Rendered {size}x{size}')

# Save as multi-resolution ICO
images[0].save(
    'wasabidrive.ico',
    format='ICO',
    sizes=[(size, size) for size in sizes],
    append_images=images[1:]
)
print(f'ICO created: {os.path.getsize("wasabidrive.ico") / 1024:.1f} KB')

# Save 256x256 as PNG
images[0].save('logo.png', 'PNG')
print(f'PNG created: {os.path.getsize("logo.png") / 1024:.1f} KB')
