# Smart Thumbnail Crop - YOLO11n ONNX Model

## Setup

Place the `yolo11n.onnx` model file in this directory.

### How to obtain the model:

**Export from Ultralytics**
```bash
pip install ultralytics
python -c "from ultralytics import YOLO; model = YOLO('yolo11n.pt'); model.export(format='onnx', imgsz=416, opset=13, simplify=True)"
```

The export must use `imgsz=416` — the app preprocesses to a fixed 416x416
letterbox input and the model input test asserts this shape.

## Model Details

- **Architecture**: YOLO11 Nano (smallest variant)
- **Input**: 416x416 RGB image (NCHW format, letterboxed, /255 normalized)
- **Output**: [1, 84, 3549] tensor (4 bbox + 80 class scores × 3549 predictions)
- **Size**: ~10.2MB
- **Inference**: ~15-25ms on modern CPU
- **Classes**: 80 COCO classes (person, car, etc.)

## How it works

The smart crop feature:
1. Runs YOLO11n inference on the thumbnail
2. Detects the main subject (prioritizes "person" class)
3. Crops the square region centered on the detected subject
4. Falls back to text-region/saliency analysis if no subject detected

This ensures music thumbnails (especially YouTube 16:9) are cropped
to show the artist/main content rather than arbitrary center cropping.
