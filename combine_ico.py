from PIL import Image
import os

sizes = [256, 128, 64, 48, 32, 16]
images = []

for size in sizes:
    path = f'logo_{size}.png'
    img = Image.open(path).convert('RGBA')
    images.append(img)
    print(f'Loaded {size}x{size}')

# Save as multi-resolution ICO
images[0].save(
    'wasabidrive.ico',
    format='ICO',
    sizes=[(size, size) for size in sizes],
    append_images=images[1:]
)
print(f'ICO created: {os.path.getsize("wasabidrive.ico") / 1024:.1f} KB')

# 256x256 PNG for logo
images[0].save('logo.png', 'PNG')
print(f'PNG created: {os.path.getsize("logo.png") / 1024:.1f} KB')
